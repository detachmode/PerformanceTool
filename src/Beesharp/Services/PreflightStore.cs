using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Logging;
using PerformanceTool.Services.Environments;

namespace PerformanceTool.Services;

/// <summary>Globals injected into every C# preflight script.</summary>
public sealed class PreflightGlobals
{
    public required HttpClient Http { get; init; }
    public required ILogger Logger { get; init; }
    public required EnvironmentValues Env { get; init; }
    public required CancellationToken CancellationToken { get; init; }
    public required Action<string> Log { get; init; }
    public required Action<string> Fail { get; init; }
    public required Func<string, string, Task<int>> RunProcessAsync { get; init; }
}

public sealed record PreflightScriptSummary(
    string ScriptId,
    string FileName,
    string Name,
    string Code,
    DateTimeOffset UpdatedUtc);

public sealed record PreflightCompileResult(
    bool Success,
    PreflightScriptSummary? Script,
    IReadOnlyList<ScriptDiagnosticDto> Diagnostics,
    string? Error = null);

public sealed record PreflightRunResult(
    bool Success,
    double DurationMs,
    string? Error,
    IReadOnlyList<string> Logs);

/// <summary>
/// Compiles, persists (<c>scripts/preflight/*.cs</c>) and runs preflight C# scripts that execute before a load test
/// starts. Only if the preflight completes without throwing / failing will the load scenarios begin.
/// </summary>
public sealed class PreflightStore
{
    private readonly string directory;
    private readonly EnvironmentStore? environments;
    private readonly Lock sync = new();
    private readonly Dictionary<string, StoredPreflight> scripts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ScriptOptions DefaultOptions = CreateScriptOptions();

    private sealed record StoredPreflight(
        string ScriptId,
        string FileName,
        string Name,
        string Code,
        Script<object> Compiled,
        DateTimeOffset UpdatedUtc);

