using PerformanceTool.Services;

namespace PerformanceTool.Client.Services;

// What the pages read from an IRunSource. Plain immutable data, so a later ApiRunSource can fill the same shapes from JSON.

public enum FakeRunState { Idle, Running, Finished }

/// <summary>
/// One call inside an actor's playthrough (docs/context.md). Times are relative to the start of the actor. A skipped call never ran
/// because an earlier call of the actor failed. <see cref="ThinkBeforeMs"/> is the pause that precedes it.
/// </summary>
public sealed record FakeCall(
    int Sequence,
    int Index,
    string Name,
    double OffsetMs,
    double DurationMs,
    int? Status,
    string? Error,
    bool Skipped,
    double ThinkBeforeMs,
    string? Group = null)
{
    public bool Failed => !Skipped && Error is not null;
}

/// <summary>One log statement of one actor (brain 0012): level, message, optional exception and the actor's scope values.</summary>
public sealed record FakeLogEntry(
    long Id,
    double AtSeconds,
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Exception,
    int Entry,
    int Sequence,
    IReadOnlyDictionary<string, string> Scope,
    int? CallIndex = null)
{
    public static readonly string[] Levels = ["Trace", "Debug", "Information", "Warning", "Error"];
    public static int Rank(string level) => Array.IndexOf(Levels, level);
}

/// <summary>Immutable view of the run for the pages. Arrays are copies, so rendering never races the simulation.</summary>
public sealed record RunSnapshot(
    FakeRunState State,
    double Elapsed,
    LoadPlan Plan,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string?> Descriptions,
    IReadOnlyList<ExecutionResult> All,
    IReadOnlyList<ExecutionResult>[] ByEntry,
    IReadOnlyList<FakeLogEntry>[] LogsByEntry,
    int[] InFlight,
    int[] Starts,
    int[] Dropped,
    double?[] SinceLastStart,
    IReadOnlyDictionary<int, IReadOnlyList<FakeCall>> Calls)
{
    public bool Running => State == FakeRunState.Running;
    public int Count => Plan.Scenarios.Count;
}

