using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis.Scripting;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services;

/// <summary>Globals object injected into every C# scenario script (brain 0040).</summary>
public sealed class ScenarioScriptGlobals
{
    public required ScenarioScriptHost Scenario { get; init; }
}

/// <summary>
/// Compiles, persists and test-runs custom C# scenario scripts (<c>.csx</c> / <c>.cs</c>) using Roslyn Scripting
/// (brain 0040). Compiled scenarios are registered into <see cref="ScenarioCatalog"/> immediately so they work in
/// the generated parameter editor, single test runs and full <see cref="LoadRunner"/> runs.
/// </summary>
public sealed class ScriptScenarioStore
{
    private readonly ScriptFolder folder;
    private readonly ScenarioCatalog catalog;
    private readonly EnvironmentStore? environments;
    private readonly Func<HttpMessageHandler>? innerHandlerFactory;
    private readonly Lock sync = new();
    private readonly Dictionary<string, StoredScript> scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActiveTestRun> liveTestRuns = new(StringComparer.OrdinalIgnoreCase);

    private sealed class ActiveTestRun(string testRunId, IScenario scenario, CancellationTokenSource cts)
    {
        public string TestRunId { get; } = testRunId;
        public IScenario Scenario { get; } = scenario;
        public CancellationTokenSource Cts { get; } = cts;
        public ConcurrentQueue<ScriptRecordedCall> Calls { get; } = new();
        public ConcurrentQueue<LiveLogEntryDto> Logs { get; } = new();
        public Stopwatch StartupSw { get; } = new();
        public Stopwatch ExecSw { get; } = new();
        public Stopwatch TeardownSw { get; } = new();
        public ScenarioContext? ExecCtx;
        public long NextLogId;
        public volatile bool Running = true;
        public bool Success;
        public int? StatusCode;
        public long Bytes;
        public string? Error;
        public int FailedStep = -1;
        public int FailedRep = -1;
        public DateTimeOffset? FinishedUtc;

        public void AddLog(RecordedLog l)
        {
            var id = Interlocked.Increment(ref NextLogId);
            Logs.Enqueue(new LiveLogEntryDto(id, 0, "Test", 0, l.AtSeconds, DateTimeOffset.UtcNow, l.Level, l.Message, l.Exception, l.Step, l.Rep));
        }

        public LiveTestRunStatusDto Snapshot()
        {
            var calls = Calls.ToArray();
            var logs = Logs.ToArray();
            var ctx = ExecCtx;
            var hasSteps = Scenario.Steps.Count > 0;
            var status = Running ? calls.LastOrDefault(c => c.StatusCode is not null)?.StatusCode : StatusCode;
            var bytes = Running ? calls.Sum(c => c.Bytes) : Bytes;
            return new LiveTestRunStatusDto(
                TestRunId,
                Running,
                Success,
                Scenario.Id,
                Scenario.Name,
                status,
                bytes,
                ExecSw.Elapsed.TotalMilliseconds,
                StartupSw.Elapsed.TotalMilliseconds,
                TeardownSw.Elapsed.TotalMilliseconds,
                Error,
                calls,
                logs,
                hasSteps ? Scenario.Steps : null,
                ctx?.CurrentStep ?? -1,
                ctx?.CurrentRep ?? 0,
                FailedStep,
                FailedRep,
                ctx?.SnapshotStepVisits());
        }
    }

    /// <summary>References and imports of scenario scripts, on top of the common ones.</summary>
    private static readonly ScriptOptions ScenarioOptions = ScriptCompiler.CreateOptions(
        extraReferences:
        [
            typeof(DescriptionAttribute).Assembly,
            typeof(RangeAttribute).Assembly,
            typeof(ConcurrentDictionary<,>).Assembly,
            typeof(IScenario).Assembly,
        ],
        extraImports:
        [
            "System.Collections.Concurrent",
            "System.ComponentModel",
            "System.ComponentModel.DataAnnotations",
            "Beesharp.Services.Scenarios",
            "Beesharp.Services.Scenarios.Spec",
        ]);

