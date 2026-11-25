using System.Text.Json.Nodes;
using Beesharp.Services;

namespace Beesharp.Ai;

// The ports (brain 0047/0051): what the assistant needs from the app. The host implements them as thin adapters over its own services,
// the assistant never references the host. All indexes here are 0-based (entry index, bee sequence), like the app's own data;
// the tools translate to the 1-based numbers people see.

/// <summary>The Live plan being edited, as the server holds it.</summary>
/// <param name="Plan">The current Live load plan.</param>
/// <param name="PresetName">Name of the currently loaded preset, or empty if none.</param>
/// <param name="SavedPlan">The last saved preset snapshot used for dirty comparison.</param>
/// <param name="HasUnsavedChanges">Whether <paramref name="Plan"/> differs from <paramref name="SavedPlan"/>.</param>
/// <param name="Version">Monotonically increasing workspace revision number.</param>
public sealed record PlanState(LoadPlan Plan, string PresetName, LoadPlan SavedPlan, bool HasUnsavedChanges, long Version);

/// <summary>Port for reading and editing the Live load plan and managing saved presets.</summary>
public interface IPlanWorkspacePort
{
    /// <summary>Returns the current Live plan state snapshot.</summary>
    /// <returns>The current <see cref="PlanState"/>.</returns>
    PlanState Get();

    /// <summary>Changes a copy of the plan under the workspace's lock; <paramref name="edit"/> returns <see langword="false"/> when nothing changed.</summary>
    /// <param name="edit">Callback that mutates the cloned <see cref="LoadPlan"/> and returns whether changes were made.</param>
    /// <returns>The resulting <see cref="PlanState"/> after applying the edit.</returns>
    PlanState Update(Func<LoadPlan, bool> edit);

    /// <summary>Adds an entry with default parameters and returns its 0-based index.</summary>
    /// <param name="scenarioId">Identifier of the scenario to add to the plan.</param>
    /// <returns>The 0-based index of the newly added entry.</returns>
    int AddEntry(string scenarioId);

    /// <summary>Removes the scenario entry at the specified 0-based index.</summary>
    /// <param name="index">The 0-based index of the scenario entry to remove.</param>
    /// <returns>The updated <see cref="PlanState"/>.</returns>
    PlanState RemoveEntry(int index);

    /// <summary>Lists all saved presets available on the server.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of saved <see cref="PresetInfo"/> summaries.</returns>
    Task<IReadOnlyList<PresetInfo>> ListPresetsAsync(CancellationToken ct);

    /// <summary>Saves the current Live plan as a named preset.</summary>
    /// <param name="name">Name under which to save the preset.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SavePresetAsync(string name, CancellationToken ct);

    /// <summary>Loads a preset into the plan. Returns <see langword="false"/> when there is none with that name.</summary>
    /// <param name="name">Name of the preset to load.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if the preset existed and was loaded; otherwise, <see langword="false"/>.</returns>
    Task<bool> LoadPresetAsync(string name, CancellationToken ct);

    /// <summary>Deletes a saved preset by name.</summary>
    /// <param name="name">Name of the preset to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if the preset existed and was deleted; otherwise, <see langword="false"/>.</returns>
    Task<bool> DeletePresetAsync(string name, CancellationToken ct);
}

/// <summary>Summary metadata for a saved load plan preset.</summary>
/// <param name="Name">Preset name.</param>
/// <param name="SavedAt">Timestamp when the preset was last saved.</param>
/// <param name="Entries">Number of scenario entries in the preset.</param>
/// <param name="DurationSeconds">Configured run duration in seconds.</param>
public sealed record PresetInfo(string Name, DateTimeOffset SavedAt, int Entries, int DurationSeconds);

