using System.Diagnostics;
using System.Reflection;

namespace Beesharp.Services.Scenarios;

/// <summary>Per-request options attached to <see cref="HttpRequestMessage"/> for call naming, thresholds and deduplication.</summary>
public static class HttpRequestCallExtensions
{
    internal static readonly HttpRequestOptionsKey<string> CallNameKey = new("Beesharp.CallName");
    internal static readonly HttpRequestOptionsKey<double> SlowThresholdMsKey = new("Beesharp.SlowThresholdMs");
    internal static readonly HttpRequestOptionsKey<bool> RecordedKey = new("Beesharp.CallRecorded");
    internal static readonly HttpRequestOptionsKey<bool> TimingSplitKey = new("Beesharp.MeasureTimingSplit");

    /// <summary>Overrides the auto-detected call/endpoint name for this HTTP request (brain 0013, 0060).</summary>
    /// <param name="request">The outgoing HTTP request message.</param>
    /// <param name="name">The custom call or endpoint name to associate with the request.</param>
    /// <returns>The same <see cref="HttpRequestMessage"/> instance for fluent chaining.</returns>
    public static HttpRequestMessage AsCall(this HttpRequestMessage request, string name)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        request.Options.Set(CallNameKey, name.Trim());
        return request;
    }

    /// <summary>Sets an explicit slow threshold for this single HTTP request, overriding scenario rules and <c>2× typical</c> (brain 0060).</summary>
    /// <param name="request">The outgoing HTTP request message.</param>
    /// <param name="threshold">The duration above which this request is classified as slow; must be greater than zero.</param>
    /// <returns>The same <see cref="HttpRequestMessage"/> instance for fluent chaining.</returns>
    public static HttpRequestMessage SlowAbove(this HttpRequestMessage request, TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Slow threshold must be greater than zero.");
        request.Options.Set(SlowThresholdMsKey, threshold.TotalMilliseconds);
        return request;
    }

    /// <summary>Measures time-to-first-byte (<c>TtfbMs</c>) separately from response body read (<c>ResponseReadMs</c>) for this request.</summary>
    /// <param name="request">The outgoing HTTP request message.</param>
    /// <param name="enabled"><c>true</c> to record separate TTFB and body-read timings; <c>false</c> to disable the split.</param>
    /// <returns>The same <see cref="HttpRequestMessage"/> instance for fluent chaining.</returns>
    public static HttpRequestMessage MeasureTimingSplit(this HttpRequestMessage request, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(TimingSplitKey, enabled);
        return request;
    }
}

/// <summary>Ambient override active inside <see cref="ScenarioContext.CallScope"/>.</summary>
internal sealed record CallScopeOverride(string? Name, double? SlowThresholdMs);

/// <summary>
/// <see cref="DelegatingHandler"/> that records every <see cref="HttpClient"/> request against the active bee's
/// <see cref="ScenarioContext.Current"/> (brain 0013, 0056, 0060), tagging each call with its step, repetition,
/// normalized endpoint and optional per-call slow threshold. Also marks the request so <see cref="HttpCallDiagnosticTracker"/>
/// does not double-record requests that pass through both this handler and <see cref="SocketsHttpHandler"/>.
/// </summary>
public sealed class RecordingHttpHandler : DelegatingHandler
{
    /// <summary>Initializes a new <see cref="RecordingHttpHandler"/> without an inner handler.</summary>
    public RecordingHttpHandler() { }

    /// <summary>Initializes a new <see cref="RecordingHttpHandler"/> wrapping the specified inner <see cref="HttpMessageHandler"/>.</summary>
    /// <param name="inner">The inner handler to delegate HTTP requests to.</param>
    public RecordingHttpHandler(HttpMessageHandler inner) : base(inner) { }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var ctx = ScenarioContext.Current;
        if (ctx is null)
            return await base.SendAsync(request, cancellationToken);

        // Idempotent when multiple RecordingHttpHandlers sit in the same pipeline (e.g. a typed client + custom wrapper).
        if (request.Options.TryGetValue(HttpRequestCallExtensions.RecordedKey, out var alreadyRecorded) && alreadyRecorded)
            return await base.SendAsync(request, cancellationToken);

