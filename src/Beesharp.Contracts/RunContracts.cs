using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Contracts;

// /api/runs: the active run (incremental polling), stored runs, one bee, log pages (brain 0045, 0054).
// Indexes are 0-based, like the rest of /api/runs.

/// <summary>Compact bee summary returned by incremental <c>/api/runs/current</c> polling (no calls/logs embedded).</summary>
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
    int FailedRep = -1);

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

public sealed record LiveRunningBeeDto(int Sequence, int Entry, double StartedAt, int Step = -1, int Rep = 0, IReadOnlyList<RecordedStepVisit>? StepVisits = null);

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
    IReadOnlyList<IReadOnlyList<StepInfo>>? Steps = null);

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
    IReadOnlyList<RecordedLog> Logs,
    int FailedStep = -1,
    int FailedRep = -1,
    IReadOnlyList<RecordedStepVisit>? StepVisits = null);

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

public sealed record LogPageResult(
    long TotalCount,
    int Offset,
    int Limit,
    IReadOnlyList<StoredLogRow> Items);

/// <summary>Lines of one step of a scenario entry in the logs, and how many of them are errors (brain 0057).</summary>
public sealed record LogStepCount(int Step, long Count, long Errors);

/// <summary>
/// The counts behind the Logs page's filter column (brain 0057), <c>GET /api/runs/{runId}/logs/facets</c>. <see cref="Total"/> is the number of lines the whole filter matches;
/// <see cref="Levels"/> counts per level name (ignoring the level filter), <see cref="Entries"/> per scenario entry (index = entry; ignoring the entry and step filters),
/// <see cref="Steps"/> per step of the filtered entry (empty without an entry filter; ignoring the step and level filters).
/// </summary>
public sealed record LogFacets(
    long Total,
    IReadOnlyDictionary<string, long> Levels,
    IReadOnlyList<long> Entries,
    IReadOnlyList<LogStepCount> Steps);

public sealed record StartRunRequest(LoadPlan? Plan = null, string? EnvironmentName = null);
