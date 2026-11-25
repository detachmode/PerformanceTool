namespace Beesharp.Contracts;

/// <summary>Aggregated performance summary for one auto-detected (or explicitly named) endpoint across a run (brain 0060).</summary>
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
    string? StepName = null);

/// <summary>Aggregated call latency and slow/failure counts for one (entry, step) pair.</summary>
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
public sealed record CallEndpointsResult(
    IReadOnlyList<CallEndpointSummaryDto> Endpoints,
    long TotalCalls,
    long TotalSlowCalls,
    long TotalFailedCalls);

/// <summary>One individual call execution belonging to an endpoint, with exact raw URL, bee link, and slow evaluation.</summary>
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
    double? SlowThresholdMs);

/// <summary>One duration-distribution bucket in an endpoint's histogram.</summary>
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
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>Top-N slowest outlier calls payload for <c>GET /api/runs/{runId}/calls/slowest</c>.</summary>
public sealed record SlowestCallsResult(
    IReadOnlyList<SlowestCallDto> Calls,
    long MatchingCalls);
