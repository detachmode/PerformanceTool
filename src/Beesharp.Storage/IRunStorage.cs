using Beesharp.Contracts;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Services.Storage;

/// <summary>Filter parameters for querying endpoint call summaries in a stored run.</summary>
/// <param name="Entry">Optional zero-based scenario entry index filter.</param>
/// <param name="SlowOnly">When <see langword="true"/>, include only slow calls.</param>
/// <param name="FailedOnly">When <see langword="true"/>, include only failed calls.</param>
/// <param name="MinMs">Optional minimum call duration in milliseconds.</param>
/// <param name="Search">Optional substring filter matched against endpoint, URL, or name.</param>
/// <param name="Step">Optional zero-based step index filter.</param>
/// <param name="ByStep">When <see langword="true"/>, group endpoint summaries by step as well as endpoint.</param>
/// <param name="Scenario">Optional scenario ID or label filter.</param>
/// <param name="Endpoint">Optional exact endpoint key filter.</param>
public sealed record CallEndpointsQuery(
    int? Entry = null,
    bool SlowOnly = false,
    bool FailedOnly = false,
    double? MinMs = null,
    string? Search = null,
    int? Step = null,
    bool ByStep = false,
    string? Scenario = null,
    string? Endpoint = null);

/// <summary>Filter parameters for querying the slowest individual calls in a stored run.</summary>
/// <param name="Limit">Maximum number of slowest calls to return.</param>
/// <param name="Entry">Optional zero-based scenario entry index filter.</param>
/// <param name="Scenario">Optional scenario ID or label filter.</param>
/// <param name="Step">Optional zero-based step index filter.</param>
/// <param name="Endpoint">Optional endpoint key filter.</param>
/// <param name="SlowOnly">When <see langword="true"/>, include only slow calls.</param>
/// <param name="FailedOnly">When <see langword="true"/>, include only failed calls.</param>
/// <param name="MinMs">Optional minimum call duration in milliseconds.</param>
/// <param name="Search">Optional substring filter matched against endpoint, URL, name, or error.</param>
public sealed record SlowestCallsQuery(
    int Limit = 20,
    int? Entry = null,
    string? Scenario = null,
    int? Step = null,
    string? Endpoint = null,
    bool SlowOnly = false,
    bool FailedOnly = false,
    double? MinMs = null,
    string? Search = null);

/// <summary>Query parameters for inspecting timeline, histogram, and individual calls of a single endpoint.</summary>
/// <param name="Endpoint">The endpoint identifier to inspect.</param>
/// <param name="Entry">Optional zero-based scenario entry index filter.</param>
/// <param name="Show">Optional outcome filter (e.g. slow or failed).</param>
/// <param name="MinMs">Optional minimum call duration in milliseconds.</param>
/// <param name="MaxMs">Optional maximum call duration in milliseconds.</param>
/// <param name="FromSec">Optional inclusive start offset in run seconds.</param>
/// <param name="ToSec">Optional inclusive end offset in run seconds.</param>
/// <param name="Sort">Optional sort order for the paged call list.</param>
/// <param name="Offset">Zero-based row offset for paging individual calls.</param>
/// <param name="Limit">Maximum number of individual calls to return per page.</param>
/// <param name="MaxPoints">Maximum number of timeline points/buckets to aggregate.</param>
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

/// <summary>Filter and paging parameters for querying stored run logs.</summary>
/// <param name="Entry">Optional scenario entry index filter (or synthetic <c>ProgramLogEntry</c> value).</param>
/// <param name="Bee">Optional zero-based bee sequence number filter.</param>
/// <param name="BeeName">Optional display bee name filter (e.g. <c>B1</c>).</param>
/// <param name="Outcome">Optional bee outcome code filter.</param>
/// <param name="Failed">Optional filter for logs belonging to failed bees or programs.</param>
/// <param name="Slow">Optional filter for logs belonging to slow bees.</param>
/// <param name="Levels">Optional list of log level names to include.</param>
/// <param name="Search">Optional case-insensitive substring filter across message, exception, and bee name.</param>
/// <param name="Offset">Zero-based row offset for paging.</param>
/// <param name="Limit">Maximum number of log rows to return.</param>
/// <param name="Step">Optional zero-based step index filter.</param>
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