/// <summary>Figures of one scenario entry in a run.</summary>
/// <param name="Entry">0-based scenario entry index.</param>
/// <param name="Label">Display label of the scenario entry.</param>
/// <param name="Starts">Total bees started for this entry.</param>
/// <param name="Finished">Total bees that have completed.</param>
/// <param name="InFlight">Bees currently running.</param>
/// <param name="Failed">Bees that failed or were canceled.</param>
/// <param name="SlowBees">Bees classified as slow.</param>
/// <param name="SlowCalls">Total slow calls made by bees of this entry.</param>
/// <param name="Dropped">Bees dropped due to concurrency limits.</param>
/// <param name="BeesPerMinute">Configured target spawn rate in bees per minute.</param>
public sealed record EntryFigures(int Entry, string Label, int Starts, int Finished, int InFlight, int Failed, int SlowBees, int SlowCalls, int Dropped, double BeesPerMinute);

/// <summary>Summary snapshot of a load test run and its per-entry counters.</summary>
/// <param name="RunId">Unique run identifier.</param>
/// <param name="State">Lifecycle state of the run (for example <c>Running</c>, <c>Finished</c>, or <c>Stopped</c>).</param>
/// <param name="ElapsedSeconds">Elapsed duration of the run in seconds.</param>
/// <param name="StartedUtc">UTC start timestamp of the run.</param>
/// <param name="TotalBees">Total bees executed or started.</param>
/// <param name="FailedBees">Number of failed or canceled bees.</param>
/// <param name="SlowBees">Number of slow bees.</param>
/// <param name="TotalLogs">Total log lines recorded for the run.</param>
/// <param name="Error">Optional run-level error message.</param>
/// <param name="Entries">Per-entry execution figures ordered by 0-based entry index.</param>
/// <param name="PreflightState">Optional preflight execution state.</param>
/// <param name="PreflightError">Optional error produced by the preflight script.</param>
public sealed record RunInfo(
    string RunId,
    string State,
    double ElapsedSeconds,
    DateTimeOffset StartedUtc,
    int TotalBees,
    int FailedBees,
    int SlowBees,
    long TotalLogs,
    string? Error,
    IReadOnlyList<EntryFigures> Entries,
    string? PreflightState = null,
    string? PreflightError = null);

/// <summary>Port for inspecting and controlling the active load test run.</summary>
public interface IRunPort
{
    /// <summary>The current run (running or last finished), or <see langword="null"/> when the server never had one.</summary>
    /// <returns>The current <see cref="RunInfo"/>, or <see langword="null"/> if no run exists.</returns>
    RunInfo? GetCurrent();

    /// <summary>Starts a new run of the Live plan.</summary>
    /// <param name="environmentName">Optional environment name supplying placeholder values, or <see langword="null"/> for none.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Initial <see cref="RunInfo"/> of the started run.</returns>
    Task<RunInfo> StartAsync(string? environmentName, CancellationToken ct);

    /// <summary>Stops the currently active run.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resulting <see cref="RunInfo"/> after stopping.</returns>
    Task<RunInfo> StopAsync(CancellationToken ct);
}

/// <summary>Filter for querying bees by execution outcome.</summary>
public enum BeeOutcomeFilter
{
    /// <summary>Include bees of all outcomes.</summary>
    All,
    /// <summary>Include only successful, non-slow bees.</summary>
    Ok,
    /// <summary>Include only slow (non-failed) bees.</summary>
    Slow,
    /// <summary>Include only failed or canceled bees.</summary>
    Failed
}

/// <summary>Summary information for a single bee execution.</summary>
/// <param name="Sequence">0-based bee sequence number within the run.</param>
/// <param name="Entry">0-based scenario entry index.</param>
/// <param name="StartedAt">Start offset in seconds from the start of the run.</param>
/// <param name="DurationMs">Total bee duration in milliseconds.</param>
/// <param name="StatusCode">Optional final or primary status code.</param>
/// <param name="Bytes">Total bytes transferred by the bee.</param>
/// <param name="Error">Error message if the bee failed, or <see langword="null"/>.</param>
/// <param name="SlowCalls">Number of slow calls made by this bee.</param>
/// <param name="FailedStep">0-based index of the step that failed, or <c>-1</c> if none.</param>
/// <param name="FailedRep">0-based repetition index of the failed step, or <c>-1</c> if none.</param>
public sealed record BeeInfo(int Sequence, int Entry, double StartedAt, double DurationMs, int? StatusCode, long Bytes, string? Error, int SlowCalls, int FailedStep = -1, int FailedRep = -1)
{
    /// <summary>Gets the outcome classification string (<c>failed</c>, <c>slow</c>, or <c>ok</c>).</summary>
    public string Outcome => Error is not null ? "failed" : SlowCalls > 0 ? "slow" : "ok";
}

