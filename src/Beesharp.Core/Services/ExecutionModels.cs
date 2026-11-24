namespace Beesharp.Services;

/// <summary>One call recorded inside a bee's execution (HTTP or custom step).</summary>
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
    string? Url = null);

/// <summary>One log statement emitted by a bee via <c>ctx.Logger</c> or <c>ctx.Log</c>.</summary>
public sealed record RecordedLog(
    double AtSeconds,
    string Level,
    string Message,
    string? Exception = null,
    int? CallIndex = null,
    int Step = 0,
    int Rep = 0);

/// <summary>One visit to a declared step inside a bee, with the step record's captured fields as compact JSON (brain 0056).</summary>
public sealed record RecordedStepVisit(int Step, int Rep, string? Data = null);

/// <summary>One scenario execution (one bee's playthrough). <see cref="Entry"/> is the index into <see cref="LoadPlan.Scenarios"/>.</summary>
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
    IReadOnlyList<RecordedStepVisit>? StepVisits = null)
{
    public bool Success => Error is null;

    /// <summary>
    /// The error text of a bee that was in flight when the user stopped the run (brain 0053). Canceled bees look and count like failed
    /// (they carry this error), but <see cref="Canceled"/> keeps them tellable apart. Storage keeps only the error, so
    /// <see cref="IsCanceledError"/> recognises them again when read back.
    /// </summary>
    public const string CanceledError = "Canceled: run stopped";

    public static bool IsCanceledError(string? error) => error == CanceledError;
}

/// <summary>
/// Start or end of one execution, with the number of executions in flight afterwards (docs/brain/0008).
/// Every <see cref="Started"/> is followed by exactly one end event, whether the execution reported a result or was
/// cancelled because the user stopped the run. The end event is raised after <c>onResult</c>.
/// </summary>
public sealed record ExecutionActivity(int Sequence, int Entry, double AtSeconds, bool Started, int InFlight, int TotalInFlight);

/// <summary>A log line emitted in real time by a currently flying bee (or scenario startup/teardown when Sequence &lt; 0).</summary>
public sealed record LiveLogEvent(int Sequence, int Entry, RecordedLog Log);
