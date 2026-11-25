using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Contracts;

// /api/runs: the active run (incremental polling), stored runs, one bee, log pages (brain 0045, 0054).
// Indexes are 0-based, like the rest of /api/runs.

/// <summary>Compact bee summary returned by incremental <c>/api/runs/current</c> polling (no calls/logs embedded).</summary>
/// <param name="Sequence">Zero-based global bee execution sequence number.</param>
/// <param name="Entry">Zero-based scenario entry index in the load plan.</param>
/// <param name="StartedAt">Run-relative start time in seconds.</param>
/// <param name="DurationMs">Total execution duration of the bee in milliseconds.</param>
/// <param name="StatusCode">Optional HTTP or scenario status code summarizing the bee.</param>
/// <param name="Bytes">Total bytes transferred by the bee.</param>
/// <param name="Error">Error message if the bee failed or was canceled; <see langword="null"/> on success.</param>
/// <param name="SlowCalls">Number of slow calls recorded during this bee's execution.</param>
/// <param name="FailedStep">Zero-based index of the step that failed, or <c>-1</c> if not applicable.</param>
/// <param name="FailedRep">Zero-based repetition index of the failed step, or <c>-1</c> if not applicable.</param>
/// <param name="RatioCode">Encoded worst call-to-typical latency ratio byte used for graded colour rendering.</param>
public sealed record LiveBeeDto(
    int Sequence,
    int Entry,
    double StartedAt,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    int SlowCalls,
    int FailedStep = -1,
    int FailedRep = -1,
    byte RatioCode = 0);

/// <summary>Live execution counters for a single scenario entry in <see cref="LiveRunStatusDto.Scenarios"/>.</summary>
/// <param name="Starts">Total number of bees started for this scenario entry.</param>
/// <param name="Finished">Total number of bees finished (succeeded or failed) for this entry.</param>
/// <param name="InFlight">Number of bees currently executing for this entry.</param>
/// <param name="Failed">Total number of failed or canceled bees for this entry.</param>
/// <param name="SlowBees">Number of successful bees that contained at least one slow call.</param>
/// <param name="SlowCalls">Total number of slow calls across all bees in this entry.</param>
/// <param name="Dropped">Number of scheduled bees dropped because concurrency/capacity limits were reached.</param>
/// <param name="FailedPerStep">Optional per-step failure counts aligned with the scenario's declared steps.</param>
/// <param name="InFlightPerStep">Optional per-step in-flight bee counts aligned with the scenario's declared steps.</param>
public sealed record LiveScenarioViewDto(
    int Starts,
    int Finished,
    int InFlight,
    int Failed,
    int SlowBees,
    int SlowCalls,
    int Dropped,
    IReadOnlyList<int>? FailedPerStep = null,
    IReadOnlyList<int>? InFlightPerStep = null);

/// <summary>Snapshot of a bee currently in flight during an active run.</summary>
/// <param name="Sequence">Zero-based global bee execution sequence number.</param>
/// <param name="Entry">Zero-based scenario entry index in the load plan.</param>
/// <param name="StartedAt">Run-relative start time in seconds.</param>
/// <param name="Step">Zero-based index of the step currently executing, or <c>-1</c> if unknown.</param>
/// <param name="Rep">Zero-based repetition index within the current step.</param>
/// <param name="StepVisits">Optional list of declared step visits recorded so far for this bee.</param>
public sealed record LiveRunningBeeDto(int Sequence, int Entry, double StartedAt, int Step = -1, int Rep = 0, IReadOnlyList<RecordedStepVisit>? StepVisits = null);

/// <summary>Structured log entry emitted during an active run or live script test run.</summary>
/// <param name="Id">Unique log line identifier.</param>
/// <param name="Sequence">Zero-based bee sequence number, or negative for program/host logs.</param>
/// <param name="BeeName">1-based bee label (for example <c>B1</c>) or program/host label.</param>
/// <param name="Entry">Zero-based scenario entry index, or a <see cref="ProgramLogEntry"/> sentinel value.</param>
/// <param name="AtSeconds">Run-relative timestamp in seconds when the log entry was emitted.</param>
/// <param name="Timestamp">UTC wall-clock timestamp when the log entry was emitted.</param>
/// <param name="Level">Log severity level string.</param>
/// <param name="Message">Formatted log message text.</param>
/// <param name="Exception">Optional formatted exception details associated with the log entry.</param>
/// <param name="Step">Zero-based step index during which the log was emitted.</param>
/// <param name="Rep">Zero-based repetition index within the step.</param>
public sealed record LiveLogEntryDto(
    long Id,
    int Sequence,
    string BeeName,
    int Entry,
    double AtSeconds,
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Exception,
    int Step = 0,
    int Rep = 0);

