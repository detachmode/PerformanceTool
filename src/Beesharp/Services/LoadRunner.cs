using System.Collections.Concurrent;
using System.Diagnostics;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios;

namespace Beesharp.Services;

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
        EnvironmentDefinition? environment = null,
        Action<LiveLogEvent>? onLiveLog = null,
        RunData? data = null)
    {
        var scenarios = plan.Scenarios.Select(e => catalog.Find(e.ScenarioId)
            ?? throw new InvalidOperationException($"Unknown scenario '{e.ScenarioId}'.")).ToArray();

        // Environment (brain 0033): fill {{Key}} placeholders once, up front. A key the environment doesn't have would only make every bee
        // fail with a confusing URL error, so the run refuses to start and names the keys instead.
        var env = environment?.ToValues() ?? EnvironmentValues.None;
        var unknown = new List<string>();
        var resolved = plan.Scenarios.Select(e => Placeholders.ResolveJson(e.Parameters, env, unknown)).ToArray();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"{(env.Name.Length == 0 ? "No environment is active" : $"Environment '{env.Name}' has no value")} for: {string.Join(", ", unknown.Select(k => "{{" + k + "}}"))}.");

        var entryLimits = plan.Scenarios.Select(e => Math.Max(1, e.MaxInFlight ?? plan.MaxConcurrency)).ToArray();
        var maxConnections = Math.Clamp(Math.Max(plan.MaxConcurrency, entryLimits.Sum()), 1, 2000);
        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = maxConnections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        // Per-execution timeouts are enforced with a CancellationToken so multi-step scenarios are bounded as a whole.
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Beesharp/1.0");

        // Runtime data (brain 0052): the run bag and one entry bag per entry. A caller that wants to read them (RunManager) passes its own.
        Stopwatch? runClock = null;
        data ??= new RunData(plan.Scenarios.Count, () => runClock?.Elapsed.TotalSeconds ?? 0);
        var entryGates = entryLimits.Select(limit => new SemaphoreSlim(limit, limit)).ToArray();
        var inFlight = new List<Task>();
        var inFlightByEntry = new int[plan.Scenarios.Count]; // executions running right now, per entry (brain 0008)
        var inFlightTotal = 0;
        // Counter changes and their activity events happen under one async lock, so observers receive events in the
        // same order the counters changed. Without it, concurrent ends could arrive with stale in-flight counts.
        using var activityLock = new SemaphoreSlim(1, 1);
        var sequence = -1;
        var duration = TimeSpan.FromSeconds(plan.DurationSeconds);
        var timeout = TimeSpan.FromSeconds(plan.TimeoutSeconds);

        // Bind parameters and run StartupAsync lifecycle hooks once per scenario entry before any bees start (brain 0040).
        var boundParams = new object[plan.Scenarios.Count];
                var backgrounds = new ScenarioBackgroundHost[plan.Scenarios.Count];
        var startedEntries = new List<int>(plan.Scenarios.Count);

        try
        {
            for (var i = 0; i < plan.Scenarios.Count; i++)
            {
                boundParams[i] = scenarios[i].Spec.Bind(resolved[i]);
                scenarios[i].Spec.ResolveSecrets(boundParams[i]);
                backgrounds[i] = new ScenarioBackgroundHost(ct);
                var entryIdx = i;
                var startupCtx = new ScenarioContext(
                    http,
                    env,
                    ct,
                    data.EntryBagFor(i, new DataWriter(DataWriterKind.Startup, i)),
                    backgrounds[i],
                    structuredLogSink: onLiveLog is null ? null : l => onLiveLog(new LiveLogEvent(-1, entryIdx, l)),
                    runBag: data.RunBagFor(new DataWriter(DataWriterKind.Startup, i)));
                await scenarios[i].StartupAsync(boundParams[i], startupCtx, ct);
                startedEntries.Add(i);
            }

            var clock = Stopwatch.StartNew();
            runClock = clock;

            async Task ScheduleEntry(int entry)
            {
                var config = plan.Scenarios[entry];
                var parameters = boundParams[entry];
                // Due times are computed per run in ticks (not n × a rounded interval), so the run count is exact:
                // 180/min for 8 s is 24 runs, not 25. See docs/brain/0004.
                // Bursts (brain 0034): one tick starts BeesPerBurst bees together; linear is a "burst" of one.
                var bursts = config.Pattern == SpawningPattern.Bursts;
                var perTick = bursts ? Math.Max(1, config.BeesPerBurst) : 1;
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

                    var gate = entryGates[entry];
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
                                await ExecuteOne(scenarios[entry], entry, seq, parameters, clock);
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

            async Task ExecuteOne(IScenario scenario, int entry, int seq, object parameters, Stopwatch clockRef)
            {
                using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                execCts.CancelAfter(timeout);
                var startedAt = clockRef.Elapsed.TotalSeconds;
                var calls = new List<RecordedCall>();
                var logs = new List<RecordedLog>();
                var ctx = new ScenarioContext(
                    http,
                    env,
                    execCts.Token,
                    data.EntryBagFor(entry, new DataWriter(DataWriterKind.Bee, entry, seq)),
                    callSink: c => { lock (calls) calls.Add(c); },
                    structuredLogSink: l =>
                    {
                        lock (logs) logs.Add(l);
                        onLiveLog?.Invoke(new LiveLogEvent(seq, entry, l));
                    },
                    runSeconds: () => clockRef.Elapsed.TotalSeconds,
                    runBag: data.RunBagFor(new DataWriter(DataWriterKind.Bee, entry, seq)),
                    runStopped: () => ct.IsCancellationRequested);
                var sw = Stopwatch.StartNew();
                ScenarioOutcome outcome;
                var canceled = false;
                try
                {
                    outcome = await scenario.ExecuteAsync(parameters, ctx, execCts.Token);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // The user stopped the run (brain 0053): keep the bee as canceled with the logs and calls it already has. Any exception
                    // thrown because of the stop (OperationCanceledException, a wrapped TaskCanceledException, ...) ends here, not as an ordinary failure.
                    outcome = ScenarioOutcome.Failed(ExecutionResult.CanceledError);
                    canceled = true;
                }
                catch (OperationCanceledException)
                {
                    outcome = ScenarioOutcome.Failed($"Timeout after {timeout.TotalSeconds:0} s");
                }
                catch (Exception ex)
                {
                    var msg = ex.InnerException?.Message ?? ex.Message;
                    var formatted = ScenarioExceptionFormatter.Format(ex, scenario.GetType().Assembly);
                    var errLog = new RecordedLog(clockRef.Elapsed.TotalSeconds, "Error", $"Unhandled exception: {msg}", formatted);
                    lock (logs) logs.Add(errLog);
                    onLiveLog?.Invoke(new LiveLogEvent(seq, entry, errLog));
                    outcome = ScenarioOutcome.Failed(msg);
                }
                sw.Stop();

                var elapsed = outcome.Measured ?? sw.Elapsed;
                await onResult(new ExecutionResult(
                    seq,
                    entry,
                    startedAt,
                    elapsed.TotalMilliseconds,
                    outcome.StatusCode,
                    outcome.Bytes,
                    outcome.FailureReason,
                    calls.Count > 0 ? calls : null,
                    logs.Count > 0 ? logs : null,
                    canceled));
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
                    var teardownCtx = new ScenarioContext(http, env, CancellationToken.None,
                        data.EntryBagFor(idx, new DataWriter(DataWriterKind.Teardown, idx)),
                        runBag: data.RunBagFor(new DataWriter(DataWriterKind.Teardown, idx)));
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
            foreach (var g in entryGates) g.Dispose();
        }
    }
}