/// <summary>Information about one recorded call made by a bee.</summary>
/// <param name="Name">Display name of the call or request.</param>
/// <param name="OffsetMs">Start offset in milliseconds relative to the bee's start.</param>
/// <param name="DurationMs">Call duration in milliseconds.</param>
/// <param name="StatusCode">Optional HTTP or operation status code.</param>
/// <param name="Error">Optional error message if the call failed.</param>
/// <param name="Step">0-based step index that issued the call.</param>
/// <param name="Rep">0-based step repetition index.</param>
/// <param name="Endpoint">Optional normalized endpoint identifier.</param>
/// <param name="IsSlow">Whether the call exceeded its slow threshold.</param>
public sealed record BeeCallInfo(string Name, double OffsetMs, double DurationMs, int? StatusCode, string? Error, int Step = 0, int Rep = 0, string? Endpoint = null, bool IsSlow = false);

/// <summary>Single log entry emitted during a bee's execution.</summary>
/// <param name="AtSeconds">Offset in seconds from the start of the run.</param>
/// <param name="Level">Log severity level.</param>
/// <param name="Message">Formatted log message.</param>
/// <param name="Exception">Optional exception text.</param>
/// <param name="CallIndex">Optional 0-based index of the related call.</param>
public sealed record BeeLogInfo(double AtSeconds, string Level, string Message, string? Exception, int? CallIndex);

/// <summary>Detailed view of a single bee including its calls and logs.</summary>
/// <param name="Bee">Summary information for the bee.</param>
/// <param name="Calls">Recorded calls made by the bee.</param>
/// <param name="Logs">Recorded log lines emitted by the bee.</param>
public sealed record BeeDetailInfo(BeeInfo Bee, IReadOnlyList<BeeCallInfo> Calls, IReadOnlyList<BeeLogInfo> Logs);

/// <summary>Aggregated latency and outcome statistics for a normalized endpoint.</summary>
/// <param name="Endpoint">Normalized endpoint identifier (for example <c>GET /orders/{id}</c>).</param>
/// <param name="Method">HTTP method or call kind.</param>
/// <param name="TotalCount">Total calls recorded against this endpoint.</param>
/// <param name="SlowCount">Number of calls classified as slow.</param>
/// <param name="FailedCount">Number of failed calls.</param>
/// <param name="TypicalMs">Median (typical) latency in milliseconds.</param>
/// <param name="P95Ms">95th-percentile latency in milliseconds.</param>
/// <param name="MaxMs">Maximum observed latency in milliseconds.</param>
/// <param name="SlowThresholdMs">Effective slow threshold in milliseconds.</param>
/// <param name="HasCustomThreshold">Whether a custom slow threshold is configured for this endpoint.</param>
public sealed record CallEndpointInfo(
    string Endpoint,
    string Method,
    long TotalCount,
    long SlowCount,
    long FailedCount,
    double TypicalMs,
    double P95Ms,
    double MaxMs,
    double SlowThresholdMs,
    bool HasCustomThreshold);

/// <summary>Paged summary of call endpoints for a run.</summary>
/// <param name="TotalCalls">Total calls across all matching endpoints.</param>
/// <param name="TotalSlowCalls">Total slow calls across all matching endpoints.</param>
/// <param name="TotalFailedCalls">Total failed calls across all matching endpoints.</param>
/// <param name="Endpoints">Endpoint summaries matching the query.</param>
public sealed record CallEndpointsPage(long TotalCalls, long TotalSlowCalls, long TotalFailedCalls, IReadOnlyList<CallEndpointInfo> Endpoints);