        var offsetMs = ctx.ElapsedMs;
        var step = Math.Max(0, ctx.CurrentStep);
        var rep = ctx.CurrentRep;
        var scope = ctx.CurrentCallScope;
        var sw = Stopwatch.StartNew();

        var measureSplit = ctx.MeasureHttpTimingSplit
            || (request.Options.TryGetValue(HttpRequestCallExtensions.TimingSplitKey, out var reqSplit) && reqSplit);

        request.Options.Set(HttpRequestCallExtensions.RecordedKey, true);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            double? ttfbMs = null;
            double? responseReadMs = null;
            if (measureSplit)
            {
                ttfbMs = sw.Elapsed.TotalMilliseconds;
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                sw.Stop();
                responseReadMs = Math.Max(0, sw.Elapsed.TotalMilliseconds - ttfbMs.Value);
            }
            else
            {
                sw.Stop();
            }

            var bytes = response.Content.Headers.ContentLength ?? 0;
            var status = (int)response.StatusCode;
            var error = status >= 400 ? $"HTTP {status}" : null;
            ctx.RecordHttpRequest(request, offsetMs, sw.Elapsed.TotalMilliseconds, status, bytes, error, step, rep, scope, response, ttfbMs, responseReadMs);
            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var error = ex is OperationCanceledException && ctx.IsRunStopped ? ExecutionResult.CanceledError : ex.Message;
            ctx.RecordHttpRequest(request, offsetMs, sw.Elapsed.TotalMilliseconds, null, 0, error, step, rep, scope);
            throw;
        }
        finally
        {
            request.Options.Set(HttpRequestCallExtensions.RecordedKey, false);
        }
    }
}

/// <summary>
/// Subscribes once to .NET's <c>HttpHandlerDiagnosticListener</c> so HTTP calls made through user-created <see cref="HttpClient"/>s
/// (e.g. <c>new HttpClient()</c>, <c>new HttpClient(customDelegatingHandler)</c>, third-party SDKs) are automatically measured
/// against <see cref="ScenarioContext.Current"/> without double-counting requests already tracked by <see cref="RecordingHttpHandler"/> (brain 0060).
/// </summary>
internal static class HttpCallDiagnosticTracker
{
    private const string HttpListenerName = "HttpHandlerDiagnosticListener";
    private const string EventRoot = "System.Net.Http.HttpRequestOut";
    private const string EventStart = "System.Net.Http.HttpRequestOut.Start";
    private const string EventStop = "System.Net.Http.HttpRequestOut.Stop";
    private const string EventException = "System.Net.Http.Exception";

    private static readonly HttpRequestOptionsKey<PendingCall> PendingKey = new("Beesharp.DiagPending");
    private static int installed;
    private static int activePending;
    private static IDisposable? allListenersSub;
    private static readonly List<IDisposable> listenerSubs = [];
    private static readonly Lock subLock = new();

    private sealed class PendingCall(ScenarioContext context, double offsetMs, long startTimestamp, int step, int rep, CallScopeOverride? scope)
    {
        public ScenarioContext Context { get; } = context;
        public double OffsetMs { get; } = offsetMs;
        public long StartTimestamp { get; } = startTimestamp;
        public int Step { get; } = step;
        public int Rep { get; } = rep;
        public CallScopeOverride? Scope { get; } = scope;
        public Exception? Exception { get; set; }
        public bool Completed { get; set; }
    }

    private sealed class PayloadAccessors(Type payloadType)
    {
        public PropertyInfo? Request { get; } = payloadType.GetProperty("Request");
        public PropertyInfo? Response { get; } = payloadType.GetProperty("Response");
        public PropertyInfo? Exception { get; } = payloadType.GetProperty("Exception");
        public PropertyInfo? RequestTaskStatus { get; } = payloadType.GetProperty("RequestTaskStatus");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PayloadAccessors> AccessorCache = new();

    public static void EnsureInstalled()
    {
        if (Interlocked.Exchange(ref installed, 1) != 0) return;
        allListenersSub = DiagnosticListener.AllListeners.Subscribe(new ListenerRegistrar());
    }

