using System.Collections.Concurrent;
using System.Diagnostics;
using PerformanceTool.Services.Environments;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Services;

/// <summary>
/// Runs a <see cref="LoadPlan"/> as an open workload: every entry fires at its own fixed rate, independent of
/// how long earlier executions take. If <see cref="LoadPlan.MaxConcurrency"/> executions are already in flight,
/// the run is dropped and reported instead of queued. See docs/brain/0004-open-model-rate-scheduling.md.
/// </summary>
public sealed class LoadRunner(ScenarioCatalog catalog)
{
    public async Task RunAsync(
        LoadPlan plan,
        Func<ExecutionResult, ValueTask> onResult,
        Func<int, ValueTask> onDropped,
        CancellationToken ct,
        Func<ExecutionActivity, ValueTask>? onActivity = null,
        EnvironmentDefinition? environment = null)
    {
        var scenarios = plan.Scenarios.Select(e => catalog.Find(e.ScenarioId)
            ?? throw new InvalidOperationException($"Unknown scenario '{e.ScenarioId}'.")).ToArray();

        // Environment (brain 0033): fill {{Key}} placeholders once, up front. A key the environment doesn't have would only make every actor
        // fail with a confusing URL error, so the run refuses to start and names the keys instead.
        var env = environment?.ToValues() ?? EnvironmentValues.None;
        var unknown = new List<string>();
        var resolved = plan.Scenarios.Select(e => Placeholders.ResolveJson(e.Parameters, env, unknown)).ToArray();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"{(env.Name.Length == 0 ? "No environment is active" : $"Environment '{env.Name}' has no value")} for: {string.Join(", ", unknown.Select(k => "{{" + k + "}}"))}.");

        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = plan.MaxConcurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        // Per-execution timeouts are enforced with a CancellationToken so multi-step scenarios are bounded as a whole.
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PerformanceTool/1.0");

        using var gate = new SemaphoreSlim(plan.MaxConcurrency, plan.MaxConcurrency);
        var inFlight = new List<Task>();
        var inFlightByEntry = new int[plan.Scenarios.Count]; // executions running right now, per entry (brain 0008)
        var inFlightTotal = 0;
        // Counter changes and their activity events happen under one async lock, so observers receive events in the
        // same order the counters changed. Without it, concurrent ends could arrive with stale in-flight counts.
        using var activityLock = new SemaphoreSlim(1, 1);
        var sequence = -1;
        var duration = TimeSpan.FromSeconds(plan.DurationSeconds);
        var timeout = TimeSpan.FromSeconds(plan.TimeoutSeconds);

        // Bind parameters and run StartupAsync lifecycle hooks once per scenario entry before any actors start (brain 0040).
        var boundParams = new object[plan.Scenarios.Count];
        var states = new ConcurrentDictionary<string, object?>[plan.Scenarios.Count];
        var backgrounds = new ScenarioBackgroundHost[plan.Scenarios.Count];
        var startedEntries = new List<int>(plan.Scenarios.Count);