/// <summary>Individual recorded call sample belonging to a normalized endpoint.</summary>
/// <param name="Sequence">0-based bee sequence number that issued the call.</param>
/// <param name="Entry">0-based scenario entry index.</param>
/// <param name="AtSeconds">Start time in seconds from the start of the run.</param>
/// <param name="DurationMs">Call duration in milliseconds.</param>
/// <param name="StatusCode">Optional status code returned by the call.</param>
/// <param name="Error">Optional error message if the call failed.</param>
/// <param name="IsSlow">Whether the call is classified as slow.</param>
/// <param name="TypicalMs">Optional endpoint typical (median) latency in milliseconds.</param>
/// <param name="SlowThresholdMs">Optional endpoint slow threshold in milliseconds.</param>
/// <param name="Url">Exact URL or target recorded for the call.</param>
public sealed record CallSampleInfo(
    int Sequence,
    int Entry,
    double AtSeconds,
    double DurationMs,
    int? StatusCode,
    string? Error,
    bool IsSlow,
    double? TypicalMs,
    double? SlowThresholdMs,
    string Url);

/// <summary>Detailed call breakdown for a single normalized endpoint.</summary>
/// <param name="Summary">Aggregated metrics for the endpoint.</param>
/// <param name="MatchingCalls">Total number of calls matching the filter criteria.</param>
/// <param name="Offset">0-based paging offset of <paramref name="Calls"/>.</param>
/// <param name="Calls">Page of individual call samples.</param>
public sealed record CallEndpointDetailInfo(
    CallEndpointInfo Summary,
    long MatchingCalls,
    int Offset,
    IReadOnlyList<CallSampleInfo> Calls);

/// <summary>Paged list of bee summaries.</summary>
/// <param name="Total">Total number of matching bees.</param>
/// <param name="Items">Page of <see cref="BeeInfo"/> items.</param>
public sealed record BeePage(long Total, IReadOnlyList<BeeInfo> Items);

/// <summary>Filters for log queries. <see cref="Bee"/> and <see cref="Entry"/> are 0-based.</summary>
/// <param name="Entry">Optional 0-based scenario entry index filter.</param>
/// <param name="Bee">Optional 0-based bee sequence number filter.</param>
/// <param name="FailedBees">When <see langword="true"/>, include only logs from failed bees.</param>
/// <param name="SlowBees">When <see langword="true"/>, include only logs from slow bees.</param>
/// <param name="Levels">Optional list of log level names to include.</param>
/// <param name="Search">Optional case-insensitive substring filter on the message.</param>
/// <param name="Offset">0-based number of matching log lines to skip.</param>
/// <param name="Limit">Maximum number of log lines to return.</param>
public sealed record LogFilter(
    int? Entry = null,
    int? Bee = null,
    bool? FailedBees = null,
    bool? SlowBees = null,
    IReadOnlyList<string>? Levels = null,
    string? Search = null,
    int Offset = 0,
    int Limit = 20);

/// <summary>Single log line returned from a run log query.</summary>
/// <param name="Id">Unique log line identifier.</param>
/// <param name="Sequence">0-based bee sequence number.</param>
/// <param name="Entry">0-based scenario entry index.</param>
/// <param name="AtSeconds">Offset in seconds from the start of the run.</param>
/// <param name="Level">Log severity level.</param>
/// <param name="Message">Formatted log message.</param>
/// <param name="Exception">Optional exception text.</param>
public sealed record LogLineInfo(long Id, int Sequence, int Entry, double AtSeconds, string Level, string Message, string? Exception);

/// <summary>Paged result of a run log query.</summary>
/// <param name="Total">Total number of matching log lines.</param>
/// <param name="Items">Page of <see cref="LogLineInfo"/> rows.</param>
public sealed record LogPageInfo(long Total, IReadOnlyList<LogLineInfo> Items);

/// <summary>Past and current runs, their bees, calls and logs.</summary>
public interface IRunHistoryPort
{
    /// <summary>Id of the current run, or of the newest stored run; <see langword="null"/> when there is none.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest run id, or <see langword="null"/> if no runs exist.</returns>
    Task<string?> LatestRunIdAsync(CancellationToken ct);

    /// <summary>Lists past and current runs, newest first.</summary>
    /// <param name="limit">Maximum number of runs to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="RunInfo"/> summaries.</returns>
    Task<IReadOnlyList<RunInfo>> ListRunsAsync(int limit, CancellationToken ct);