    private sealed class ListenerRegistrar : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != HttpListenerName) return;
            var observer = new HttpEventObserver();
            var sub = listener.Subscribe(observer, observer.IsEnabled);
            lock (subLock) listenerSubs.Add(sub);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class HttpEventObserver : IObserver<KeyValuePair<string, object?>>
    {
        public bool IsEnabled(string eventName, object? arg1, object? arg2)
        {
            switch (eventName)
            {
                case EventRoot:
                case EventStart:
                    if (ScenarioContext.Current is null) return false;
                    if (arg1 is HttpRequestMessage req && req.Options.TryGetValue(HttpRequestCallExtensions.RecordedKey, out var recorded) && recorded)
                        return false;
                    return true;

                case EventStop:
                case EventException:
                    return ScenarioContext.Current is not null || Volatile.Read(ref activePending) > 0;

                default:
                    return false;
            }
        }

        public void OnNext(KeyValuePair<string, object?> ev)
        {
            if (ev.Value is null) return;

            switch (ev.Key)
            {
                case EventStart:
                    OnStart(ev.Value);
                    break;
                case EventException:
                    OnException(ev.Value);
                    break;
                case EventStop:
                    OnStop(ev.Value);
                    break;
            }
        }

        private static void OnStart(object payload)
        {
            var ctx = ScenarioContext.Current;
            if (ctx is null) return;

            var acc = AccessorCache.GetOrAdd(payload.GetType(), static t => new PayloadAccessors(t));
            if (acc.Request?.GetValue(payload) is not HttpRequestMessage req) return;
            if (req.Options.TryGetValue(HttpRequestCallExtensions.RecordedKey, out var recorded) && recorded) return;
            if (req.Options.TryGetValue(PendingKey, out _)) return;

            var pending = new PendingCall(ctx, ctx.ElapsedMs, Stopwatch.GetTimestamp(), Math.Max(0, ctx.CurrentStep), ctx.CurrentRep, ctx.CurrentCallScope);
            req.Options.Set(PendingKey, pending);
            Interlocked.Increment(ref activePending);
        }

        private static void OnException(object payload)
        {
            var acc = AccessorCache.GetOrAdd(payload.GetType(), static t => new PayloadAccessors(t));
            if (acc.Request?.GetValue(payload) is not HttpRequestMessage req) return;
            if (!req.Options.TryGetValue(PendingKey, out var pending)) return;
            if (acc.Exception?.GetValue(payload) is Exception ex)
                pending.Exception = ex;
        }

        private static void OnStop(object payload)
        {
            var acc = AccessorCache.GetOrAdd(payload.GetType(), static t => new PayloadAccessors(t));
            if (acc.Request?.GetValue(payload) is not HttpRequestMessage req) return;
            if (!req.Options.TryGetValue(PendingKey, out var pending) || pending.Completed) return;

            pending.Completed = true;
            Interlocked.Decrement(ref activePending);

            var durationMs = Stopwatch.GetElapsedTime(pending.StartTimestamp).TotalMilliseconds;
            var response = acc.Response?.GetValue(payload) as HttpResponseMessage;

            if (response is not null)
            {
                var status = (int)response.StatusCode;
                var bytes = response.Content.Headers.ContentLength ?? 0;
                var error = status >= 400 ? $"HTTP {status}" : null;
                pending.Context.RecordHttpRequest(req, pending.OffsetMs, durationMs, status, bytes, error, pending.Step, pending.Rep, pending.Scope, response);
                return;
            }

            string err;
            if (pending.Exception is { } ex)
            {
                err = ex is OperationCanceledException && pending.Context.IsRunStopped
                    ? ExecutionResult.CanceledError
                    : ex.Message;
            }
            else if (acc.RequestTaskStatus?.GetValue(payload) is TaskStatus.Canceled)
            {
                err = pending.Context.IsRunStopped ? ExecutionResult.CanceledError : "Request canceled";
            }
            else
            {
                err = "Request failed";
            }

            pending.Context.RecordHttpRequest(req, pending.OffsetMs, durationMs, null, 0, err, pending.Step, pending.Rep, pending.Scope);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
