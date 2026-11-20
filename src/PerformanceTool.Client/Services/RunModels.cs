using PerformanceTool.Services;

namespace PerformanceTool.Client.Services;

// What the pages read from an IRunSource. Everything is bounded (brain 0021): counters are kept incrementally, actor records are tiny,
// and the heavy detail (calls, log lines) is only held for the most recent actors of each scenario.

public enum FakeRunState { Idle, Running, Paused, Finished }

/// <summary>How an actor ended. Drives the only colours of the run views: failed = red, slow = amber (brain 0018).</summary>
public enum ActorOutcome : byte { Ok = 0, Slow = 1, Failed = 2 }

/// <summary>
/// One call inside an actor's playthrough (docs/context.md). Times are relative to the start of the actor. A skipped call never ran
/// because an earlier call of the actor failed. <see cref="TypicalMs"/> is what <see cref="SlowCallRule"/> compared against.
/// </summary>
public sealed record FakeCall(
    int Index,
    string Name,
    double OffsetMs,
    double DurationMs,
    int? Status,
    string? Error,
    bool Skipped,
    double ThinkBeforeMs,
    bool Slow = false,
    double? TypicalMs = null)
{
    public bool Failed => !Skipped && Error is not null;
}

/// <summary>One log statement of one actor (brain 0012): level, message, optional exception and the actor's scope values.</summary>
public sealed record FakeLogEntry(
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
    public int LevelRank { get; } = Rank(Level);
}

/// <summary>The small, permanent record of one finished actor. Cheap enough to keep for hours of run.</summary>
public readonly record struct ActorRecord(int Sequence, int Entry, double StartedAt, double DurationMs, int SlowCalls, string? Error)
{
    public bool Failed => Error is not null;
    public ActorOutcome Outcome => Error is not null ? ActorOutcome.Failed : SlowCalls > 0 ? ActorOutcome.Slow : ActorOutcome.Ok;
    public string Name => $"Actor #{Sequence + 1}";
}

/// <summary>Calls and log lines of one actor. Only kept for the latest actors per scenario; log lines are generated on first use.</summary>
public sealed class ActorDetail(int sequence, int entry, IReadOnlyList<FakeCall> calls, Func<IReadOnlyList<FakeLogEntry>> makeLogs)
{
    private IReadOnlyList<FakeLogEntry>? logs;
    public int Sequence { get; } = sequence;
    public int Entry { get; } = entry;
    public IReadOnlyList<FakeCall> Calls { get; } = calls;
    public IReadOnlyList<FakeLogEntry> Logs => logs ??= makeLogs();
    public bool Evicted { get; internal set; }
    internal bool Indexed { get; set; }
}

/// <summary>Counters of one scenario entry at one moment (immutable copy).</summary>
public sealed record ScenarioView(int Finished, int Failed, int SlowActors, int SlowCalls, int InFlight, int Starts);

/// <summary>
/// An actor that is being executed right now (brain 0018). Its outcome is not known yet; <see cref="Calls"/> is the whole playthrough, so a
/// reader may only show the calls whose time has come (offset within the elapsed time).
/// </summary>
public sealed record RunningActor(int Sequence, int Entry, double StartedAt, IReadOnlyList<FakeCall> Calls)
{
    public string Name => $"Actor #{Sequence + 1}";
}

/// <summary>Immutable view of the run for the pages. Arrays are small; the bulk data sits in <see cref="Store"/> and is read up to the counts here.</summary>
public sealed record RunSnapshot(
    FakeRunState State,
    double Elapsed,
    LoadPlan Plan,
    IReadOnlyList<string> Labels,
    ScenarioView[] Scenarios,
    RunStore Store,
    IReadOnlyList<RunningActor>[]? ActorsInFlight = null)
{
    /// <summary>True while a run is active (running or paused).</summary>
    public bool Running => State is FakeRunState.Running or FakeRunState.Paused;
    public bool Paused => State == FakeRunState.Paused;
    public int Count => Plan.Scenarios.Count;
}