    public PreflightStore(IConfiguration configuration, IWebHostEnvironment env, EnvironmentStore environments)
    {
        var configured = configuration["Scripts:Directory"];
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "scripts")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
        directory = Path.Combine(root, "preflight");
        this.environments = environments;
    }

    /// <summary>Convenience constructor for unit tests.</summary>
    public PreflightStore(string directory, EnvironmentStore? environments = null)
    {
        this.directory = directory;
        this.environments = environments;
    }

    public async Task LoadAllFromDiskAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs").OrderBy(f => f))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var code = await File.ReadAllTextAsync(file, ct);
            await CompileAndSaveAsync(id, Path.GetFileName(file), code, persist: false, ct);
        }
    }

    public IReadOnlyList<PreflightScriptSummary> List()
    {
        lock (sync)
            return scripts.Values
                .OrderBy(s => s.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(ToSummary)
                .ToList();
    }

    public PreflightScriptSummary? Get(string id)
    {
        lock (sync)
            return scripts.TryGetValue(id, out var s) ? ToSummary(s) : null;
    }

    public async Task<PreflightCompileResult> CompileAndSaveAsync(
        string? scriptId,
        string? fileName,
        string code,
        bool persist = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new PreflightCompileResult(false, null, [], "Preflight script code cannot be empty.");

        var script = CSharpScript.Create<object>(code, DefaultOptions, typeof(PreflightGlobals));
        var diagnostics = script.Compile(ct);
        var mapped = diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(ToDiagnosticDto)
            .ToList();

        if (mapped.Any(d => d.Severity == "Error"))
            return new PreflightCompileResult(false, null, mapped, "Compilation failed.");

        var baseName = string.IsNullOrWhiteSpace(fileName)
            ? (string.IsNullOrWhiteSpace(scriptId) ? "preflight.cs" : $"{scriptId}.cs")
            : Path.GetFileName(fileName.Trim());
        if (!baseName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            baseName += ".cs";

        var id = SanitizeId(string.IsNullOrWhiteSpace(scriptId) ? Path.GetFileNameWithoutExtension(baseName) : scriptId);
        var displayName = Path.GetFileNameWithoutExtension(baseName).Replace('-', ' ').Replace('_', ' ');
        var updated = DateTimeOffset.UtcNow;

        if (persist)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{id}.cs"), code, ct);
        }

        var stored = new StoredPreflight(id, baseName, displayName, code, script, updated);
        lock (sync) scripts[id] = stored;

        return new PreflightCompileResult(true, ToSummary(stored), mapped);
    }

    public bool Delete(string scriptId)
    {
        lock (sync)
        {
            if (!scripts.Remove(scriptId)) return false;
        }
        var path = Path.Combine(directory, $"{SanitizeId(scriptId)}.cs");
        if (File.Exists(path)) File.Delete(path);
        return true;
    }

    public async Task<PreflightRunResult> ExecuteAsync(
        string scriptId,
        string? environmentName,
        Action<string>? onLog = null,
        int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        StoredPreflight? stored;
        lock (sync) scripts.TryGetValue(scriptId, out stored);
        if (stored is null)
            throw new KeyNotFoundException($"Preflight script '{scriptId}' was not found.");

        EnvironmentDefinition? envDef = null;
        if (environments is not null && !string.IsNullOrWhiteSpace(environmentName))
            envDef = await environments.GetAsync(environmentName, ct);
        var envValues = envDef?.ToValues() ?? EnvironmentValues.None;

        var logs = new ConcurrentQueue<string>();
        var sw = Stopwatch.StartNew();

        void Emit(string level, string message)
        {
            var line = $"[{sw.Elapsed.TotalSeconds,6:0.00}s] [{level}] {message}";
            logs.Enqueue(line);
            onLog?.Invoke(line);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 300)) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Beesharp-Preflight/1.0");
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600)));

        var logger = new CallbackLogger(Emit);
        var globals = new PreflightGlobals
        {
            Http = http,
            Logger = logger,
            Env = envValues,
            CancellationToken = runCts.Token,
            Log = msg => Emit("INF", msg),
            Fail = reason => throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason) ? "Preflight failed." : reason),
            RunProcessAsync = (file, args) => RunExternalProcessAsync(file, args, Emit, runCts.Token),
        };

        Emit("INF", $"Starting preflight '{stored.FileName}'…");
        try
        {
            await stored.Compiled.RunAsync(globals, runCts.Token);
            sw.Stop();
            Emit("INF", $"Preflight finished successfully in {sw.Elapsed.TotalMilliseconds:0.#} ms.");
            return new PreflightRunResult(true, sw.Elapsed.TotalMilliseconds, null, logs.ToArray());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            Emit("WRN", "Preflight was cancelled.");
            return new PreflightRunResult(false, sw.Elapsed.TotalMilliseconds, "Preflight was cancelled.", logs.ToArray());
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var msg = $"Preflight timed out after {timeoutSeconds}s.";
            Emit("ERR", msg);
            return new PreflightRunResult(false, sw.Elapsed.TotalMilliseconds, msg, logs.ToArray());
        }
        catch (Exception ex)
        {
            sw.Stop();
            var msg = ex.InnerException?.Message ?? ex.Message;
            Emit("ERR", msg);
            return new PreflightRunResult(false, sw.Elapsed.TotalMilliseconds, msg, logs.ToArray());
        }
    }

    private static async Task<int> RunExternalProcessAsync(string fileName, string arguments, Action<string, string> emit, CancellationToken ct)
    {
        emit("INF", $"> {fileName} {arguments}".TrimEnd());
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        proc.Start();

        var outTask = Task.Run(async () =>
        {
            while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
                emit("OUT", line);
        }, ct);
        var errTask = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync(ct) is { } line)
                emit("ERR", line);
        }, ct);

        try
        {
            await proc.WaitForExitAsync(ct);
            await Task.WhenAll(outTask, errTask);
            emit("INF", $"Process exited with code {proc.ExitCode}.");
            return proc.ExitCode;
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    private static PreflightScriptSummary ToSummary(StoredPreflight s) =>
        new(s.ScriptId, s.FileName, s.Name, s.Code, s.UpdatedUtc);

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
        return clean.Length == 0 ? $"preflight-{Guid.NewGuid():N}"[..18] : clean;
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
            typeof(JsonSerializer).Assembly,
            typeof(Process).Assembly,
            typeof(ILogger).Assembly,
            typeof(EnvironmentValues).Assembly,
        }.Distinct();

        return ScriptOptions.Default
            .AddReferences(references)
            .AddImports(
                "System",
                "System.Collections.Generic",
                "System.Diagnostics",
                "System.IO",
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
                "PerformanceTool.Services.Environments");
    }

    private sealed class CallbackLogger(Action<string, string> emit) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var lvl = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Warning => "WRN",
                LogLevel.Error or LogLevel.Critical => "ERR",
                _ => "INF",
            };
            var msg = formatter(state, exception);
            if (exception is not null) msg = $"{msg} — {exception.Message}";
            emit(lvl, msg);
        }
    }
}
