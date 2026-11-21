using System.Diagnostics;
using PerformanceTool.Services.Environments;
using PerformanceTool.Services.Storage;

namespace PerformanceTool.Services;

/// <summary>Compact actor summary returned by incremental <c>/api/runs/current</c> polling (no calls/logs embedded).</summary>
public sealed record LiveActorDto(
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
    int SlowActors,
    int SlowCalls,
    int Dropped);

public sealed record LiveRunStatusDto(
    string RunId,
    string State,
    double ElapsedSeconds,
    DateTimeOffset StartedUtc,
    LoadPlan Plan,
    IReadOnlyList<LiveScenarioViewDto> Scenarios,
    IReadOnlyList<LiveActorDto> NewActors,
    int LastSequence,
    long TotalLogs,
    string StorageProvider,
    string? Error);

/// <summary>
/// Hosts the currently active (or most recent) server-side <see cref="LoadRunner"/> run, persisting all actors,
/// calls and logs into <see cref="IRunStorage"/> (SQLite WAL by default) without blocking actors, and serving
/// lightweight incremental polls to the Blazor WebAssembly client (brain 0045).
/// </summary>
public sealed class RunManager(LoadRunner runner, EnvironmentStore environments, IRunStorage storage)
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
    private int[] slowActors = [];
    private int[] slowCalls = [];
    private int[] dropped = [];
    private readonly List<LiveActorDto> actors = [];
    private int lastSequence = -1;
    private long totalLogs;
    private string? runError;

    /// <summary>Restores the most recent persisted run from <see cref="IRunStorage"/> on server startup.</summary>
    public async Task RestoreLatestAsync(CancellationToken ct = default)
    {
        var runs = await storage.ListRunsAsync(20, ct);
        var best = runs.FirstOrDefault(r => r.TotalActors > 0) ?? runs.FirstOrDefault();
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
            slowActors = new int[count];
            slowCalls = new int[count];
            dropped = new int[count];
            actors.Clear();
            lastSequence = -1;
            totalLogs = data.TotalLogs;
            runError = data.Error;

            foreach (var a in data.Actors)
            {
                actors.Add(new LiveActorDto(a.Sequence, a.Entry, a.StartedAt, a.DurationMs, a.StatusCode, a.Bytes, a.Error, a.SlowCalls));
                if (a.Sequence > lastSequence) lastSequence = a.Sequence;
                if (a.Entry >= 0 && a.Entry < count)
                {
                    starts[a.Entry]++;
                    finished[a.Entry]++;
                    if (a.Error is not null) failed[a.Entry]++;
                    if (a.SlowCalls > 0)
                    {
                        slowActors[a.Entry]++;
                        slowCalls[a.Entry] += a.SlowCalls;
                    }
                }
            }
        }

        return GetCurrent();
    }

    /// <summary>
    /// Returns current run status plus only compact actors with <c>Sequence &gt; afterSeq</c> (up to <paramref name="maxActors"/>).
    /// Full calls and logs live in <see cref="IRunStorage"/> and are fetched lazily on demand.
    /// </summary>
    public LiveRunStatusDto GetCurrent(int afterSeq = -1, int maxActors = 2000)
    {
        maxActors = Math.Clamp(maxActors, 1, 10_000);
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
                    i < slowActors.Length ? slowActors[i] : 0,
                    i < slowCalls.Length ? slowCalls[i] : 0,
                    i < dropped.Length ? dropped[i] : 0);

            var delta = new List<LiveActorDto>();
            if (actors.Count > 0)
            {
                // Actors arrive mostly in sequence order; scan from the end or filter cleanly.
                for (var i = 0; i < actors.Count && delta.Count < maxActors; i++)
                {
                    if (actors[i].Sequence > afterSeq) delta.Add(actors[i]);
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
                runError);
        }
    }

    public async Task<LiveRunStatusDto> StartAsync(LoadPlan plan, string? environmentName, CancellationToken ct = default)
    {
        EnvironmentDefinition? env = null;
        if (!string.IsNullOrWhiteSpace(environmentName))
            env = await environments.GetAsync(environmentName, ct);

        CancellationTokenSource runCts;
        LoadPlan cloned;
        string currentRunId;
        DateTimeOffset currentStartedUtc;
        lock (sync)
        {
            if (state == "Running")
                throw new InvalidOperationException("A run is already in progress. Stop it before starting a new one.");
            if (plan.Scenarios.Count == 0)
                throw new ArgumentException("The load plan has no scenarios.");

            cloned = plan.Clone();
            currentRunId = $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(100, 999)}";
            currentStartedUtc = DateTimeOffset.UtcNow;
            runId = currentRunId;
            state = "Running";
            startedUtc = currentStartedUtc;
            clock = Stopwatch.StartNew();
            finalElapsed = 0;
            activePlan = cloned;
            starts = new int[cloned.Scenarios.Count];
            finished = new int[cloned.Scenarios.Count];
            inFlight = new int[cloned.Scenarios.Count];
            failed = new int[cloned.Scenarios.Count];
            slowActors = new int[cloned.Scenarios.Count];
            slowCalls = new int[cloned.Scenarios.Count];
            dropped = new int[cloned.Scenarios.Count];
            actors.Clear();
            lastSequence = -1;
            totalLogs = 0;
            runError = null;
            runCts = new CancellationTokenSource();
            cts = runCts;
        }

        storage.EnqueueRunStarted(currentRunId, currentStartedUtc, cloned);

        runningTask = Task.Run(async () =>
        {
            string? err = null;
            try
            {
                await runner.RunAsync(
                    cloned,
                    onResult: res =>
                    {
                        var actorSlowCalls = CountSlowCalls(res);
                        var outcome = res.Error is not null ? 2 : actorSlowCalls > 0 ? 1 : 0;
                        var compact = new LiveActorDto(
                            res.Sequence,
                            res.Entry,
                            res.StartedAtSeconds,
                            res.ElapsedMs,
                            res.StatusCode,
                            res.Bytes,
                            res.Error,
                            actorSlowCalls);

                        lock (sync)
                        {
                            if (res.Entry >= 0 && res.Entry < finished.Length)
                            {
                                finished[res.Entry]++;
                                if (res.Error is not null) failed[res.Entry]++;
                                if (actorSlowCalls > 0)
                                {
                                    slowActors[res.Entry]++;
                                    slowCalls[res.Entry] += actorSlowCalls;
                                }
                            }
                            if (res.Sequence > lastSequence) lastSequence = res.Sequence;
                            totalLogs += res.Logs?.Count ?? 0;
                            actors.Add(compact);
                        }

                        storage.EnqueueActorCompleted(currentRunId, res, outcome, actorSlowCalls);
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
