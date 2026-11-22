using System.Diagnostics;
using System.Text.Json;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;
using Beesharp.Services.Storage;

namespace Beesharp.Services;

/// <summary>
/// Hosts the currently active (or most recent) server-side <see cref="LoadRunner"/> run, persisting all bees,
/// calls and logs into <see cref="IRunStorage"/> (DuckDB by default) without blocking bees, and serving
/// lightweight incremental polls to the Blazor WebAssembly client (brain 0045).
/// </summary>
public sealed class RunManager(LoadRunner runner, EnvironmentStore environments, IRunStorage storage, PreflightStore? preflights = null, ShutdownStore? shutdowns = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const int ShutdownTimeoutSeconds = 120;
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
    private readonly Dictionary<int, LiveRunningBeeDto> activeBees = [];
    private readonly Queue<LiveLogEntryDto> liveLogs = new();
    private long nextLiveLogId;
    private const int MaxLiveLogs = 5000;
    private int lastSequence = -1;
    private long totalLogs;
    private string? runError;
    private string? preflightState;
    private string? preflightScriptName;
    private readonly List<string> preflightLogs = [];
    private string? preflightError;
    // Shutdown program (brain 0052): "Pending" until the run ends, then "Running", "Succeeded" or "Failed"; null when the plan has none.
    private string? shutdownState;
    private string? shutdownScriptName;
    private readonly List<string> shutdownLogs = [];
    private string? shutdownError;
    // Runtime data (brain 0052): live bags of the current run, or the final values of a stored run that was loaded.
    private RunData? runData;
    private RuntimeDataSnapshot? storedRuntime;

    private bool IsActive => state is "Running" or "Preflight" or "Shutdown";

    /// <summary>
    /// The runtime data (run bag and entry bags) of the active run: live while it runs, the final values afterwards. Null when there is none (e.g. an old stored run).
    /// </summary>
    public RuntimeDataSnapshot? GetCurrentRuntime()
    {
        lock (sync) return runData?.Snapshot() ?? storedRuntime;
    }

    /// <summary>The runtime data of a run by id: the active run from memory, any other run from storage (its final values).</summary>
    public async Task<RuntimeDataSnapshot?> GetRuntimeAsync(string targetRunId, CancellationToken ct = default)
    {
        lock (sync)
            if (targetRunId == runId && (runData is not null || storedRuntime is not null))
                return runData?.Snapshot() ?? storedRuntime;
        var json = await storage.GetRuntimeDataAsync(targetRunId, ct);
        return json is null ? null : JsonSerializer.Deserialize<RuntimeDataSnapshot>(json, JsonOpts);
    }

    /// <summary>Returns live log lines emitted by currently flying bees (and preflight) with <c>Id &gt; afterId</c>.</summary>
    public IReadOnlyList<LiveLogEntryDto> GetLiveLogs(long afterId = 0, int limit = 500, int? sequence = null, int? entry = null)
    {
        limit = Math.Clamp(limit, 1, 2000);
        lock (sync)
        {
            if (liveLogs.Count == 0) return [];
            var result = new List<LiveLogEntryDto>();
            foreach (var item in liveLogs)
            {
                if (item.Id <= afterId) continue;
                if (sequence is { } seq && item.Sequence != seq) continue;
                if (entry is { } ent && item.Entry != ent) continue;
                result.Add(item);
                if (result.Count >= limit) break;
            }
            return result;
        }
    }

    private void AppendLiveLog(int sequence, int entry, double atSeconds, string level, string message, string? exception = null, string? programName = null)
    {
        lock (sync)
        {
            var id = ++nextLiveLogId;
            var name = sequence >= 0 ? $"B{sequence + 1}" : entry >= 0 ? $"Startup #{entry + 1}" : programName ?? "Preflight";
            liveLogs.Enqueue(new LiveLogEntryDto(id, sequence, name, entry, atSeconds, DateTimeOffset.UtcNow, level, message, exception));
            while (liveLogs.Count > MaxLiveLogs)
                liveLogs.Dequeue();
        }
    }

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
            if (IsActive)
                throw new InvalidOperationException("Cannot switch runs while a live run is in progress. Stop it first.");
        }

        var data = await storage.GetRunAsync(targetRunId, ct);
        if (data is null) return null;
        RuntimeDataSnapshot? loadedRuntime = null;
        if (await storage.GetRuntimeDataAsync(targetRunId, ct) is { } runtimeJson)
        {
            try { loadedRuntime = JsonSerializer.Deserialize<RuntimeDataSnapshot>(runtimeJson, JsonOpts); }
            catch (JsonException) { }
        }

        lock (sync)
        {
            if (IsActive)
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
            activeBees.Clear();
            liveLogs.Clear();
            lastSequence = -1;
            totalLogs = data.TotalLogs;
            runError = data.Error;
            preflightState = null;
            preflightScriptName = null;
            preflightLogs.Clear();
            preflightError = null;
            shutdownState = null;
            shutdownScriptName = null;
            shutdownLogs.Clear();
            shutdownError = null;
            runData = null;
            storedRuntime = loadedRuntime;

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
                preflightError,
                activeBees.Count > 0 ? activeBees.Values.OrderBy(b => b.Sequence).ToArray() : null,
                shutdownState,
                shutdownScriptName,
                shutdownLogs.Count > 0 ? shutdownLogs.ToArray() : null,
                shutdownError,
                runData?.Version ?? (storedRuntime is null ? 0 : 1));
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

        var shutdownId = string.IsNullOrWhiteSpace(plan.ShutdownScriptId) ? null : plan.ShutdownScriptId.Trim();
        PreflightScriptSummary? shutdownMeta = null;
        if (shutdownId is not null)
        {
            shutdownMeta = shutdowns?.Get(shutdownId)
                ?? throw new ArgumentException($"Shutdown script '{shutdownId}' was not found.");
        }

        CancellationTokenSource runCts;
        RunData data;
        LoadPlan cloned;
        string currentRunId;
        DateTimeOffset currentStartedUtc;
        lock (sync)
        {
            if (IsActive)
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
            activeBees.Clear();
            liveLogs.Clear();
            lastSequence = -1;
            totalLogs = 0;
            runError = null;
            preflightState = preflightMeta is not null ? "Running" : null;
            preflightScriptName = preflightMeta?.FileName;
            preflightLogs.Clear();
            preflightError = null;
            shutdownState = shutdownMeta is not null ? "Pending" : null;
            shutdownScriptName = shutdownMeta?.FileName;
            shutdownLogs.Clear();
            shutdownError = null;
            storedRuntime = null;
            var runClock = clock;
            data = runData = new RunData(cloned.Scenarios.Count, () => runClock.Elapsed.TotalSeconds);
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
                        runData: data,
                        onLog: line =>
                        {
                            lock (sync) preflightLogs.Add(line);
                            var lvl = line.Contains("[ERR]") ? "Error" : line.Contains("[WRN]") ? "Warning" : "Information";
                            AppendLiveLog(-1, -1, 0, lvl, line);
                        },
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
                                if (act.Started)
                                {
                                    starts[act.Entry]++;
                                    activeBees[act.Sequence] = new LiveRunningBeeDto(act.Sequence, act.Entry, act.AtSeconds);
                                }
                                else
                                {
                                    activeBees.Remove(act.Sequence);
                                }
                            }
                        }
                        return ValueTask.CompletedTask;
                    },
                    environment: env,
                    onLiveLog: ev => AppendLiveLog(ev.Sequence, ev.Entry, ev.Log.AtSeconds, ev.Log.Level, ev.Log.Message, ev.Log.Exception),
                    data: data);
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
                    activeBees.Clear();
                    runError = err;
                    if (shutdownMeta is not null)
                    {
                        state = "Shutdown";
                        shutdownState = "Running";
                    }
                    else state = "Finished";
                }

                // The shutdown program always runs at the end (brain 0052): after a normal finish, after a stop and after a failed preflight.
                // It is not cancelled by the stop (the run token is already cancelled); a failure shows on its own and leaves the scenarios' results as they are.
                if (shutdownMeta is not null && shutdowns is not null)
                {
                    string? shutdownErr = null;
                    try
                    {
                        var res = await shutdowns.ExecuteAsync(
                            shutdownMeta.ScriptId,
                            environmentName,
                            onLog: line =>
                            {
                                lock (sync) shutdownLogs.Add(line);
                                var lvl = line.Contains("[ERR]") ? "Error" : line.Contains("[WRN]") ? "Warning" : "Information";
                                AppendLiveLog(-1, -1, elapsed, lvl, line, programName: "Shutdown");
                            },
                            timeoutSeconds: ShutdownTimeoutSeconds,
                            ct: CancellationToken.None,
                            runData: data);
                        if (!res.Success) shutdownErr = res.Error ?? "Shutdown program failed.";
                    }
                    catch (Exception ex)
                    {
                        shutdownErr = ex.InnerException?.Message ?? ex.Message;
                    }
                    lock (sync)
                    {
                        shutdownState = shutdownErr is null ? "Succeeded" : "Failed";
                        shutdownError = shutdownErr;
                        state = "Finished";
                    }
                }

                if (runPersisted)
                {
                    try { await storage.SaveRuntimeDataAsync(currentRunId, JsonSerializer.Serialize(data.Snapshot(), JsonOpts)); }
                    catch (Exception) { /* losing the final values must not lose the run itself */ }
                    storage.EnqueueRunFinished(currentRunId, DateTimeOffset.UtcNow, "Finished", elapsed, err);
                }
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
            // Wait until the scenarios are down. The shutdown program may still be running then (it runs after a stop too); callers see state "Shutdown" and keep polling.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !toAwait.IsCompleted)
            {
                lock (sync) if (state is not ("Running" or "Preflight")) break;
                await Task.Delay(20);
            }
        }
        await storage.FlushAsync();
        return GetCurrent();
    }

    private static int CountSlowCalls(ExecutionResult res)
    {
        if (res.Calls is { Count: > 0 })
            return res.Calls.Count(c => c.Error is null && c.DurationMs >= SlowThresholdMs);
        return 0;
    }
}
