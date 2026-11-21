using PerformanceTool.Client.Services;

namespace PerformanceTool.Client.Components;

public enum ActorShow { All, Failed, Slow }

/// <summary>One bar of the actor chart: a single actor, or a time bucket of actors (brain 0021). Red if any actor in it failed, amber if any was slow.</summary>
/// <remarks>
/// <see cref="Running"/> counts actors in flight (brain 0018): a single running actor is a bar with Count 0 and Running 1 whose
/// <see cref="MaxMs"/> is its elapsed time; in a time bucket <see cref="Running"/> is the number of running actors.
/// <see cref="BucketStart"/> is the start time of a single actor (running or finished) or of a time bucket.
/// </remarks>
public readonly record struct ActorBar(int Count, int Failed, int Slow, double MaxMs, int Sequence, double BucketStart, int Running = 0)
{
    public int Total => Count + Running;
    public bool IsRunningActor => Sequence >= 0 && Running > 0 && Count == 0;
    public bool IsBucket => Sequence < 0;
    public ActorOutcome Outcome => Failed > 0 ? ActorOutcome.Failed : Slow > 0 ? ActorOutcome.Slow : ActorOutcome.Ok;
}

/// <summary>
/// Builds the bounded bar list of the actor chart. Up to <see cref="MaxBars"/> actors get one bar each; more than that are aggregated
/// into time buckets of a "nice" width, so the number of bars never depends on the length of the run.
/// </summary>
public static class ActorBars
{
    public const int MaxBars = 240;
    private static readonly double[] Widths = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 43200, 86400];

    public static bool Includes(ActorShow show, in ActorRecord a) => show switch
    {
        ActorShow.Failed => a.Failed,
        ActorShow.Slow => a.Outcome == ActorOutcome.Slow,
        _ => true,
    };

    /// <summary>Returns the bars; <paramref name="bucketWidth"/> is 0 when every bar is one actor.</summary>
    /// <remarks>Only actors that started in [from, to) count (the brushed time window); maxBars is the user setting (brain 0023, update 3).</remarks>
    public static List<ActorBar> Build(IReadOnlyList<ActorRecord> actors, int count, ActorShow show, out int total, out double bucketWidth,
        double from = double.NegativeInfinity, double to = double.PositiveInfinity, int maxBars = MaxBars)
    {
        total = 0;
        double min = double.MaxValue, max = double.MinValue;
        for (var i = 0; i < count; i++)
        {
            var a = actors[i];
            if (!Includes(show, a) || a.StartedAt < from || a.StartedAt >= to) continue;
            total++;
            if (a.StartedAt < min) min = a.StartedAt;
            if (a.StartedAt > max) max = a.StartedAt;
        }
        bucketWidth = 0;
        var bars = new List<ActorBar>(Math.Min(total, maxBars + 1));
        if (total == 0) return bars;

        if (total <= maxBars)
        {
            for (var i = 0; i < count; i++)
            {
                var a = actors[i];
                if (Includes(show, a) && a.StartedAt >= from && a.StartedAt < to) bars.Add(new(1, a.Failed ? 1 : 0, a.Outcome == ActorOutcome.Slow ? 1 : 0, a.DurationMs, a.Sequence, a.StartedAt));
            }
            return bars;
        }

        var span = Math.Max(max - min, 1);
        bucketWidth = Widths.FirstOrDefault(w => span / w < maxBars, Widths[^1]);
        var first = (long)Math.Floor(min / bucketWidth);
        var slots = (int)((long)Math.Floor(max / bucketWidth) - first + 1);
        var n = new int[slots]; var f = new int[slots]; var sl = new int[slots]; var ms = new double[slots];
        for (var i = 0; i < count; i++)
        {
            var a = actors[i];
            if (!Includes(show, a) || a.StartedAt < from || a.StartedAt >= to) continue;
            var k = (int)((long)Math.Floor(a.StartedAt / bucketWidth) - first);
            n[k]++;
            if (a.Failed) f[k]++;
            else if (a.SlowCalls > 0) sl[k]++;
            if (a.DurationMs > ms[k]) ms[k] = a.DurationMs;
        }
        for (var k = 0; k < slots; k++) bars.Add(new(n[k], f[k], sl[k], ms[k], -1, (first + k) * bucketWidth));
        return bars;
    }

    private const int MaxRunningBars = 24;

    /// <summary>Adds the actors in flight to the bars: as blue bars at the right edge, or counted into their (newest) time bucket.</summary>
    public static List<ActorBar> WithRunning(List<ActorBar> bars, IReadOnlyList<RunningActor> running, double bucketWidth, double elapsed, double? gridFirst = null)
    {
        if (running.Count == 0) return bars;
        var result = new List<ActorBar>(bars);
        if (bucketWidth <= 0)
        {
            foreach (var r in running.Skip(Math.Max(0, running.Count - MaxRunningBars)))
                result.Add(new(0, 0, 0, Math.Max(0, (elapsed - r.StartedAt) * 1000), r.Sequence, r.StartedAt, 1));
            return result;
        }
        var first = gridFirst ?? result[0].BucketStart;
        foreach (var r in running)
        {
            var k = (int)Math.Max(0, Math.Round((BucketOf(r.StartedAt, bucketWidth) - first) / bucketWidth));
            while (result.Count <= k && result.Count < bars.Count + 3) result.Add(new(0, 0, 0, 0, -1, first + result.Count * bucketWidth));
            k = Math.Min(k, result.Count - 1);
            var b = result[k];
            // a single-actor slot of a grid becomes a plain slot again once an actor runs in it
            if (gridFirst is not null && !b.IsBucket && b.Total > 0) b = b with { Sequence = -1, BucketStart = Slot(SlotIndex(first, bucketWidth) + k, bucketWidth) };
            result[k] = b with { Running = b.Running + 1, MaxMs = Math.Max(b.MaxMs, (elapsed - r.StartedAt) * 1000) };
        }
        return result;
    }

    /// <summary>
    /// Actors that were running when the chart froze for aiming (brain 0026) and have finished since get their final colour, in place
    /// (brain 0023, update 3): a running bar becomes a finished bar at the same position, a running actor of a time bucket moves into
    /// the bucket's counts. Only the actors finished since the freeze (frozenCount..nowCount) are looked at.
    /// </summary>
    public static List<ActorBar> ResolveFinished(List<ActorBar> bars, IReadOnlyList<RunningActor> frozenRunning, IReadOnlyList<ActorRecord> actors, int frozenCount, int nowCount, double bucketWidth, double? gridFirst = null)
    {
        if (frozenRunning.Count == 0 || nowCount <= frozenCount || bars.Count == 0) return bars;
        var seqs = new HashSet<int>(frozenRunning.Count);
        foreach (var r in frozenRunning) seqs.Add(r.Sequence);
        List<ActorBar>? result = null;
        for (var i = frozenCount; i < nowCount; i++)
        {
            var a = actors[i];
            if (!seqs.Contains(a.Sequence)) continue;
            result ??= new List<ActorBar>(bars);
            var done = new ActorBar(1, a.Failed ? 1 : 0, a.Outcome == ActorOutcome.Slow ? 1 : 0, a.DurationMs, a.Sequence, a.StartedAt);
            if (bucketWidth <= 0)
            {
                var k = result.FindIndex(b => b.IsRunningActor && b.Sequence == a.Sequence);
                if (k >= 0) result[k] = done;
            }
            else
            {
                var k = (int)Math.Round((BucketOf(a.StartedAt, bucketWidth) - (gridFirst ?? result[0].BucketStart)) / bucketWidth);
                if (k < 0 || k >= result.Count || result[k].Running == 0) continue;
                var b = result[k];
                result[k] = b with { Running = b.Running - 1, Count = b.Count + 1, Failed = b.Failed + done.Failed, Slow = b.Slow + done.Slow, MaxMs = Math.Max(b.MaxMs, a.DurationMs) };
            }
        }
        return result ?? bars;
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
    public static double BucketOf(double startedAt, double width) => Slot(SlotIndex(startedAt, width), width);

    // ---- stable time grid (brain 0031 update 2): while zoomed, bucket edges are multiples of the width counted from the run start, so a bucket
    // keeps the same actors whatever part of the run is in view; panning only moves the bars.
    private static readonly double[] GridWidths = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 43200, 86400];

    /// <summary>The bucket width for a view of <paramref name="span"/> seconds: the smallest nice width that keeps the visible bars below <paramref name="maxBars"/>.</summary>
    public static double GridWidth(double span, int maxBars) => GridWidths.FirstOrDefault(w => span / w < maxBars, GridWidths[^1]);
    public static long SlotIndex(double t, double width) => (long)Math.Floor(t / width + 1e-9);
    /// <summary>Start time of a grid slot, rounded to 3 decimals so it survives being written to a key and parsed back.</summary>
    public static double Slot(long index, double width) => Math.Round(index * width, 3);

    /// <summary>
    /// The bars of the grid slots firstIdx..lastIdx (empty slots included, so bar i is always the slot firstIdx + i). A slot with exactly one actor
    /// is a plain actor bar. <paramref name="total"/> counts the actors that started in the view [viewFrom, viewTo).
    /// </summary>
    public static List<ActorBar> BuildGrid(IReadOnlyList<ActorRecord> actors, int count, ActorShow show, double width, long firstIdx, long lastIdx,
        double viewFrom, double viewTo, out int total)
    {
        total = 0;
        var slots = (int)Math.Max(0, lastIdx - firstIdx + 1);
        var n = new int[slots]; var f = new int[slots]; var sl = new int[slots]; var ms = new double[slots];
        var seq = new int[slots]; var st = new double[slots]; var slowOne = new bool[slots];
        for (var i = 0; i < count; i++)
        {
            var a = actors[i];
            if (!Includes(show, a)) continue;
            if (a.StartedAt >= viewFrom && a.StartedAt < viewTo) total++;
            var idx = SlotIndex(a.StartedAt, width);
            if (idx < firstIdx || idx > lastIdx) continue;
            var k = (int)(idx - firstIdx);
            n[k]++; seq[k] = a.Sequence; st[k] = a.StartedAt; slowOne[k] = a.Outcome == ActorOutcome.Slow;
            if (a.Failed) f[k]++;
            else if (a.SlowCalls > 0) sl[k]++;
            if (a.DurationMs > ms[k]) ms[k] = a.DurationMs;
        }
        var bars = new List<ActorBar>(slots);
        for (var k = 0; k < slots; k++)
        {
            if (n[k] == 1) bars.Add(new(1, f[k], slowOne[k] ? 1 : 0, ms[k], seq[k], st[k]));
            else bars.Add(new(n[k], f[k], sl[k], ms[k], -1, Slot(firstIdx + k, width)));
        }
        return bars;
    }

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

