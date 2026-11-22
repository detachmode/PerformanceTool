using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services.Scenarios;

/// <summary>
/// A load-test scenario defined in code or C# script. One execution = one bar in the live chart.
/// Derive from <see cref="Scenario{TParameters}"/> for compiled classes, or use <c>Scenario.Define&lt;T&gt;(...)</c>
/// in C# scripts. Lifecycle hooks (<see cref="StartupAsync"/> / <see cref="TeardownAsync"/>) run once per scenario
/// entry per load run (and around single test runs). See docs/brain/0003, 0007 and 0040.
/// </summary>
public interface IScenario
{
    /// <summary>Stable identifier. Stored in presets, so never rename it once released.</summary>
    string Id { get; }
    string Name { get; }
    string Description { get; }

    /// <summary>Editor, validation and schema model generated from the parameter type (or JSON Schema on the client).</summary>
    ParameterSpec Spec { get; }

    /// <summary>
    /// Lifecycle hook called once per scenario entry before any bees start.
    /// Use <see cref="ScenarioContext.State"/> and <see cref="ScenarioContext.RunBackground"/> to share state or
    /// run background jobs (e.g. token refresh) across all bees of this entry.
    /// </summary>
    Task StartupAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs once per bee. <paramref name="parameters"/> is an instance of <see cref="ParameterSpec.Type"/>.</summary>
    Task<ScenarioOutcome> ExecuteAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Lifecycle hook called once per scenario entry after all bees finish and background jobs are cancelled.
    /// Always runs in a <c>finally</c> block even if bees fail or the run is stopped.
    /// </summary>
    Task TeardownAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Base class for scenarios with typed parameters. <typeparamref name="TParameters"/> is a plain class or positional
/// record whose public properties become editor fields (see docs/brain/0007): property initialisers / constructor defaults
/// are the defaults, and DataAnnotations (<c>[Display]</c>, <c>[Description]</c>, <c>[Required]</c>, <c>[Range]</c>,
/// <c>[MinLength]</c>) plus <see cref="HttpUrlAttribute"/>, <see cref="MultilineAttribute"/> and <see cref="SecretAttribute"/>
/// shape the editor and validation. The same instance is shared by concurrent executions: treat it as read-only.
/// </summary>
public abstract class Scenario<TParameters> : IScenario where TParameters : class
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }

    public ParameterSpec Spec { get; } = ParameterSpec.For(typeof(TParameters));

    Task IScenario.StartupAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        StartupAsync((TParameters)parameters, context, cancellationToken);

    Task<ScenarioOutcome> IScenario.ExecuteAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        ExecuteAsync((TParameters)parameters, context, cancellationToken);

    Task IScenario.TeardownAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        TeardownAsync((TParameters)parameters, context, cancellationToken);

    protected virtual Task StartupAsync(TParameters parameters, ScenarioContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    protected abstract Task<ScenarioOutcome> ExecuteAsync(TParameters parameters, ScenarioContext context, CancellationToken cancellationToken);

    protected virtual Task TeardownAsync(TParameters parameters, ScenarioContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Result of one execution. Timing is measured by the runner unless <see cref="Measured"/> is set.</summary>
public sealed record ScenarioOutcome(int? StatusCode, long Bytes, string? Error = null)
{
    /// <summary>When set, success requires exactly this status code instead of any 2xx/3xx.</summary>
    public int? ExpectedStatus { get; init; }

    /// <summary>Overrides the runner's wall-clock time, e.g. to exclude think time between steps.</summary>
    public TimeSpan? Measured { get; init; }

    public bool Success => Error is null && (ExpectedStatus is { } expected ? StatusCode == expected : StatusCode is >= 200 and < 400);

    public string? FailureReason => Success ? null
        : Error ?? (ExpectedStatus is { } expected ? $"Expected HTTP {expected}, got {StatusCode}" : $"HTTP {StatusCode}");

    public ScenarioOutcome Expect(int? status) => this with { ExpectedStatus = status };

    public static ScenarioOutcome Failed(string error, int? statusCode = null) => new(statusCode, 0, error);
}

/// <summary>
/// Tracks background tasks started by a scenario entry's <see cref="IScenario.StartupAsync"/> hook
/// (for example, a periodic token-refresh loop) and cancels/awaits them cleanly before <see cref="IScenario.TeardownAsync"/>.
/// </summary>
public sealed class ScenarioBackgroundHost : IAsyncDisposable
{
    private readonly CancellationTokenSource cts;
    private readonly List<Task> tasks = [];
    private int disposed;

    public ScenarioBackgroundHost(CancellationToken runStoppingToken = default) =>
        cts = CancellationTokenSource.CreateLinkedTokenSource(runStoppingToken);

    public CancellationToken Token => cts.Token;

    public void Run(Func<CancellationToken, Task> work)
    {
        var task = Task.Run(() => work(cts.Token), CancellationToken.None);
        lock (tasks) tasks.Add(task);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await cts.CancelAsync();
        Task[] snapshot;
        lock (tasks) snapshot = [.. tasks];
        foreach (var t in snapshot)
        {
            try { await t; }
            catch (OperationCanceledException) { }
            catch { }
        }
        cts.Dispose();
    }
}

/// <summary>Shared services for one execution or lifecycle hook.</summary>
public sealed class ScenarioContext
{
    private readonly ScenarioBackgroundHost? background;
    private readonly Action<RecordedCall>? callSink;
    private readonly Action<RecordedLog>? logSink;
    private readonly Func<double> runSeconds;
    private readonly Stopwatch beeClock;
    private readonly Func<bool>? runStopped;
    private int currentCallIndex = -1;

    public ScenarioContext(
        HttpClient http,
        EnvironmentValues? environment = null,
        CancellationToken cancellationToken = default,
        BagAccess? state = null,
        ScenarioBackgroundHost? background = null,
        Action<string>? logSink = null,
        Action<RecordedCall>? callSink = null,
        Action<RecordedLog>? structuredLogSink = null,
        Func<double>? runSeconds = null,
        BagAccess? runBag = null,
        Func<bool>? runStopped = null)
    {
        this.runStopped = runStopped;
        Http = http;
        Environment = environment ?? EnvironmentValues.None;
        CancellationToken = cancellationToken;
        State = state ?? new BagAccess(new DataBag(), new DataWriter(DataWriterKind.Bee));
        RunBag = runBag ?? new BagAccess(new DataBag(), new DataWriter(DataWriterKind.Bee));
        this.background = background;
        this.callSink = callSink;
        beeClock = Stopwatch.StartNew();
        this.runSeconds = runSeconds ?? (() => beeClock.Elapsed.TotalSeconds);

        if (structuredLogSink is not null)
        {
            this.logSink = structuredLogSink;
        }
        else if (logSink is not null)
        {
            this.logSink = entry =>
            {
                var line = entry.Exception is null
                    ? $"[{entry.Level}] {entry.Message}"
                    : $"[{entry.Level}] {entry.Message} ({entry.Exception})";
                logSink(line);
            };
        }

        Logger = new ScenarioContextLogger(this);
    }

    /// <summary>
    /// The active environment's values (brain 0033), e.g. <c>context.Environment["BaseUrl"]</c>. Placeholders in parameter values are already
    /// filled in when the run starts; use this when code needs a value that is not a parameter. Empty when no environment is active.
    /// </summary>
    public EnvironmentValues Environment { get; }

    /// <summary>Shared by the whole run. Don't dispose it or change its default headers.</summary>
    public HttpClient Http { get; }

    /// <summary>Alias for <see cref="Http"/> (for C# scripts using <c>ctx.HttpClient</c>).</summary>
    public HttpClient HttpClient => Http;

    /// <summary>Cancellation token for this execution (carries the per-bee timeout and run cancellation).</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// The <b>entry bag</b> (brain 0052): thread-safe data shared across <c>StartupAsync</c>, all <c>ExecuteAsync</c> bees of this scenario entry,
    /// and <c>TeardownAsync</c>. Ideal for sharing refreshed auth tokens or pre-fetched seed data. Private to this entry; the UI shows it on the Environment tab.
    /// </summary>
    public BagAccess State { get; }

    /// <summary>
    /// The <b>run bag</b> (brain 0052): thread-safe data shared by the whole run: the preflight program, every scenario entry (startup, bees, teardown)
    /// and the shutdown program, all reading and writing. The last writer of each key is recorded and shown on the Environment tab.
    /// </summary>
    public BagAccess RunBag { get; }

    /// <summary>
    /// Per-bee <see cref="ILogger"/> (brain 0012): log lines are attached to this bee and shown in test runs,
    /// the scenario details popup and the Logs popup.
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Starts a background job scoped to the scenario entry's lifetime (e.g. a periodic token refresher started in
    /// <c>StartupAsync</c>). Automatically cancelled and awaited before <c>TeardownAsync</c>.
    /// </summary>
    public void RunBackground(Func<CancellationToken, Task> job)
    {
        if (background is null)
            throw new InvalidOperationException("RunBackground can only be called during StartupAsync.");
        background.Run(job);
    }

    /// <summary>Writes an Information log line captured by test runs and run logs.</summary>
    public void Log(string message) => Logger.LogInformation("{Message}", message);

    /// <summary>Records one call/step inside this bee (for multi-step scenarios or simulated calls without HTTP).</summary>
    public void RecordCall(string name, double durationMs, int? statusCode = 200, long bytes = 0, string? error = null, double? offsetMs = null)
    {
        var off = offsetMs ?? Math.Max(0, beeClock.Elapsed.TotalMilliseconds - durationMs);
        callSink?.Invoke(new RecordedCall(name, off, durationMs, statusCode, bytes, error));
    }

    /// <summary>Measures an async step as a named call, recording its offset, duration and any failure.</summary>
    public async Task MeasureCallAsync(string name, Func<Task> work, int? statusCode = 200, long bytes = 0)
    {
        var idx = Interlocked.Increment(ref currentCallIndex);
        var offsetMs = beeClock.Elapsed.TotalMilliseconds;
        var sw = Stopwatch.StartNew();
        try
        {
            await work();
            sw.Stop();
            callSink?.Invoke(new RecordedCall(name, offsetMs, sw.Elapsed.TotalMilliseconds, statusCode, bytes));
        }
        catch (Exception ex)
        {
            sw.Stop();
            // A call cut off because the user stopped the run is kept as failed with the time it ran until the stop (brain 0053).
            var error = ex is OperationCanceledException && runStopped?.Invoke() == true ? ExecutionResult.CanceledError : ex.Message;
            callSink?.Invoke(new RecordedCall(name, offsetMs, sw.Elapsed.TotalMilliseconds, null, 0, error));
            throw;
        }
        finally
        {
            _ = idx;
        }
    }

    internal void EmitLog(LogLevel level, string message, Exception? exception)
    {
        if (logSink is null) return;
        var levelName = level switch
        {
            LogLevel.Trace => "Trace",
            LogLevel.Debug => "Debug",
            LogLevel.Information => "Information",
            LogLevel.Warning => "Warning",
            LogLevel.Error or LogLevel.Critical => "Error",
            _ => "Information",
        };
        int? callIdx = currentCallIndex >= 0 ? currentCallIndex : null;
        logSink(new RecordedLog(runSeconds(), levelName, message, exception?.Message, callIdx));
    }

    /// <summary>Sends a request, reads the whole body (so transfer time is included) and maps it to an outcome.</summary>
    public async Task<ScenarioOutcome> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            return new ScenarioOutcome((int)response.StatusCode, body.LongLength);
        }
    }

    public static bool IsHttpUrl([NotNullWhen(true)] string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private sealed class ScenarioContextLogger(ScenarioContext owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.EmitLog(logLevel, formatter(state, exception), exception);
        }
    }
}
