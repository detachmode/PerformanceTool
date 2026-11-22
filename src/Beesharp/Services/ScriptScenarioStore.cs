using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Logging;
using PerformanceTool.Services.Environments;
using PerformanceTool.Services.Runtime;
using PerformanceTool.Services.Scenarios;
using PerformanceTool.Services.Scenarios.Spec;

namespace PerformanceTool.Services;

/// <summary>Globals object injected into every C# scenario script (brain 0040).</summary>
public sealed class ScenarioScriptGlobals
{
    public required ScenarioScriptHost Scenario { get; init; }
}

public sealed record ScriptDiagnosticDto(string Id, string Severity, int Line, int Column, string Message);

public sealed record ScriptScenarioSummary(
    string ScriptId,
    string FileName,
    string ScenarioId,
    string ScenarioName,
    string Description,
    string Code,
    DateTimeOffset UpdatedUtc,
    JsonObject Parameters);

public sealed record ScriptCompileResult(
    bool Success,
    ScriptScenarioSummary? Script,
    IReadOnlyList<ScriptDiagnosticDto> Diagnostics,
    string? Error = null);

public sealed record ScriptRecordedCall(string Method, string Url, int? StatusCode, double DurationMs, long Bytes, string? Error = null);

public sealed record ScriptTestRunResult(
    bool Success,
    string ScenarioId,
    string ScenarioName,
    int? StatusCode,
    long Bytes,
    double DurationMs,
    double StartupDurationMs,
    double TeardownDurationMs,
    string? Error,
    IReadOnlyList<ScriptRecordedCall> Calls,
    IReadOnlyList<string> Logs);

/// <summary>
/// Compiles, persists and test-runs custom C# scenario scripts (<c>.csx</c> / <c>.cs</c>) using Roslyn Scripting
/// (brain 0040). Compiled scenarios are registered into <see cref="ScenarioCatalog"/> immediately so they work in
/// the generated parameter editor, single test runs and full <see cref="LoadRunner"/> runs.
/// </summary>
public sealed class ScriptScenarioStore
{
    private readonly string directory;
    private readonly ScenarioCatalog catalog;
    private readonly EnvironmentStore? environments;
    private readonly Func<HttpMessageHandler>? innerHandlerFactory;
    private readonly Lock sync = new();
    private readonly Dictionary<string, StoredScript> scripts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ScriptOptions DefaultOptions = CreateScriptOptions();

    private sealed record StoredScript(
        string ScriptId,
        string FileName,
        string Code,
        IScenario Scenario,
        DateTimeOffset UpdatedUtc);

    public ScriptScenarioStore(
        IConfiguration configuration,
        IWebHostEnvironment env,
        ScenarioCatalog catalog,
        EnvironmentStore environments,
        Func<HttpMessageHandler>? innerHandlerFactory = null)
    {
        var configured = configuration["Scripts:Directory"];
        directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "scripts")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
        this.catalog = catalog;
        this.environments = environments;
        this.innerHandlerFactory = innerHandlerFactory;
    }

    /// <summary>Convenience constructor for unit tests.</summary>
    public ScriptScenarioStore(
        string directory,
        ScenarioCatalog catalog,
        EnvironmentStore? environments = null,
        Func<HttpMessageHandler>? innerHandlerFactory = null)
    {
        this.directory = directory;
        this.catalog = catalog;
        this.environments = environments;
        this.innerHandlerFactory = innerHandlerFactory;
    }

    public async Task LoadAllFromDiskAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.csx").Concat(Directory.EnumerateFiles(directory, "*.cs")).OrderBy(f => f))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var code = await File.ReadAllTextAsync(file, ct);
            await CompileAndRegisterAsync(id, Path.GetFileName(file), code, persist: false, ct);
        }
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

        var script = CSharpScript.Create(code, DefaultOptions, typeof(ScenarioScriptGlobals));
        var diagnostics = script.Compile(ct);
        var mapped = diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(ToDiagnosticDto)
            .ToList();

        if (mapped.Any(d => d.Severity == "Error"))
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
        var id = SanitizeId(string.IsNullOrWhiteSpace(scriptId) ? scenario.Id : scriptId);
        var cleanFileName = string.IsNullOrWhiteSpace(fileName) ? $"{id}.cs" : Path.GetFileName(fileName.Trim());
        if (!cleanFileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !cleanFileName.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
            cleanFileName += ".cs";

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
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{id}.cs");
            await File.WriteAllTextAsync(path, code, ct);
            var legacyCsx = Path.Combine(directory, $"{id}.csx");
            if (File.Exists(legacyCsx)) File.Delete(legacyCsx);
        }

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
        var safeId = SanitizeId(scriptId);
        foreach (var ext in new[] { ".cs", ".csx" })
        {
            var path = Path.Combine(directory, $"{safeId}{ext}");
            if (File.Exists(path)) File.Delete(path);
        }
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

    private static ScriptDiagnosticDto ToDiagnosticDto(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        return new ScriptDiagnosticDto(
            d.Id,
            d.Severity.ToString(),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            d.GetMessage());
    }

    private static string SanitizeId(string raw)
    {
        var clean = new string(raw.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return clean.Length == 0 ? $"script-{Guid.NewGuid():N}"[..15] : clean;
    }

    private static ScriptOptions CreateScriptOptions()
    {
        var references = new[]
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(HttpClient).Assembly,
            typeof(AuthenticationHeaderValue).Assembly,
            typeof(HttpClientJsonExtensions).Assembly,
            typeof(DescriptionAttribute).Assembly,
            typeof(RangeAttribute).Assembly,
            typeof(JsonSerializer).Assembly,
            typeof(ConcurrentDictionary<,>).Assembly,
            typeof(ILogger).Assembly,
            typeof(IScenario).Assembly,
        }.Distinct();

        return ScriptOptions.Default
            .AddReferences(references)
            .AddImports(
                "System",
                "System.Collections.Concurrent",
                "System.Collections.Generic",
                "System.ComponentModel",
                "System.ComponentModel.DataAnnotations",
                "System.Linq",
                "System.Net.Http",
                "System.Net.Http.Headers",
                "System.Net.Http.Json",
                "System.Text",
                "System.Text.Json",
                "System.Text.Json.Nodes",
                "System.Threading",
                "System.Threading.Tasks",
                "Microsoft.Extensions.Logging",
                "PerformanceTool.Services.Scenarios",
                "PerformanceTool.Services.Scenarios.Spec",
                "PerformanceTool.Services.Environments");
    }

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
