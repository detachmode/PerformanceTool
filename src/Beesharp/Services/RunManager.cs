using System.Diagnostics;
using PerformanceTool.Services.Environments;
using PerformanceTool.Services.Storage;

namespace PerformanceTool.Services;

/// <summary>Compact bee summary returned by incremental <c>/api/runs/current</c> polling (no calls/logs embedded).</summary>
public sealed record LiveBeeDto(
    int Sequence,
    int Entry,
    double StartedAt,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    int SlowCalls);

public sealed record LiveScenarioViewDto(
    int Starts,
    int Finished,
    int InFlight,
    int Failed,
    int SlowBees,
    int SlowCalls,
    int Dropped);

public sealed record LiveRunStatusDto(
    string RunId,
    string State,
    double ElapsedSeconds,
    DateTimeOffset StartedUtc,
    LoadPlan Plan,
    IReadOnlyList<LiveScenarioViewDto> Scenarios,
    IReadOnlyList<LiveBeeDto> NewBees,
    int LastSequence,
    long TotalLogs,
    string StorageProvider,
    string? Error,
    string? PreflightState = null,
    string? PreflightScriptName = null,
    IReadOnlyList<string>? PreflightLogs = null,
    string? PreflightError = null);

/// <summary>
/// Hosts the currently active (or most recent) server-side <see cref="LoadRunner"/> run, persisting all bees,
/// calls and logs into <see cref="IRunStorage"/> (DuckDB by default) without blocking bees, and serving
/// lightweight incremental polls to the Blazor WebAssembly client (brain 0045).
/// </summary>
public sealed class RunManager(LoadRunner runner, EnvironmentStore environments, IRunStorage storage, PreflightStore? preflights = null)
{
    private const double SlowThresholdMs = 1500;
    private readonly Lock sync = new();
    private CancellationTokenSource? cts;
    private Task? runningTask;

    private string runId = "";
    private string state = "Idle";
    private DateTimeOffset startedUtc;
    private Stopwatch clock = new();
    private double finalElapsed;
    private LoadPlan activePlan = new();
    private int[] starts = [];
    private int[] finished = [];
    private int[] inFlight = [];
    private int[] failed = [];
    private int[] slowBees = [];
    private int[] slowCalls = [];
    private int[] dropped = [];
    private readonly List<LiveBeeDto> bees = [];
    private int lastSequence = -1;
    private long totalLogs;
    private string? runError;
    private string? preflightState;
    private string? preflightScriptName;
    private readonly List<string> preflightLogs = [];
    private string? preflightError;

    /// <summary>Restores the most recent persisted run from <see cref="IRunStorage"/> on server startup.</summary>
    public async Task RestoreLatestAsync(CancellationToken ct = default)
    {
        var runs = await storage.ListRunsAsync(20, ct);
        var best = runs.FirstOrDefault(r => r.TotalBees > 0) ?? runs.FirstOrDefault();
        if (best is not null)
            await LoadStoredRunAsync(best.RunId, ct);
    }

    /// <summary>Loads a persisted historical run from <see cref="IRunStorage"/> into the active view.</summary>
    public async Task<LiveRunStatusDto?> LoadStoredRunAsync(string targetRunId, CancellationToken ct = default)
    {
        lock (sync)
        {
            if (state == "Running")
                throw new InvalidOperationException("Cannot switch runs while a live run is in progress. Stop it first.");
        }

        var data = await storage.GetRunAsync(targetRunId, ct);
        if (data is null) return null;

        lock (sync)
        {
            if (state == "Running")
                throw new InvalidOperationException("Cannot switch runs while a live run is in progress. Stop it first.");

            var count = data.Plan.Scenarios.Count;
            runId = data.RunId;
            state = data.State == "Running" ? "Finished" : data.State;
            startedUtc = data.StartedUtc;
            clock = new Stopwatch();
            finalElapsed = data.ElapsedSeconds;
            activePlan = data.Plan.Clone();
            starts = new int[count];
            finished = new int[count];
            inFlight = new int[count];
            failed = new int[count];
            slowBees = new int[count];
            slowCalls = new int[count];
            dropped = new int[count];
            bees.Clear();
            lastSequence = -1;
            totalLogs = data.TotalLogs;
            runError = data.Error;
            preflightState = null;
            preflightScriptName = null;
            preflightLogs.Clear();
            preflightError = null;

            foreach (var a in data.Bees)
            {
                bees.Add(new LiveBeeDto(a.Sequence, a.Entry, a.StartedAt, a.DurationMs, a.StatusCode, a.Bytes, a.Error, a.SlowCalls));
                if (a.Sequence > lastSequence) lastSequence = a.Sequence;
                if (a.Entry >= 0 && a.Entry < count)
                {
                    starts[a.Entry]++;
                    finished[a.Entry]++;
                    if (a.Error is not null) failed[a.Entry]++;
                    if (a.SlowCalls > 0)
                    {
                        slowBees[a.Entry]++;
                        slowCalls[a.Entry] += a.SlowCalls;
                    }
                }
            }
        }

        return GetCurrent();
    }