    /// <summary>Gets summary information for a single run by id.</summary>
    /// <param name="runId">Identifier of the run to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="RunInfo"/> if found; otherwise, <see langword="null"/>.</returns>
    Task<RunInfo?> GetRunAsync(string runId, CancellationToken ct);

    /// <summary>Bees of a run, failed first, then slow, then ok, within each group by start time (brain 0028).</summary>
    /// <param name="runId">Identifier of the run.</param>
    /// <param name="entry">Optional 0-based scenario entry filter.</param>
    /// <param name="outcome">Outcome filter.</param>
    /// <param name="offset">0-based paging offset.</param>
    /// <param name="limit">Maximum number of bees to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="failedStep">Optional 0-based failed step index filter.</param>
    /// <returns>A paged list of matching <see cref="BeeInfo"/> records.</returns>
    Task<BeePage> ListBeesAsync(string runId, int? entry, BeeOutcomeFilter outcome, int offset, int limit, CancellationToken ct, int? failedStep = null);

    /// <summary>Bees by sequence (0-based); unknown sequences are left out.</summary>
    /// <param name="runId">Identifier of the run.</param>
    /// <param name="sequences">0-based bee sequence numbers to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The found <see cref="BeeInfo"/> entries.</returns>
    Task<IReadOnlyList<BeeInfo>> GetBeesAsync(string runId, IReadOnlyList<int> sequences, CancellationToken ct);

    /// <summary>Gets detailed calls and logs for a single bee.</summary>
    /// <param name="runId">Identifier of the run.</param>
    /// <param name="sequence">0-based bee sequence number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="BeeDetailInfo"/> if stored detail is available; otherwise, <see langword="null"/>.</returns>
    Task<BeeDetailInfo?> GetBeeAsync(string runId, int sequence, CancellationToken ct);

    /// <summary>Queries log lines of a run matching <paramref name="filter"/>.</summary>
    /// <param name="runId">Identifier of the run.</param>
    /// <param name="filter">Filter and paging parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="LogPageInfo"/> containing matching log lines.</returns>
    Task<LogPageInfo> QueryLogsAsync(string runId, LogFilter filter, CancellationToken ct);

    /// <summary>Lists normalized call endpoints recorded in a run.</summary>
    /// <param name="runId">Identifier of the run.</param>
    /// <param name="entry">Optional 0-based scenario entry index filter.</param>
    /// <param name="slowOnly">When <see langword="true"/>, include only endpoints with at least one slow call.</param>
    /// <param name="failedOnly">When <see langword="true"/>, include only endpoints with at least one failed call.</param>
    /// <param name="minMs">Optional minimum call duration filter in milliseconds.</param>
    /// <param name="search">Optional substring filter on endpoint name or method.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CallEndpointsPage"/> containing endpoint totals and summaries.</returns>
    Task<CallEndpointsPage> ListCallEndpointsAsync(string runId, int? entry, bool slowOnly, bool failedOnly, double? minMs, string? search, CancellationToken ct);

    /// <summary>Gets detailed call samples for a specific normalized endpoint in a run.</summary>
    /// <param name="runId">Identifier of the run.</param>
    /// <param name="endpoint">Normalized endpoint name.</param>
    /// <param name="entry">Optional 0-based scenario entry index filter.</param>
    /// <param name="show">Optional outcome filter (<c>slow</c> or <c>failed</c>).</param>
    /// <param name="minMs">Optional minimum call duration in milliseconds.</param>
    /// <param name="maxMs">Optional maximum call duration in milliseconds.</param>
    /// <param name="sort">Optional sort order (<c>slowest</c>, <c>time</c>, or <c>failed</c>).</param>
    /// <param name="offset">0-based paging offset.</param>
    /// <param name="limit">Maximum number of call samples to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="CallEndpointDetailInfo"/> if the endpoint has calls; otherwise, <see langword="null"/>.</returns>
    Task<CallEndpointDetailInfo?> GetCallEndpointDetailAsync(string runId, string endpoint, int? entry, string? show, double? minMs, double? maxMs, string? sort, int offset, int limit, CancellationToken ct);
}