    private sealed record StoredScript(
        string ScriptId,
        string FileName,
        string Code,
        IScenario Scenario,
        DateTimeOffset UpdatedUtc);

    /// <param name="directory">The scripts folder (<c>scripts</c>); preflights and shutdown programs have their own sub folders.</param>
    public ScriptScenarioStore(
        string directory,
        ScenarioCatalog catalog,
        EnvironmentStore? environments = null,
        Func<HttpMessageHandler>? innerHandlerFactory = null)
    {
        folder = new ScriptFolder(directory, includeLegacyCsx: true);
        this.catalog = catalog;
        this.environments = environments;
        this.innerHandlerFactory = innerHandlerFactory;
    }

    /// <summary>Compiles every script file and registers the scenarios. Returns the files that did not compile or register (empty when all went well), so a caller can report them.</summary>
    public async Task<IReadOnlyList<ScriptLoadProblem>> LoadAllFromDiskAsync(CancellationToken ct = default)
    {
        var problems = new List<ScriptLoadProblem>();
        foreach (var file in await folder.ReadAllAsync(ct))
        {
            var res = await CompileAndRegisterAsync(file.Id, file.FileName, file.Code, persist: false, ct);
            if (!res.Success) problems.Add(ScriptLoadProblem.From("Scenario script", file.FileName, res.Diagnostics, res.Error));
        }
        return problems;
    }

    public IReadOnlyList<ScriptScenarioSummary> List()
    {
        lock (sync)
            return scripts.Values
                .OrderBy(s => s.Scenario.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToSummary)
                .ToList();
    }

    public ScriptScenarioSummary? Get(string id)
    {
        lock (sync)
            return scripts.TryGetValue(id, out var s) ? ToSummary(s) : null;
    }

    public string? FindScriptIdForScenario(string scenarioId)
    {
        lock (sync)
            return scripts.Values.FirstOrDefault(s => string.Equals(s.Scenario.Id, scenarioId, StringComparison.OrdinalIgnoreCase))?.ScriptId;
    }

    public async Task<ScriptCompileResult> CompileAndRegisterAsync(
        string? scriptId,
        string? fileName,
        string code,
        bool persist = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new ScriptCompileResult(false, null, [], "Script code cannot be empty.");

        var (script, mapped) = ScriptCompiler.Compile(
            code, ScriptNames.FileName(fileName, scriptId, "script.cs", allowCsx: true), ScenarioOptions, typeof(ScenarioScriptGlobals), ct);

        if (script is null)
            return new ScriptCompileResult(false, null, mapped, "Compilation failed.");

        var host = new ScenarioScriptHost();
        try
        {
            await script.RunAsync(new ScenarioScriptGlobals { Scenario = host }, ct);
        }
        catch (Exception ex)
        {
            return new ScriptCompileResult(false, null, mapped, $"Script execution failed while registering scenarios: {ex.InnerException?.Message ?? ex.Message}");
        }

        IReadOnlyList<IScenario> built;
        try
        {
            built = host.BuildAll();
        }
        catch (Exception ex)
        {
            return new ScriptCompileResult(false, null, mapped, ex.Message);
        }

        if (built.Count == 0)
            return new ScriptCompileResult(false, null, mapped, "Script did not register any scenario. Call Scenario.Define<TParams>(\"Name\", async (p, ctx) => ...).");

        var scenario = built[0];
        var id = ScriptNames.SanitizeId(string.IsNullOrWhiteSpace(scriptId) ? scenario.Id : scriptId);
        var cleanFileName = ScriptNames.FileName(fileName, id, $"{id}.cs", allowCsx: true);

        StoredScript? previous;
        lock (sync) scripts.TryGetValue(id, out previous);

        try
        {
            if (previous is not null && !string.Equals(previous.Scenario.Id, scenario.Id, StringComparison.OrdinalIgnoreCase))
                catalog.UnregisterCustom(previous.Scenario.Id);
            catalog.RegisterCustom(scenario);
        }
        catch (Exception ex)
        {
            return new ScriptCompileResult(false, null, mapped, ex.Message);
        }

        var updated = DateTimeOffset.UtcNow;
        if (persist)
            await folder.SaveAsync(id, code, ct);

        var stored = new StoredScript(id, cleanFileName, code, scenario, updated);
        lock (sync) scripts[id] = stored;

        return new ScriptCompileResult(true, ToSummary(stored), mapped);
    }

