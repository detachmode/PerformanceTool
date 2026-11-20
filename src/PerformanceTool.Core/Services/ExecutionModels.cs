namespace PerformanceTool.Services;

/// <summary>One scenario execution (one actor's playthrough). <see cref="Entry"/> is the index into <see cref="LoadPlan.Scenarios"/>.</summary>
public sealed record ExecutionResult(int Sequence, int Entry, double StartedAtSeconds, double ElapsedMs, int? StatusCode, long Bytes, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// Start or end of one execution, with the number of executions in flight afterwards (docs/brain/0008).
/// Every <see cref="Started"/> is followed by exactly one end event, whether the execution reported a result or was
/// cancelled because the user stopped the run. The end event is raised after <c>onResult</c>.
/// </summary>
public sealed record ExecutionActivity(int Sequence, int Entry, double AtSeconds, bool Started, int InFlight, int TotalInFlight);
