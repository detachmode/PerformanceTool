using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios.Spec;
using Beesharp.Services.Tracing;

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

    /// <summary>Human-readable display name shown in the scenario picker and overview cards.</summary>
    string Name { get; }

    /// <summary>Short explanation of what the scenario does, shown in the UI and assistant listings.</summary>
    string Description { get; }

    /// <summary>Editor, validation and schema model generated from the parameter type (or JSON Schema on the client).</summary>
    ParameterSpec Spec { get; }

    /// <summary>Declared steps of this scenario, in order (brain 0056). Empty for scenarios without steps.</summary>
    IReadOnlyList<StepInfo> Steps => [];

    /// <summary>Optional scenario-wide and per-endpoint slow thresholds (brain 0060). Default: automatic <c>2× typical</c>.</summary>
    CallThresholds SlowThresholds => CallThresholds.None;

    /// <summary>
    /// Lifecycle hook called once per scenario entry before any bees start.
    /// Use <see cref="ScenarioContext.State"/> and <see cref="ScenarioContext.RunBackground"/> to share state or
    /// run background jobs (e.g. token refresh) across all bees of this entry.
    /// </summary>
    /// <param name="parameters">Bound parameter object of type <see cref="ParameterSpec.Type"/>.</param>
    /// <param name="context">Shared entry-level context for state, background jobs, HTTP, and logging.</param>
    /// <param name="cancellationToken">Token cancelled if the run stops or aborts during startup.</param>
    /// <returns>A task representing the asynchronous startup operation.</returns>
    Task StartupAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs once per bee. <paramref name="parameters"/> is an instance of <see cref="ParameterSpec.Type"/>.</summary>
    /// <param name="parameters">Bound parameter object of type <see cref="ParameterSpec.Type"/>.</param>
    /// <param name="context">Per-bee execution context for HTTP calls, steps, bags, and logging.</param>
    /// <param name="cancellationToken">Token cancelled on per-bee timeout or run stop.</param>
    /// <returns>The <see cref="ScenarioOutcome"/> describing status, transferred bytes, and any error.</returns>
    Task<ScenarioOutcome> ExecuteAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Lifecycle hook called once per scenario entry after all bees finish and background jobs are cancelled.
    /// Always runs in a <c>finally</c> block even if bees fail or the run is stopped.
    /// </summary>
    /// <param name="parameters">Bound parameter object of type <see cref="ParameterSpec.Type"/>.</param>
    /// <param name="context">Shared entry-level context for cleanup and logging.</param>
    /// <param name="cancellationToken">Token signalled if teardown itself is cancelled.</param>
    /// <returns>A task representing the asynchronous teardown operation.</returns>
    Task TeardownAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Base class for scenarios with typed parameters. <typeparamref name="TParameters"/> is a plain class or positional
/// record whose public properties become editor fields (see docs/brain/0007): property initialisers / constructor defaults
/// are the defaults, and DataAnnotations (<c>[Display]</c>, <c>[Description]</c>, <c>[Required]</c>, <c>[Range]</c>,
/// <c>[MinLength]</c>) plus <see cref="HttpUrlAttribute"/>, <see cref="MultilineAttribute"/> and <see cref="SecretAttribute"/>
/// shape the editor and validation. The same instance is shared by concurrent executions: treat it as read-only.
/// </summary>
/// <typeparam name="TParameters">Parameter class or record type whose public properties define the scenario's inputs.</typeparam>
public abstract class Scenario<TParameters> : IScenario where TParameters : class
{
    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public ParameterSpec Spec { get; } = ParameterSpec.For(typeof(TParameters));

    /// <inheritdoc />
    public virtual IReadOnlyList<StepInfo> Steps => [];

    /// <inheritdoc />
    public virtual CallThresholds SlowThresholds => CallThresholds.None;

    Task IScenario.StartupAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        StartupAsync((TParameters)parameters, context, cancellationToken);

    Task<ScenarioOutcome> IScenario.ExecuteAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        ExecuteAsync((TParameters)parameters, context, cancellationToken);

    Task IScenario.TeardownAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        TeardownAsync((TParameters)parameters, context, cancellationToken);

