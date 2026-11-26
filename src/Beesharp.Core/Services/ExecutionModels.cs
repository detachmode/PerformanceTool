namespace Beesharp.Services;

/// <summary>One call recorded inside a bee's execution (HTTP or custom step).</summary>
/// <param name="Name">Display name of the call (e.g. HTTP method and path, or custom step call name).</param>
/// <param name="OffsetMs">Start offset in milliseconds relative to the bee's execution start.</param>
/// <param name="DurationMs">Total call duration in milliseconds.</param>
/// <param name="StatusCode">Optional HTTP status code or custom status code returned by the call.</param>
/// <param name="Bytes">Number of payload bytes transferred.</param>
/// <param name="Error">Optional error message when the call failed.</param>
/// <param name="Step">Zero-based step index during which this call was recorded.</param>
/// <param name="Rep">Zero-based repetition index within the step.</param>
/// <param name="Endpoint">Optional normalized endpoint template or route identifier.</param>
/// <param name="Method">Optional HTTP method (e.g. <c>GET</c>, <c>POST</c>).</param>
/// <param name="Host">Optional target host name.</param>
/// <param name="SlowThresholdMs">Optional slow-call threshold in milliseconds configured for this call.</param>
/// <param name="Url">Optional full request URL.</param>
/// <param name="TtfbMs">Optional time-to-first-byte in milliseconds when HTTP timing split is enabled.</param>
/// <param name="ResponseReadMs">Optional response body read duration in milliseconds when HTTP timing split is enabled.</param>
/// <param name="Headers">Optional captured request/response headers for slow or failed calls.</param>
/// <param name="TraceId">W3C trace ID (32 hex characters) of the actor's trace this call belongs to (brain 0063); <see langword="null"/> when no trace was started.</param>
/// <param name="SpanId">W3C span ID (16 hex characters) of this call's span, the parent of the service's server span; <see langword="null"/> when no trace was started.</param>
public sealed record RecordedCall(
    string Name,
    double OffsetMs,
    double DurationMs,
    int? StatusCode,
    long Bytes = 0,
    string? Error = null,
    int Step = 0,
    int Rep = 0,
    string? Endpoint = null,
    string? Method = null,
    string? Host = null,
    double? SlowThresholdMs = null,
    string? Url = null,
    double? TtfbMs = null,
    double? ResponseReadMs = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? TraceId = null,
    string? SpanId = null);

/// <summary>One log statement emitted by a bee via <c>ctx.Logger</c> or <c>ctx.Log</c>.</summary>
/// <param name="AtSeconds">Run-relative timestamp in seconds when the log entry was emitted.</param>
/// <param name="Level">Log severity level string.</param>
/// <param name="Message">Formatted log message.</param>
/// <param name="Exception">Optional formatted exception details associated with the log entry.</param>
/// <param name="CallIndex">Optional zero-based index into <see cref="ExecutionResult.Calls"/> if emitted during a call.</param>
/// <param name="Step">Zero-based step index during which this log was emitted.</param>
/// <param name="Rep">Zero-based repetition index within the step.</param>
public sealed record RecordedLog(
    double AtSeconds,
    string Level,
    string Message,
    string? Exception = null,
    int? CallIndex = null,
    int Step = 0,
    int Rep = 0);

/// <summary>One visit to a declared step inside a bee, with the step record's captured fields as compact JSON (brain 0056).</summary>
/// <param name="Step">Zero-based step index visited.</param>
/// <param name="Rep">Zero-based repetition index of the visit.</param>
/// <param name="Data">Optional compact JSON payload of captured step fields.</param>
public sealed record RecordedStepVisit(int Step, int Rep, string? Data = null);

