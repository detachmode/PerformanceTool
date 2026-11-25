using System.Diagnostics;
using Beesharp.Services.Scenarios;

namespace Beesharp.Services.Tracing;

/// <summary>What an actor's root span is told about the actor (brain 0062).</summary>
/// <param name="Name">Span name: the scenario entry's label.</param>
/// <param name="RunId">Identifier of the load run, if known.</param>
/// <param name="ScenarioId">Identifier of the scenario the entry plays.</param>
/// <param name="EntryNumber">1-based number of the scenario entry in the plan.</param>
/// <param name="ActorNumber">1-based actor (bee) number, the number the UI shows.</param>
/// <param name="Steps">The scenario's declared steps (names for the step spans); <see langword="null"/> or empty for a scenario without steps.</param>
public sealed record ActorTraceInfo(
    string Name,
    string? RunId,
    string ScenarioId,
    int EntryNumber,
    int ActorNumber,
    IReadOnlyList<StepInfo>? Steps = null);

/// <summary>
/// The distributed trace of one actor (brain 0062): one actor = one bar = one logging scope = one trace. The root span is the actor, each step a child span, and every call a
/// child of the step it was made in (or of the actor when the scenario has no steps). Create it with <see cref="Start"/>, hand it to the actor's <see cref="ScenarioContext"/>,
/// call <see cref="Complete"/> when the actor ends and dispose it.
/// </summary>
public sealed class ActorTrace : IDisposable
{
    private readonly Activity actor;
    private readonly IReadOnlyList<StepInfo> steps;
    private Activity? step;
    private int completed;

    private ActorTrace(Activity actor, IReadOnlyList<StepInfo> steps)
    {
        this.actor = actor;
        this.steps = steps;
    }

    /// <summary>W3C trace ID (32 hex characters) of this actor's trace; every call of the actor carries it in its <c>traceparent</c>.</summary>
    public string TraceId => actor.TraceId.ToHexString();

    /// <summary>W3C span ID (16 hex characters) of the actor's root span.</summary>
    public string SpanId => actor.SpanId.ToHexString();

    /// <summary>Starts the root span of a new trace for one actor. Returns <see langword="null"/> only if span creation is disabled.</summary>
    /// <param name="info">What to say about the actor on its root span.</param>
    /// <returns>The actor's trace, or <see langword="null"/>.</returns>
    public static ActorTrace? Start(ActorTraceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        // A run starts inside a request (the REST call of the UI) or under whatever else the host was doing, and the actors inherit that ambient Activity.Current.
        // A default parent context would still fall back to it, so the actor first cuts loose from it: its flow starts without a current activity and so gets its own trace ID.
        // Activity.Current is per async flow: only this actor's flow changes.
        Activity.Current = null;
        var activity = BeesharpTracing.Source.StartActivity(string.IsNullOrWhiteSpace(info.Name) ? info.ScenarioId : info.Name, ActivityKind.Internal, default(ActivityContext));
        if (activity is null) return null;

        if (info.RunId is { Length: > 0 } runId) activity.SetTag("beesharp.run.id", runId);
        activity.SetTag("beesharp.scenario.id", info.ScenarioId);
        activity.SetTag("beesharp.entry.number", info.EntryNumber);
        activity.SetTag("beesharp.actor.number", info.ActorNumber);
        return new ActorTrace(activity, info.Steps ?? []);
    }

    /// <summary>
    /// The span a new call hangs below: the innermost running Beesharp span of this trace on the current flow (a measured call around the HTTP call, a step),
    /// else the current step, else the actor. Walks up from <see cref="Activity.Current"/> because HttpClient may have started a span of its own in between.
    /// </summary>
    private Activity ResolveParent()
    {
        var traceId = actor.TraceId;
        for (var a = Activity.Current; a is not null; a = a.Parent)
            if (!a.IsStopped && a.TraceId == traceId && ReferenceEquals(a.Source, BeesharpTracing.Source))
                return a;
        return Volatile.Read(ref step) ?? actor;
    }

    /// <summary>Ends the previous step's span and starts the span of the step the actor just entered.</summary>
    internal void EnterStep(int stepIndex, int rep)
    {
        var name = stepIndex >= 0 && stepIndex < steps.Count ? steps[stepIndex].Name : $"Step {stepIndex + 1}";
        var next = BeesharpTracing.Source.StartActivity(rep > 0 ? $"{name} #{rep + 1}" : name, ActivityKind.Internal, actor.Context);
        var previous = Interlocked.Exchange(ref step, next);
        previous?.Stop();
        if (next is null) return;
        next.SetTag("beesharp.step.number", stepIndex + 1);
        next.SetTag("beesharp.step.name", name);
        next.SetTag("beesharp.step.visit", rep + 1);
        // Stopping the previous step moved Activity.Current to the actor; the new step becomes current again so plain code in the step sees it.
        Activity.Current = next;
    }

