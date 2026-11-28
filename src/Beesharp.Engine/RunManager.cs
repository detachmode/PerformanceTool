using System.Diagnostics;
using System.Text.Json;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;
using Beesharp.Services.TraceStore;
using Microsoft.Extensions.Logging;

namespace Beesharp.Services;

/// <summary>
/// Hosts the currently active (or most recent) server-side <see cref="LoadRunner"/> run, persisting all bees,
/// calls and logs into <see cref="IRunStorage"/> (DuckDB by default) without blocking bees, and serving
/// lightweight incremental polls to the Blazor WebAssembly client (brain 0045).
/// </summary>
/// <param name="runner">The open-model load runner used to execute scenario entries.</param>
/// <param name="environments">The store used to resolve named environments before starting a run.</param>
/// <param name="storage">The run persistence store where runs, bees, calls, logs, and runtime data are saved.</param>
/// <param name="preflights">Optional store of preflight scripts executed before scenario bees start.</param>
/// <param name="shutdowns">Optional store of shutdown scripts executed at the end of a run.</param>
/// <param name="traces">Optional background trace fetching (brain 0065): when the run's environment names a trace store, the traces of selected bees are fetched into the run. Without it nothing is fetched.</param>
/// <param name="loggerFactory">Optional: the fetch's lines (category <c>Beesharp.Traces</c>) are also written to this factory's loggers (console), besides the run's host logs.</param>
/// <param name="scripts">Optional store of custom C# scenario scripts used to snapshot scenario source code at run start.</param>
/// <param name="catalog">Optional scenario catalog used to snapshot compiled scenario source code at run start.</param>
public sealed class RunManager(
    LoadRunner runner, EnvironmentStore environments, IRunStorage storage, PreflightStore? preflights = null, ShutdownStore? shutdowns = null,
    TraceFetching? traces = null, ILoggerFactory? loggerFactory = null, ScriptScenarioStore? scripts = null, ScenarioCatalog? catalog = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const int ShutdownTimeoutSeconds = 120;
    private readonly Lock sync = new();
    private CallTypicals typicals = new();
    private CancellationTokenSource? cts;
    private Task? runningTask;

    private string runId = "";
    private string state = "Idle";
    private DateTimeOffset startedUtc;
    // The run time (brain 0027): it stands still while the run is paused; LoadRunner schedules bees by it.
    private RunClock clock = new();
    private double finalElapsed;
    private LoadPlan activePlan = new();
    private int[] starts = [];
    private int[] finished = [];
    private int[] inFlight = [];
    private int[] failed = [];
    private int[] slowBees = [];
    private int[] slowCalls = [];
    private int[] dropped = [];
    private IReadOnlyList<StepInfo>[] runSteps = [];
    private int[][] failedPerStep = [];
    private readonly List<LiveBeeDto> bees = [];
    private readonly Dictionary<int, LiveRunningBeeDto> activeBees = [];
    private readonly Dictionary<int, ScenarioContext> activeBeeContexts = [];
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

    private bool IsActive => state is "Running" or "Paused" or "Preflight" or "Shutdown";

    /// <summary>
    /// Checks whether <paramref name="targetRunId"/> matches the run currently loaded in memory.
    /// </summary>
    /// <param name="targetRunId">The run identifier to compare against the active or loaded run.</param>
    /// <returns><see langword="true"/> if <paramref name="targetRunId"/> is the current run; otherwise <see langword="false"/>.</returns>
    public bool IsCurrent(string targetRunId)
    {
        lock (sync) return !string.IsNullOrEmpty(runId) && string.Equals(runId, targetRunId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime data (run bag and entry bags) of the active run: live while it runs, the final values afterwards. Null when there is none (e.g. an old stored run).
    /// </summary>
    /// <returns>The current <see cref="RuntimeDataSnapshot"/>, or null when unavailable.</returns>
    public RuntimeDataSnapshot? GetCurrentRuntime()
    {
        lock (sync) return runData?.Snapshot() ?? storedRuntime;
    }

    /// <summary>The runtime data of a run by id: the active run from memory, any other run from storage (its final values).</summary>
    /// <param name="targetRunId">The identifier of the run whose runtime data is requested.</param>
    /// <param name="ct">A token to cancel reading from storage.</param>
    /// <returns>The <see cref="RuntimeDataSnapshot"/> for the requested run, or null if not found.</returns>
    public async Task<RuntimeDataSnapshot?> GetRuntimeAsync(string targetRunId, CancellationToken ct = default)
    {
        lock (sync)
            if (targetRunId == runId && (runData is not null || storedRuntime is not null))
                return runData?.Snapshot() ?? storedRuntime;
        var json = await storage.GetRuntimeDataAsync(targetRunId, ct);
        return json is null ? null : JsonSerializer.Deserialize<RuntimeDataSnapshot>(json, JsonOpts);
    }

    /// <summary>Returns live log lines emitted by currently flying bees (and preflight) with <c>Id &gt; afterId</c>.</summary>
    /// <param name="afterId">Exclusive lower bound on <see cref="LiveLogEntryDto.Id"/>.</param>
    /// <param name="limit">Maximum number of log entries to return (clamped between 1 and 2000).</param>
    /// <param name="sequence">Optional zero-based bee sequence number filter.</param>
    /// <param name="entry">Optional zero-based scenario entry index filter.</param>
    /// <returns>Matching live log entries in chronological arrival order.</returns>
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

    private void AppendLiveLog(int sequence, int entry, double atSeconds, string level, string message, string? exception = null, string? programName = null, int step = 0, int rep = 0)
    {
        lock (sync)
        {
            var id = ++nextLiveLogId;
            var name = sequence >= 0 ? $"B{sequence + 1}" : entry >= 0 ? $"Startup #{entry + 1}" : programName ?? "Preflight";
            liveLogs.Enqueue(new LiveLogEntryDto(id, sequence, name, entry, atSeconds, DateTimeOffset.UtcNow, level, message, exception, step, rep));
            while (liveLogs.Count > MaxLiveLogs)
                liveLogs.Dequeue();
        }
    }

    /// <summary>Records a host / background-service <see cref="Microsoft.Extensions.Logging.ILogger"/> message into the active run so it streams to the Logs UI and persists for export.</summary>
    /// <param name="level">Severity label (e.g. Information, Warning, Error).</param>
    /// <param name="category">Logger category name; its short tail is prefixed to the formatted message.</param>
    /// <param name="message">The formatted log message.</param>
    /// <param name="exception">Optional exception text associated with the log entry.</param>
    public void RecordHostLog(string level, string category, string message, string? exception = null)
    {
        string activeRunId;
        double atSeconds;
        lock (sync)
        {
            if (!IsActive || string.IsNullOrEmpty(runId)) return;
            activeRunId = runId;
            atSeconds = clock.Elapsed.TotalSeconds;
        }

        var shortCat = ShortCategory(category);
        var formatted = string.IsNullOrEmpty(shortCat) ? message : $"[{shortCat}] {message}";
        AppendLiveLog(ProgramLogEntry.Host, ProgramLogEntry.Host, atSeconds, level, formatted, exception, "Host");
        storage.EnqueueHostLog(activeRunId, atSeconds, level, formatted, exception);
    }

    /// <summary>
    /// Writes a line to a run's host logs by run id, also after the run finished (the background trace fetch of brain 0065 says things then): to storage always, to the live
    /// log stream (UI, <c>beesharp run --logs</c>) while it is the current run.
    /// </summary>
    private void RecordHostLogFor(string forRunId, DateTimeOffset runStartedUtc, string level, string category, string message, string? exception)
    {
        bool current;
        double at;
        lock (sync)
        {
            current = runId == forRunId;
            var wall = (DateTimeOffset.UtcNow - runStartedUtc).TotalSeconds;
            at = current && IsActive ? clock.Elapsed.TotalSeconds : Math.Max(wall, current ? finalElapsed : 0);
        }
        var shortCat = ShortCategory(category);
        var formatted = string.IsNullOrEmpty(shortCat) ? message : $"[{shortCat}] {message}";
        if (current) AppendLiveLog(ProgramLogEntry.Host, ProgramLogEntry.Host, at, level, formatted, exception, "Host");
        storage.EnqueueHostLog(forRunId, at, level, formatted, exception);
    }

    /// <summary>The logger of the trace fetch of one run: writes to that run's host logs and, when a logger factory is known, to the console.</summary>
    private sealed class RunLogger(RunManager owner, string runId, DateTimeOffset runStartedUtc, ILogger? inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null) return;
            var level = logLevel switch
            {
                LogLevel.Trace => "Trace",
                LogLevel.Debug => "Debug",
                LogLevel.Warning => "Warning",
                LogLevel.Error or LogLevel.Critical => "Error",
                _ => "Information",
            };
            try { owner.RecordHostLogFor(runId, runStartedUtc, level, TraceFetchSession.LogCategory, message, exception?.ToString()); }
            catch (Exception) { /* a log line must never hurt the fetch, let alone the run */ }
            try { inner?.Log(logLevel, eventId, state, exception, formatter); }
            catch (Exception) { }
        }
    }

    private static string ShortCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "";
        var dot = category.LastIndexOf('.');
        return dot >= 0 && dot + 1 < category.Length ? category[(dot + 1)..] : category;
    }

    /// <summary>Restores the most recent persisted run from <see cref="IRunStorage"/> on server startup.</summary>
    /// <param name="ct">A token to cancel loading the latest run from storage.</param>
    /// <returns>A task representing the asynchronous restore operation.</returns>
    public async Task RestoreLatestAsync(CancellationToken ct = default)
    {
        var runs = await storage.ListRunsAsync(20, ct);
        var best = runs.FirstOrDefault(r => r.TotalBees > 0) ?? runs.FirstOrDefault();
        if (best is not null)
            await LoadStoredRunAsync(best.RunId, ct);
    }

    /// <summary>Clears a loaded historical run and returns to the idle state (only when no live run is active).</summary>
    /// <returns><see langword="true"/> if the state was cleared; <see langword="false"/> if a live run is active.</returns>
    public bool UnloadStoredRun()
    {
        lock (sync)
        {
            if (IsActive) return false;
            runId = "";
            state = "Idle";
            startedUtc = default;
            clock = new RunClock();
            finalElapsed = 0;
            activePlan = new LoadPlan();
            starts = [];
            finished = [];
            inFlight = [];
            failed = [];
            slowBees = [];
            slowCalls = [];
            dropped = [];
            runSteps = [];
            failedPerStep = [];
            bees.Clear();
            activeBees.Clear();
            activeBeeContexts.Clear();
            liveLogs.Clear();
            typicals = new CallTypicals();
            lastSequence = -1;
            totalLogs = 0;
            runError = null;
            preflightState = null;
            preflightScriptName = null;
            preflightLogs.Clear();
            preflightError = null;
            shutdownState = null;
            shutdownScriptName = null;
            shutdownLogs.Clear();
            shutdownError = null;
            runData = null;
            storedRuntime = null;
            return true;
        }
    }

    /// <summary>Loads a persisted historical run from <see cref="IRunStorage"/> into the active view.</summary>
    /// <param name="targetRunId">The identifier of the stored run to load.</param>
    /// <param name="ct">A token to cancel reading from storage.</param>
    /// <returns>The loaded <see cref="LiveRunStatusDto"/>, or null if no run with <paramref name="targetRunId"/> exists.</returns>
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
            clock = new RunClock();
            finalElapsed = data.ElapsedSeconds;
            activePlan = data.Plan.Clone();
            starts = new int[count];
            finished = new int[count];
            inFlight = new int[count];
            failed = new int[count];
            slowBees = new int[count];
            slowCalls = new int[count];
            dropped = new int[count];
            runSteps = BuildRunSteps(data.Plan, data.Steps);
            failedPerStep = runSteps.Select(s => new int[s.Count]).ToArray();
            bees.Clear();
            activeBees.Clear();
            activeBeeContexts.Clear();
            liveLogs.Clear();
            typicals = new CallTypicals();
            lastSequence = -1;
            totalLogs = data.TotalLogs;
            runError = data.Error;
            preflightState = data.Preflight?.State;
            preflightScriptName = data.Preflight?.Name;
            preflightLogs.Clear();
            if (data.Preflight?.Logs is { Count: > 0 } pfLogs) preflightLogs.AddRange(pfLogs);
            preflightError = data.Preflight?.Error;
            shutdownState = data.Shutdown?.State;
            shutdownScriptName = data.Shutdown?.Name;
            shutdownLogs.Clear();
            if (data.Shutdown?.Logs is { Count: > 0 } sdLogs) shutdownLogs.AddRange(sdLogs);
            shutdownError = data.Shutdown?.Error;
            runData = null;
            storedRuntime = loadedRuntime;

            foreach (var a in data.Bees)
            {
                bees.Add(new LiveBeeDto(a.Sequence, a.Entry, a.StartedAt, a.DurationMs, a.StatusCode, a.Bytes, a.Error, a.SlowCalls, a.FailedStep, a.FailedRep));
                if (a.Sequence > lastSequence) lastSequence = a.Sequence;
                if (a.Entry >= 0 && a.Entry < count)
                {
                    starts[a.Entry]++;
                    finished[a.Entry]++;
                    if (a.Error is not null)
                    {
                        failed[a.Entry]++;
                        if (a.FailedStep >= 0 && a.FailedStep < failedPerStep[a.Entry].Length)
                            failedPerStep[a.Entry][a.FailedStep]++;
                    }
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
    /// Bees finish out of sequence order, so a client that must not miss any passes <paramref name="afterIndex"/> instead: the number of bees it has already
    /// received (<see cref="LiveRunStatusDto.NextBeeIndex"/> of the last answer) and the run it got them from (<paramref name="forRunId"/>; another run means start from the first bee); then the bees in finishing order from that position on are returned.
    /// Full calls and logs live in <see cref="IRunStorage"/> and are fetched lazily on demand.
    /// </summary>
    /// <param name="afterSeq">Exclusive lower bound on bee sequence when <paramref name="afterIndex"/> is null.</param>
    /// <param name="maxBees">Maximum number of completed bee records to return (clamped between 1 and 10,000).</param>
    /// <param name="afterIndex">Optional zero-based finishing-order cursor (<see cref="LiveRunStatusDto.NextBeeIndex"/>) for lossless incremental polling.</param>
    /// <param name="forRunId">Optional run ID associated with <paramref name="afterIndex"/>; resets the cursor to 0 if the run ID has changed.</param>
    /// <returns>A snapshot of the current or loaded run status and incremental bee delta.</returns>
    public LiveRunStatusDto GetCurrent(int afterSeq = -1, int maxBees = 2000, int? afterIndex = null, string? forRunId = null)
    {
        maxBees = Math.Clamp(maxBees, 1, 10_000);
        lock (sync)
        {
            var elapsed = state is "Running" or "Paused" ? clock.Elapsed.TotalSeconds : finalElapsed;
            var inFlightByStep = runSteps.Select(s => s.Count > 0 ? new int[s.Count] : null).ToArray();
            LiveRunningBeeDto[]? runningSnapshot = null;
            if (activeBees.Count > 0)
            {
                var list = new List<LiveRunningBeeDto>(activeBees.Count);
                foreach (var b in activeBees.Values.OrderBy(x => x.Sequence))
                {
                    var step = -1;
                    var rep = 0;
                    IReadOnlyList<RecordedStepVisit>? stepVisits = null;
                    if (activeBeeContexts.TryGetValue(b.Sequence, out var bctx))
                    {
                        step = bctx.CurrentStep;
                        rep = bctx.CurrentRep;
                        stepVisits = bctx.SnapshotStepVisits();
                    }
                    if (b.Entry >= 0 && b.Entry < inFlightByStep.Length && inFlightByStep[b.Entry] is { } arr && step >= 0 && step < arr.Length)
                        arr[step]++;
                    list.Add(b with { Step = step, Rep = rep, StepVisits = stepVisits });
                }
                runningSnapshot = list.ToArray();
            }

            var views = new LiveScenarioViewDto[activePlan.Scenarios.Count];
            for (var i = 0; i < views.Length; i++)
            {
                var failedSteps = i < failedPerStep.Length && failedPerStep[i].Length > 0 ? failedPerStep[i].ToArray() : null;
                var activeSteps = i < inFlightByStep.Length ? inFlightByStep[i] : null;
                views[i] = new LiveScenarioViewDto(
                    i < starts.Length ? starts[i] : 0,
                    i < finished.Length ? finished[i] : 0,
                    i < inFlight.Length ? inFlight[i] : 0,
                    i < failed.Length ? failed[i] : 0,
                    i < slowBees.Length ? slowBees[i] : 0,
                    i < slowCalls.Length ? slowCalls[i] : 0,
                    i < dropped.Length ? dropped[i] : 0,
                    failedSteps,
                    activeSteps);
            }

            var delta = new List<LiveBeeDto>();
            var nextBee = 0;
            if (afterIndex is { } from)
            {
                if (forRunId is not null && !string.Equals(forRunId, runId, StringComparison.Ordinal)) from = 0;
                from = Math.Clamp(from, 0, bees.Count);
                for (var i = from; i < bees.Count && delta.Count < maxBees; i++) delta.Add(bees[i]);
                nextBee = from + delta.Count;
            }
            else if (bees.Count > 0)
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
                runningSnapshot,
                shutdownState,
                shutdownScriptName,
                shutdownLogs.Count > 0 ? shutdownLogs.ToArray() : null,
                shutdownError,
                runData?.Version ?? (storedRuntime is null ? 0 : 1),
                runSteps.Any(s => s.Count > 0) ? runSteps : null,
                nextBee);
        }
    }

    /// <summary>
    /// Starts a new load run in the background using the specified <paramref name="plan"/> and optional <paramref name="environmentName"/>.
    /// Executes any configured preflight script, runs the load plan via <see cref="LoadRunner"/>, and always runs any configured shutdown script afterwards.
    /// </summary>
    /// <param name="plan">The load plan to execute.</param>
    /// <param name="environmentName">Optional name of the environment to resolve placeholders and expose to scripts.</param>
    /// <param name="ct">A token to cancel resolving the environment before the run starts.</param>
    /// <returns>The initial <see cref="LiveRunStatusDto"/> immediately after the run is queued.</returns>
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
        RunClock runClock;
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
            clock = new RunClock();
            if (preflightMeta is null) clock.Start();   // with a preflight the run time starts when the scenarios do
            finalElapsed = 0;
            activePlan = cloned;
            starts = new int[cloned.Scenarios.Count];
            finished = new int[cloned.Scenarios.Count];
            inFlight = new int[cloned.Scenarios.Count];
            failed = new int[cloned.Scenarios.Count];
            slowBees = new int[cloned.Scenarios.Count];
            slowCalls = new int[cloned.Scenarios.Count];
            dropped = new int[cloned.Scenarios.Count];
            runSteps = BuildRunSteps(cloned, null);
            failedPerStep = runSteps.Select(s => new int[s.Count]).ToArray();
            bees.Clear();
            activeBees.Clear();
            activeBeeContexts.Clear();
            liveLogs.Clear();
            typicals = new CallTypicals();
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
            runClock = clock;
            data = runData = new RunData(cloned.Scenarios.Count, () => runClock.Elapsed.TotalSeconds);
            runCts = new CancellationTokenSource();
            cts = runCts;
        }

        runningTask = Task.Run(async () =>
        {
            string? err = null;
            var runPersisted = false;
            TraceFetchSession? traceSession = null;
            try
            {
                storage.EnqueueRunStarted(currentRunId, currentStartedUtc, cloned, runSteps);
                runPersisted = true;

                try
                {
                    var snapshots = CompiledSourceResolver.CaptureForPlan(cloned, catalog, scripts, preflights, shutdowns);
                    if (snapshots.Count > 0)
                        await storage.SaveRunSourcesAsync(currentRunId, snapshots);
                }
                catch (Exception) { }

                // Background trace fetch (brain 0065): only when the environment names a trace store; it never fails or slows the run (own task, bounded work per bee).
                if (traces is not null)
                {
                    try
                    {
                        traceSession = traces.Begin(currentRunId, currentStartedUtc, env?.ToValues(), storage,
                            new RunLogger(this, currentRunId, currentStartedUtc, loggerFactory?.CreateLogger(TraceFetchSession.LogCategory)));
                    }
                    catch (Exception) { traceSession = null; }
                }

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
                            AppendLiveLog(ProgramLogEntry.Preflight, ProgramLogEntry.Preflight, 0, lvl, line);
                        },
                        ct: runCts.Token);

                    string[] pfSnapshot;
                    if (!preRes.Success)
                    {
                        err = preRes.Error ?? "Preflight failed.";
                        lock (sync)
                        {
                            preflightState = "Failed";
                            preflightError = err;
                            pfSnapshot = preflightLogs.ToArray();
                            totalLogs += pfSnapshot.Length;
                        }
                        try { await storage.SaveProgramRunAsync(currentRunId, "preflight", new StoredProgramRunDto("Failed", preflightMeta.FileName, err, pfSnapshot)); }
                        catch (Exception) { }
                        return;
                    }

                    lock (sync)
                    {
                        preflightState = "Succeeded";
                        pfSnapshot = preflightLogs.ToArray();
                        totalLogs += pfSnapshot.Length;
                        state = "Running";
                        clock.Start();
                    }
                    try { await storage.SaveProgramRunAsync(currentRunId, "preflight", new StoredProgramRunDto("Succeeded", preflightMeta.FileName, null, pfSnapshot)); }
                    catch (Exception) { }
                }

                await runner.RunAsync(
                    cloned,
                    onResult: res =>
                    {
                        int beeSlowCalls;
                        int outcome;
                        ExecutionResult persistedRes;
                        bool[]? callSlow;
                        lock (sync)
                        {
                            byte ratioCode;
                            (beeSlowCalls, ratioCode, persistedRes, callSlow) = EvaluateCallsLocked(res);
                            outcome = res.Error is not null ? 2 : beeSlowCalls > 0 ? 1 : 0;
                            var compact = new LiveBeeDto(
                                res.Sequence,
                                res.Entry,
                                res.StartedAtSeconds,
                                res.ElapsedMs,
                                res.StatusCode,
                                res.Bytes,
                                res.Error,
                                beeSlowCalls,
                                res.FailedStep,
                                res.FailedRep,
                                ratioCode);

                            if (res.Entry >= 0 && res.Entry < finished.Length)
                            {
                                finished[res.Entry]++;
                                if (res.Error is not null)
                                {
                                    failed[res.Entry]++;
                                    if (res.FailedStep >= 0 && res.FailedStep < failedPerStep[res.Entry].Length)
                                        failedPerStep[res.Entry][res.FailedStep]++;
                                }
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

                        storage.EnqueueBeeCompleted(currentRunId, persistedRes, outcome, beeSlowCalls);
                        traceSession?.Observe(ToTraceObservation(res, callSlow));
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
                                    activeBeeContexts.Remove(act.Sequence);
                                }
                            }
                        }
                        return ValueTask.CompletedTask;
                    },
                    environment: env,
                    onLiveLog: ev => AppendLiveLog(ev.Sequence, ev.Entry, ev.Log.AtSeconds, ev.Log.Level, ev.Log.Message, ev.Log.Exception, step: ev.Log.Step, rep: ev.Log.Rep),
                    data: data,
                    onBeeContext: (seq, _, bctx) =>
                    {
                        lock (sync) activeBeeContexts[seq] = bctx;
                    },
                    clock: runClock,
                    runId: currentRunId);
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
                    clock.Pause();   // the run time stops at the end
                    for (var i = 0; i < inFlight.Length; i++) inFlight[i] = 0;
                    activeBees.Clear();
                    activeBeeContexts.Clear();
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
                    string[] sdSnapshot;
                    var sdState = shutdownErr is null ? "Succeeded" : "Failed";
                    lock (sync)
                    {
                        shutdownState = sdState;
                        shutdownError = shutdownErr;
                        sdSnapshot = shutdownLogs.ToArray();
                        totalLogs += sdSnapshot.Length;
                    }
                    if (runPersisted)
                    {
                        try
                        {
                            await storage.FlushAsync();
                            await storage.SaveProgramRunAsync(currentRunId, "shutdown", new StoredProgramRunDto(sdState, shutdownMeta.FileName, shutdownErr, sdSnapshot));
                        }
                        catch (Exception) { }
                    }
                    lock (sync) state = "Finished";
                }

                if (runPersisted)
                {
                    try { await storage.SaveRuntimeDataAsync(currentRunId, JsonSerializer.Serialize(data.Snapshot(), JsonOpts)); }
                    catch (Exception) { /* losing the final values must not lose the run itself */ }
                    storage.EnqueueRunFinished(currentRunId, DateTimeOffset.UtcNow, "Finished", elapsed, err);
                }

                // The final trace pass (brain 0065): the run is already finished and shows as such; this task (and so beesharp run) stays until the last traces came or are known missing.
                if (traceSession is not null)
                {
                    try { await traceSession.CompleteAsync(stopped: runCts.IsCancellationRequested); }
                    catch (Exception) { /* the session reports its own problems in the run's host logs */ }
                    finally { await traceSession.DisposeAsync(); }
                }
                runCts.Dispose();
            }
        }, CancellationToken.None);

        return GetCurrent();
    }

    /// <summary>Completes when the active run is completely done: scenarios, shutdown program and the last writes to storage queued. Returns at once when no run is active. For callers without polling (the command-line tool).</summary>
    /// <returns>A task that completes once the run and storage flush have finished.</returns>
    public async Task WaitUntilFinishedAsync()
    {
        Task? running;
        lock (sync) running = runningTask;
        if (running is not null) await running;
        await storage.FlushAsync();
    }

    /// <summary>
    /// Pauses the run (brain 0027): no new bees start and the run time stands still; bees already in flight finish. Only a running run can
    /// be paused (not during the preflight or the shutdown program). Stop still ends a paused run.
    /// </summary>
    /// <returns>The updated <see cref="LiveRunStatusDto"/> after attempting to pause.</returns>
    public LiveRunStatusDto Pause()
    {
        lock (sync)
            if (state == "Running" && clock.Pause()) state = "Paused";
        return GetCurrent();
    }

    /// <summary>Resumes a paused run: the run time goes on from where it stood and the remaining bees of the plan start on schedule.</summary>
    /// <returns>The updated <see cref="LiveRunStatusDto"/> after attempting to resume.</returns>
    public LiveRunStatusDto Resume()
    {
        lock (sync)
            if (state == "Paused" && clock.Resume()) state = "Running";
        return GetCurrent();
    }

    /// <summary>
    /// Signals the active run to stop, waits briefly for scenario bees to unwind, flushes storage, and returns the latest status.
    /// If a shutdown script is configured, the run enters the <c>Shutdown</c> state while that script executes.
    /// </summary>
    /// <returns>The <see cref="LiveRunStatusDto"/> after requesting cancellation and flushing storage.</returns>
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
                lock (sync) if (state is not ("Running" or "Paused" or "Preflight")) break;
                await Task.Delay(20);
            }
        }
        await storage.FlushAsync();
        return GetCurrent();
    }

    private IReadOnlyList<StepInfo>[] BuildRunSteps(LoadPlan plan, IReadOnlyList<IReadOnlyList<StepInfo>>? stored)
    {
        var result = new IReadOnlyList<StepInfo>[plan.Scenarios.Count];
        for (var i = 0; i < result.Length; i++)
        {
            if (stored is not null && i < stored.Count)
                result[i] = stored[i];
            else
                result[i] = runner.Catalog.Find(plan.Scenarios[i].ScenarioId)?.Steps ?? [];
        }
        return result;
    }

    private static TraceBeeObservation ToTraceObservation(ExecutionResult res, bool[]? callSlow) => new(
        res.TraceId,
        res.Sequence,
        Failed: res.Error is not null && !res.Canceled && !ExecutionResult.IsCanceledError(res.Error),
        DateTimeOffset.UtcNow,
        (res.Calls ?? []).Select((c, i) => new TraceCallObservation(
            string.IsNullOrWhiteSpace(c.Endpoint) ? EndpointNormalizer.NormalizeCallName(c.Name) : c.Endpoint,
            c.DurationMs, callSlow is not null && i < callSlow.Length && callSlow[i], c.Error is not null)).ToList());

    private (int SlowCalls, byte RatioCode, ExecutionResult Persisted, bool[]? CallSlow) EvaluateCallsLocked(ExecutionResult res)
    {
        if (res.Calls is not { Count: > 0 } calls) return (0, 0, res, null);
        var slow = 0;
        var worst = 1.0;
        var callSlow = new bool[calls.Count];
        List<RecordedCall>? trimmed = null;
        for (var i = 0; i < calls.Count; i++)
        {
            var c = calls[i];
            var isSlow = false;
            if (c.Error is null)
            {
                var ep = string.IsNullOrWhiteSpace(c.Endpoint) ? EndpointNormalizer.NormalizeCallName(c.Name) : c.Endpoint;
                var typical = typicals.Typical(ep);
                isSlow = SlowCallRule.IsSlow(c.DurationMs, typical, c.SlowThresholdMs);
                var ratio = SlowCallRule.Ratio(c.DurationMs, typical, c.SlowThresholdMs);
                typicals.Add(ep, c.DurationMs);
                if (isSlow) { slow++; callSlow[i] = true; }
                if (ratio > worst) worst = ratio;
            }

            // Keep captured headers only on slow or failed calls to avoid bloating storage on healthy traffic.
            if (c.Headers is { Count: > 0 } && !isSlow && c.Error is null)
            {
                trimmed ??= [.. calls];
                trimmed[i] = c with { Headers = null };
            }
        }
        return (slow, SlowCallRule.Encode(worst), trimmed is null ? res : res with { Calls = trimmed }, callSlow);
    }
}
