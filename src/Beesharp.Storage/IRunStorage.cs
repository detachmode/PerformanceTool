using Beesharp.Contracts;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Services.Storage;

public sealed record CallEndpointsQuery(
    int? Entry = null,
    bool SlowOnly = false,
    bool FailedOnly = false,
    double? MinMs = null,
    string? Search = null);

public sealed record CallDetailQuery(
    string Endpoint,
    int? Entry = null,
    string? Show = null,
    double? MinMs = null,
    double? MaxMs = null,
    double? FromSec = null,
    double? ToSec = null,
    string? Sort = null,
    int Offset = 0,
    int Limit = 200,
    int MaxPoints = 240);

public sealed record LogQuery(
    int? Entry = null,
    int? Bee = null,
    string? BeeName = null,
    int? Outcome = null,
    bool? Failed = null,
    bool? Slow = null,
    IReadOnlyList<string>? Levels = null,
    string? Search = null,
    int Offset = 0,
    int Limit = 200,
    int? Step = null);

public sealed record StoredBeeSummary(
    int Sequence,
    int Entry,
    double StartedAt,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    int SlowCalls,
    int FailedStep = -1,
    int FailedRep = -1);

public sealed record StoredRunData(
    string RunId,
    DateTimeOffset StartedUtc,
    string State,
    double ElapsedSeconds,
    LoadPlan Plan,
    IReadOnlyList<StoredBeeSummary> Bees,
    long TotalLogs,
    string? Error,
    IReadOnlyList<IReadOnlyList<StepInfo>>? Steps = null,
    StoredProgramRunDto? Preflight = null,
    StoredProgramRunDto? Shutdown = null);

public sealed record RunExportResult(
    string FileName,
    string ContentType,
    byte[] Content);

/// <summary>
/// Pluggable persistence layer for runs, bees, calls and logs (brain 0045, 0046).
/// Implementations must accept high-throughput writes without blocking <see cref="LoadRunner"/> bees.
/// </summary>
public interface IRunStorage : IAsyncDisposable
{
    string ProviderName { get; }

    void EnqueueRunStarted(string runId, DateTimeOffset startedUtc, LoadPlan plan, IReadOnlyList<IReadOnlyList<StepInfo>>? steps = null);
    void EnqueueBeeCompleted(string runId, ExecutionResult result, int beeOutcome, int slowCalls);
    void EnqueueHostLog(string runId, double atSeconds, string level, string message, string? exception = null);
    void EnqueueRunFinished(string runId, DateTimeOffset finishedUtc, string state, double elapsedSeconds, string? error);

    /// <summary>
    /// Stores the final runtime data of a run (brain 0052) as JSON: the values as they were at the end, no history. Written once, after the shutdown program.
    /// A second call for the same run replaces the first.
    /// </summary>
    Task SaveRuntimeDataAsync(string runId, string json, CancellationToken ct = default);

    /// <summary>The final runtime data of a run as JSON, or null when the run kept none.</summary>
    Task<string?> GetRuntimeDataAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Stores a run-level program's result and log lines (<paramref name="kind"/> is <c>"preflight"</c> or <c>"shutdown"</c>)
    /// and writes its log lines into the run's log stream so they are queryable on the Logs page.
    /// </summary>
    Task SaveProgramRunAsync(string runId, string kind, StoredProgramRunDto program, CancellationToken ct = default);

    /// <summary>Waits until all currently queued writes are committed to storage.</summary>
    Task FlushAsync(CancellationToken ct = default);

    Task<IReadOnlyList<StoredRunSummary>> ListRunsAsync(int limit = 50, CancellationToken ct = default);
    Task<StoredRunData?> GetRunAsync(string runId, CancellationToken ct = default);
    Task<StoredBeeDetail?> GetBeeDetailAsync(string runId, int sequence, CancellationToken ct = default);
    Task<LogPageResult> QueryLogsAsync(string runId, LogQuery query, CancellationToken ct = default);

    /// <summary>
    /// The counts behind the Logs page's filter column (brain 0059) for the filters in <paramref name="query"/> (offset and limit are ignored): how many lines per level,
    /// per scenario entry, and, when the query names an entry, per step with its error lines. Each group ignores its own filter, so the other choices show what they would give.
    /// </summary>
    Task<LogFacets> GetLogFacetsAsync(string runId, LogQuery query, CancellationToken ct = default);

    /// <summary>Auto-detected endpoint summaries and aggregate call counts for the Calls analysis page (brain 0060).</summary>
    Task<CallEndpointsResult> GetCallEndpointsAsync(string runId, CallEndpointsQuery query, CancellationToken ct = default);

    /// <summary>Duration-over-time timeline, latency histogram, and paged calls for one endpoint (brain 0060).</summary>
    Task<CallEndpointDetailDto?> GetCallEndpointDetailAsync(string runId, CallDetailQuery query, CancellationToken ct = default);

    /// <summary>Exports one run in the requested format (<c>json</c>, <c>csv</c>, <c>parquet</c>, or <c>duckdb</c>).</summary>
    Task<RunExportResult?> ExportRunAsync(string runId, string format, CancellationToken ct = default);

    /// <summary>Imports a previously exported run file (<c>.zip</c> Parquet bundle, <c>.duckdb</c>, or <c>.json</c>) into storage.</summary>
    Task<StoredRunSummary> ImportRunAsync(string fileName, Stream content, CancellationToken ct = default);
}