    public bool Delete(string scriptId)
    {
        StoredScript? existing;
        lock (sync)
        {
            if (!scripts.Remove(scriptId, out existing)) return false;
        }
        catalog.UnregisterCustom(existing.Scenario.Id);
        folder.Delete(ScriptNames.SanitizeId(scriptId));
        return true;
    }

    /// <summary>
    /// Executes a single bee run of the compiled script (<c>StartupAsync</c> → <c>ExecuteAsync</c> → <c>TeardownAsync</c>)
    /// and returns status, timing, recorded HTTP calls and logs.
    /// </summary>
    public async Task<ScriptTestRunResult> TestRunAsync(
        string scriptOrScenarioId,
        JsonObject? parameters,
        string? environmentName,
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        IScenario? scenario;
        lock (sync)
        {
            scenario = scripts.TryGetValue(scriptOrScenarioId, out var stored)
                ? stored.Scenario
                : catalog.Find(scriptOrScenarioId);
        }
        if (scenario is null)
            throw new KeyNotFoundException($"Script or scenario '{scriptOrScenarioId}' was not found.");

        EnvironmentDefinition? envDef = null;
        if (environments is not null && !string.IsNullOrWhiteSpace(environmentName))
            envDef = await environments.GetAsync(environmentName, ct);
        var envValues = envDef?.ToValues() ?? EnvironmentValues.None;

        var rawParams = parameters is null ? scenario.Spec.DefaultJson() : (JsonObject)parameters.DeepClone();
        var unknown = new List<string>();
        var resolvedJson = Placeholders.ResolveJson(rawParams, envValues, unknown);
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"{(envValues.Name.Length == 0 ? "No environment is active" : $"Environment '{envValues.Name}' has no value")} for: {string.Join(", ", unknown.Select(k => "{{" + k + "}}"))}.");

        var bound = scenario.Spec.Bind(resolvedJson);
        scenario.Spec.ResolveSecrets(bound);
        var validationErrors = scenario.Spec.Validate(resolvedJson);
        if (validationErrors.Count > 0)
            throw new ArgumentException(string.Join(" ", validationErrors));