    /// <summary>
    /// Lifecycle hook called once per scenario entry before any bees start.
    /// </summary>
    /// <param name="parameters">Typed scenario parameters for this entry.</param>
    /// <param name="context">Shared entry-level context for state, background jobs, HTTP, and logging.</param>
    /// <param name="cancellationToken">Token cancelled if the run stops or aborts during startup.</param>
    /// <returns>A task representing the asynchronous startup operation.</returns>
    protected virtual Task StartupAsync(TParameters parameters, ScenarioContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Executes a single bee iteration of this scenario.
    /// </summary>
    /// <param name="parameters">Typed scenario parameters (shared across concurrent bees; treat as read-only).</param>
    /// <param name="context">Per-bee execution context for HTTP calls, steps, bags, and logging.</param>
    /// <param name="cancellationToken">Token cancelled on per-bee timeout or run stop.</param>
    /// <returns>The <see cref="ScenarioOutcome"/> describing status, transferred bytes, and any error.</returns>
    protected abstract Task<ScenarioOutcome> ExecuteAsync(TParameters parameters, ScenarioContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Lifecycle hook called once per scenario entry after all bees finish and background jobs are cancelled.
    /// </summary>
    /// <param name="parameters">Typed scenario parameters for this entry.</param>
    /// <param name="context">Shared entry-level context for cleanup and logging.</param>
    /// <param name="cancellationToken">Token signalled if teardown itself is cancelled.</param>
    /// <returns>A task representing the asynchronous teardown operation.</returns>
    protected virtual Task TeardownAsync(TParameters parameters, ScenarioContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Result of one execution. Timing is measured by the runner unless <see cref="Measured"/> is set.</summary>
/// <param name="StatusCode">HTTP status code (or synthetic status code) returned by the execution, if any.</param>
/// <param name="Bytes">Total payload bytes transferred during the execution.</param>
/// <param name="Error">Optional error message indicating failure regardless of status code.</param>
public sealed record ScenarioOutcome(int? StatusCode, long Bytes, string? Error = null)
{
    /// <summary>When set, success requires exactly this status code instead of any 2xx/3xx.</summary>
    public int? ExpectedStatus { get; init; }

    /// <summary>Overrides the runner's wall-clock time, e.g. to exclude think time between steps.</summary>
    public TimeSpan? Measured { get; init; }

    /// <summary>Gets whether the execution succeeded (<see cref="Error"/> is null and <see cref="StatusCode"/> matches <see cref="ExpectedStatus"/> or is 2xx/3xx).</summary>
    public bool Success => Error is null && (ExpectedStatus is { } expected ? StatusCode == expected : StatusCode is >= 200 and < 400);

    /// <summary>Gets a human-readable failure description when <see cref="Success"/> is <c>false</c>, or <c>null</c> on success.</summary>
    public string? FailureReason => Success ? null
        : Error ?? (ExpectedStatus is { } expected ? $"Expected HTTP {expected}, got {StatusCode}" : $"HTTP {StatusCode}");

    /// <summary>Returns a copy of this outcome that requires an exact <paramref name="status"/> code to count as successful.</summary>
    /// <param name="status">Required HTTP status code, or <c>null</c> to revert to default 2xx/3xx matching.</param>
    /// <returns>A new <see cref="ScenarioOutcome"/> with <see cref="ExpectedStatus"/> set.</returns>
    public ScenarioOutcome Expect(int? status) => this with { ExpectedStatus = status };

    /// <summary>Creates a failed outcome with the given <paramref name="error"/> message and optional <paramref name="statusCode"/>.</summary>
    /// <param name="error">Description of the failure.</param>
    /// <param name="statusCode">Optional associated status code.</param>
    /// <returns>A failed <see cref="ScenarioOutcome"/> with zero bytes.</returns>
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

    /// <summary>Creates a background host linked to the run's stopping token.</summary>
    /// <param name="runStoppingToken">Token cancelled when the run stops or finishes.</param>
    public ScenarioBackgroundHost(CancellationToken runStoppingToken = default) =>
        cts = CancellationTokenSource.CreateLinkedTokenSource(runStoppingToken);

    /// <summary>Gets the cancellation token passed to background tasks managed by this host.</summary>
    public CancellationToken Token => cts.Token;

    /// <summary>Starts a background task and tracks it until <see cref="DisposeAsync"/> cancels and awaits all tasks.</summary>
    /// <param name="work">Async delegate receiving the host's cancellation <see cref="Token"/>.</param>
    public void Run(Func<CancellationToken, Task> work)
    {
        var task = Task.Run(() => work(cts.Token), CancellationToken.None);
        lock (tasks) tasks.Add(task);
    }

    /// <summary>Cancels all tracked background tasks and awaits their completion.</summary>
    /// <returns>A <see cref="ValueTask"/> completing after all background jobs have finished.</returns>
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
    private static readonly AsyncLocal<ScenarioContext?> currentContext = new();
    private static readonly AsyncLocal<CallScopeOverride?> currentCallScope = new();

    private readonly ScenarioBackgroundHost? background;
    private readonly Action<RecordedCall>? callSink;
    private readonly Action<RecordedLog>? logSink;
    private readonly Action<RecordedStepVisit>? stepVisitSink;
    private readonly Func<double> runSeconds;
    private readonly Stopwatch beeClock;
    private readonly Func<bool>? runStopped;
    private readonly CallThresholds thresholds;
    private readonly ActorTrace? trace;
    private int currentCallIndex = -1;
    private int currentStep = -1;
    private int currentRep;
    private readonly List<RecordedStepVisit> liveVisits = [];

    /// <summary>The <see cref="ScenarioContext"/> active on the current async flow, used by <see cref="RecordingHttpHandler"/> and <see cref="HttpCallDiagnosticTracker"/>.</summary>
    public static ScenarioContext? Current => currentContext.Value;

    internal CallScopeOverride? CurrentCallScope => currentCallScope.Value;

    /// <summary>Binds this context to <see cref="Current"/> for the duration of the bee so HTTP calls are recorded automatically.</summary>
    /// <returns>An <see cref="IDisposable"/> scope that restores the previous <see cref="Current"/> context when disposed.</returns>
    public IDisposable Activate()
    {
        HttpCallDiagnosticTracker.EnsureInstalled();
        var previous = currentContext.Value;
        currentContext.Value = this;
        return new ActivationScope(previous);
    }

    /// <summary>
    /// Temporarily names or sets a slow threshold for HTTP calls and measured steps executed inside the returned scope
    /// (works for typed clients, Refit, SDKs, or raw <see cref="HttpClient"/> calls, brain 0060).
    /// </summary>
    /// <param name="name">Optional explicit endpoint/call name overriding automatic URI normalization.</param>
    /// <param name="slowAbove">Optional positive slow threshold overriding the scenario/endpoint default.</param>
    /// <returns>An <see cref="IDisposable"/> scope that restores the previous call scope override on disposal.</returns>
    public IDisposable CallScope(string? name = null, TimeSpan? slowAbove = null)
    {
        if (slowAbove is { } s && s <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(slowAbove), "Slow threshold must be greater than zero.");

        var previous = currentCallScope.Value;
        var cleanName = string.IsNullOrWhiteSpace(name) ? previous?.Name : name.Trim();
        var thresholdMs = slowAbove?.TotalMilliseconds ?? previous?.SlowThresholdMs;
        currentCallScope.Value = new CallScopeOverride(cleanName, thresholdMs);
        return new CallScopeGuard(previous);
    }

    private sealed class ActivationScope(ScenarioContext? previous) : IDisposable
    {
        public void Dispose() => currentContext.Value = previous;
    }

    private sealed class CallScopeGuard(CallScopeOverride? previous) : IDisposable
    {
        public void Dispose() => currentCallScope.Value = previous;
    }

    /// <summary>Zero-based index of the step this bee is currently executing, or -1 before the first step (or when the scenario has no steps).</summary>
    public int CurrentStep => Volatile.Read(ref currentStep);

    /// <summary>Zero-based repetition/visit index of the current step (0 on the first visit, 1 on the second, ...).</summary>
    public int CurrentRep => Volatile.Read(ref currentRep);

    internal double ElapsedMs => beeClock.Elapsed.TotalMilliseconds;
    internal bool IsRunStopped => runStopped?.Invoke() == true;

    /// <summary>The distributed trace of this actor (brain 0062), or <see langword="null"/> when the context was created without one (startup and teardown hooks, test runs).</summary>
    public ActorTrace? Trace => trace;

    /// <summary>W3C trace ID of this actor's trace: the ID every call of the actor sends in its <c>traceparent</c> (brain 0062). <see langword="null"/> without a trace.</summary>
    public string? TraceId => trace?.TraceId;

    internal void EnterStep(int step, int rep, string? dataJson = null)
    {
        Volatile.Write(ref currentStep, step);
        Volatile.Write(ref currentRep, rep);
        trace?.EnterStep(step, rep);
        var visit = new RecordedStepVisit(step, rep, dataJson);
        lock (liveVisits) liveVisits.Add(visit);
        stepVisitSink?.Invoke(visit);
    }

    /// <summary>Thread-safe snapshot of the chronological step visits executed so far by this bee (for live inspection).</summary>
    /// <returns>A snapshot list of recorded step visits, or <c>null</c> if no step has been entered yet.</returns>
    public IReadOnlyList<RecordedStepVisit>? SnapshotStepVisits()
    {
        lock (liveVisits) return liveVisits.Count == 0 ? null : liveVisits.ToArray();
    }

    internal void RecordHttpCall(string name, double offsetMs, double durationMs, int? statusCode, long bytes, string? error)
    {
        Interlocked.Increment(ref currentCallIndex);
        var endpoint = EndpointNormalizer.NormalizeCallName(name);
        var method = EndpointNormalizer.TryExtractMethod(name);
        var thresholdMs = thresholds.ResolveMs(endpoint, name, method);
        callSink?.Invoke(new RecordedCall(name, offsetMs, durationMs, statusCode, bytes, error, Math.Max(0, CurrentStep), CurrentRep, endpoint, method, SlowThresholdMs: thresholdMs));
    }

    internal bool MeasureHttpTimingSplit { get; }
    private readonly IReadOnlyList<string> captureHeaders;

    /// <summary>Starts the span of an HTTP call of this actor and puts its <c>traceparent</c> on the request (brain 0062). <see langword="null"/> without a trace.</summary>
    internal CallSpan? BeginHttpCallSpan(HttpRequestMessage request, bool makeCurrent) =>
        trace?.StartCall(request.Method.Method, request, makeCurrent);

    internal void RecordHttpRequest(
        HttpRequestMessage request,
        double offsetMs,
        double durationMs,
        int? statusCode,
        long bytes,
        string? error,
        int step,
        int rep,
        CallScopeOverride? scope = null,
        HttpResponseMessage? response = null,
        double? ttfbMs = null,
        double? responseReadMs = null,
        CallSpan? span = null)
    {
        Interlocked.Increment(ref currentCallIndex);
        var norm = EndpointNormalizer.NormalizeHttp(request.Method, request.RequestUri);
        string? explicitName = null;
        if (request.Options.TryGetValue(HttpRequestCallExtensions.CallNameKey, out var optName) && !string.IsNullOrWhiteSpace(optName))
            explicitName = optName;
        else if (!string.IsNullOrWhiteSpace(scope?.Name))
            explicitName = scope.Name;

        var endpoint = explicitName ?? norm.Endpoint;
        var rawName = explicitName ?? norm.RawName;

        double? thresholdMs = request.Options.TryGetValue(HttpRequestCallExtensions.SlowThresholdMsKey, out var optMs) && optMs > 0
            ? optMs
            : scope?.SlowThresholdMs ?? thresholds.ResolveMs(endpoint, rawName, norm.Method);

        var headers = ExtractAllowedHeaders(request, response, captureHeaders);
        span?.Finish(endpoint, statusCode, error);

        callSink?.Invoke(new RecordedCall(
            rawName,
            offsetMs,
            durationMs,
            statusCode,
            bytes,
            error,
            step,
            rep,
            endpoint,
            norm.Method,
            norm.Host,
            thresholdMs,
            norm.Url,
            ttfbMs,
            responseReadMs,
            headers,
            span?.TraceId,
            span?.SpanId));
    }

    private static Dictionary<string, string>? ExtractAllowedHeaders(
        HttpRequestMessage request,
        HttpResponseMessage? response,
        IReadOnlyList<string> allowlist)
    {
        if (allowlist.Count == 0) return null;

        Dictionary<string, string>? captured = null;
        for (var i = 0; i < allowlist.Count; i++)
        {
            var name = allowlist[i];
            if (string.IsNullOrWhiteSpace(name)) continue;
            string? value = null;

            if (response is not null)
            {
                if (response.Headers.TryGetValues(name, out var respVals))
                    value = string.Join(", ", respVals);
                else if (response.Content?.Headers.TryGetValues(name, out var respContentVals) == true)
                    value = string.Join(", ", respContentVals);
            }

            if (value is null)
            {
                if (request.Headers.TryGetValues(name, out var reqVals))
                    value = string.Join(", ", reqVals);
                else if (request.Content?.Headers.TryGetValues(name, out var reqContentVals) == true)
                    value = string.Join(", ", reqContentVals);
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                captured ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                captured[name] = value;
            }
        }

        return captured;
    }

    /// <summary>
    /// Initializes a new <see cref="ScenarioContext"/> for a scenario lifecycle hook or single bee execution.
    /// </summary>
    /// <param name="http">Shared <see cref="HttpClient"/> used by the scenario.</param>
    /// <param name="environment">Active environment values, or <c>null</c> to use <see cref="EnvironmentValues.None"/>.</param>
    /// <param name="cancellationToken">Token cancelled on per-bee timeout or run stop.</param>
    /// <param name="state">Shared entry bag, or <c>null</c> to create an isolated per-context bag.</param>
    /// <param name="background">Background task host available during <see cref="IScenario.StartupAsync"/>.</param>
    /// <param name="logSink">Optional plain-text log callback used when <paramref name="structuredLogSink"/> is omitted.</param>
    /// <param name="callSink">Optional sink receiving each recorded HTTP or measured call.</param>
    /// <param name="structuredLogSink">Optional sink receiving structured <see cref="RecordedLog"/> entries.</param>
    /// <param name="runSeconds">Optional clock delegate returning elapsed run seconds for log timestamps.</param>
    /// <param name="runBag">Shared run-wide bag, or <c>null</c> to create a local bag.</param>
    /// <param name="runStopped">Optional predicate returning <c>true</c> when the run has been stopped by the user.</param>
    /// <param name="stepVisitSink">Optional sink invoked whenever a step-machine step is entered.</param>
    /// <param name="thresholds">Optional slow-call thresholds for this scenario entry.</param>
    /// <param name="captureHeaders">Allowlisted HTTP header names to capture on recorded calls; defaults to <see cref="LoadPlan.DefaultCaptureHeaders"/>.</param>
    /// <param name="measureHttpTimingSplit">When <c>true</c>, measures TTFB and response-body read times separately on HTTP calls.</param>
    /// <param name="trace">The actor's distributed trace (brain 0062); calls become spans of it and carry its <c>traceparent</c>. <c>null</c>: no tracing.</param>
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
        Func<bool>? runStopped = null,
        Action<RecordedStepVisit>? stepVisitSink = null,
        CallThresholds? thresholds = null,
        IReadOnlyList<string>? captureHeaders = null,
        bool measureHttpTimingSplit = false,
        ActorTrace? trace = null)
    {
        this.trace = trace;
        this.runStopped = runStopped;
        this.stepVisitSink = stepVisitSink;
        this.thresholds = thresholds ?? CallThresholds.None;
        this.captureHeaders = captureHeaders ?? LoadPlan.DefaultCaptureHeaders;
        MeasureHttpTimingSplit = measureHttpTimingSplit;
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
    /// <param name="job">Async background delegate receiving a token cancelled when the entry finishes or the run stops.</param>
    public void RunBackground(Func<CancellationToken, Task> job)
    {
        if (background is null)
            throw new InvalidOperationException("RunBackground can only be called during StartupAsync.");
        background.Run(job);
    }

    /// <summary>Writes an Information log line captured by test runs and run logs.</summary>
    /// <param name="message">Log message text to emit.</param>
    public void Log(string message) => Logger.LogInformation("{Message}", message);

    /// <summary>Records one call/step inside this bee (for multi-step scenarios or simulated calls without HTTP).</summary>
    /// <param name="name">Call or endpoint label.</param>
    /// <param name="durationMs">Elapsed duration of the call in milliseconds.</param>
    /// <param name="statusCode">Optional HTTP or synthetic status code (defaults to <c>200</c>).</param>
    /// <param name="bytes">Transferred payload size in bytes.</param>
    /// <param name="error">Optional error message if the call failed.</param>
    /// <param name="offsetMs">Optional start offset relative to bee start in milliseconds; inferred from <paramref name="durationMs"/> when omitted.</param>
    /// <param name="slowAbove">Optional positive slow threshold overriding the active scope and scenario thresholds.</param>
    public void RecordCall(string name, double durationMs, int? statusCode = 200, long bytes = 0, string? error = null, double? offsetMs = null, TimeSpan? slowAbove = null)
    {
        if (slowAbove is { } s && s <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(slowAbove), "Slow threshold must be greater than zero.");
        var scope = CurrentCallScope;
        var off = offsetMs ?? Math.Max(0, beeClock.Elapsed.TotalMilliseconds - durationMs);
        var endpoint = scope?.Name ?? EndpointNormalizer.NormalizeCallName(name);
        var method = EndpointNormalizer.TryExtractMethod(name);
        var thresholdMs = slowAbove?.TotalMilliseconds ?? scope?.SlowThresholdMs ?? thresholds.ResolveMs(endpoint, name, method);

        // An already finished call still gets its span (brain 0062): back-dated to when it started.
        var span = trace?.StartCall(endpoint, null, makeCurrent: false, DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(durationMs));
        span?.Finish(endpoint, statusCode, error);
        span?.Stop();
        callSink?.Invoke(new RecordedCall(name, off, durationMs, statusCode, bytes, error, Math.Max(0, CurrentStep), CurrentRep, endpoint, method, SlowThresholdMs: thresholdMs, TraceId: span?.TraceId, SpanId: span?.SpanId));
    }

    /// <summary>Measures an async step as a named call, recording its offset, duration and any failure.</summary>
    /// <param name="name">Call or endpoint label.</param>
    /// <param name="work">Async work delegate to time and record.</param>
    /// <param name="statusCode">Status code recorded if <paramref name="work"/> completes without throwing (defaults to <c>200</c>).</param>
    /// <param name="bytes">Transferred payload size in bytes recorded on success.</param>
    /// <param name="slowAbove">Optional positive slow threshold overriding the active scope and scenario thresholds.</param>
    /// <returns>A task that completes when <paramref name="work"/> finishes or rethrows its exception after recording the failed call.</returns>
    public async Task MeasureCallAsync(string name, Func<Task> work, int? statusCode = 200, long bytes = 0, TimeSpan? slowAbove = null)
    {
        if (slowAbove is { } s && s <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(slowAbove), "Slow threshold must be greater than zero.");
        var idx = Interlocked.Increment(ref currentCallIndex);
        var step = Math.Max(0, CurrentStep);
        var rep = CurrentRep;
        var scope = CurrentCallScope;
        var endpoint = scope?.Name ?? EndpointNormalizer.NormalizeCallName(name);
        var method = EndpointNormalizer.TryExtractMethod(name);
        var thresholdMs = slowAbove?.TotalMilliseconds ?? scope?.SlowThresholdMs ?? thresholds.ResolveMs(endpoint, name, method);
        var offsetMs = beeClock.Elapsed.TotalMilliseconds;
        var sw = Stopwatch.StartNew();
        // The measured work runs inside the call's span (brain 0062), so HTTP calls it makes (and plain code) nest under it.
        var span = trace?.StartCall(endpoint, null, makeCurrent: true);
        try
        {
            await work();
            sw.Stop();
            span?.Finish(endpoint, statusCode, null);
            callSink?.Invoke(new RecordedCall(name, offsetMs, sw.Elapsed.TotalMilliseconds, statusCode, bytes, null, step, rep, endpoint, method, SlowThresholdMs: thresholdMs, TraceId: span?.TraceId, SpanId: span?.SpanId));
        }
        catch (Exception ex)
        {
            sw.Stop();
            // A call cut off because the user stopped the run is kept as failed with the time it ran until the stop (brain 0053).
            var error = ex is OperationCanceledException && runStopped?.Invoke() == true ? ExecutionResult.CanceledError : ex.Message;
            span?.Finish(endpoint, null, error);
            callSink?.Invoke(new RecordedCall(name, offsetMs, sw.Elapsed.TotalMilliseconds, null, 0, error, step, rep, endpoint, method, SlowThresholdMs: thresholdMs, TraceId: span?.TraceId, SpanId: span?.SpanId));
            throw;
        }
        finally
        {
            span?.Stop();
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
        logSink(new RecordedLog(runSeconds(), levelName, message, exception?.Message, callIdx, Math.Max(0, CurrentStep), CurrentRep));
    }

    /// <summary>Sends a request, reads the whole body (so transfer time is included) and maps it to an outcome.</summary>
    /// <param name="request">Outgoing HTTP request message (disposed after completion).</param>
    /// <param name="ct">Cancellation token for sending and reading the response body.</param>
    /// <returns>A <see cref="ScenarioOutcome"/> containing the HTTP status code and response body byte length.</returns>
    public async Task<ScenarioOutcome> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            return new ScenarioOutcome((int)response.StatusCode, body.LongLength);
        }
    }

    /// <summary>Returns <c>true</c> if <paramref name="value"/> (trimmed) is an absolute HTTP or HTTPS URI.</summary>
    /// <param name="value">Candidate URL string to validate.</param>
    /// <returns><c>true</c> when <paramref name="value"/> parses as an absolute <c>http</c> or <c>https</c> URI; otherwise <c>false</c>.</returns>
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
