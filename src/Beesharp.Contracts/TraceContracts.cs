namespace Beesharp.Contracts;

// Distributed traces fetched from the trace store into a run (brain 0065): the spans, what happened to each trace's fetch,
// the flat span list of `beesharp runs trace` and the hotspots of `beesharp runs hotspots`.
// Bee numbers here are 0-based sequences like the rest of /api/runs; BeeName ("B5") is the display name.

/// <summary>One span of a distributed trace as the trace store returned it (OTLP model, durations in milliseconds).</summary>
/// <param name="TraceId">W3C trace ID (32 hex characters).</param>
/// <param name="SpanId">W3C span ID (16 hex characters).</param>
/// <param name="ParentSpanId">Span ID of the parent, or <see langword="null"/> for a root span. A parent that is not part of the trace (e.g. Beesharp's own spans were not exported) makes the span a root of the stored trace.</param>
/// <param name="Service">The <c>service.name</c> of the process that made the span (<c>unknown</c> when absent).</param>
/// <param name="Name">The span name (e.g. <c>GET /products/{id:int}</c>, <c>SELECT product</c>).</param>
/// <param name="Kind">Span kind: <c>internal</c>, <c>server</c>, <c>client</c>, <c>producer</c> or <c>consumer</c>; <see langword="null"/> when unspecified.</param>
/// <param name="StartUnixNanos">Start time as Unix nanoseconds, in the trace store's clock.</param>
/// <param name="DurationMs">Duration of the span in milliseconds.</param>
/// <param name="IsError">Whether the span's status is error.</param>
/// <param name="StatusMessage">The status message of an error span.</param>
/// <param name="Attributes">The span's attributes as text (bounded: at most 40 attributes, values cut at 2000 characters); <see langword="null"/> when none.</param>
public sealed record TraceSpanDto(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Service,
    string Name,
    string? Kind,
    long StartUnixNanos,
    double DurationMs,
    bool IsError = false,
    string? StatusMessage = null,
    IReadOnlyDictionary<string, string>? Attributes = null)
{
    /// <summary>End time as Unix nanoseconds.</summary>
    public long EndUnixNanos => StartUnixNanos + (long)(DurationMs * 1_000_000d);
}

/// <summary>The states of a trace fetch (<see cref="TraceFetchDto.Status"/>).</summary>
public static class TraceFetchStatus
{
    /// <summary>The trace store returned the trace; its spans are stored.</summary>
    public const string Found = "found";

    /// <summary>The trace store does not have the trace (see <see cref="TraceFetchDto.Reason"/>).</summary>
    public const string NotFound = "not-found";

    /// <summary>The trace store could not be asked or answered with an error (see <see cref="TraceFetchDto.Reason"/>).</summary>
    public const string Error = "error";
}

/// <summary>The codes explaining why a trace is not in a run (<see cref="TraceFetchDto.Reason"/>, and what <c>beesharp runs trace</c> reports).</summary>
public static class TraceFetchReasons
{
    /// <summary>The bee ended too recently: the services' spans take up to about 30 seconds to arrive in the trace store.</summary>
    public const string NotArrived = "not-arrived";

    /// <summary>The store is reachable but does not have the trace: the services are not instrumented, sampled it out, dropped it or export to another store.</summary>
    public const string NotInStore = "not-in-store";

    /// <summary>The run is older than the store's usual retention, so the trace is probably deleted.</summary>
    public const string Expired = "expired";

    /// <summary>The trace store could not be reached.</summary>
    public const string StoreUnreachable = "store-unreachable";

    /// <summary>The trace store answered with an error (unauthorized, server error, unreadable answer).</summary>
    public const string StoreError = "store-error";

    /// <summary>The trace was not selected for fetching: only failed bees and the slowest and typical calls per endpoint are fetched, within caps.</summary>
    public const string NotSelected = "not-selected";

    /// <summary>No trace store was configured for the run (or the run predates trace fetching).</summary>
    public const string NotConfigured = "not-configured";

    /// <summary>The bee has no trace ID (the run predates tracing).</summary>
    public const string NoTraceId = "no-trace-id";

    /// <summary>A sentence for a reason code, for people and agents.</summary>
    /// <param name="code">One of the codes in this class.</param>
    /// <returns>The explanation, or the code itself when unknown.</returns>
    public static string Describe(string? code) => code switch
    {
        NotArrived => "not arrived yet: the bee ended shortly before the last attempt and the services' spans can take up to about 30 seconds to reach the trace store",
        NotInStore => "the trace store is reachable but does not have this trace: the services are not instrumented, sampled it out, dropped the spans or export to another store",
        Expired => "probably expired: the run is older than the trace store's usual retention",
        StoreUnreachable => "the trace store could not be reached",
        StoreError => "the trace store answered with an error",
        NotSelected => "this trace was not selected for fetching (only failed bees and the slowest and typical calls per endpoint are fetched, within caps)",
        NotConfigured => "no trace store was configured for this run (set TraceStore.Kind and TraceStore.Url in the environment), or the run was made before traces were fetched",
        NoTraceId => "this bee has no trace ID (the run was made before tracing)",
        null or "" => "unknown",
        _ => code,
    };
}

