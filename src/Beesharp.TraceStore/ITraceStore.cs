namespace Beesharp.Services.TraceStore;

/// <summary>How a trace lookup ended.</summary>
public enum TraceLookupOutcome
{
    /// <summary>The store returned the trace.</summary>
    Found,

    /// <summary>The store answered, and does not have the trace.</summary>
    NotFound,

    /// <summary>The store could not be reached (connection refused, DNS, timeout).</summary>
    Unreachable,

    /// <summary>The store answered with an error (unauthorized, server error, unreadable answer).</summary>
    Error,
}

/// <summary>The result of asking a trace store for one trace.</summary>
/// <param name="Outcome">How the lookup ended.</param>
/// <param name="Spans">The trace's spans when found; empty otherwise.</param>
/// <param name="Message">For <see cref="TraceLookupOutcome.Unreachable"/> and <see cref="TraceLookupOutcome.Error"/>, what went wrong.</param>
public sealed record TraceLookup(TraceLookupOutcome Outcome, IReadOnlyList<TraceSpanDto> Spans, string? Message = null)
{
    /// <summary>The store has no such trace.</summary>
    public static TraceLookup Missing { get; } = new(TraceLookupOutcome.NotFound, []);

    /// <summary>The store could not be reached.</summary>
    /// <param name="message">What went wrong.</param>
    /// <returns>An unreachable lookup.</returns>
    public static TraceLookup Unreachable(string message) => new(TraceLookupOutcome.Unreachable, [], message);

    /// <summary>The store answered with an error.</summary>
    /// <param name="message">What went wrong.</param>
    /// <returns>An error lookup.</returns>
    public static TraceLookup Failed(string message) => new(TraceLookupOutcome.Error, [], message);
}

/// <summary>
/// A tracing backend Beesharp can fetch whole traces from by trace ID (brain 0065). Jaeger first (<see cref="JaegerTraceStore"/>);
/// Elastic is a later, small step behind this interface. Implementations never throw for a missing or unreachable store; they return the outcome.
/// </summary>
public interface ITraceStore
{
    /// <summary>The kind (<see cref="TraceStoreKinds"/>).</summary>
    string Kind { get; }

    /// <summary>For logs: what this store is, e.g. <c>Jaeger at http://localhost:16686</c>.</summary>
    string Description { get; }

    /// <summary>Fetches one trace with all its spans.</summary>
    /// <param name="traceId">The W3C trace ID (32 hex characters).</param>
    /// <param name="from">Start of the time window to look in (the run's start, with a margin); a store that can narrow its search uses it.</param>
    /// <param name="to">End of the time window.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome with the spans when found.</returns>
    Task<TraceLookup> GetTraceAsync(string traceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

/// <summary>Creates the <see cref="ITraceStore"/> for the settings of a run's environment.</summary>
public interface ITraceStoreFactory
{
    /// <summary>Creates the store.</summary>
    /// <param name="settings">The settings read from the environment.</param>
    /// <returns>The store.</returns>
    ITraceStore Create(TraceStoreSettings settings);
}

/// <summary>The default <see cref="ITraceStoreFactory"/>: Jaeger over HTTP.</summary>
/// <param name="handlerFactory">Optional: the HTTP handler to use (for tests: a fake Jaeger); default is a plain <see cref="SocketsHttpHandler"/>.</param>
public sealed class TraceStoreFactory(Func<HttpMessageHandler>? handlerFactory = null) : ITraceStoreFactory
{
    /// <inheritdoc />
    public ITraceStore Create(TraceStoreSettings settings) => settings.Kind switch
    {
        TraceStoreKinds.Jaeger => new JaegerTraceStore(settings, handlerFactory?.Invoke()),
        _ => throw new NotSupportedException($"Trace store kind '{settings.Kind}' is not supported; supported: {string.Join(", ", TraceStoreKinds.Supported)}."),
    };
}