/// <summary>One scenario execution (one bee's playthrough). <see cref="Entry"/> is the index into <see cref="LoadPlan.Scenarios"/>.</summary>
/// <param name="Sequence">Zero-based global execution sequence number within the run.</param>
/// <param name="Entry">Zero-based index of the scenario entry in <see cref="LoadPlan.Scenarios"/>.</param>
/// <param name="StartedAtSeconds">Run-relative start time in seconds.</param>
/// <param name="ElapsedMs">Total execution duration in milliseconds.</param>
/// <param name="StatusCode">Optional HTTP or scenario status code summarizing the execution.</param>
/// <param name="Bytes">Total bytes transferred during the execution.</param>
/// <param name="Error">Error message if the execution failed or was canceled; <see langword="null"/> on success.</param>
/// <param name="Calls">Optional list of calls recorded during this execution.</param>
/// <param name="Logs">Optional list of log entries recorded during this execution.</param>
/// <param name="Canceled"><see langword="true"/> when the bee was in flight and canceled because the run stopped.</param>
/// <param name="FailedStep">Zero-based index of the step that failed, or <c>-1</c> if not applicable.</param>
/// <param name="FailedRep">Zero-based repetition index of the failed step, or <c>-1</c> if not applicable.</param>
/// <param name="StepVisits">Optional list of declared step visits recorded during the execution.</param>
/// <param name="TraceId">W3C trace ID (32 hex characters) of this actor's trace (brain 0063), the same ID on every call of the actor; <see langword="null"/> when no trace was started.</param>
public sealed record ExecutionResult(
    int Sequence,
    int Entry,
    double StartedAtSeconds,
    double ElapsedMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    IReadOnlyList<RecordedCall>? Calls = null,
    IReadOnlyList<RecordedLog>? Logs = null,
    bool Canceled = false,
    int FailedStep = -1,
    int FailedRep = -1,
    IReadOnlyList<RecordedStepVisit>? StepVisits = null,
    string? TraceId = null)
{
    /// <summary>Whether the execution completed without an error.</summary>
    public bool Success => Error is null;

    /// <summary>
    /// The error text of a bee that was in flight when the user stopped the run (brain 0053). Canceled bees look and count like failed
    /// (they carry this error), but <see cref="Canceled"/> keeps them tellable apart. Storage keeps only the error, so
    /// <see cref="IsCanceledError"/> recognises them again when read back.
    /// </summary>
    public const string CanceledError = "Canceled: run stopped";

    /// <summary>Returns <see langword="true"/> when <paramref name="error"/> matches <see cref="CanceledError"/>.</summary>
    /// <param name="error">Error string to inspect.</param>
    /// <returns><see langword="true"/> if the error indicates cancellation due to run stop; otherwise <see langword="false"/>.</returns>
    public static bool IsCanceledError(string? error) => error == CanceledError;
}

/// <summary>
/// Start or end of one execution, with the number of executions in flight afterwards (docs/brain/0008).
/// Every <see cref="Started"/> is followed by exactly one end event, whether the execution reported a result or was
/// cancelled because the user stopped the run. The end event is raised after <c>onResult</c>.
/// </summary>
/// <param name="Sequence">Zero-based global execution sequence number within the run.</param>
/// <param name="Entry">Zero-based index of the scenario entry in <see cref="LoadPlan.Scenarios"/>.</param>
/// <param name="AtSeconds">Run-relative timestamp in seconds when the activity occurred.</param>
/// <param name="Started"><see langword="true"/> when the execution started; <see langword="false"/> when it ended.</param>
/// <param name="InFlight">Number of in-flight executions for this scenario entry after the event.</param>
/// <param name="TotalInFlight">Total number of in-flight executions across all scenario entries after the event.</param>
public sealed record ExecutionActivity(int Sequence, int Entry, double AtSeconds, bool Started, int InFlight, int TotalInFlight);

/// <summary>A log line emitted in real time by a currently flying bee (or scenario startup/teardown when Sequence &lt; 0).</summary>
/// <param name="Sequence">Execution sequence number, or a negative value for scenario startup/teardown logs.</param>
/// <param name="Entry">Zero-based index of the scenario entry in <see cref="LoadPlan.Scenarios"/>.</param>
/// <param name="Log">The emitted log entry.</param>
public sealed record LiveLogEvent(int Sequence, int Entry, RecordedLog Log);