/// <summary>Summary metrics for one persisted bee execution within a stored run.</summary>
/// <param name="Sequence">Zero-based bee sequence number within the run.</param>
/// <param name="Entry">Zero-based scenario entry index the bee executed.</param>
/// <param name="StartedAt">Start offset in seconds from run start.</param>
/// <param name="DurationMs">Total execution duration in milliseconds.</param>
/// <param name="StatusCode">Representative HTTP status code, if any.</param>
/// <param name="Bytes">Total bytes transferred by the bee.</param>
/// <param name="Error">Error message if the bee failed, or <see langword="null"/>.</param>
/// <param name="SlowCalls">Number of calls recorded as slow during this bee execution.</param>
/// <param name="FailedStep">Zero-based index of the step that failed, or <c>-1</c> if none.</param>
/// <param name="FailedRep">Zero-based repetition index of the failed step, or <c>-1</c> if none.</param>
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

/// <summary>Full stored record for a run, including its plan, bee summaries, step metadata, and program runs.</summary>
/// <param name="RunId">Unique run identifier.</param>
/// <param name="StartedUtc">UTC timestamp when the run started.</param>
/// <param name="State">Final or current run state string.</param>
/// <param name="ElapsedSeconds">Total elapsed duration of the run in seconds.</param>
/// <param name="Plan">Snapshot of the <see cref="LoadPlan"/> used for the run.</param>
/// <param name="Bees">Ordered list of persisted bee summaries.</param>
/// <param name="TotalLogs">Total number of log rows persisted for the run.</param>
/// <param name="Error">Run-level error message, if any.</param>
/// <param name="Steps">Optional step definitions per scenario entry.</param>
/// <param name="Preflight">Optional persisted preflight program execution result and logs.</param>
/// <param name="Shutdown">Optional persisted shutdown program execution result and logs.</param>
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

/// <summary>Serialized export artifact for a stored run.</summary>
/// <param name="FileName">Suggested download file name including extension.</param>
/// <param name="ContentType">MIME content type of the exported payload.</param>
/// <param name="Content">Raw exported bytes.</param>
public sealed record RunExportResult(
    string FileName,
    string ContentType,
    byte[] Content);

/// <summary>
/// Pluggable persistence layer for runs, bees, calls and logs (brain 0045, 0046).
/// Implementations must accept high-throughput writes without blocking LoadRunner bees.
/// </summary>
public interface IRunStorage : IAsyncDisposable
{
    /// <summary>Display name of the storage provider (for example, <c>DuckDb</c> or <c>Memory</c>).</summary>
    string ProviderName { get; }

    /// <summary>Enqueues a run-started event for persistence.</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="startedUtc">UTC timestamp when the run began.</param>
    /// <param name="plan">The load plan executed by the run.</param>
    /// <param name="steps">Optional step metadata per scenario entry.</param>
    void EnqueueRunStarted(string runId, DateTimeOffset startedUtc, LoadPlan plan, IReadOnlyList<IReadOnlyList<StepInfo>>? steps = null);

    /// <summary>Enqueues a completed bee execution and its recorded calls and logs for persistence.</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="result">Execution result produced by the bee.</param>
    /// <param name="beeOutcome">Numeric outcome classification for the bee.</param>
    /// <param name="slowCalls">Number of slow calls observed in <paramref name="result"/>.</param>
    void EnqueueBeeCompleted(string runId, ExecutionResult result, int beeOutcome, int slowCalls);

    /// <summary>Enqueues a host-level log entry associated with a run.</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="atSeconds">Elapsed run time in seconds when the log occurred.</param>
    /// <param name="level">Normalized log level name.</param>
    /// <param name="message">Log message text.</param>
    /// <param name="exception">Optional formatted exception details.</param>
    void EnqueueHostLog(string runId, double atSeconds, string level, string message, string? exception = null);

    /// <summary>Enqueues a run-finished event with final status and duration.</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="finishedUtc">UTC timestamp when the run finished.</param>
    /// <param name="state">Final run state string.</param>
    /// <param name="elapsedSeconds">Total run duration in seconds.</param>
    /// <param name="error">Optional run-level error message.</param>
    void EnqueueRunFinished(string runId, DateTimeOffset finishedUtc, string state, double elapsedSeconds, string? error);

