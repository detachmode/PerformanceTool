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
        using var inner = innerHandlerFactory?.Invoke() ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        using var recordingHandler = new RecordingHttpHandler(inner, calls);
        using var http = new HttpClient(recordingHandler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Beesharp/1.0");

        // A test run is one execution with its own private bags (the real run's bags are not touched).
        var scratch = new RunData(1);
        var state = scratch.EntryBagFor(0, new DataWriter(DataWriterKind.Bee, 0, 0));
        var runBag = scratch.RunBagFor(new DataWriter(DataWriterKind.Bee, 0, 0));
        using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        execCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)));

        var startupSw = Stopwatch.StartNew();
        var execSw = new Stopwatch();
        var teardownSw = new Stopwatch();
        ScenarioOutcome outcome;

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
            var execCtx = new ScenarioContext(
                http,
                envValues,
                execCts.Token,
                state,
                structuredLogSink: l => logs.Enqueue(FormatSerilog(l)),
                callSink: c => calls.Enqueue(new ScriptRecordedCall("STEP", c.Name, c.StatusCode, c.DurationMs, c.Bytes, c.Error)),
                runBag: runBag);
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
            logs.ToArray());
    }

    private static ScriptScenarioSummary ToSummary(StoredScript s) => new(
        s.ScriptId,
        s.FileName,
        s.Scenario.Id,
        s.Scenario.Name,
        s.Scenario.Description,
        s.Code,
        s.UpdatedUtc,
        s.Scenario.Spec.ToJsonSchema());

    private sealed class RecordingHttpHandler(HttpMessageHandler inner, ConcurrentQueue<ScriptRecordedCall> calls) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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
                    bytes));
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
                    ex.Message));
                throw;
            }
        }
    }
}