/// <summary>Incremental status snapshot of the active (or most recently finished) load test run.</summary>
/// <param name="RunId">Unique run identifier.</param>
/// <param name="State">Lifecycle state of the run (for example <c>Idle</c>, <c>Preflight</c>, <c>Running</c>, <c>Stopping</c>, <c>Shutdown</c>, <c>Completed</c>, <c>Failed</c>, or <c>Stopped</c>).</param>
/// <param name="ElapsedSeconds">Elapsed run duration in seconds.</param>
/// <param name="StartedUtc">UTC timestamp when the run started.</param>
/// <param name="Plan">Load plan being executed by this run.</param>
/// <param name="Scenarios">Per-entry live counters indexed by 0-based scenario entry.</param>
/// <param name="NewBees">Newly completed bees since the caller's last polled sequence cursor.</param>
/// <param name="LastSequence">Highest completed bee sequence number included in this snapshot, or <c>-1</c> if none.</param>
/// <param name="TotalLogs">Total number of log lines recorded so far in the run.</param>
/// <param name="StorageProvider">Active run storage provider name (for example <c>DuckDb</c> or <c>Memory</c>).</param>
/// <param name="Error">Optional run-level error message if the run failed.</param>
/// <param name="PreflightState">Optional execution state of the run's preflight program.</param>
/// <param name="PreflightScriptName">Optional display name of the run's preflight program.</param>
/// <param name="PreflightLogs">Optional log lines emitted by the preflight program.</param>
/// <param name="PreflightError">Optional error message if the preflight program failed.</param>
/// <param name="RunningBees">Optional list of bees currently in flight.</param>
/// <param name="ShutdownState">Optional execution state of the run's shutdown program.</param>
/// <param name="ShutdownScriptName">Optional display name of the run's shutdown program.</param>
/// <param name="ShutdownLogs">Optional log lines emitted by the shutdown program.</param>
/// <param name="ShutdownError">Optional error message if the shutdown program failed.</param>
/// <param name="RuntimeVersion">Monotonically increasing version of the run's runtime data bags.</param>
/// <param name="Steps">Optional declared step definitions per scenario entry.</param>
/// <param name="NextBeeIndex">Next 0-based bee sequence index to be assigned.</param>
public sealed record LiveRunStatusDto(
    string RunId,
    string State,
    double ElapsedSeconds,
    DateTimeOffset StartedUtc,
    LoadPlan Plan,
    IReadOnlyList<LiveScenarioViewDto> Scenarios,
    IReadOnlyList<LiveBeeDto> NewBees,
    int LastSequence,
    long TotalLogs,
    string StorageProvider,
    string? Error,
    string? PreflightState = null,
    string? PreflightScriptName = null,
    IReadOnlyList<string>? PreflightLogs = null,
    string? PreflightError = null,
    IReadOnlyList<LiveRunningBeeDto>? RunningBees = null,
    string? ShutdownState = null,
    string? ShutdownScriptName = null,
    IReadOnlyList<string>? ShutdownLogs = null,
    string? ShutdownError = null,
    long RuntimeVersion = 0,
    IReadOnlyList<IReadOnlyList<StepInfo>>? Steps = null,
    int NextBeeIndex = 0);

/// <summary>Reserved negative entry indices for run-level logs (preflight / shutdown / host) in <see cref="StoredLogRow.Entry"/>.</summary>
public static class ProgramLogEntry
{
    /// <summary>Sentinel entry index for logs emitted by the preflight program.</summary>
    public const int Preflight = -1;

    /// <summary>Sentinel entry index for logs emitted by the shutdown program.</summary>
    public const int Shutdown = -2;

    /// <summary>Sentinel entry index for host/run-manager lifecycle logs.</summary>
    public const int Host = -3;
}

/// <summary>Persisted result and log lines of a run's preflight or shutdown program.</summary>
/// <param name="State">Final execution state of the program.</param>
/// <param name="Name">Display name of the executed program script.</param>
/// <param name="Error">Optional error message if the program failed.</param>
/// <param name="Logs">Log lines emitted by the program.</param>
public sealed record StoredProgramRunDto(
    string State,
    string Name,
    string? Error,
    IReadOnlyList<string> Logs);

/// <summary>Summary metadata for a persisted load test run returned by <c>GET /api/runs</c>.</summary>
/// <param name="RunId">Unique run identifier.</param>
/// <param name="StartedUtc">UTC timestamp when the run started.</param>
/// <param name="FinishedUtc">UTC timestamp when the run finished, or <see langword="null"/> if still running.</param>
/// <param name="State">Final or current run state string.</param>
/// <param name="ElapsedSeconds">Total elapsed run duration in seconds.</param>
/// <param name="TotalBees">Total number of executed bees in the run.</param>
/// <param name="FailedBees">Number of failed or canceled bees in the run.</param>
/// <param name="TotalLogs">Total number of log rows stored for the run.</param>
/// <param name="Error">Optional run-level error message.</param>
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