/// <summary>An environment as the assistant may see it. Secret-looking values are masked by the adapter's caller, not here.</summary>
/// <param name="Name">Name of the environment.</param>
/// <param name="Variables">Ordered list of environment key-value pairs.</param>
public sealed record EnvironmentInfo(string Name, IReadOnlyList<KeyValuePair<string, string>> Variables);

/// <summary>Port for reading and managing named environments.</summary>
public interface IEnvironmentPort
{
    /// <summary>Lists all configured environments.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of <see cref="EnvironmentInfo"/> objects.</returns>
    Task<IReadOnlyList<EnvironmentInfo>> ListAsync(CancellationToken ct);

    /// <summary>Gets a single environment by name.</summary>
    /// <param name="name">Environment name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="EnvironmentInfo"/> if found; otherwise, <see langword="null"/>.</returns>
    Task<EnvironmentInfo?> GetAsync(string name, CancellationToken ct);

    /// <summary>Creates or replaces. Throws <see cref="ArgumentException"/> with the reason when it is not valid.</summary>
    /// <param name="environment">The environment definition to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(EnvironmentInfo environment, CancellationToken ct);

    /// <summary>Deletes a named environment.</summary>
    /// <param name="name">Name of the environment to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if the environment existed and was deleted; otherwise, <see langword="false"/>.</returns>
    Task<bool> DeleteAsync(string name, CancellationToken ct);
}

/// <summary>Metadata and C# source code of a custom scenario or preflight script.</summary>
/// <param name="ScriptId">Unique script identifier.</param>
/// <param name="FileName">Backing file name on disk.</param>
/// <param name="Name">Display name of the script or scenario.</param>
/// <param name="ScenarioId">Catalog scenario identifier defined by the script, or <see langword="null"/> for preflights.</param>
/// <param name="Description">Human-readable description extracted from the script.</param>
/// <param name="UpdatedUtc">Last modification timestamp in UTC.</param>
/// <param name="Code">C# source code of the script.</param>
public sealed record ScriptInfo(string ScriptId, string FileName, string Name, string? ScenarioId, string Description, DateTimeOffset UpdatedUtc, string Code);

/// <summary>Compiler diagnostic produced while compiling a custom script.</summary>
/// <param name="Severity">Diagnostic severity (for example <c>Error</c> or <c>Warning</c>).</param>
/// <param name="Line">1-based source line number.</param>
/// <param name="Column">1-based source column number.</param>
/// <param name="Message">Diagnostic message text.</param>
public sealed record Diagnostic(string Severity, int Line, int Column, string Message);

/// <summary>Result of compiling and storing a custom scenario or preflight script.</summary>
/// <param name="Success">Whether compilation and registration succeeded.</param>
/// <param name="Script">Compiled script metadata when successful, or <see langword="null"/>.</param>
/// <param name="Diagnostics">Compiler diagnostics emitted during compilation.</param>
/// <param name="Error">Optional high-level error message.</param>
public sealed record CompileOutcome(bool Success, ScriptInfo? Script, IReadOnlyList<Diagnostic> Diagnostics, string? Error);

/// <summary>Recorded call summary from a single-bee script test run.</summary>
/// <param name="Method">HTTP method or operation name.</param>
/// <param name="Url">Target URL or operation target.</param>
/// <param name="StatusCode">Optional response status code.</param>
/// <param name="DurationMs">Call duration in milliseconds.</param>
/// <param name="Error">Optional error message.</param>
public sealed record TestCallInfo(string Method, string Url, int? StatusCode, double DurationMs, string? Error);

/// <summary>Outcome of executing one test bee of a custom scenario script.</summary>
/// <param name="Success">Whether the test run succeeded without error.</param>
/// <param name="StatusCode">Optional primary status code returned by the script.</param>
/// <param name="DurationMs">Total test run duration in milliseconds.</param>
/// <param name="Error">Optional failure message.</param>
/// <param name="Calls">Calls recorded during the test execution.</param>
/// <param name="Logs">Log lines emitted during the test execution.</param>
public sealed record ScriptTestOutcome(bool Success, int? StatusCode, double DurationMs, string? Error, IReadOnlyList<TestCallInfo> Calls, IReadOnlyList<string> Logs);

