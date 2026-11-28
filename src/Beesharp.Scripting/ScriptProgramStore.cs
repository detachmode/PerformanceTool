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
    /// <summary>Pre-configured HTTP client scoped to the program's execution timeout.</summary>
    public required HttpClient Http { get; init; }
    /// <summary>Logger whose entries are captured into the program's output stream.</summary>
    public required ILogger Logger { get; init; }
    /// <summary>Resolved key-value settings for the active environment, or <see cref="EnvironmentValues.None"/> when none is selected.</summary>
    public required EnvironmentValues Env { get; init; }
    /// <summary>The run bag (brain 0052): runtime data shared by the whole run. The program reads and writes it; the Environment tab shows it.</summary>
    public required BagAccess Data { get; init; }
    /// <summary>Token triggered if the program is cancelled or exceeds its execution timeout.</summary>
    public required CancellationToken CancellationToken { get; init; }
    /// <summary>Writes an informational log line to the program's execution log.</summary>
    public required Action<string> Log { get; init; }
    /// <summary>Aborts the program immediately with the specified failure reason.</summary>
    public required Action<string> Fail { get; init; }
    /// <summary>Runs an external process (<c>fileName</c>, <c>arguments</c>), streaming stdout and stderr to the program log and returning its exit code.</summary>
    public required Func<string, string, Task<int>> RunProcessAsync { get; init; }
}

