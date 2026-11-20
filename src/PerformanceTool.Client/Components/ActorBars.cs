using PerformanceTool.Client.Services;

namespace PerformanceTool.Client.Components;

public enum ActorShow { All, Failed, Slow }

/// <summary>One bar of the actor chart: a single actor, or a time bucket of actors (brain 0021). Red if any actor in it failed, amber if any was slow.</summary>
public readonly record struct ActorBar(int Count, int Failed, int Slow, double MaxMs, int Sequence, double BucketStart)
{
    public bool IsBucket => Sequence < 0;
    public ActorOutcome Outcome => Failed > 0 ? ActorOutcome.Failed : Slow > 0 ? ActorOutcome.Slow : ActorOutcome.Ok;
}

/// <summary>
/// Builds the bounded bar list of the actor chart. Up to <see cref="MaxBars"/> actors get one bar each; more than that are aggregated
/// into time buckets of a "nice" width, so the number of bars never depends on the length of the run.
/// </summary>
public static class ActorBars
{
    public const int MaxBars = 48;
    private static readonly double[] Widths = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 43200, 86400];

    public static bool Includes(ActorShow show, in ActorRecord a) => show switch
    {
        ActorShow.Failed => a.Failed,
        ActorShow.Slow => a.Outcome == ActorOutcome.Slow,
        _ => true,
    };

    /// <summary>Returns the bars; <paramref name="bucketWidth"/> is 0 when every bar is one actor.</summary>
    public static List<ActorBar> Build(IReadOnlyList<ActorRecord> actors, int count, ActorShow show, out int total, out double bucketWidth)
    {
        total = 0;
        double min = double.MaxValue, max = double.MinValue;
        for (var i = 0; i < count; i++)
        {
            var a = actors[i];
            if (!Includes(show, a)) continue;
            total++;
            if (a.StartedAt < min) min = a.StartedAt;
            if (a.StartedAt > max) max = a.StartedAt;
        }
        bucketWidth = 0;
        var bars = new List<ActorBar>(Math.Min(total, MaxBars + 1));
        if (total == 0) return bars;

        if (total <= MaxBars)
        {
            for (var i = 0; i < count; i++)
            {
                var a = actors[i];
                if (Includes(show, a)) bars.Add(new(1, a.Failed ? 1 : 0, a.Outcome == ActorOutcome.Slow ? 1 : 0, a.DurationMs, a.Sequence, double.NaN));
            }
            return bars;
        }

        var span = Math.Max(max - min, 1);
        bucketWidth = Widths.FirstOrDefault(w => span / w < MaxBars, Widths[^1]);
        var first = (long)Math.Floor(min / bucketWidth);
        var slots = (int)((long)Math.Floor(max / bucketWidth) - first + 1);
        var n = new int[slots]; var f = new int[slots]; var sl = new int[slots]; var ms = new double[slots];
        for (var i = 0; i < count; i++)
        {
            var a = actors[i];
            if (!Includes(show, a)) continue;
            var k = (int)((long)Math.Floor(a.StartedAt / bucketWidth) - first);
            n[k]++;
            if (a.Failed) f[k]++;
            else if (a.SlowCalls > 0) sl[k]++;
            if (a.DurationMs > ms[k]) ms[k] = a.DurationMs;
        }
        for (var k = 0; k < slots; k++) bars.Add(new(n[k], f[k], sl[k], ms[k], -1, (first + k) * bucketWidth));
        return bars;
    }

    /// <summary>The actors of one time bucket, in completion order.</summary>
    public static List<ActorRecord> BucketActors(IReadOnlyList<ActorRecord> actors, int count, ActorShow show, double start, double width)
    {
        var list = new List<ActorRecord>();
        for (var i = 0; i < count; i++)
        {
            var a = actors[i];
            if (Includes(show, a) && a.StartedAt >= start && a.StartedAt < start + width) list.Add(a);
        }
        return list;
    }

    /// <summary>The bucket start a given actor falls into (for highlighting and for stepping to an actor).</summary>
    public static double BucketOf(double startedAt, double width) => Math.Floor(startedAt / width) * width;

    /// <summary>The next (or previous) actor with the given outcome after <paramref name="fromSequence"/>, wrapping around; null if there is none.</summary>
    public static ActorRecord? Step(IReadOnlyList<ActorRecord> actors, int count, ActorOutcome outcome, int? fromSequence, int direction)
    {
        if (count == 0) return null;
        var pos = -1;
        if (fromSequence is { } seq)
            for (var i = 0; i < count; i++) if (actors[i].Sequence == seq) { pos = i; break; }
        if (pos < 0) pos = direction > 0 ? -1 : count;
        for (var step = 1; step <= count; step++)
        {
            var i = ((pos + direction * step) % count + count) % count;
            if (actors[i].Outcome == outcome) return actors[i];
        }
        return null;
    }
}
