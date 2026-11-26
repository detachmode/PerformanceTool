using System.Collections.Concurrent;
using System.Diagnostics;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Tracing;

namespace Beesharp.Services;

/// <summary>
/// Runs a <see cref="LoadPlan"/> as an open workload: every entry fires at its own fixed rate, independent of
/// how long earlier executions take. If <see cref="LoadPlan.MaxConcurrency"/> executions are already in flight,
/// the run is dropped and reported instead of queued. See docs/brain/0004-open-model-rate-scheduling.md.
/// </summary>
/// <param name="catalog">The catalog used to resolve scenario definitions referenced by <see cref="LoadPlan.Scenarios"/>.</param>
public sealed class LoadRunner(ScenarioCatalog catalog)
{
    /// <summary>Gets the scenario catalog used by this runner.</summary>
    public ScenarioCatalog Catalog => catalog;

    /// <summary>
    /// Executes the specified <paramref name="plan"/> as an open-model load test, scheduling each scenario entry on its configured cadence
    /// until the duration elapses or <paramref name="ct"/> is canceled, then waiting for in-flight executions and running teardown hooks.
    /// </summary>
    /// <param name="plan">The load plan specifying duration, concurrency limits, timeouts, and scenario entries.</param>
    /// <param name="onResult">Callback invoked for each completed, timed-out, failed, or canceled bee execution.</param>
    /// <param name="onDropped">Callback invoked with the zero-based scenario entry index whenever a scheduled bee is dropped due to concurrency limits.</param>
    /// <param name="ct">A token used to stop scheduling and cancel in-flight bee executions.</param>
    /// <param name="onActivity">Optional callback invoked whenever a bee starts or finishes, reporting updated in-flight counts.</param>
    /// <param name="environment">Optional environment definition used to resolve <c>{{Key}}</c> placeholders in scenario parameters.</param>
    /// <param name="onLiveLog">Optional sink receiving structured log events emitted by startup hooks and executing bees in real time.</param>
    /// <param name="data">Optional runtime data container holding the shared run bag and per-entry bags; a new instance is created when null.</param>
    /// <param name="onBeeContext">Optional callback invoked immediately before a bee executes, providing its sequence number, entry index, and <see cref="ScenarioContext"/>.</param>
    /// <param name="clock">Optional run clock supporting pause and resume; a new clock is created when null.</param>
    /// <param name="runId">Optional run identifier, put on every actor's root span as <c>beesharp.run.id</c> (brain 0063).</param>
    /// <returns>A task that completes when all scheduled entries and in-flight bees have finished and teardown hooks have run.</returns>
    public async Task RunAsync(
        LoadPlan plan,
        Func<ExecutionResult, ValueTask> onResult,
        Func<int, ValueTask> onDropped,
        CancellationToken ct,
        Func<ExecutionActivity, ValueTask>? onActivity = null,
        EnvironmentDefinition? environment = null,
        Action<LiveLogEvent>? onLiveLog = null,
        RunData? data = null,
        Action<int, int, ScenarioContext>? onBeeContext = null,
        RunClock? clock = null,
        string? runId = null)
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
        // Record HTTP calls automatically against the active bee's ScenarioContext (brain 0013, 0056).
        using var recordingHandler = new RecordingHttpHandler(handler);
        // Per-execution timeouts are enforced with a CancellationToken so multi-step scenarios are bounded as a whole.
        using var http = new HttpClient(recordingHandler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Beesharp/1.0");

        // Runtime data (brain 0052): the run bag and one entry bag per entry. A caller that wants to read them (RunManager) passes its own.
        // The run time (brain 0027): a caller that pauses and resumes the run (RunManager) passes its own clock.
        clock ??= new RunClock();
        var runClock = clock;
        data ??= new RunData(plan.Scenarios.Count, () => runClock.Elapsed.TotalSeconds);
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

            clock.Start();

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
                    // Waits through a pause: a paused run starts no new bees (brain 0027).
                    try { await clock.WaitUntilAsync(due, ct); }
                    catch (OperationCanceledException) { return; }

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

            async Task ExecuteOne(IScenario scenario, int entry, int seq, object parameters, RunClock clockRef)
            {
                using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                execCts.CancelAfter(timeout);
                var startedAt = clockRef.Elapsed.TotalSeconds;
                var calls = new List<RecordedCall>();
                var logs = new List<RecordedLog>();
                var stepVisits = new List<RecordedStepVisit>();
                // One actor = one trace (brain 0063): the actor's root span, started here so every call and step of the actor hangs below it.
                var entryConfig = plan.Scenarios[entry];
                using var trace = ActorTrace.Start(new ActorTraceInfo(
                    string.IsNullOrWhiteSpace(entryConfig.Label) ? scenario.Name : entryConfig.Label,
                    runId,
                    entryConfig.ScenarioId,
                    entry + 1,
                    seq + 1,
                    scenario.Steps));
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
                    runStopped: () => ct.IsCancellationRequested,
                    stepVisitSink: v => { lock (stepVisits) stepVisits.Add(v); },
                    thresholds: scenario.SlowThresholds,
                    captureHeaders: plan.CaptureHeaders,
                    measureHttpTimingSplit: plan.MeasureHttpTimingSplit,
                    trace: trace);
                onBeeContext?.Invoke(seq, entry, ctx);
                var sw = Stopwatch.StartNew();
                ScenarioOutcome outcome;
                var canceled = false;
                try
                {
                    using (ctx.Activate())
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
                    var errLog = new RecordedLog(clockRef.Elapsed.TotalSeconds, "Error", $"Unhandled exception: {msg}", formatted, Step: Math.Max(0, ctx.CurrentStep), Rep: ctx.CurrentRep);
                    lock (logs) logs.Add(errLog);
                    onLiveLog?.Invoke(new LiveLogEvent(seq, entry, errLog));
                    outcome = ScenarioOutcome.Failed(msg);
                }
                sw.Stop();

                var hasSteps = scenario.Steps.Count > 0;
                var failedStep = !outcome.Success && hasSteps ? (ctx.CurrentStep >= 0 ? ctx.CurrentStep : 0) : -1;
                var failedRep = !outcome.Success && hasSteps ? (ctx.CurrentStep >= 0 ? ctx.CurrentRep : 0) : -1;

                var elapsed = outcome.Measured ?? sw.Elapsed;
                // The actor's span ends with the actor, not after the result was stored.
                trace?.Complete(outcome.Success, outcome.FailureReason, canceled);
                trace?.Dispose();
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
                    canceled,
                    failedStep,
                    failedRep,
                    stepVisits.Count > 0 ? stepVisits : null,
                    trace?.TraceId));
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
