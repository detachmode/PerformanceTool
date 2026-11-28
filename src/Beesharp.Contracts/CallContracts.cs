namespace Beesharp.Contracts;

/// <summary>Aggregated performance summary for one auto-detected (or explicitly named) endpoint across a run (brain 0060).</summary>
/// <param name="Endpoint">Canonical normalized endpoint label (for example <c>GET /products/{id}</c>).</param>
/// <param name="Method">HTTP method associated with the endpoint, if detected.</param>
/// <param name="Host">Target host name when all calls to the endpoint share a single host, or <see langword="null"/> when mixed or absent.</param>
/// <param name="TotalCount">Total number of calls recorded for this endpoint.</param>
/// <param name="SlowCount">Number of successful calls exceeding the slow threshold.</param>
/// <param name="FailedCount">Number of calls that failed with an error.</param>
/// <param name="TypicalMs">Median (typical) call duration in milliseconds.</param>
/// <param name="P95Ms">95th-percentile call duration in milliseconds.</param>
/// <param name="MaxMs">Maximum observed call duration in milliseconds.</param>
/// <param name="MinMs">Minimum observed call duration in milliseconds.</param>
/// <param name="AvgMs">Mean call duration in milliseconds.</param>
/// <param name="SlowThresholdMs">Effective slow threshold in milliseconds, or <see langword="null"/> if not enough samples and no custom threshold exists.</param>
/// <param name="HasCustomThreshold"><see langword="true"/> when at least one call carried an explicit custom slow threshold.</param>
/// <param name="Entries">Distinct zero-based scenario entry indices that invoked this endpoint.</param>
/// <param name="Step">Optional zero-based step index when summaries are grouped by step.</param>
/// <param name="StepName">Optional human-readable step name when grouped by step.</param>
/// <param name="P99Ms">99th-percentile call duration in milliseconds.</param>
public sealed record CallEndpointSummaryDto(
    string Endpoint,
    string? Method,
    string? Host,
    int TotalCount,
    int SlowCount,
    int FailedCount,
    double TypicalMs,
    double P95Ms,
    double MaxMs,
    double MinMs,
    double AvgMs,
    double? SlowThresholdMs,
    bool HasCustomThreshold,
    IReadOnlyList<int> Entries,
    int? Step = null,
    string? StepName = null,
    double P99Ms = 0);

/// <summary>Aggregated call latency and slow/failure counts for one (entry, step) pair.</summary>
/// <param name="Entry">Zero-based scenario entry index.</param>
/// <param name="Step">Zero-based step index within the scenario.</param>
/// <param name="TotalCalls">Total number of calls recorded for this step.</param>
/// <param name="SlowCalls">Number of successful calls classified as slow.</param>
/// <param name="FailedCalls">Number of calls that failed with an error.</param>
/// <param name="MinMs">Minimum call duration in milliseconds.</param>
/// <param name="P50Ms">Median (50th-percentile) call duration in milliseconds.</param>
/// <param name="P95Ms">95th-percentile call duration in milliseconds.</param>
/// <param name="MaxMs">Maximum call duration in milliseconds.</param>
/// <param name="AvgMs">Mean call duration in milliseconds.</param>
public sealed record CallStepMetrics(
    int Entry,
    int Step,
    int TotalCalls,
    int SlowCalls,
    int FailedCalls,
    double? MinMs,
    double? P50Ms,
    double? P95Ms,
    double? MaxMs,
    double? AvgMs);

/// <summary>All auto-detected endpoints of a run and aggregate call counts for <c>GET /api/runs/{runId}/calls/endpoints</c>.</summary>
/// <param name="Endpoints">Sorted list of per-endpoint call summaries.</param>
/// <param name="TotalCalls">Total number of calls across all returned endpoint summaries.</param>
/// <param name="TotalSlowCalls">Total number of slow calls across all returned endpoint summaries.</param>
/// <param name="TotalFailedCalls">Total number of failed calls across all returned endpoint summaries.</param>
public sealed record CallEndpointsResult(
    IReadOnlyList<CallEndpointSummaryDto> Endpoints,
    long TotalCalls,
    long TotalSlowCalls,
    long TotalFailedCalls);