/// <summary>What happened when Beesharp fetched one trace from the trace store: kept with the run, so "x of y found" is known afterwards.</summary>
/// <param name="TraceId">W3C trace ID (32 hex characters) of the bee's trace.</param>
/// <param name="Sequence">Zero-based sequence of the bee that owns the trace.</param>
/// <param name="Status">One of <see cref="TraceFetchStatus"/>.</param>
/// <param name="Reason">For a trace that is not found, a code from <see cref="TraceFetchReasons"/>; otherwise <see langword="null"/>.</param>
/// <param name="SpanCount">Number of spans stored for the trace (0 when not found).</param>
/// <param name="Attempts">How many times the trace store was asked.</param>
/// <param name="SelectedFor">Why the trace was selected: <c>failed</c>, <c>slowest GET /products/{id}</c>, <c>typical GET /products/{id}</c>.</param>
/// <param name="FetchedUtc">When the last attempt was made.</param>
/// <param name="Truncated">Whether the trace had more spans than the per-trace cap and was cut.</param>
public sealed record TraceFetchDto(
    string TraceId,
    int Sequence,
    string Status,
    string? Reason,
    int SpanCount,
    int Attempts,
    IReadOnlyList<string> SelectedFor,
    DateTimeOffset? FetchedUtc,
    bool Truncated = false);

/// <summary>A fetched trace as stored: the fetch record and its spans.</summary>
/// <param name="Fetch">What happened when the trace was fetched.</param>
/// <param name="Spans">The spans (empty when the trace was not found).</param>
public sealed record StoredTrace(TraceFetchDto Fetch, IReadOnlyList<TraceSpanDto> Spans);

/// <summary>"x of y traces found" for a run (brain 0065): the figures of the background fetch.</summary>
/// <param name="Selected">Traces selected for fetching.</param>
/// <param name="Found">Traces found and stored.</param>
/// <param name="NotFound">Traces the store does not have.</param>
/// <param name="Errors">Traces that could not be fetched because the store was unreachable or answered with an error.</param>
/// <param name="Reasons">Count per reason code (<see cref="TraceFetchReasons"/>) of the traces that are not found or failed.</param>
/// <param name="Line">The one-line text, e.g. <c>traces: 42 of 50 found, 8 not found: 6 not arrived, 2 expired</c>.</param>
public sealed record TraceFetchSummaryDto(
    int Selected,
    int Found,
    int NotFound,
    int Errors,
    IReadOnlyDictionary<string, int> Reasons,
    string Line)
{
    /// <summary>The summary of a set of fetch records.</summary>
    /// <param name="fetches">The run's fetch records.</param>
    /// <returns>The counts and the one-line text.</returns>
    public static TraceFetchSummaryDto From(IReadOnlyList<TraceFetchDto> fetches)
    {
        var found = fetches.Count(f => f.Status == TraceFetchStatus.Found);
        var notFound = fetches.Count(f => f.Status == TraceFetchStatus.NotFound);
        var errors = fetches.Count(f => f.Status == TraceFetchStatus.Error);
        var reasons = fetches.Where(f => f.Status != TraceFetchStatus.Found)
            .GroupBy(f => f.Reason ?? "unknown")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());
        return new TraceFetchSummaryDto(fetches.Count, found, notFound, errors, reasons, BuildLine(fetches.Count, found, notFound + errors, reasons));
    }

    /// <summary>The line for a run that fetched nothing because no trace store is configured.</summary>
    public static TraceFetchSummaryDto NotConfigured { get; } =
        new(0, 0, 0, 0, new Dictionary<string, int>(), "traces: not fetched (no trace store configured in the environment)");

    private static string BuildLine(int selected, int found, int missing, IReadOnlyDictionary<string, int> reasons)
    {
        if (selected == 0) return "traces: none selected";
        var line = $"traces: {found} of {selected} found";
        if (missing > 0)
            line += $", {missing} not found: " + string.Join(", ", reasons.Select(r => $"{r.Value} {ShortReason(r.Key)}"));
        return line;
    }

    private static string ShortReason(string code) => code switch
    {
        TraceFetchReasons.NotArrived => "not arrived",
        TraceFetchReasons.NotInStore => "not in the store (sampled out or not instrumented)",
        TraceFetchReasons.Expired => "expired",
        TraceFetchReasons.StoreUnreachable => "store unreachable",
        TraceFetchReasons.StoreError => "store error",
        _ => code,
    };
}