    /// <summary>
    /// Returns current run status plus only compact bees with <c>Sequence &gt; afterSeq</c> (up to <paramref name="maxBees"/>).
    /// Full calls and logs live in <see cref="IRunStorage"/> and are fetched lazily on demand.
    /// </summary>
    public LiveRunStatusDto GetCurrent(int afterSeq = -1, int maxBees = 2000)
    {
        maxBees = Math.Clamp(maxBees, 1, 10_000);
        lock (sync)
        {
            var elapsed = state == "Running" ? clock.Elapsed.TotalSeconds : finalElapsed;
            var views = new LiveScenarioViewDto[activePlan.Scenarios.Count];
            for (var i = 0; i < views.Length; i++)
                views[i] = new LiveScenarioViewDto(
                    i < starts.Length ? starts[i] : 0,
                    i < finished.Length ? finished[i] : 0,
                    i < inFlight.Length ? inFlight[i] : 0,
                    i < failed.Length ? failed[i] : 0,
                    i < slowBees.Length ? slowBees[i] : 0,
                    i < slowCalls.Length ? slowCalls[i] : 0,
                    i < dropped.Length ? dropped[i] : 0);

            var delta = new List<LiveBeeDto>();
            if (bees.Count > 0)
            {
                // Bees arrive mostly in sequence order; scan from the end or filter cleanly.
                for (var i = 0; i < bees.Count && delta.Count < maxBees; i++)
                {
                    if (bees[i].Sequence > afterSeq) delta.Add(bees[i]);
                }
            }

            return new LiveRunStatusDto(
                runId,
                state,
                elapsed,
                startedUtc,
                activePlan.Clone(),
                views,
                delta,
                lastSequence,
                totalLogs,
                storage.ProviderName,
                runError,
                preflightState,
                preflightScriptName,
                preflightLogs.Count > 0 ? preflightLogs.ToArray() : null,
                preflightError);
        }
    }

    public async Task<LiveRunStatusDto> StartAsync(LoadPlan plan, string? environmentName, CancellationToken ct = default)
    {
        EnvironmentDefinition? env = null;
        if (!string.IsNullOrWhiteSpace(environmentName))
            env = await environments.GetAsync(environmentName, ct);

        var preflightId = string.IsNullOrWhiteSpace(plan.PreflightScriptId) ? null : plan.PreflightScriptId.Trim();
        PreflightScriptSummary? preflightMeta = null;
        if (preflightId is not null)
        {
            preflightMeta = preflights?.Get(preflightId)
                ?? throw new ArgumentException($"Preflight script '{preflightId}' was not found.");
        }

        CancellationTokenSource runCts;
        LoadPlan cloned;
        string currentRunId;
        DateTimeOffset currentStartedUtc;
        lock (sync)
        {
            if (state is "Running" or "Preflight")
                throw new InvalidOperationException("A run is already in progress. Stop it before starting a new one.");
            if (plan.Scenarios.Count == 0)
                throw new ArgumentException("The load plan has no scenarios.");

            cloned = plan.Clone();
            currentRunId = $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(100, 999)}";
            currentStartedUtc = DateTimeOffset.UtcNow;
            runId = currentRunId;
            state = preflightMeta is not null ? "Preflight" : "Running";
            startedUtc = currentStartedUtc;
            clock = preflightMeta is not null ? new Stopwatch() : Stopwatch.StartNew();
            finalElapsed = 0;
            activePlan = cloned;
            starts = new int[cloned.Scenarios.Count];
            finished = new int[cloned.Scenarios.Count];
            inFlight = new int[cloned.Scenarios.Count];
            failed = new int[cloned.Scenarios.Count];
            slowBees = new int[cloned.Scenarios.Count];
            slowCalls = new int[cloned.Scenarios.Count];
            dropped = new int[cloned.Scenarios.Count];
            bees.Clear();
            lastSequence = -1;
            totalLogs = 0;
            runError = null;
            preflightState = preflightMeta is not null ? "Running" : null;
            preflightScriptName = preflightMeta?.FileName;
            preflightLogs.Clear();
            preflightError = null;
            runCts = new CancellationTokenSource();
            cts = runCts;
        }