/// <summary>Full persisted execution details for one bee in <c>GET /api/runs/{runId}/bees/{sequence}</c>.</summary>
/// <param name="RunId">Identifier of the run containing this bee.</param>
/// <param name="Sequence">Zero-based global bee execution sequence number.</param>
/// <param name="Entry">Zero-based scenario entry index in the load plan.</param>
/// <param name="StartedAt">Run-relative start time in seconds.</param>
/// <param name="DurationMs">Total bee execution duration in milliseconds.</param>
/// <param name="StatusCode">Optional HTTP or scenario status code summarizing the bee.</param>
/// <param name="Bytes">Total bytes transferred by the bee.</param>
/// <param name="Error">Error message if the bee failed or was canceled; <see langword="null"/> on success.</param>
/// <param name="Calls">Ordered list of calls recorded by this bee.</param>
/// <param name="Logs">Ordered list of log entries emitted by this bee.</param>
/// <param name="FailedStep">Zero-based index of the step that failed, or <c>-1</c> if not applicable.</param>
/// <param name="FailedRep">Zero-based repetition index of the failed step, or <c>-1</c> if not applicable.</param>
/// <param name="StepVisits">Optional list of declared step visits recorded during the bee's execution.</param>
/// <param name="EntryLabel">Optional display label of the scenario entry.</param>
/// <param name="Steps">Optional declared step metadata for the bee's scenario entry.</param>
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
    IReadOnlyList<RecordedLog> Logs,
    int FailedStep = -1,
    int FailedRep = -1,
    IReadOnlyList<RecordedStepVisit>? StepVisits = null,
    string? EntryLabel = null,
    IReadOnlyList<StepInfo>? Steps = null);

/// <summary>One persisted log row returned in a paged log query.</summary>
/// <param name="Id">Unique log row identifier within the run.</param>
/// <param name="Sequence">Zero-based bee sequence number, or negative for program/host logs.</param>
/// <param name="BeeName">1-based bee label (for example <c>B1</c>) or program/host label.</param>
/// <param name="Entry">Zero-based scenario entry index, or a <see cref="ProgramLogEntry"/> sentinel value.</param>
/// <param name="AtSeconds">Run-relative timestamp in seconds when the log entry was emitted.</param>
/// <param name="Level">Log severity level string.</param>
/// <param name="Message">Formatted log message text.</param>
/// <param name="Exception">Optional formatted exception details associated with the log entry.</param>
/// <param name="CallIndex">Optional zero-based call index associated with this log entry.</param>
/// <param name="BeeOutcome">Numeric outcome code of the emitting bee.</param>
/// <param name="IsFailed"><see langword="true"/> when the emitting bee failed or was canceled.</param>
/// <param name="IsSlow"><see langword="true"/> when the emitting bee was classified as slow.</param>
/// <param name="Step">Zero-based step index during which the log was emitted.</param>
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
    bool IsSlow,
    int Step = 0);

/// <summary>Paged log query response returned by <c>GET /api/runs/{runId}/logs</c>.</summary>
/// <param name="TotalCount">Total number of log rows matching the active filters across all pages.</param>
/// <param name="Offset">Zero-based row offset of this page.</param>
/// <param name="Limit">Maximum number of rows requested for this page.</param>
/// <param name="Items">Log rows in the requested page.</param>
public sealed record LogPageResult(
    long TotalCount,
    int Offset,
    int Limit,
    IReadOnlyList<StoredLogRow> Items);

/// <summary>Lines of one step of a scenario entry in the logs, and how many of them are errors (brain 0059).</summary>
/// <param name="Step">Zero-based step index.</param>
/// <param name="Count">Total number of matching log lines in this step.</param>
/// <param name="Errors">Number of error-level log lines in this step.</param>
public sealed record LogStepCount(int Step, long Count, long Errors);

/// <summary>
/// The counts behind the Logs page's filter column (brain 0059), <c>GET /api/runs/{runId}/logs/facets</c>. <see cref="Total"/> is the number of lines the whole filter matches;
/// <see cref="Levels"/> counts per level name (ignoring the level filter), <see cref="Entries"/> per scenario entry (index = entry; ignoring the entry and step filters),
/// <see cref="Steps"/> per step of the filtered entry (empty without an entry filter; ignoring the step and level filters).
/// </summary>
/// <param name="Total">Total number of log lines matching all active filters.</param>
/// <param name="Levels">Log counts grouped by severity level name (ignoring the level filter).</param>
/// <param name="Entries">Log counts per zero-based scenario entry index (ignoring entry and step filters).</param>
/// <param name="Steps">Log counts per step of the currently filtered entry (empty if no entry filter is active).</param>
/// <param name="Preflight">Number of matching preflight program log lines.</param>
/// <param name="Shutdown">Number of matching shutdown program log lines.</param>
/// <param name="Host">Number of matching host lifecycle log lines.</param>
public sealed record LogFacets(
    long Total,
    IReadOnlyDictionary<string, long> Levels,
    IReadOnlyList<long> Entries,
    IReadOnlyList<LogStepCount> Steps,
    long Preflight = 0,
    long Shutdown = 0,
    long Host = 0);

/// <summary>Request payload for starting a new load test run.</summary>
/// <param name="Plan">Optional load plan to execute; when <see langword="null"/>, the server's live workspace plan is used.</param>
/// <param name="EnvironmentName">Optional environment name supplying variables and secrets for the run.</param>
public sealed record StartRunRequest(LoadPlan? Plan = null, string? EnvironmentName = null);
