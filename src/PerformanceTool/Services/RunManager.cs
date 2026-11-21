using System.Diagnostics;
using PerformanceTool.Services.Environments;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Services;

public sealed record LiveActorDto(
    int Sequence,
    int Entry,
    double StartedAt,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    IReadOnlyList<RecordedCall>? Calls = null,
    IReadOnlyList<RecordedLog>? Logs = null);

public sealed record LiveScenarioViewDto(
    int Starts,
    int InFlight,
    int Failed,
    int Dropped);

public sealed record LiveRunStatusDto(
    string RunId,
    string State,
    double ElapsedSeconds,
    DateTimeOffset StartedUtc,
    LoadPlan Plan,
    IReadOnlyList<LiveScenarioViewDto> Scenarios,
    IReadOnlyList<LiveActorDto> RecentActors,
    string? Error);

/// <summary>
/// Hosts the currently active (or most recent) server-side <see cref="LoadRunner"/> run so the Blazor WebAssembly
/// client in Live mode can start, stop and poll real runs (including custom C# script scenarios).
/// </summary>
public sealed class RunManager(LoadRunner runner, EnvironmentStore environments)
{
    private const int MaxStoredActors = 4000;
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
    private int[] inFlight = [];
    private int[] failed = [];
    private int[] dropped = [];
    private readonly List<LiveActorDto> actors = [];
    private string? runError;

    public LiveRunStatusDto GetCurrent()
    {
        lock (sync)
        {
            var elapsed = state == "Running" ? clock.Elapsed.TotalSeconds : finalElapsed;
            var views = new LiveScenarioViewDto[activePlan.Scenarios.Count];
            for (var i = 0; i < views.Length; i++)
                views[i] = new LiveScenarioViewDto(
                    i < starts.Length ? starts[i] : 0,
                    i < inFlight.Length ? inFlight[i] : 0,
                    i < failed.Length ? failed[i] : 0,
                    i < dropped.Length ? dropped[i] : 0);

            return new LiveRunStatusDto(
                runId,
                state,
                elapsed,
                startedUtc,
                activePlan.Clone(),
                views,
                [.. actors],
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
        lock (sync)
        {
            if (state == "Running")
                throw new InvalidOperationException("A run is already in progress. Stop it before starting a new one.");
            if (plan.Scenarios.Count == 0)
                throw new ArgumentException("The load plan has no scenarios.");

            cloned = plan.Clone();
            runId = $"run-{DateTimeOffset.UtcNow:HHmmss}-{Random.Shared.Next(100, 999)}";
            state = "Running";
            startedUtc = DateTimeOffset.UtcNow;
            clock = Stopwatch.StartNew();
            finalElapsed = 0;
            activePlan = cloned;
            starts = new int[cloned.Scenarios.Count];
            inFlight = new int[cloned.Scenarios.Count];
            failed = new int[cloned.Scenarios.Count];
            dropped = new int[cloned.Scenarios.Count];
            actors.Clear();
            runError = null;
            runCts = new CancellationTokenSource();
            cts = runCts;
        }

        runningTask = Task.Run(async () =>
        {
            try
            {
                await runner.RunAsync(
                    cloned,
                    onResult: res =>
                    {
                        lock (sync)
                        {
                            if (res.Error is not null && res.Entry >= 0 && res.Entry < failed.Length)
                                failed[res.Entry]++;
                            if (actors.Count >= MaxStoredActors)
                                actors.RemoveAt(0);
                            actors.Add(new LiveActorDto(
                                res.Sequence,
                                res.Entry,
                                res.StartedAtSeconds,
                                res.ElapsedMs,
                                res.StatusCode,
                                res.Bytes,
                                res.Error,
                                res.Calls,
                                res.Logs));
                        }
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

                lock (sync)
                {
                    finalElapsed = clock.Elapsed.TotalSeconds;
                    clock.Stop();
                    for (var i = 0; i < inFlight.Length; i++) inFlight[i] = 0;
                    state = "Finished";
                }
            }
            catch (OperationCanceledException)
            {
                lock (sync)
                {
                    finalElapsed = clock.Elapsed.TotalSeconds;
                    clock.Stop();
                    for (var i = 0; i < inFlight.Length; i++) inFlight[i] = 0;
                    state = "Finished";
                }
            }
            catch (Exception ex)
            {
                lock (sync)
                {
                    finalElapsed = clock.Elapsed.TotalSeconds;
                    clock.Stop();
                    for (var i = 0; i < inFlight.Length; i++) inFlight[i] = 0;
                    runError = ex.InnerException?.Message ?? ex.Message;
                    state = "Finished";
                }
            }
            finally
            {
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
        return GetCurrent();
    }
}