        runningTask = Task.Run(async () =>
        {
            string? err = null;
            var runPersisted = false;
            try
            {
                if (preflightMeta is not null && preflights is not null)
                {
                    var preRes = await preflights.ExecuteAsync(
                        preflightMeta.ScriptId,
                        environmentName,
                        onLog: line => { lock (sync) preflightLogs.Add(line); },
                        ct: runCts.Token);

                    if (!preRes.Success)
                    {
                        err = preRes.Error ?? "Preflight failed.";
                        lock (sync)
                        {
                            preflightState = "Failed";
                            preflightError = err;
                        }
                        return;
                    }

                    lock (sync)
                    {
                        preflightState = "Succeeded";
                        state = "Running";
                        clock.Restart();
                    }
                }

                storage.EnqueueRunStarted(currentRunId, DateTimeOffset.UtcNow, cloned);
                runPersisted = true;

                await runner.RunAsync(
                    cloned,
                    onResult: res =>
                    {
                        var beeSlowCalls = CountSlowCalls(res);
                        var outcome = res.Error is not null ? 2 : beeSlowCalls > 0 ? 1 : 0;
                        var compact = new LiveBeeDto(
                            res.Sequence,
                            res.Entry,
                            res.StartedAtSeconds,
                            res.ElapsedMs,
                            res.StatusCode,
                            res.Bytes,
                            res.Error,
                            beeSlowCalls);

                        lock (sync)
                        {
                            if (res.Entry >= 0 && res.Entry < finished.Length)
                            {
                                finished[res.Entry]++;
                                if (res.Error is not null) failed[res.Entry]++;
                                if (beeSlowCalls > 0)
                                {
                                    slowBees[res.Entry]++;
                                    slowCalls[res.Entry] += beeSlowCalls;
                                }
                            }
                            if (res.Sequence > lastSequence) lastSequence = res.Sequence;
                            totalLogs += res.Logs?.Count ?? 0;
                            bees.Add(compact);
                        }

                        storage.EnqueueBeeCompleted(currentRunId, res, outcome, beeSlowCalls);
                        return ValueTask.CompletedTask;
                    },
                    onDropped: entry =>
                    {
                        lock (sync)
                            if (entry >= 0 && entry < dropped.Length) dropped[entry]++;
                        return ValueTask.CompletedTask;
                    },
                    ct: runCts.Token,
                    onActivity: act =>
                    {
                        lock (sync)
                        {
                            if (act.Entry >= 0 && act.Entry < inFlight.Length)
                            {
                                inFlight[act.Entry] = act.InFlight;
                                if (act.Started) starts[act.Entry]++;
                            }
                        }
                        return ValueTask.CompletedTask;
                    },
                    environment: env);
            }
            catch (OperationCanceledException)
            {
                // User stopped the run.
            }
            catch (Exception ex)
            {
                err = ex.InnerException?.Message ?? ex.Message;
            }
            finally
            {
                double elapsed;
                lock (sync)
                {
                    elapsed = clock.Elapsed.TotalSeconds;
                    finalElapsed = elapsed;
                    clock.Stop();
                    for (var i = 0; i < inFlight.Length; i++) inFlight[i] = 0;
                    runError = err;
                    state = "Finished";
                }
                if (runPersisted)
                    storage.EnqueueRunFinished(currentRunId, DateTimeOffset.UtcNow, "Finished", elapsed, err);
                runCts.Dispose();
            }
        }, CancellationToken.None);

        return GetCurrent();
    }

    public async Task<LiveRunStatusDto> StopAsync()
    {
        CancellationTokenSource? toCancel;
        Task? toAwait;
        lock (sync)
        {
            toCancel = cts;
            toAwait = runningTask;
        }
        if (toCancel is not null)
        {
            try { await toCancel.CancelAsync(); } catch (ObjectDisposedException) { }
        }
        if (toAwait is not null)
        {
            try { await toAwait.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        await storage.FlushAsync();
        return GetCurrent();
    }

    private static int CountSlowCalls(ExecutionResult res)
    {
        if (res.Calls is { Count: > 0 })
            return res.Calls.Count(c => c.Error is null && c.DurationMs >= SlowThresholdMs);
        return res.Error is null && res.ElapsedMs >= SlowThresholdMs ? 1 : 0;
    }
}