    /// <summary>Starts the span of one call as a child of the current step (or the actor) and puts the <c>traceparent</c> of that span on the request.</summary>
    /// <param name="spanName">Provisional span name, replaced by the normalized endpoint when the call ends.</param>
    /// <param name="request">The outgoing request that gets the <c>traceparent</c> header, or <see langword="null"/> for a call that is not HTTP.</param>
    /// <param name="makeCurrent">Whether the span becomes <see cref="Activity.Current"/> for the code that makes the call.</param>
    /// <param name="startTime">Start of an already finished call; <see langword="default"/> means now.</param>
    internal CallSpan? StartCall(string spanName, HttpRequestMessage? request, bool makeCurrent, DateTimeOffset startTime = default)
    {
        var previous = Activity.Current;
        var span = BeesharpTracing.Source.StartActivity(spanName, ActivityKind.Client, ResolveParent().Context, null, null, startTime);
        if (span is null) return null;
        if (!makeCurrent) Activity.Current = previous;

        if (request is not null)
        {
            span.SetTag("http.request.method", request.Method.Method);
            if (request.RequestUri is { IsAbsoluteUri: true } uri)
            {
                span.SetTag("server.address", uri.Host);
                span.SetTag("server.port", uri.Port);
                span.SetTag("url.scheme", uri.Scheme);
                span.SetTag("url.path", uri.AbsolutePath);
            }
            // The services under test get the sampled flag and join this trace as children of the call. HttpClient would otherwise invent an
            // intermediate span of its own that nobody exports, and it does not overwrite a traceparent that is already there.
            request.Headers.Remove("traceparent");
            request.Headers.TryAddWithoutValidation("traceparent", span.Id);
            request.Headers.Remove("tracestate");
            if (span.TraceStateString is { Length: > 0 } state)
                request.Headers.TryAddWithoutValidation("tracestate", state);
        }
        return new CallSpan(span, !makeCurrent, previous);
    }

    /// <summary>Records the outcome of the actor on its root span (and on the step it ended in).</summary>
    /// <param name="success">Whether the actor ended without an error.</param>
    /// <param name="error">The error text of a failed or canceled actor.</param>
    /// <param name="canceled">Whether the actor was canceled because the run was stopped.</param>
    public void Complete(bool success, string? error, bool canceled)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
        var outcome = canceled ? "canceled" : success ? "ok" : "failed";
        actor.SetTag("beesharp.outcome", outcome);
        if (!success)
        {
            actor.SetTag("beesharp.error", error);
            actor.SetStatus(ActivityStatusCode.Error, error);
            if (Volatile.Read(ref step) is { } open)
                open.SetStatus(ActivityStatusCode.Error, error);
        }
    }

    /// <summary>Ends the open step span and the actor's root span.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref step, null)?.Stop();
        actor.Stop();
    }
}

/// <summary>The span of one call (brain 0062); created by <see cref="ActorTrace"/>, finished when the call is recorded.</summary>
internal sealed class CallSpan
{
    private readonly Activity activity;
    private readonly bool detached;
    private readonly Activity? previous;
    private int stopped;

    internal CallSpan(Activity activity, bool detached, Activity? previous)
    {
        this.activity = activity;
        this.detached = detached;
        this.previous = previous;
    }

    public string TraceId => activity.TraceId.ToHexString();
    public string SpanId => activity.SpanId.ToHexString();

    /// <summary>Names the span after the call's endpoint and records the outcome. Does not end the span.</summary>
    public void Finish(string endpoint, int? statusCode, string? error)
    {
        activity.DisplayName = endpoint;
        activity.SetTag("beesharp.endpoint", endpoint);
        if (statusCode is { } code) activity.SetTag("http.response.status_code", code);
        if (error is not null)
        {
            activity.SetTag("error.type", statusCode is >= 400 ? statusCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "error");
            activity.SetStatus(ActivityStatusCode.Error, error);
        }
    }

    /// <summary>Ends the span (once). <paramref name="end"/> is the end of an already finished call; <see langword="default"/> means now.</summary>
    public void Stop(DateTime end = default)
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        if (end != default) activity.SetEndTime(end);
        activity.Stop();
        // Stopping moves Activity.Current to the span's parent; a span that was never made current must leave it where it was.
        if (detached) Activity.Current = previous;
    }
}