/// <summary>One line of the flat span list of <c>beesharp runs trace</c>: the span with its depth, self time and share.</summary>
/// <param name="Depth">0 for a root; one more per level of nesting.</param>
/// <param name="SpanId">The span ID.</param>
/// <param name="Service">The service of the span.</param>
/// <param name="Name">The span name.</param>
/// <param name="Kind">The span kind, when known.</param>
/// <param name="StartOffsetMs">Start of the span relative to the start of the first shown span, in milliseconds (the trace store's clock).</param>
/// <param name="DurationMs">Duration of the span in milliseconds.</param>
/// <param name="SelfMs">Duration minus the time covered by its children: where the span itself spent its time.</param>
/// <param name="Share">Self time as a fraction (0 to 1) of the reference duration (<see cref="TraceViewDto.ReferenceMs"/>).</param>
/// <param name="IsError">Whether the span has error status.</param>
/// <param name="StatusMessage">The error text, if any.</param>
/// <param name="HttpStatus">The HTTP status code from the span's attributes, if it has one.</param>
/// <param name="Attributes">The span's stored attributes (the text output shows the key ones such as <c>db.query.text</c>).</param>
public sealed record TraceSpanViewDto(
    int Depth,
    string SpanId,
    string Service,
    string Name,
    string? Kind,
    double StartOffsetMs,
    double DurationMs,
    double SelfMs,
    double Share,
    bool IsError,
    string? StatusMessage,
    int? HttpStatus,
    IReadOnlyDictionary<string, string>? Attributes);

/// <summary>One bee's trace (or the part below one call) as a flat, bounded span list: <c>GET /api/runs/{runId}/traces/{key}</c>.</summary>
/// <param name="RunId">The run.</param>
/// <param name="TraceId">The trace ID.</param>
/// <param name="Found">Whether the trace is stored with the run. When <see langword="false"/>, <see cref="Reason"/> says why.</param>
/// <param name="Reason">A code from <see cref="TraceFetchReasons"/> when the trace is not stored.</param>
/// <param name="ReasonText">The sentence for <see cref="Reason"/>.</param>
/// <param name="Sequence">Zero-based bee sequence, when known.</param>
/// <param name="BeeName">Display name of the bee (<c>B5</c>), when known.</param>
/// <param name="Call">1-based number of the call within the bee when the list is limited to one call; otherwise <see langword="null"/>.</param>
/// <param name="Endpoint">The call's endpoint when limited to one call.</param>
/// <param name="ReferenceMs">What <see cref="TraceSpanViewDto.Share"/> is relative to: the bee's duration, or the call's duration with <see cref="Call"/>.</param>
/// <param name="ReferenceKind"><c>bee</c> or <c>call</c>.</param>
/// <param name="TotalSpans">Number of spans in the trace (or below the call) before the bound.</param>
/// <param name="ShownSpans">Number of spans in <see cref="Spans"/>.</param>
/// <param name="ErrorSpans">Number of spans with error status in the whole selection.</param>
/// <param name="Services">The services that have spans in the selection.</param>
/// <param name="SelectedFor">Why the trace was fetched.</param>
/// <param name="FetchedUtc">When the trace was fetched.</param>
/// <param name="Note">A hint, e.g. that the trace was truncated at the per-trace cap or that spans were left out.</param>
/// <param name="Spans">The spans in tree order (children after their parent, ordered by start). When over the bound, the spans with the largest self time and their ancestors are kept.</param>
public sealed record TraceViewDto(
    string RunId,
    string TraceId,
    bool Found,
    string? Reason,
    string? ReasonText,
    int? Sequence,
    string? BeeName,
    int? Call,
    string? Endpoint,
    double ReferenceMs,
    string ReferenceKind,
    int TotalSpans,
    int ShownSpans,
    int ErrorSpans,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> SelectedFor,
    DateTimeOffset? FetchedUtc,
    string? Note,
    IReadOnlyList<TraceSpanViewDto> Spans);

/// <summary>Which trace <c>GET /api/runs/{runId}/traces/{key}</c> returns and how much of it.</summary>
/// <param name="Key">A bee (1-based number as <c>5</c> or <c>B5</c>) or a trace ID (32 hex characters).</param>
/// <param name="Call">Optional 1-based call number within the bee: only the spans below that call.</param>
/// <param name="MaxSpans">Maximum number of spans in the answer.</param>
public sealed record TraceViewQuery(string Key, int? Call = null, int MaxSpans = 50);

