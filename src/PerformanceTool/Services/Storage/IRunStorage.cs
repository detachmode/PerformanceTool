using PerformanceTool.Services;

namespace PerformanceTool.Services.Storage;

public sealed record StoredRunSummary(
    string RunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string State,
    double ElapsedSeconds,
    int TotalActors,
    int FailedActors,
    long TotalLogs,
    string? Error);

public sealed record StoredActorDetail(
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
    string ActorName,
    int Entry,
    double AtSeconds,
    string Level,
    string Message,
    string? Exception,
    int? CallIndex,
    int ActorOutcome,
    bool IsFailed,
    bool IsSlow);

public sealed record LogQuery(
    int? Entry = null,
    int? Actor = null,
    string? ActorName = null,
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

public sealed record RunExportResult(
    string FileName,
    string ContentType,
    byte[] Content);

/// <summary>
/// Pluggable persistence layer for runs, actors, calls and logs (brain 0045, 0046).
/// Implementations must accept high-throughput writes without blocking <see cref="LoadRunner"/> actors.
/// </summary>
public interface IRunStorage : IAsyncDisposable
{
    string ProviderName { get; }

    void EnqueueRunStarted(string runId, DateTimeOffset startedUtc, LoadPlan plan);
    void EnqueueActorCompleted(string runId, ExecutionResult result, int actorOutcome, int slowCalls);
    void EnqueueRunFinished(string runId, DateTimeOffset finishedUtc, string state, double elapsedSeconds, string? error);

    /// <summary>Waits until all currently queued writes are committed to storage.</summary>
    Task FlushAsync(CancellationToken ct = default);

    Task<IReadOnlyList<StoredRunSummary>> ListRunsAsync(int limit = 50, CancellationToken ct = default);
    Task<StoredActorDetail?> GetActorDetailAsync(string runId, int sequence, CancellationToken ct = default);
    Task<LogPageResult> QueryLogsAsync(string runId, LogQuery query, CancellationToken ct = default);

    /// <summary>Exports one run in the requested format (<c>json</c>, <c>csv</c>, <c>parquet</c>, or <c>duckdb</c>).</summary>
    Task<RunExportResult?> ExportRunAsync(string runId, string format, CancellationToken ct = default);
}
