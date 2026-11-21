using PerformanceTool.Client.Services;

namespace PerformanceTool.Client.Components;

/// <summary>What the scenario popup lists when it opens (e.g. from a figure on an overview card, brain 0029).</summary>
public enum DetailFilter { All, Failed, Slow, Slowest5Pct }

/// <summary>How an actor looks in a list or chip: its outcome, or "running" while it has none yet (brain 0028).</summary>
public enum ActorState : byte { Failed = 0, Slow = 1, Running = 2, Ok = 3 }

/// <summary>One entry of an actor list. <see cref="Index"/> is the position in the finished-actor list of its scenario, -1 while running.</summary>
public readonly record struct ActorRow(int Sequence, double StartedAt, double DurationMs, ActorState State, int Index, int Tint = 0)
{
    public bool IsRunning => State == ActorState.Running;
}

/// <summary>
/// The one place that defines how actors are ordered and coloured (brain 0028): failed, then slow, then (running), then successful, each
/// by start time. Everything that lists or groups actors uses <see cref="Comparer"/>.
/// </summary>
public static class ActorOrder
{
    public static ActorState StateOf(ActorOutcome o) => o switch { ActorOutcome.Failed => ActorState.Failed, ActorOutcome.Slow => ActorState.Slow, _ => ActorState.Ok };

    /// <summary>CSS class of the outcome colour: bad = red, warn = yellow, good = green, run = blue.</summary>
    public static string Css(ActorState s, int tint = 0) => s switch { ActorState.Failed => "bad", ActorState.Slow => "warn", ActorState.Running => "run", _ => Good(tint) };

    /// <summary>Green, or a step towards amber (brain 0041): "good" at typical, "good t1".."good t3" above it. The step is set as <c>t</c> class that moves the colour.</summary>
    public static string Good(int tint) => tint > 0 && tint < SlowCallRule.MaxStep ? "good t" + tint : tint >= SlowCallRule.MaxStep ? "warn" : "good";

    public static string Word(ActorState s) => s switch { ActorState.Failed => "failed", ActorState.Slow => "slow", ActorState.Running => "running", _ => "ok" };

    public static readonly Comparison<ActorRow> Comparer = (a, b) =>
    {
        var c = ((int)a.State).CompareTo((int)b.State);
        if (c != 0) return c;
        c = a.StartedAt.CompareTo(b.StartedAt);
        return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    };

    /// <summary>All actors of one scenario that started in [from, to), filtered like the chart, in the general order. Running actors are included only for "All".</summary>
    public static List<ActorRow> Rows(IReadOnlyList<ActorRecord> actors, int count, IReadOnlyList<RunningActor> running, ActorShow show, double from, double to, double minMs = 0)
    {
        var rows = new List<ActorRow>();
        for (var i = 0; i < count; i++)
        {
            var a = actors[i];
            if (!ActorBars.Includes(show, a) || a.StartedAt < from || a.StartedAt >= to || a.DurationMs < minMs) continue;
            rows.Add(new(a.Sequence, a.StartedAt, a.DurationMs, StateOf(a.Outcome), i, a.Tint));
        }
        if (show == ActorShow.All && minMs <= 0)
            foreach (var r in running)
                if (r.StartedAt >= from && r.StartedAt < to) rows.Add(new(r.Sequence, r.StartedAt, 0, ActorState.Running, -1));
        rows.Sort(Comparer);
        return rows;
    }
}
