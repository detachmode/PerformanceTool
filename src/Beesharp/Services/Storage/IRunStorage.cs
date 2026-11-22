using PerformanceTool.Services;

namespace PerformanceTool.Services.Storage;

public sealed record StoredRunSummary(
    string RunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string State,
    double ElapsedSeconds,
    int TotalBees,
    int FailedBees,
    long TotalLogs,
    string? Error);

public sealed record StoredBeeDetail(
    string RunId,
    int Sequence,
    int Entry,
    double StartedAt,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    IReadOnlyList<RecordedCall> Calls,
    IReadOnlyList<RecordedLog> Logs);

public sealed record StoredLogRow(
    long Id,
    int Sequence,
    string BeeName,
    int Entry,
    double AtSeconds,
    string Level,
    string Message,
    string? Exception,
    int? CallIndex,
    int BeeOutcome,
    bool IsFailed,
    bool IsSlow);

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
    int Limit = 200);

public sealed record LogPageResult(
    long TotalCount,
    int Offset,
    int Limit,
    IReadOnlyList<StoredLogRow> Items);

public sealed record StoredBeeSummary(
    int Sequence,
    int Entry,
    double StartedAt,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    int SlowCalls);

public sealed record StoredRunData(
    string RunId,
    DateTimeOffset StartedUtc,
    string State,
    double ElapsedSeconds,
    LoadPlan Plan,
    IReadOnlyList<StoredBeeSummary> Bees,
    long TotalLogs,
    string? Error);

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

    void EnqueueRunStarted(string runId, DateTimeOffset startedUtc, LoadPlan plan);
    void EnqueueBeeCompleted(string runId, ExecutionResult result, int beeOutcome, int slowCalls);
    void EnqueueRunFinished(string runId, DateTimeOffset finishedUtc, string state, double elapsedSeconds, string? error);

    /// <summary>
    /// Stores the final runtime data of a run (brain 0052) as JSON: the values as they were at the end, no history. Written once, after the shutdown program.
    /// A second call for the same run replaces the first.
    /// </summary>
    Task SaveRuntimeDataAsync(string runId, string json, CancellationToken ct = default);

    /// <summary>The final runtime data of a run as JSON, or null when the run kept none.</summary>
    Task<string?> GetRuntimeDataAsync(string runId, CancellationToken ct = default);

    /// <summary>Waits until all currently queued writes are committed to storage.</summary>
    Task FlushAsync(CancellationToken ct = default);

    Task<IReadOnlyList<StoredRunSummary>> ListRunsAsync(int limit = 50, CancellationToken ct = default);
    Task<StoredRunData?> GetRunAsync(string runId, CancellationToken ct = default);
    Task<StoredBeeDetail?> GetBeeDetailAsync(string runId, int sequence, CancellationToken ct = default);
    Task<LogPageResult> QueryLogsAsync(string runId, LogQuery query, CancellationToken ct = default);

    /// <summary>Exports one run in the requested format (<c>json</c>, <c>csv</c>, <c>parquet</c>, or <c>duckdb</c>).</summary>
    Task<RunExportResult?> ExportRunAsync(string runId, string format, CancellationToken ct = default);
}