    /// <summary>
    /// Stores the final runtime data of a run (brain 0052) as JSON: the values as they were at the end, no history. Written once, after the shutdown program.
    /// A second call for the same run replaces the first.
    /// </summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="json">Serialized runtime data snapshot JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveRuntimeDataAsync(string runId, string json, CancellationToken ct = default);

    /// <summary>The final runtime data of a run as JSON, or null when the run kept none.</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored runtime data JSON string, or <see langword="null"/> if unavailable.</returns>
    Task<string?> GetRuntimeDataAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Stores a run-level program's result and log lines (<paramref name="kind"/> is <c>"preflight"</c> or <c>"shutdown"</c>)
    /// and writes its log lines into the run's log stream so they are queryable on the Logs page.
    /// </summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="kind">Program kind (<c>"preflight"</c> or <c>"shutdown"</c>).</param>
    /// <param name="program">Program execution result and log lines.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveProgramRunAsync(string runId, string kind, StoredProgramRunDto program, CancellationToken ct = default);

    /// <summary>Waits until all currently queued writes are committed to storage.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>Lists recent stored runs ordered newest first.</summary>
    /// <param name="limit">Maximum number of runs to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of stored run summaries.</returns>
    Task<IReadOnlyList<StoredRunSummary>> ListRunsAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>Loads full details for a single stored run.</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored run data, or <see langword="null"/> if the run does not exist.</returns>
    Task<StoredRunData?> GetRunAsync(string runId, CancellationToken ct = default);

    /// <summary>Loads detailed execution data (calls, logs, step visits) for a single bee within a stored run.</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="sequence">Zero-based bee sequence number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored bee detail, or <see langword="null"/> if not found.</returns>
    Task<StoredBeeDetail?> GetBeeDetailAsync(string runId, int sequence, CancellationToken ct = default);

    /// <summary>Queries a page of persisted log rows for a run matching the specified filters.</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="query">Filter and paging parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A page of matching log rows and total match count.</returns>
    Task<LogPageResult> QueryLogsAsync(string runId, LogQuery query, CancellationToken ct = default);

    /// <summary>
    /// The counts behind the Logs page's filter column (brain 0059) for the filters in <paramref name="query"/> (offset and limit are ignored): how many lines per level,
    /// per scenario entry, and, when the query names an entry, per step with its error lines. Each group ignores its own filter, so the other choices show what they would give.
    /// </summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="query">Active filter criteria (paging fields are ignored).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated log facet counts for levels, entries, and steps.</returns>
    Task<LogFacets> GetLogFacetsAsync(string runId, LogQuery query, CancellationToken ct = default);

    /// <summary>Auto-detected endpoint summaries and aggregate call counts for the Calls analysis page (brain 0060).</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="query">Filter and grouping parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated endpoint summaries and total call statistics.</returns>
    Task<CallEndpointsResult> GetCallEndpointsAsync(string runId, CallEndpointsQuery query, CancellationToken ct = default);

    /// <summary>Duration-over-time timeline, latency histogram, and paged calls for one endpoint (brain 0060).</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="query">Endpoint selection, filter, and paging parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detailed call analytics for the endpoint, or <see langword="null"/> if the run is not found.</returns>
    Task<CallEndpointDetailDto?> GetCallEndpointDetailAsync(string runId, CallDetailQuery query, CancellationToken ct = default);

    /// <summary>Top-N slowest individual calls matching the optional filters.</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="query">Limit and filter parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The slowest matching calls and total match count.</returns>
    Task<SlowestCallsResult> GetSlowestCallsAsync(string runId, SlowestCallsQuery query, CancellationToken ct = default);

    /// <summary>Exports one run in the requested format (<c>json</c>, <c>csv</c>, <c>parquet</c>, or <c>duckdb</c>).</summary>
    /// <param name="runId">Unique run identifier.</param>
    /// <param name="format">Target export format.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The exported artifact, or <see langword="null"/> if the run does not exist.</returns>
    Task<RunExportResult?> ExportRunAsync(string runId, string format, CancellationToken ct = default);

    /// <summary>Imports a previously exported run file (<c>.zip</c> Parquet bundle, <c>.duckdb</c>, or <c>.json</c>) into storage.</summary>
    /// <param name="fileName">Original file name used to detect the import format.</param>
    /// <param name="content">Stream containing the exported run data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summary of the imported run.</returns>
    Task<StoredRunSummary> ImportRunAsync(string fileName, Stream content, CancellationToken ct = default);
}