/// <summary>One individual call execution belonging to an endpoint, with exact raw URL, bee link, and slow evaluation.</summary>
/// <param name="Id">Unique call record identifier.</param>
/// <param name="Sequence">Zero-based global bee execution sequence number.</param>
/// <param name="BeeName">1-based display label of the bee (for example <c>B1</c>).</param>
/// <param name="Entry">Zero-based scenario entry index.</param>
/// <param name="Step">Zero-based step index in which the call occurred.</param>
/// <param name="Rep">Zero-based repetition index within the step.</param>
/// <param name="AtSeconds">Run-relative timestamp in seconds when the call started.</param>
/// <param name="OffsetMs">Start offset in milliseconds relative to the bee's execution start.</param>
/// <param name="DurationMs">Total call duration in milliseconds.</param>
/// <param name="StatusCode">Optional HTTP or custom status code returned by the call.</param>
/// <param name="Bytes">Number of payload bytes transferred.</param>
/// <param name="Error">Optional error message if the call failed.</param>
/// <param name="RawName">Raw unnormalized call name.</param>
/// <param name="Url">Optional full request URL.</param>
/// <param name="Endpoint">Canonical normalized endpoint label.</param>
/// <param name="IsSlow"><see langword="true"/> when the call succeeded and exceeded its effective slow threshold.</param>
/// <param name="TypicalMs">Typical (median) duration in milliseconds for the endpoint, if positive.</param>
/// <param name="SlowThresholdMs">Effective slow threshold in milliseconds applied to this call.</param>
/// <param name="TraceId">W3C trace ID of the actor's distributed trace this call belongs to (brain 0063), or <see langword="null"/> when the run was made without tracing.</param>
/// <param name="SpanId">W3C span ID of this call's span, the parent of the service's server span (brain 0063), or <see langword="null"/>.</param>
public sealed record CallSampleDto(
    long Id,
    int Sequence,
    string BeeName,
    int Entry,
    int Step,
    int Rep,
    double AtSeconds,
    double OffsetMs,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    string RawName,
    string? Url,
    string Endpoint,
    bool IsSlow,
    double? TypicalMs,
    double? SlowThresholdMs,
    string? TraceId = null,
    string? SpanId = null);

/// <summary>One duration-distribution bucket in an endpoint's histogram.</summary>
/// <param name="FromMs">Inclusive lower duration bound in milliseconds.</param>
/// <param name="ToMs">Exclusive upper duration bound in milliseconds.</param>
/// <param name="Count">Total number of calls falling into this bucket.</param>
/// <param name="SlowCount">Number of slow calls in this bucket.</param>
/// <param name="FailedCount">Number of failed calls in this bucket.</param>
public sealed record CallHistogramBucketDto(
    double FromMs,
    double ToMs,
    int Count,
    int SlowCount,
    int FailedCount);

/// <summary>
/// One point or bounded time bucket on an endpoint's duration-over-time chart.
/// When <see cref="Count"/> is 1, <see cref="SampleId"/>, <see cref="Sequence"/>, <see cref="StatusCode"/>, <see cref="RawName"/> and <see cref="Error"/> describe the single call.
/// </summary>
/// <param name="AtSeconds">Run-relative timestamp in seconds of the point or bucket start.</param>
/// <param name="BucketWidthSeconds">Width of the time bucket in seconds, or <c>0</c> for an individual unbucketed call point.</param>
/// <param name="DurationMs">Observed duration (for single points) or maximum duration within the bucket in milliseconds.</param>
/// <param name="TypicalMs">Endpoint typical duration (for single points) or bucket median duration in milliseconds.</param>
/// <param name="Count">Number of calls aggregated into this point.</param>
/// <param name="SlowCount">Number of slow calls in this point or bucket.</param>
/// <param name="FailedCount">Number of failed calls in this point or bucket.</param>
/// <param name="SampleId">Call record identifier when <see cref="Count"/> is <c>1</c>.</param>
/// <param name="Sequence">Zero-based bee sequence number when <see cref="Count"/> is <c>1</c>.</param>
/// <param name="Entry">Zero-based scenario entry index when <see cref="Count"/> is <c>1</c>.</param>
/// <param name="StatusCode">Status code when <see cref="Count"/> is <c>1</c>.</param>
/// <param name="RawName">Raw call name when <see cref="Count"/> is <c>1</c>.</param>
/// <param name="Error">Error message when <see cref="Count"/> is <c>1</c> and the call failed.</param>
public sealed record CallTimelinePointDto(
    double AtSeconds,
    double BucketWidthSeconds,
    double DurationMs,
    double TypicalMs,
    int Count,
    int SlowCount,
    int FailedCount,
    long? SampleId = null,
    int? Sequence = null,
    int? Entry = null,
    int? StatusCode = null,
    string? RawName = null,
    string? Error = null);

