namespace PerformanceTool.Services;

/// <summary>One call recorded inside an actor's execution (HTTP or custom step).</summary>
public sealed record RecordedCall(
    string Name,
    double OffsetMs,
    double DurationMs,
    int? StatusCode,
    long Bytes = 0,
    string? Error = null);

/// <summary>One log statement emitted by an actor via <c>ctx.Logger</c> or <c>ctx.Log</c>.</summary>
public sealed record RecordedLog(
    double AtSeconds,
    string Level,
    string Message,
    string? Exception = null,
    int? CallIndex = null);

/// <summary>One scenario execution (one actor's playthrough). <see cref="Entry"/> is the index into <see cref="LoadPlan.Scenarios"/>.</summary>
public sealed record ExecutionResult(
    int Sequence,
    int Entry,
    double StartedAtSeconds,
    double ElapsedMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    IReadOnlyList<RecordedCall>? Calls = null,
    IReadOnlyList<RecordedLog>? Logs = null)
{
    public bool Success => Error is null;
}

/// <summary>
/// Start or end of one execution, with the number of executions in flight afterwards (docs/brain/0008).
/// Every <see cref="Started"/> is followed by exactly one end event, whether the execution reported a result or was
/// cancelled because the user stopped the run. The end event is raised after <c>onResult</c>.
/// </summary>
public sealed record ExecutionActivity(int Sequence, int Entry, double AtSeconds, bool Started, int InFlight, int TotalInFlight);
