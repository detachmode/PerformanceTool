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
/// The error text is kept once in the run's <see cref="ErrorDictionary"/> (brain 0028); <see cref="Error"/> resolves it.
/// </summary>
public sealed record FakeCall(
    int Index,
    string Name,
    double OffsetMs,
    double DurationMs,
    int? Status,
    int ErrorId,
    bool Skipped,
    double ThinkBeforeMs,
    bool Slow = false,
    double? TypicalMs = null,
    ErrorDictionary? Errors = null)
{
    public string? Error => Errors?.Resolve(ErrorId);
    public bool Failed => !Skipped && ErrorId != ErrorDictionary.None;
}

/// <summary>
/// One log statement of one actor (brain 0012): level, message, optional exception and the actor's scope values.
/// Exception texts (and repeated messages) can be stored in the run's <see cref="ErrorDictionary"/> (brain 0028); <see cref="Message"/> and
/// <see cref="Exception"/> resolve them, so readers do not care.
/// </summary>
public sealed class FakeLogEntry
{
    public static readonly string[] Levels = ["Trace", "Debug", "Information", "Warning", "Error"];
    public static int Rank(string level) => Array.IndexOf(Levels, level);

    private readonly string? message, exception;
    private readonly int messageId, exceptionId;
    private readonly ErrorDictionary? errors;

    public FakeLogEntry(double atSeconds, DateTimeOffset timestamp, string level, string message, string? exception, int entry, int sequence,
        IReadOnlyDictionary<string, string> scope, int? callIndex = null)
    {
        AtSeconds = atSeconds; Timestamp = timestamp; Level = level; this.message = message; this.exception = exception;
        Entry = entry; Sequence = sequence; Scope = scope; CallIndex = callIndex;
        LevelRank = Rank(level);
    }

    /// <summary>A log entry whose message and exception texts are interned in <paramref name="errors"/> (ids from <see cref="ErrorDictionary.Intern"/>).</summary>
    public FakeLogEntry(double atSeconds, DateTimeOffset timestamp, string level, int messageId, int exceptionId, ErrorDictionary errors, int entry, int sequence,
        IReadOnlyDictionary<string, string> scope, int? callIndex = null)
        : this(atSeconds, timestamp, level, null!, null, entry, sequence, scope, callIndex)
    {
        message = null; this.messageId = messageId; this.exceptionId = exceptionId; this.errors = errors;
    }

    /// <summary>A log entry with its own message text (unique, not worth interning) and an interned exception text.</summary>
    public FakeLogEntry(double atSeconds, DateTimeOffset timestamp, string level, string message, int exceptionId, ErrorDictionary errors, int entry, int sequence,
        IReadOnlyDictionary<string, string> scope, int? callIndex = null)
        : this(atSeconds, timestamp, level, message, null, entry, sequence, scope, callIndex)
    {
        this.exceptionId = exceptionId; this.errors = errors;
    }

    public double AtSeconds { get; }
    public DateTimeOffset Timestamp { get; }
    public string Level { get; }
    public string Message => message ?? errors?.Resolve(messageId) ?? "";
    public string? Exception => exception ?? errors?.Resolve(exceptionId);
    public int Entry { get; }
    public int Sequence { get; }
    public IReadOnlyDictionary<string, string> Scope { get; }
    public int? CallIndex { get; }
    public int LevelRank { get; }
}

/// <summary>
/// The small, permanent record of one finished actor. Cheap enough to keep for hours of run. The error text lives once in the
/// run's <see cref="ErrorDictionary"/> (brain 0028); <see cref="Error"/> resolves it.
/// </summary>
public readonly record struct ActorRecord(int Sequence, int Entry, double StartedAt, double DurationMs, int SlowCalls, int ErrorId, ErrorDictionary? Errors, byte RatioCode = 0)
{
    public string? Error => Errors?.Resolve(ErrorId);
    public bool Failed => ErrorId != ErrorDictionary.None;
    public ActorOutcome Outcome => Failed ? ActorOutcome.Failed : SlowCalls > 0 ? ActorOutcome.Slow : ActorOutcome.Ok;
    public string Name => $"Actor #{Sequence + 1}";
    /// <summary>How many times its typical duration the actor's worst call took (brain 0041); kept in one byte (<see cref="RatioCode"/>).</summary>
    public double Ratio => SlowCallRule.Decode(RatioCode);
    /// <summary>The colour step (0 pure green to <see cref="SlowCallRule.MaxStep"/> full amber). A slow actor is always full amber.</summary>
    public int Tint => SlowCalls > 0 ? SlowCallRule.MaxStep : SlowCallRule.Step(Ratio);
}

/// <summary>Calls and log lines of one actor. Only kept for a limited number of actors per scenario (<see cref="RunStoreOptions"/>); log lines are generated on first use.</summary>
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
