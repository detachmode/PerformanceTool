using System.Diagnostics;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Services;

/// <summary>One scenario execution. <see cref="Entry"/> is the index into <see cref="LoadPlan.Scenarios"/>.</summary>
public sealed record ExecutionResult(int Sequence, int Entry, double StartedAtSeconds, double ElapsedMs, int? StatusCode, long Bytes, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// Start or end of one execution, with the number of executions in flight afterwards (docs/brain/0008).
/// Every <see cref="Started"/> is followed by exactly one end event, whether the execution reported a result or was
/// cancelled because the user stopped the run. The end event is raised after <c>onResult</c>.
/// </summary>
public sealed record ExecutionActivity(int Sequence, int Entry, double AtSeconds, bool Started, int InFlight, int TotalInFlight);

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
        Func<ExecutionActivity, ValueTask>? onActivity = null)
    {
        var scenarios = plan.Scenarios.Select(e => catalog.Find(e.ScenarioId)
            ?? throw new InvalidOperationException($"Unknown scenario '{e.ScenarioId}'.")).ToArray();

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
        var sequence = -1;
        var clock = Stopwatch.StartNew();
        var duration = TimeSpan.FromSeconds(plan.DurationSeconds);
        var timeout = TimeSpan.FromSeconds(plan.TimeoutSeconds);

        async Task ScheduleEntry(int entry)
        {
            var config = plan.Scenarios[entry];
            // Bind once per run: typed parameters, ${env:NAME} secrets resolved. Shared read-only by all executions.
            var parameters = scenarios[entry].Spec.Bind(config.Parameters);
            scenarios[entry].Spec.ResolveSecrets(parameters);
            // Due times are computed per run in ticks (not n × a rounded interval), so the run count is exact:
            // 180/min for 8 s is 24 runs, not 25. See docs/brain/0004.
            var ticksPerRun = TimeSpan.TicksPerMinute / config.RatePerMinute;

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

                if (!gate.Wait(0))
                {
                    await onDropped(entry);
                    continue;
                }

                var seq = Interlocked.Increment(ref sequence);
                // Counted before the task is queued; the callback runs inside the task so a slow observer never delays scheduling.
                var nowInFlight = Interlocked.Increment(ref inFlightByEntry[entry]);
                var total = Interlocked.Increment(ref inFlightTotal);
                var task = Task.Run(async () =>
                {
                    try
                    {
                        if (onActivity is not null)
                            await onActivity(new ExecutionActivity(seq, entry, clock.Elapsed.TotalSeconds, true, nowInFlight, total));
                        await ExecuteOne(scenarios[entry], entry, seq, parameters);
                    }
                    finally
                    {
                        gate.Release();
                        var left = Interlocked.Decrement(ref inFlightByEntry[entry]);
                        var totalLeft = Interlocked.Decrement(ref inFlightTotal);
                        if (onActivity is not null)
                            await onActivity(new ExecutionActivity(seq, entry, clock.Elapsed.TotalSeconds, false, left, totalLeft));
                    }
                }, CancellationToken.None);
                lock (inFlight) inFlight.Add(task);
            }
        }

        async Task ExecuteOne(IScenario scenario, int entry, int seq, object parameters)
        {
            using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            execCts.CancelAfter(timeout);
            var startedAt = clock.Elapsed.TotalSeconds;
            var sw = Stopwatch.StartNew();
            ScenarioOutcome outcome;
            try
            {
                outcome = await scenario.ExecuteAsync(parameters, new ScenarioContext(http), execCts.Token);
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
}