/// <summary>Outcome of executing a preflight script once outside a run.</summary>
/// <param name="Success">Whether the preflight succeeded.</param>
/// <param name="DurationMs">Execution duration in milliseconds.</param>
/// <param name="Error">Optional failure message.</param>
/// <param name="Logs">Log lines emitted by the preflight.</param>
public sealed record PreflightTestOutcome(bool Success, double DurationMs, string? Error, IReadOnlyList<string> Logs);

/// <summary>Custom C# scenario scripts and preflight scripts (brain 0042).</summary>
public interface IScriptPort
{
    /// <summary>Lists all custom C# scenario scripts.</summary>
    /// <returns>A read-only list of <see cref="ScriptInfo"/> items.</returns>
    IReadOnlyList<ScriptInfo> ListScripts();

    /// <summary>Gets a custom scenario script by id.</summary>
    /// <param name="id">Script identifier.</param>
    /// <returns>The <see cref="ScriptInfo"/> if found; otherwise, <see langword="null"/>.</returns>
    ScriptInfo? GetScript(string id);

    /// <summary>Compiles and saves a custom scenario script, optionally replacing an existing id.</summary>
    /// <param name="id">Optional existing script id to replace.</param>
    /// <param name="fileName">Optional file name for the script.</param>
    /// <param name="code">C# source code to compile.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CompileOutcome"/> describing success, metadata, or diagnostics.</returns>
    Task<CompileOutcome> UploadScriptAsync(string? id, string? fileName, string code, CancellationToken ct);

    /// <summary>Executes a single test bee of a custom scenario script outside a run.</summary>
    /// <param name="id">Script identifier.</param>
    /// <param name="parameters">Optional JSON object of scenario parameters.</param>
    /// <param name="environmentName">Optional environment name for placeholder substitution.</param>
    /// <param name="timeoutSeconds">Execution timeout in seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ScriptTestOutcome"/> containing status, timing, calls, and logs.</returns>
    Task<ScriptTestOutcome> TestRunScriptAsync(string id, JsonObject? parameters, string? environmentName, int timeoutSeconds, CancellationToken ct);

    /// <summary>Deletes a custom scenario script by id.</summary>
    /// <param name="id">Script identifier.</param>
    /// <returns><see langword="true"/> if the script existed and was deleted; otherwise, <see langword="false"/>.</returns>
    bool DeleteScript(string id);

    /// <summary>Lists all custom C# preflight scripts.</summary>
    /// <returns>A read-only list of <see cref="ScriptInfo"/> items.</returns>
    IReadOnlyList<ScriptInfo> ListPreflights();

    /// <summary>Gets a preflight script by id.</summary>
    /// <param name="id">Preflight script identifier.</param>
    /// <returns>The <see cref="ScriptInfo"/> if found; otherwise, <see langword="null"/>.</returns>
    ScriptInfo? GetPreflight(string id);

    /// <summary>Compiles and saves a preflight script, optionally replacing an existing id.</summary>
    /// <param name="id">Optional existing preflight id to replace.</param>
    /// <param name="fileName">Optional file name for the preflight script.</param>
    /// <param name="code">C# source code to compile.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CompileOutcome"/> describing success, metadata, or diagnostics.</returns>
    Task<CompileOutcome> UploadPreflightAsync(string? id, string? fileName, string code, CancellationToken ct);

    /// <summary>Executes a preflight script once outside a run.</summary>
    /// <param name="id">Preflight script identifier.</param>
    /// <param name="environmentName">Optional environment name for placeholder substitution.</param>
    /// <param name="timeoutSeconds">Execution timeout in seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PreflightTestOutcome"/> containing outcome, timing, and logs.</returns>
    Task<PreflightTestOutcome> TestRunPreflightAsync(string id, string? environmentName, int timeoutSeconds, CancellationToken ct);

    /// <summary>Deletes a preflight script by id.</summary>
    /// <param name="id">Preflight script identifier.</param>
    /// <returns><see langword="true"/> if the preflight existed and was deleted; otherwise, <see langword="false"/>.</returns>
    bool DeletePreflight(string id);
}
