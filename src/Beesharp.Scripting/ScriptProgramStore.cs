using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Logging;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;

namespace Beesharp.Services;

/// <summary>Globals injected into every C# preflight and shutdown script.</summary>
public sealed class PreflightGlobals
{
    public required HttpClient Http { get; init; }
    public required ILogger Logger { get; init; }
    public required EnvironmentValues Env { get; init; }
    /// <summary>The run bag (brain 0052): runtime data shared by the whole run. The program reads and writes it; the Environment tab shows it.</summary>
    public required BagAccess Data { get; init; }
    public required CancellationToken CancellationToken { get; init; }
    public required Action<string> Log { get; init; }
    public required Action<string> Fail { get; init; }
    public required Func<string, string, Task<int>> RunProcessAsync { get; init; }
}

/// <summary>The two kinds of program that run around the scenarios (brain 0052): the preflight before them, the shutdown program after them.</summary>
public enum ScriptProgramKind { Preflight, Shutdown }

/// <summary>
/// Compiles, persists (<c>scripts/preflight/*.cs</c>, <c>scripts/shutdown/*.cs</c>) and runs C# programs that execute around a load test:
/// the preflight before the scenarios start (they only begin if it completes without throwing or failing) and the shutdown program at the end of the run.
/// <see cref="PreflightStore"/> and <see cref="ShutdownStore"/> only choose the kind; compiling, ids and files are shared with the scenario scripts
/// (<see cref="ScriptCompiler"/>, <see cref="ScriptNames"/>, <see cref="ScriptFolder"/>).
/// </summary>
public abstract class ScriptProgramStore
{
    private readonly ScriptFolder folder;
    private readonly ScriptProgramKind kind;
    private readonly string label;
    private readonly DataWriterKind writerKind;
    private readonly EnvironmentStore? environments;
    private readonly Lock sync = new();
    private readonly Dictionary<string, StoredProgram> scripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>References and imports of preflights and shutdown programs, on top of the common ones.</summary>
    private static readonly ScriptOptions ProgramOptions = ScriptCompiler.CreateOptions(
        extraReferences: [typeof(Process).Assembly],
        extraImports: ["System.Diagnostics", "System.IO", "Beesharp.Services.Runtime"]);

    private sealed record StoredProgram(
        string ScriptId,
        string FileName,
        string Name,
        string Code,
        Script<object> Compiled,
        DateTimeOffset UpdatedUtc);

    /// <param name="directory">The folder of this kind's <c>.cs</c> files (e.g. <c>scripts/preflight</c>).</param>
    protected ScriptProgramStore(string directory, EnvironmentStore? environments, ScriptProgramKind kind)
    {
        this.kind = kind;
        (label, writerKind) = kind == ScriptProgramKind.Shutdown ? ("Shutdown program", DataWriterKind.Shutdown) : ("Preflight", DataWriterKind.Preflight);
        folder = new ScriptFolder(directory, includeLegacyCsx: false);
        this.environments = environments;
    }

    public async Task LoadAllFromDiskAsync(CancellationToken ct = default)
    {
        foreach (var file in await folder.ReadAllAsync(ct))
            await CompileAndSaveAsync(file.Id, file.FileName, file.Code, persist: false, ct);
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
            return new PreflightCompileResult(false, null, [], $"{label} script code cannot be empty.");

        var defaultName = kind == ScriptProgramKind.Shutdown ? "shutdown.cs" : "preflight.cs";
        var baseName = ScriptNames.FileName(fileName, scriptId, defaultName, allowCsx: false);
        var (script, mapped) = ScriptCompiler.Compile(code, baseName, ProgramOptions, typeof(PreflightGlobals), ct);

        if (script is null)
            return new PreflightCompileResult(false, null, mapped, "Compilation failed.");

        var id = ScriptNames.SanitizeId(string.IsNullOrWhiteSpace(scriptId) ? Path.GetFileNameWithoutExtension(baseName) : scriptId);
        var displayName = Path.GetFileNameWithoutExtension(baseName).Replace('-', ' ').Replace('_', ' ');
        var updated = DateTimeOffset.UtcNow;

        if (persist)
            await folder.SaveAsync(id, code, ct);

        var stored = new StoredProgram(id, baseName, displayName, code, script, updated);
        lock (sync) scripts[id] = stored;

        return new PreflightCompileResult(true, ToSummary(stored), mapped);
    }

    public bool Delete(string scriptId)
    {
        lock (sync)
        {
            if (!scripts.Remove(scriptId)) return false;
        }
        folder.Delete(ScriptNames.SanitizeId(scriptId));
        return true;
    }

    public async Task<PreflightRunResult> ExecuteAsync(
        string scriptId,
        string? environmentName,
        Action<string>? onLog = null,
        int timeoutSeconds = 120,
        CancellationToken ct = default,
        RunData? runData = null)
    {
        StoredProgram? stored;
        lock (sync) scripts.TryGetValue(scriptId, out stored);
        if (stored is null)
            throw new KeyNotFoundException($"{label} script '{scriptId}' was not found.");

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
        http.DefaultRequestHeaders.UserAgent.ParseAdd(kind == ScriptProgramKind.Shutdown ? "Beesharp-Shutdown/1.0" : "Beesharp-Preflight/1.0");
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600)));

        var logger = new CallbackLogger(Emit);
        var globals = new PreflightGlobals
        {
            Http = http,
            Logger = logger,
            Env = envValues,
            Data = (runData ?? new RunData(0)).RunBagFor(new DataWriter(writerKind)),
            CancellationToken = runCts.Token,
            Log = msg => Emit("INF", msg),
            Fail = reason => throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason) ? $"{label} failed." : reason),
            RunProcessAsync = (file, args) => RunExternalProcessAsync(file, args, Emit, runCts.Token),
        };

        Emit("INF", $"Starting {label.ToLowerInvariant()} '{stored.FileName}'…");
        try
        {
            await stored.Compiled.RunAsync(globals, runCts.Token);
            sw.Stop();
            Emit("INF", $"{label} finished successfully in {sw.Elapsed.TotalMilliseconds:0.#} ms.");
            return new PreflightRunResult(true, sw.Elapsed.TotalMilliseconds, null, logs.ToArray());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            Emit("WRN", $"{label} was cancelled.");
            return new PreflightRunResult(false, sw.Elapsed.TotalMilliseconds, $"{label} was cancelled.", logs.ToArray());
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var msg = $"{label} timed out after {timeoutSeconds}s.";
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

    private static PreflightScriptSummary ToSummary(StoredProgram s) =>
        new(s.ScriptId, s.FileName, s.Name, s.Code, s.UpdatedUtc);

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

/// <summary>The preflight (brain 0042): runs before the scenarios; they only start if it succeeds. Stored in <c>scripts/preflight/*.cs</c>.</summary>
public sealed class PreflightStore(string directory, EnvironmentStore? environments = null)
    : ScriptProgramStore(directory, environments, ScriptProgramKind.Preflight);

/// <summary>The shutdown program (brain 0052): the same kind of C# script, stored in <c>scripts/shutdown/*.cs</c>. It always runs at the end of a run.</summary>
public sealed class ShutdownStore(string directory, EnvironmentStore? environments = null)
    : ScriptProgramStore(directory, environments, ScriptProgramKind.Shutdown);