/// <summary>Filters for the hotspot analysis.</summary>
/// <param name="Endpoint">Only endpoints containing this text (case-insensitive).</param>
/// <param name="SlowOnly">Only endpoints that have slow calls with a fetched trace.</param>
/// <param name="Limit">Maximum number of endpoints.</param>
/// <param name="Top">Maximum number of hotspots per endpoint.</param>
public sealed record HotspotsQuery(string? Endpoint = null, bool SlowOnly = false, int Limit = 10, int Top = 5);

/// <summary>One hotspot of an endpoint: the span (service + name) that took the largest share of the time of some of its slow calls.</summary>
/// <param name="Service">The service of the span (<c>(outside services)</c> for time no service span accounts for: network, queueing, client side).</param>
/// <param name="Name">The span name.</param>
/// <param name="Explains">In how many of the endpoint's analysed calls this span had the largest self time.</param>
/// <param name="ExplainsShare">That count as a fraction (0 to 1) of the analysed calls.</param>
/// <param name="MedianSelfMs">Typical duration: the median self time of the span in the calls it explains.</param>
/// <param name="MedianShare">The median share (0 to 1) of the call's duration that self time was.</param>
/// <param name="BaselineSelfMs">The median self time of the same span in the endpoint's typical calls (0 when typical calls do not have it); <see langword="null"/> without a baseline.</param>
/// <param name="Factor">How many times <see cref="MedianSelfMs"/> exceeds <see cref="BaselineSelfMs"/>; <see langword="null"/> without a usable baseline.</param>
/// <param name="ExampleTraceIds">Trace IDs of the slowest examples, to look at with <c>runs trace</c>.</param>
/// <param name="ExampleAttributes">Key attributes of an example span (e.g. <c>db.query.text</c>).</param>
/// <param name="IsOutside"><see langword="true"/> for the pseudo span of time outside all service spans.</param>
public sealed record HotspotDto(
    string Service,
    string Name,
    int Explains,
    double ExplainsShare,
    double MedianSelfMs,
    double MedianShare,
    double? BaselineSelfMs,
    double? Factor,
    IReadOnlyList<string> ExampleTraceIds,
    IReadOnlyDictionary<string, string>? ExampleAttributes,
    bool IsOutside = false);

/// <summary>The hotspots of one endpoint.</summary>
/// <param name="Endpoint">The normalized endpoint (e.g. <c>GET /products/{id}</c>).</param>
/// <param name="TotalCalls">Calls of the endpoint in the run.</param>
/// <param name="SlowCalls">Slow calls of the endpoint in the run.</param>
/// <param name="SlowCallsWithTrace">Slow calls whose trace was fetched (the ones analysed with <c>slow</c> basis).</param>
/// <param name="TypicalCallsWithTrace">Typical (not slow, not failed) calls whose trace was fetched: the baseline.</param>
/// <param name="TypicalMs">Typical (median) call duration of the endpoint.</param>
/// <param name="SlowThresholdMs">The endpoint's slow threshold.</param>
/// <param name="SlowMedianMs">Median duration of the analysed slow calls.</param>
/// <param name="Basis"><c>slow</c>: the hotspots explain the slow calls; <c>typical</c>: the endpoint has no slow call with a trace, so they show where its typical calls spend their time.</param>
/// <param name="Hotspots">The hotspots, the one explaining most calls first.</param>
public sealed record EndpointHotspotsDto(
    string Endpoint,
    int TotalCalls,
    int SlowCalls,
    int SlowCallsWithTrace,
    int TypicalCallsWithTrace,
    double TypicalMs,
    double? SlowThresholdMs,
    double? SlowMedianMs,
    string Basis,
    IReadOnlyList<HotspotDto> Hotspots);

/// <summary>Where the time of the slow calls went, per endpoint: <c>GET /api/runs/{runId}/hotspots</c> and <c>beesharp runs hotspots</c>.</summary>
/// <param name="RunId">The run.</param>
/// <param name="Coverage">How many traces were fetched ("x of y found").</param>
/// <param name="EndpointsTotal">Number of endpoints with at least one analysable call before the limit.</param>
/// <param name="Endpoints">The endpoints, the ones with the most slow traced calls first.</param>
/// <param name="Note">A hint, e.g. why nothing can be said (no traces fetched).</param>
public sealed record HotspotsResultDto(
    string RunId,
    TraceFetchSummaryDto Coverage,
    int EndpointsTotal,
    IReadOnlyList<EndpointHotspotsDto> Endpoints,
    string? Note);