        try
        {
            for (var i = 0; i < plan.Scenarios.Count; i++)
            {
                boundParams[i] = scenarios[i].Spec.Bind(resolved[i]);
                scenarios[i].Spec.ResolveSecrets(boundParams[i]);
                states[i] = new ConcurrentDictionary<string, object?>(StringComparer.Ordinal);
                backgrounds[i] = new ScenarioBackgroundHost(ct);
                var startupCtx = new ScenarioContext(http, env, ct, states[i], backgrounds[i]);
                await scenarios[i].StartupAsync(boundParams[i], startupCtx, ct);
                startedEntries.Add(i);
            }

            var clock = Stopwatch.StartNew();

            async Task ScheduleEntry(int entry)
            {
                var config = plan.Scenarios[entry];
                var parameters = boundParams[entry];
                var state = states[entry];
                // Due times are computed per run in ticks (not n × a rounded interval), so the run count is exact:
                // 180/min for 8 s is 24 runs, not 25. See docs/brain/0004.
                // Bursts (brain 0034): one tick starts ActorsPerBurst actors together; linear is a "burst" of one.
                var bursts = config.Pattern == SpawningPattern.Bursts;
                var perTick = bursts ? Math.Max(1, config.ActorsPerBurst) : 1;
                var ticksPerRun = TimeSpan.TicksPerMinute / (bursts ? config.BurstsPerMinute : config.RatePerMinute);

                for (long n = 0; ; n++)
                {
                    var due = TimeSpan.FromTicks((long)Math.Round(n * ticksPerRun)); // absolute schedule: no drift
                    if (due >= duration || ct.IsCancellationRequested) return;
                    var wait = due - clock.Elapsed;
                    if (wait > TimeSpan.Zero)
                    {
                        try { await Task.Delay(wait, ct); }
                        catch (OperationCanceledException) { return; }
                    }

                    for (var k = 0; k < perTick; k++)
                    {
                        if (!gate.Wait(0))
                        {
                            await onDropped(entry);
                            continue;
                        }

                        var seq = Interlocked.Increment(ref sequence);
                        // Counting and the callback run inside the task, so a slow observer never delays scheduling.
                        var task = Task.Run(async () =>
                        {
                            try
                            {
                                await activityLock.WaitAsync();
                                try
                                {
                                    var nowInFlight = ++inFlightByEntry[entry];
                                    var total = ++inFlightTotal;
                                    if (onActivity is not null)
                                        await onActivity(new ExecutionActivity(seq, entry, clock.Elapsed.TotalSeconds, true, nowInFlight, total));
                                }
                                finally { activityLock.Release(); }
                                await ExecuteOne(scenarios[entry], entry, seq, parameters, state, clock);
                            }
                            finally
                            {
                                gate.Release();
                                await activityLock.WaitAsync();
                                try
                                {
                                    var left = --inFlightByEntry[entry];
                                    var totalLeft = --inFlightTotal;
                                    if (onActivity is not null)
                                        await onActivity(new ExecutionActivity(seq, entry, clock.Elapsed.TotalSeconds, false, left, totalLeft));
                                }
                                finally { activityLock.Release(); }
                            }
                        }, CancellationToken.None);
                        lock (inFlight) inFlight.Add(task);
                    }
                }
            }

            async Task ExecuteOne(IScenario scenario, int entry, int seq, object parameters, ConcurrentDictionary<string, object?> state, Stopwatch clockRef)
            {
                using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                execCts.CancelAfter(timeout);
                var startedAt = clockRef.Elapsed.TotalSeconds;
                var sw = Stopwatch.StartNew();
                ScenarioOutcome outcome;
                try
                {
                    outcome = await scenario.ExecuteAsync(parameters, new ScenarioContext(http, env, execCts.Token, state), execCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return; // user stopped the run: don't report half-finished executions
                }
                catch (OperationCanceledException)
                {
                    outcome = ScenarioOutcome.Failed($"Timeout after {timeout.TotalSeconds:0} s");
                }
                catch (Exception ex)
                {
                    outcome = ScenarioOutcome.Failed(ex.InnerException?.Message ?? ex.Message);
                }
                sw.Stop();

                var elapsed = outcome.Measured ?? sw.Elapsed;
                await onResult(new ExecutionResult(seq, entry, startedAt, elapsed.TotalMilliseconds, outcome.StatusCode, outcome.Bytes, outcome.FailureReason));
            }

            await Task.WhenAll(Enumerable.Range(0, plan.Scenarios.Count).Select(ScheduleEntry));
            Task[] pending;
            lock (inFlight) pending = inFlight.ToArray();
            await Task.WhenAll(pending);
        }
        finally
        {
            // Cancel background jobs (e.g. token refreshers) and run TeardownAsync for every started entry.
            for (var i = startedEntries.Count - 1; i >= 0; i--)
            {
                var idx = startedEntries[i];
                if (backgrounds[idx] is { } bg) await bg.DisposeAsync();
                try
                {
                    var teardownCtx = new ScenarioContext(http, env, CancellationToken.None, states[idx]);
                    await scenarios[idx].TeardownAsync(boundParams[idx], teardownCtx, CancellationToken.None);
                }
                catch
                {
                    // Teardown errors must not mask the primary run outcome or prevent other entries from tearing down.
                }
            }
            for (var i = 0; i < backgrounds.Length; i++)
                if (backgrounds[i] is { } bg && !startedEntries.Contains(i))
                    await bg.DisposeAsync();
        }
    }
}