/// <summary>The two kinds of program that run around the scenarios (brain 0052): the preflight before them, the shutdown program after them.</summary>
public enum ScriptProgramKind
{
    /// <summary>Runs before scenarios start; scenarios only begin if the preflight completes without throwing or failing.</summary>
    Preflight,
    /// <summary>Runs after scenarios finish (including after a stop or a failed preflight).</summary>
    Shutdown
}

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
        Script<object>? CompiledScript,
        IRunProgram? CompiledProgram,
        DateTimeOffset UpdatedUtc);

    /// <summary>Initializes the program store for a specific program kind, backing folder, and optional compiled programs.</summary>
    /// <param name="directory">The folder of this kind's <c>.cs</c> files (e.g. <c>scripts/preflight</c>).</param>
    /// <param name="environments">Optional environment store used to resolve environment values during execution.</param>
    /// <param name="kind">Whether this store manages preflight or shutdown programs.</param>
    /// <param name="compiled">Optional compiled <see cref="IRunProgram"/> instances registered at startup.</param>
    protected ScriptProgramStore(
        string directory,
        EnvironmentStore? environments,
        ScriptProgramKind kind,
        IEnumerable<IRunProgram>? compiled = null)
    {
        this.kind = kind;
        (label, writerKind) = kind == ScriptProgramKind.Shutdown ? ("Shutdown program", DataWriterKind.Shutdown) : ("Preflight", DataWriterKind.Preflight);
        folder = new ScriptFolder(directory, includeLegacyCsx: false);
        this.environments = environments;

        if (compiled is not null)
        {
            var loadedAt = DateTimeOffset.UtcNow;
            foreach (var program in compiled)
            {
                if (string.IsNullOrWhiteSpace(program.Id))
                    throw new InvalidOperationException($"Compiled {label.ToLowerInvariant()} '{program.GetType().FullName}' must have a non-empty Id.");
                var type = program.GetType();
                var (resolvedFile, resolvedCode, _) = CompiledSourceResolver.ResolveType(type);
                var stored = new StoredProgram(
                    program.Id,
                    resolvedFile,
                    program.Name,
                    resolvedCode,
                    CompiledScript: null,
                    CompiledProgram: program,
                    loadedAt);
                if (!scripts.TryAdd(program.Id, stored))
                    throw new InvalidOperationException($"Duplicate {label.ToLowerInvariant()} id '{program.Id}'.");
            }
        }
    }

    /// <summary>Compiles every file of this kind. Returns the files that did not compile (empty when all went well), so a caller can report them.</summary>
    /// <param name="ct">Token to cancel reading or compiling files from disk.</param>
    /// <returns>The list of files that failed to compile, or an empty list if every file succeeded.</returns>
    public async Task<IReadOnlyList<ScriptLoadProblem>> LoadAllFromDiskAsync(CancellationToken ct = default)
    {
        var problems = new List<ScriptLoadProblem>();
        foreach (var file in await folder.ReadAllAsync(ct))
        {
            var res = await CompileAndSaveAsync(file.Id, file.FileName, file.Code, persist: false, ct);
            if (!res.Success) problems.Add(ScriptLoadProblem.From(label, file.FileName, res.Diagnostics, res.Error));
        }
        return problems;
    }

    /// <summary>Returns summaries of all loaded and compiled programs in this store, ordered by file name.</summary>
    /// <returns>A read-only list of program summaries.</returns>
    public IReadOnlyList<PreflightScriptSummary> List()
    {
        lock (sync)
            return scripts.Values
                .OrderBy(s => s.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(ToSummary)
                .ToList();
    }

    /// <summary>Gets the summary for the program with the specified identifier, or <see langword="null"/> if not found.</summary>
    /// <param name="id">The program script identifier.</param>
    /// <returns>The matching <see cref="PreflightScriptSummary"/>, or <see langword="null"/> if no program has that id.</returns>
    public PreflightScriptSummary? Get(string id)
    {
        lock (sync)
            return scripts.TryGetValue(id, out var s) ? ToSummary(s) : null;
    }

    /// <summary>Compiles the supplied program C# code, stores it in memory, and optionally saves it to disk.</summary>
    /// <param name="scriptId">Optional identifier for the script; derived from <paramref name="fileName"/> when omitted.</param>
    /// <param name="fileName">Optional file name shown in diagnostics and summaries.</param>
    /// <param name="code">The C# program source code to compile.</param>
    /// <param name="persist">When <see langword="true"/>, writes the source file to disk after a successful compile.</param>
    /// <param name="ct">Token to cancel compilation or file persistence.</param>
    /// <returns>A <see cref="PreflightCompileResult"/> containing compilation status, diagnostics, and the stored summary on success.</returns>
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

        var stored = new StoredProgram(id, baseName, displayName, code, script, CompiledProgram: null, updated);
        lock (sync) scripts[id] = stored;

        return new PreflightCompileResult(true, ToSummary(stored), mapped);
    }

    /// <summary>Removes a script-backed program from memory and deletes its file from disk. Compiled C# class programs cannot be deleted.</summary>
    /// <param name="scriptId">The program identifier to remove.</param>
    /// <returns><see langword="true"/> if a script program was found and deleted; otherwise <see langword="false"/>.</returns>
    public bool Delete(string scriptId)
    {
        lock (sync)
        {
            if (!scripts.TryGetValue(scriptId, out var existing) || existing.CompiledProgram is not null)
                return false;
            scripts.Remove(scriptId);
        }
        folder.Delete(ScriptNames.SanitizeId(scriptId));
        return true;
    }

    /// <summary>Executes the specified preflight or shutdown program against an optional environment and run data bag.</summary>
    /// <param name="scriptId">The identifier of the program to execute.</param>
    /// <param name="environmentName">Optional environment name used to supply <see cref="EnvironmentValues"/>.</param>
    /// <param name="onLog">Optional callback invoked for each formatted log line as it is emitted.</param>
    /// <param name="timeoutSeconds">Execution timeout in seconds (clamped between 5 and 600).</param>
    /// <param name="ct">Token to cancel program execution.</param>
    /// <param name="runData">Optional shared <see cref="RunData"/> whose run bag is exposed via <see cref="PreflightGlobals.Data"/>.</param>
    /// <returns>A <see cref="PreflightRunResult"/> with success status, duration, optional error message, and captured log lines.</returns>
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
        var data = (runData ?? new RunData(0)).RunBagFor(new DataWriter(writerKind));
        Action<string> log = msg => Emit("INF", msg);
        Action<string> fail = reason => throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason) ? $"{label} failed." : reason);
        Func<string, string, Task<int>> runProcessAsync = (file, args) => RunExternalProcessAsync(file, args, Emit, runCts.Token);

        Emit("INF", $"Starting {label.ToLowerInvariant()} '{stored.FileName}'…");
        try
        {
            if (stored.CompiledProgram is not null)
            {
                var context = new ProgramContext
                {
                    Http = http,
                    Logger = logger,
                    Environment = envValues,
                    Data = data,
                    CancellationToken = runCts.Token,
                    Log = log,
                    Fail = fail,
                    RunProcessAsync = runProcessAsync,
                };
                await stored.CompiledProgram.ExecuteAsync(context, runCts.Token);
            }
            else
            {
                var globals = new PreflightGlobals
                {
                    Http = http,
                    Logger = logger,
                    Env = envValues,
                    Data = data,
                    CancellationToken = runCts.Token,
                    Log = log,
                    Fail = fail,
                    RunProcessAsync = runProcessAsync,
                };
                await stored.CompiledScript!.RunAsync(globals, runCts.Token);
            }
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
        new(s.ScriptId, s.FileName, s.Name, s.Code, s.UpdatedUtc, IsCompiled: s.CompiledProgram is not null);

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

/// <summary>The preflight (brain 0042): runs before the scenarios; they only start if it succeeds. Supports compiled <see cref="IPreflightProgram"/> classes and <c>scripts/preflight/*.cs</c>.</summary>
/// <param name="directory">The folder containing preflight <c>.cs</c> files (typically <c>scripts/preflight</c>).</param>
/// <param name="environments">Optional environment store used to resolve environment values during execution.</param>
/// <param name="compiled">Optional compiled <see cref="IPreflightProgram"/> implementations registered at startup.</param>
public sealed class PreflightStore(
    string directory,
    EnvironmentStore? environments = null,
    IEnumerable<IPreflightProgram>? compiled = null)
    : ScriptProgramStore(directory, environments, ScriptProgramKind.Preflight, compiled);

/// <summary>The shutdown program (brain 0052): runs at the end of a run. Supports compiled <see cref="IShutdownProgram"/> classes and <c>scripts/shutdown/*.cs</c>.</summary>
/// <param name="directory">The folder containing shutdown <c>.cs</c> files (typically <c>scripts/shutdown</c>).</param>
/// <param name="environments">Optional environment store used to resolve environment values during execution.</param>
/// <param name="compiled">Optional compiled <see cref="IShutdownProgram"/> implementations registered at startup.</param>
public sealed class ShutdownStore(
    string directory,
    EnvironmentStore? environments = null,
    IEnumerable<IShutdownProgram>? compiled = null)
    : ScriptProgramStore(directory, environments, ScriptProgramKind.Shutdown, compiled);