/// <summary>
/// The sticky bar buffer of a zoomed chart: the grid slots from one view-width left of the view to one view-width right of it. It is only rebuilt
/// when the bucket width changes or the view comes within half a view-width of an edge (not at the run's start or newest end), so panning in
/// between does not touch the bars; the script just slides them.
/// </summary>
public sealed class ChartGrid
{
    public double Width { get; private set; }
    public long First { get; private set; }
    public long Last { get; private set; } = -1;
    /// <summary>The buffer reaches the newest end of the run and grows with it.</summary>
    public bool Open { get; private set; }
    public double From => ActorBars.Slot(First, Width);
    public long LastNow(double elapsed) => Open ? Math.Max(Last, ActorBars.SlotIndex(elapsed, Width)) : Last;
    public double To => Open ? double.PositiveInfinity : ActorBars.Slot(Last + 1, Width);

    public void Ensure(double viewFrom, double viewTo, double elapsed, int maxBars)
    {
        var span = Math.Max(viewTo - viewFrom, 1e-6);
        var w = ActorBars.GridWidth(span, maxBars);
        var need = Width != w || Last < First;
        if (!need)
        {
            var lo = From; var hi = To;
            if ((First > 0 && viewFrom < lo + 0.5 * span) || (!Open && viewTo > hi - 0.5 * span) || viewFrom < lo || viewTo > hi) need = true;
        }
        if (!need) return;
        Width = w;
        First = Math.Max(0, ActorBars.SlotIndex(viewFrom - span, w));
        var end = ActorBars.SlotIndex(viewTo + span, w);
        var run = ActorBars.SlotIndex(Math.Max(0, elapsed), w);
        Open = end >= run;
        Last = Open ? run : end;
    }
}