/// <summary>Full drill-down payload for one endpoint in <c>GET /api/runs/{runId}/calls/detail</c>.</summary>
/// <param name="Summary">Aggregate metrics summary for the requested endpoint.</param>
/// <param name="RunElapsedSeconds">Total elapsed duration of the run in seconds.</param>
/// <param name="Timeline">Bounded duration-over-time series for the filtered calls.</param>
/// <param name="Histogram">Duration distribution histogram buckets for the filtered calls.</param>
/// <param name="MatchingCalls">Total number of individual calls matching all active filters before paging.</param>
/// <param name="Offset">Zero-based paging offset applied to <see cref="Calls"/>.</param>
/// <param name="Limit">Maximum number of call samples requested per page.</param>
/// <param name="Calls">Paged list of individual call samples matching the active filters.</param>
public sealed record CallEndpointDetailDto(
    CallEndpointSummaryDto Summary,
    double RunElapsedSeconds,
    IReadOnlyList<CallTimelinePointDto> Timeline,
    IReadOnlyList<CallHistogramBucketDto> Histogram,
    long MatchingCalls,
    int Offset,
    int Limit,
    IReadOnlyList<CallSampleDto> Calls);

/// <summary>One individual slow/outlier call returned by <c>GET /api/runs/{runId}/calls/slowest</c> and <c>beesharp runs calls --slowest</c>.</summary>
/// <param name="Id">Unique call record identifier.</param>
/// <param name="Sequence">Zero-based global bee execution sequence number.</param>
/// <param name="BeeName">1-based display label of the bee (for example <c>B1</c>).</param>
/// <param name="Entry">Zero-based scenario entry index.</param>
/// <param name="EntryLabel">Optional display label of the scenario entry.</param>
/// <param name="Step">Zero-based step index in which the call occurred.</param>
/// <param name="StepName">Optional human-readable step name.</param>
/// <param name="Rep">Zero-based repetition index within the step.</param>
/// <param name="StartedAtSeconds">Run-relative timestamp in seconds when the call started.</param>
/// <param name="DurationMs">Total call duration in milliseconds.</param>
/// <param name="StatusCode">Optional HTTP or custom status code returned by the call.</param>
/// <param name="Bytes">Number of payload bytes transferred.</param>
/// <param name="Error">Optional error message if the call failed.</param>
/// <param name="Endpoint">Canonical normalized endpoint label.</param>
/// <param name="Url">Optional full request URL.</param>
/// <param name="IsSlow"><see langword="true"/> when the call succeeded and exceeded its effective slow threshold.</param>
/// <param name="SlowThresholdMs">Effective slow threshold in milliseconds applied to this call.</param>
/// <param name="TtfbMs">Optional time-to-first-byte in milliseconds when HTTP timing split is enabled.</param>
/// <param name="ResponseReadMs">Optional response body read duration in milliseconds when HTTP timing split is enabled.</param>
/// <param name="Headers">Optional captured request/response headers for slow or failed calls.</param>
/// <param name="TraceId">W3C trace ID of the actor's distributed trace this call belongs to (brain 0063), or <see langword="null"/>.</param>
/// <param name="SpanId">W3C span ID of this call's span (brain 0063), or <see langword="null"/>.</param>
public sealed record SlowestCallDto(
    long Id,
    int Sequence,
    string BeeName,
    int Entry,
    string? EntryLabel,
    int Step,
    string? StepName,
    int Rep,
    double StartedAtSeconds,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    string Endpoint,
    string? Url,
    bool IsSlow,
    double? SlowThresholdMs,
    double? TtfbMs = null,
    double? ResponseReadMs = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? TraceId = null,
    string? SpanId = null);

/// <summary>Top-N slowest outlier calls payload for <c>GET /api/runs/{runId}/calls/slowest</c>.</summary>
/// <param name="Calls">Top-N slowest calls ordered by descending duration.</param>
/// <param name="MatchingCalls">Total number of matching calls prior to applying the top-N limit.</param>
public sealed record SlowestCallsResult(
    IReadOnlyList<SlowestCallDto> Calls,
    long MatchingCalls);