        var calls = new ConcurrentQueue<ScriptRecordedCall>();
        var logs = new ConcurrentQueue<string>();
        var stepVisits = new ConcurrentQueue<RecordedStepVisit>();
        using var inner = innerHandlerFactory?.Invoke() ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        var execSw = new Stopwatch();
        using var recordingHandler = new RecordingHttpHandler(inner, calls, execSw);
        using var http = new HttpClient(recordingHandler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Beesharp/1.0");

        // A test run is one execution with its own private bags (the real run's bags are not touched).
        var scratch = new RunData(1);
        var state = scratch.EntryBagFor(0, new DataWriter(DataWriterKind.Bee, 0, 0));
        var runBag = scratch.RunBagFor(new DataWriter(DataWriterKind.Bee, 0, 0));
        using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        execCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)));

        var startupSw = Stopwatch.StartNew();
        var teardownSw = new Stopwatch();
        ScenarioOutcome outcome;
        ScenarioContext? execCtx = null;

        static string FormatSerilog(RecordedLog l)
        {
            var tag = l.Level switch
            {
                "Trace" => "TRC",
                "Debug" => "DBG",
                "Warning" => "WRN",
                "Error" or "Critical" => "ERR",
                _ => "INF",
            };
            var ts = DateTimeOffset.Now.ToString("HH:mm:ss.fff");
            var msg = string.IsNullOrEmpty(l.Exception) ? l.Message : $"{l.Message}\n{l.Exception}";
            return $"[{ts} {tag}] {msg}";
        }

        await using var bgHost = new ScenarioBackgroundHost(execCts.Token);
        var started = false;
        try
        {
            var startupCtx = new ScenarioContext(http, envValues, execCts.Token, state, bgHost,
                structuredLogSink: l => logs.Enqueue(FormatSerilog(l)), runBag: runBag);
            await scenario.StartupAsync(bound, startupCtx, execCts.Token);
            startupSw.Stop();
            started = true;

            execSw.Start();
            execCtx = new ScenarioContext(
                http,
                envValues,
                execCts.Token,
                state,
                structuredLogSink: l => logs.Enqueue(FormatSerilog(l)),
                callSink: c => calls.Enqueue(new ScriptRecordedCall("STEP", c.Name, c.StatusCode, c.DurationMs, c.Bytes, c.Error, c.Step, c.Rep, c.OffsetMs)),
                runBag: runBag,
                stepVisitSink: stepVisits.Enqueue);
            using (execCtx.Activate())
                outcome = await scenario.ExecuteAsync(bound, execCtx, execCts.Token);
            execSw.Stop();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            startupSw.Stop();
            execSw.Stop();
            outcome = ScenarioOutcome.Failed($"Timeout after {timeoutSeconds} s");
        }
        catch (Exception ex)
        {
            startupSw.Stop();
            execSw.Stop();
            var msg = ex.InnerException?.Message ?? ex.Message;
            var formatted = ScenarioExceptionFormatter.Format(ex, scenario.GetType().Assembly);
            logs.Enqueue(FormatSerilog(new RecordedLog(execSw.Elapsed.TotalSeconds, "Error", $"Unhandled exception: {msg}", formatted)));
            outcome = ScenarioOutcome.Failed(msg);
        }
        finally
        {
            await bgHost.DisposeAsync();
            if (started)
            {
                teardownSw.Start();
                try
                {
                    var teardownCtx = new ScenarioContext(http, envValues, CancellationToken.None, state,
                        structuredLogSink: l => logs.Enqueue(FormatSerilog(l)), runBag: runBag);
                    await scenario.TeardownAsync(bound, teardownCtx, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    var formatted = ScenarioExceptionFormatter.Format(ex, scenario.GetType().Assembly);
                    logs.Enqueue(FormatSerilog(new RecordedLog(teardownSw.Elapsed.TotalSeconds, "Error", $"Teardown error: {ex.InnerException?.Message ?? ex.Message}", formatted)));
                }
                teardownSw.Stop();
            }
        }

        var recordedCalls = calls.ToArray();
        var statusCode = recordedCalls.LastOrDefault(c => c.StatusCode is not null)?.StatusCode ?? outcome.StatusCode;
        var totalBytes = outcome.Bytes > 0 ? outcome.Bytes : recordedCalls.Sum(c => c.Bytes);
        var hasSteps = scenario.Steps.Count > 0;
        var failedStep = !outcome.Success && hasSteps ? (execCtx is { CurrentStep: >= 0 } ? execCtx.CurrentStep : 0) : -1;
        var failedRep = !outcome.Success && hasSteps ? (execCtx is { CurrentStep: >= 0 } ? execCtx.CurrentRep : 0) : -1;

        return new ScriptTestRunResult(
            outcome.Success,
            scenario.Id,
            scenario.Name,
            statusCode,
            totalBytes,
            (outcome.Measured ?? execSw.Elapsed).TotalMilliseconds,
            startupSw.Elapsed.TotalMilliseconds,
            teardownSw.Elapsed.TotalMilliseconds,
            outcome.FailureReason,
            recordedCalls,
            logs.ToArray(),
            hasSteps ? scenario.Steps : null,
            failedStep,
            failedRep,
            stepVisits.IsEmpty ? null : stepVisits.ToArray());
    }

    /// <summary>Starts a single-bee test run asynchronously so the UI can poll live steps, calls and logs as they happen.</summary>
    public async Task<LiveTestRunStatusDto> StartLiveTestRunAsync(
        string scriptOrScenarioId,
        JsonObject? parameters,
        string? environmentName,
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        PruneOldTestRuns();

        IScenario? scenario;
        lock (sync)
        {
            scenario = scripts.TryGetValue(scriptOrScenarioId, out var stored)
                ? stored.Scenario
                : catalog.Find(scriptOrScenarioId);
        }
        if (scenario is null)
            throw new KeyNotFoundException($"Script or scenario '{scriptOrScenarioId}' was not found.");

        EnvironmentDefinition? envDef = null;
        if (environments is not null && !string.IsNullOrWhiteSpace(environmentName))
            envDef = await environments.GetAsync(environmentName, ct);
        var envValues = envDef?.ToValues() ?? EnvironmentValues.None;

        var rawParams = parameters is null ? scenario.Spec.DefaultJson() : (JsonObject)parameters.DeepClone();
        var unknown = new List<string>();
        var resolvedJson = Placeholders.ResolveJson(rawParams, envValues, unknown);
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"{(envValues.Name.Length == 0 ? "No environment is active" : $"Environment '{envValues.Name}' has no value")} for: {string.Join(", ", unknown.Select(k => "{{" + k + "}}"))}.");

        var bound = scenario.Spec.Bind(resolvedJson);
        scenario.Spec.ResolveSecrets(bound);
        var validationErrors = scenario.Spec.Validate(resolvedJson);
        if (validationErrors.Count > 0)
            throw new ArgumentException(string.Join(" ", validationErrors));

        var clampedTimeout = Math.Clamp(timeoutSeconds, 1, 300);
        var execCts = new CancellationTokenSource();
        execCts.CancelAfter(TimeSpan.FromSeconds(clampedTimeout));

        var testRunId = $"test-{Guid.NewGuid():N}"[..17];
        var run = new ActiveTestRun(testRunId, scenario, execCts);
        liveTestRuns[testRunId] = run;

        _ = Task.Run(async () =>
        {
            using var inner = innerHandlerFactory?.Invoke() ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
            using var recordingHandler = new RecordingHttpHandler(inner, run.Calls, run.ExecSw);
            using var http = new HttpClient(recordingHandler) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Beesharp/1.0");

            var scratch = new RunData(1);
            var state = scratch.EntryBagFor(0, new DataWriter(DataWriterKind.Bee, 0, 0));
            var runBag = scratch.RunBagFor(new DataWriter(DataWriterKind.Bee, 0, 0));
            ScenarioOutcome outcome;

            await using var bgHost = new ScenarioBackgroundHost(execCts.Token);
            var started = false;
            try
            {
                run.StartupSw.Start();
                var startupCtx = new ScenarioContext(http, envValues, execCts.Token, state, bgHost,
                    structuredLogSink: run.AddLog, runBag: runBag);
                await scenario.StartupAsync(bound, startupCtx, execCts.Token);
                run.StartupSw.Stop();
                started = true;

                run.ExecSw.Start();
                var execCtx = new ScenarioContext(
                    http,
                    envValues,
                    execCts.Token,
                    state,
                    structuredLogSink: run.AddLog,
                    callSink: c => run.Calls.Enqueue(new ScriptRecordedCall("STEP", c.Name, c.StatusCode, c.DurationMs, c.Bytes, c.Error, c.Step, c.Rep, c.OffsetMs)),
                    runBag: runBag);
                run.ExecCtx = execCtx;
                using (execCtx.Activate())
                    outcome = await scenario.ExecuteAsync(bound, execCtx, execCts.Token);
                run.ExecSw.Stop();
            }
            catch (OperationCanceledException)
            {
                run.StartupSw.Stop();
                run.ExecSw.Stop();
                outcome = ScenarioOutcome.Failed(execCts.IsCancellationRequested ? "Stopped or timed out" : $"Timeout after {clampedTimeout} s");
            }
            catch (Exception ex)
            {
                run.StartupSw.Stop();
                run.ExecSw.Stop();
                var msg = ex.InnerException?.Message ?? ex.Message;
                var formatted = ScenarioExceptionFormatter.Format(ex, scenario.GetType().Assembly);
                var ctxNow = run.ExecCtx;
                run.AddLog(new RecordedLog(run.ExecSw.Elapsed.TotalSeconds, "Error", $"Unhandled exception: {msg}", formatted, Step: Math.Max(0, ctxNow?.CurrentStep ?? 0), Rep: ctxNow?.CurrentRep ?? 0));
                outcome = ScenarioOutcome.Failed(msg);
            }
            finally
            {
                await bgHost.DisposeAsync();
                if (started)
                {
                    run.TeardownSw.Start();
                    try
                    {
                        var teardownCtx = new ScenarioContext(http, envValues, CancellationToken.None, state,
                            structuredLogSink: run.AddLog, runBag: runBag);
                        await scenario.TeardownAsync(bound, teardownCtx, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        var formatted = ScenarioExceptionFormatter.Format(ex, scenario.GetType().Assembly);
                        run.AddLog(new RecordedLog(run.TeardownSw.Elapsed.TotalSeconds, "Error", $"Teardown error: {ex.InnerException?.Message ?? ex.Message}", formatted));
                    }
                    run.TeardownSw.Stop();
                }
            }

            var recordedCalls = run.Calls.ToArray();
            var hasSteps = scenario.Steps.Count > 0;
            var finalCtx = run.ExecCtx;
            run.Success = outcome.Success;
            run.StatusCode = recordedCalls.LastOrDefault(c => c.StatusCode is not null)?.StatusCode ?? outcome.StatusCode;
            run.Bytes = outcome.Bytes > 0 ? outcome.Bytes : recordedCalls.Sum(c => c.Bytes);
            run.Error = outcome.FailureReason;
            run.FailedStep = !outcome.Success && hasSteps ? (finalCtx is { CurrentStep: >= 0 } ? finalCtx.CurrentStep : 0) : -1;
            run.FailedRep = !outcome.Success && hasSteps ? (finalCtx is { CurrentStep: >= 0 } ? finalCtx.CurrentRep : 0) : -1;
            run.FinishedUtc = DateTimeOffset.UtcNow;
            run.Running = false;
            execCts.Dispose();
        }, CancellationToken.None);

        return run.Snapshot();
    }

    public LiveTestRunStatusDto? GetLiveTestRun(string testRunId) =>
        liveTestRuns.TryGetValue(testRunId, out var run) ? run.Snapshot() : null;

    public bool StopLiveTestRun(string testRunId)
    {
        if (!liveTestRuns.TryGetValue(testRunId, out var run)) return false;
        try { run.Cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    private void PruneOldTestRuns()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        foreach (var kv in liveTestRuns)
        {
            if (kv.Value.FinishedUtc is { } done && done < cutoff)
                liveTestRuns.TryRemove(kv.Key, out _);
        }
    }

    private static ScriptScenarioSummary ToSummary(StoredScript s) => new(
        s.ScriptId,
        s.FileName,
        s.Scenario.Id,
        s.Scenario.Name,
        s.Scenario.Description,
        s.Code,
        s.UpdatedUtc,
        s.Scenario.Spec.ToJsonSchema(),
        s.Scenario.Steps.Count > 0 ? s.Scenario.Steps : null);

    private sealed class RecordingHttpHandler(HttpMessageHandler inner, ConcurrentQueue<ScriptRecordedCall> calls, Stopwatch execSw) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var step = Math.Max(0, ScenarioContext.Current?.CurrentStep ?? 0);
            var rep = ScenarioContext.Current?.CurrentRep ?? 0;
            var offsetMs = execSw.Elapsed.TotalMilliseconds;
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                sw.Stop();
                long bytes = response.Content.Headers.ContentLength ?? 0;
                calls.Enqueue(new ScriptRecordedCall(
                    request.Method.Method,
                    request.RequestUri?.ToString() ?? "",
                    (int)response.StatusCode,
                    sw.Elapsed.TotalMilliseconds,
                    bytes,
                    null,
                    step,
                    rep,
                    offsetMs));
                return response;
            }
            catch (Exception ex)
            {
                sw.Stop();
                calls.Enqueue(new ScriptRecordedCall(
                    request.Method.Method,
                    request.RequestUri?.ToString() ?? "",
                    null,
                    sw.Elapsed.TotalMilliseconds,
                    0,
                    ex.Message,
                    step,
                    rep,
                    offsetMs));
                throw;
            }
        }
    }
}
