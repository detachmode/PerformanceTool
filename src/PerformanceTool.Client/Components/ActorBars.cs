using PerformanceTool.Client.Services;

namespace PerformanceTool.Client.Components;

public enum ActorShow { All, Failed, Slow }

/// <summary>One bar of the actor chart: a single actor, or a time bucket of actors (brain 0021). Red if any actor in it failed, amber if any was slow.</summary>
/// <remarks>
/// <see cref="Running"/> counts actors in flight (brain 0018): a single running actor is a bar with Count 0 and Running 1 whose
/// <see cref="MaxMs"/> is its elapsed time; in a time bucket <see cref="Running"/> is the number of running actors.
/// <see cref="BucketStart"/> is the start time of a single actor (running or finished) or of a time bucket.
/// </remarks>
public readonly record struct ActorBar(int Count, int Failed, int Slow, double MaxMs, int Sequence, double BucketStart, int Running = 0, int Burst = 0, double Slot = 0, double Cover = 1)
{
    /// <summary>The bar belongs to an unfolded burst (brain 0038): <see cref="Burst"/> is the burst's actor count, <see cref="Slot"/> the start of its place on the time grid.</summary>
    /// <remarks><see cref="Cover"/> is how many grid slots wide the bar is (brain 0040): 1 for a slot bar; the bars of an unfolded burst share the empty slots
    /// the burst borrowed, so together they cover a whole number of slots and x stays linear in time across the chart.</remarks>
    public bool InBurst => Burst > 0;
    public int Total => Count + Running;
    public bool IsRunningActor => Sequence >= 0 && Running > 0 && Count == 0;
    public bool IsBucket => Sequence < 0;
    public ActorOutcome Outcome => Failed > 0 ? ActorOutcome.Failed : Slow > 0 ? ActorOutcome.Slow : ActorOutcome.Ok;
}

/// <summary>An unfolded burst (brain 0038): its slot start on the time grid and its actor count (all of them, also those folded into the trailing group bar).</summary>
public readonly record struct BurstSpan(double Slot, int Actors);

/// <summary>
/// The room a zoomed chart has for unfolding bursts (brain 0040), in CSS pixels: <see cref="SlotPx"/> is the width of one grid slot now,
/// <see cref="DecideSlotPx"/> the width used to decide whether a burst fits (a little wider while zooming out, see <see cref="UnfoldHysteresis"/>),
/// <see cref="MinActorPx"/> and <see cref="MaxActorPx"/> the narrowest and widest unfolded actor bar, <see cref="ChartPx"/> the chart width (0 if unknown).
/// </summary>
public readonly record struct UnfoldRoom(double SlotPx, double DecideSlotPx, double MinActorPx, double MaxActorPx = ActorBars.MaxActorPx, double ChartPx = 0)
{
    /// <summary>The room for a view of <paramref name="span"/> seconds on a chart of <paramref name="chartPx"/> pixels with grid width <paramref name="width"/>;
    /// <paramref name="decideSpan"/> is the span the fold decision uses (from <see cref="UnfoldHysteresis"/>, never wider than the view).</summary>
    public static UnfoldRoom For(double chartPx, double span, double decideSpan, double width, int maxBars) =>
        chartPx <= 0 || span <= 0 || width <= 0 ? default
            : new(chartPx * width / span, chartPx * width / Math.Max(1e-9, Math.Min(span, decideSpan)), ActorBars.MinActorPx(chartPx, maxBars), ActorBars.MaxActorPx, chartPx);
}

/// <summary>
/// Hysteresis of unfolding (brain 0040), as play in the zoom: zooming in, the fold decision uses the real span; zooming out, it keeps the smallest span
/// until the view is <see cref="Play"/> times wider. So a burst unfolds when its bars reach the minimum width and folds again only when they are about
/// 30 % narrower, the same for every burst (also for one that comes into view while panning). Panning keeps the span, so nothing flips.
/// </summary>
public sealed class UnfoldHysteresis
{
    public const double Play = 1.4;
    private double r = double.PositiveInfinity;
    /// <summary>The span the decision uses for a view of <paramref name="span"/> seconds.</summary>
    public double Update(double span)
    {
        if (!(span > 0)) return span;
        if (span < r) r = span;
        else if (span > r * Play) r = span / Play;
        return r;
    }
    public void Reset() => r = double.PositiveInfinity;
}

/// <summary>One stripe of a highlighted group bar (brain 0037): its kind ("bad", "warn", "good", "run"), actor count and share of the bar in percent.</summary>
public readonly record struct StripeSegment(string Kind, int Count, double Share);

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

    /// <summary>Bins of the overview strip (brain 0035 update 1): the strip's cost depends on this, not on the number of actors.</summary>
    public const int OverviewBins = 300;

    /// <summary>
    /// Where failed and slow actors were over the whole run, for the overview strip: one character per bin of [0, <paramref name="elapsed"/>]:
    /// 'f' if an actor that started in the bin failed, else 's' if one was slow, else '.'. One pass over the actors, a fixed number of bins.
    /// </summary>
    public static string OverviewMarks(IReadOnlyList<ActorRecord> actors, int count, double elapsed, int bins = OverviewBins)
    {
        if (elapsed <= 0 || bins <= 0) return "";
        var m = new char[bins];
        Array.Fill(m, '.');
        for (var i = 0; i < count; i++)
        {
            var a = actors[i];
            if (a.Outcome == ActorOutcome.Ok) continue;
            var k = Math.Clamp((int)(a.StartedAt / elapsed * bins), 0, bins - 1);
            if (a.Failed) m[k] = 'f'; else if (m[k] == '.') m[k] = 's';
        }
        return new string(m);
    }

    // ---- unfolding bursts (brain 0038, rule of brain 0040)
    /// <summary>Most single-actor bars one unfolded burst shows (the rest of its actors stay one trailing group bar), for the max-bars setting.</summary>
    public static int UnfoldPerBurst(int maxBars) => Math.Max(12, maxBars / 4);
    /// <summary>Most bars all unfolded bursts together may show, for the max-bars setting (a safety net: the pixel rule already bounds them).</summary>
    public static int UnfoldBudget(int maxBars) => Math.Max(24, maxBars);
    /// <summary>The narrowest unfolded actor bar in CSS pixels: 1.5 times the bar width the max-bars setting allows on this chart, at least 4 px.</summary>
    public static double MinActorPx(double chartPx, int maxBars) => Math.Max(4, 1.5 * chartPx / Math.Max(1, maxBars));
    /// <summary>The widest unfolded actor bar in CSS pixels (3rem): zoomed in further, a burst stops growing.</summary>
    public const double MaxActorPx = 48;
    /// <summary>A burst borrows at most this share of the empty time to its nearest neighbour on each side, so the quiet gap between two bursts stays visible.</summary>
    public const double GapShare = 0.35;
    private const double SameStart = 0.001;

    /// <summary>
    /// Unfolds the group bars of a zoomed grid (brain 0038, rule of brain 0040): a slot bar of several actors that start at the same moment (or any group
    /// of a slot of the smallest grid width, which time cannot split further) becomes one ordinary bar per actor as soon as they fit. The burst borrows the
    /// empty slots around it (at most <see cref="GapShare"/> of the empty time to the nearest other actor on each side) and its bars share that room
    /// (<see cref="ActorBar.Cover"/>), so nothing else moves and x stays linear in time. It unfolds when each bar gets at least
    /// <see cref="UnfoldRoom.MinActorPx"/> of the room it may borrow, measured with <see cref="UnfoldRoom.DecideSlotPx"/> (hysteresis); it takes only as
    /// much room as bars of <see cref="UnfoldRoom.MaxActorPx"/> need. The decision depends on the zoom and the data, not on the part of the run in view,
    /// so panning never folds or unfolds a burst. Bounded: a burst shows at most <paramref name="perBurst"/> bars (failed, then slow actors first, the
    /// others folded into one trailing group bar that selects the burst), all bursts together at most <paramref name="budget"/> bars, nearest to
    /// <paramref name="focus"/> (run seconds) first.
    /// </summary>
    /// <param name="slots">Bar i of the grid is the slot starting at <c>Slot(firstIdx + i, width)</c> (running actors already counted in).</param>
    public static List<ActorBar> Unfold(List<ActorBar> slots, IReadOnlyList<ActorRecord> actors, int count, ActorShow show, IReadOnlyList<RunningActor> running,
        double width, long firstIdx, double elapsed, double focus, int perBurst, int budget, UnfoldRoom room, out List<BurstSpan> bursts)
    {
        bursts = [];
        if (width <= 0 || slots.Count == 0 || perBurst < 2 || room.SlotPx <= 0 || room.DecideSlotPx <= 0) return slots;
        var cand = new List<int>();
        for (var k = 0; k < slots.Count; k++) if (slots[k].IsBucket && slots[k].Total >= 2) cand.Add(k);
        if (cand.Count == 0) return slots;
        var floor = width <= GridWidths[0] + 1e-9;
        // the actors of the candidate slots and, per candidate, the nearest occupied slot before and after it: one pass over the actors
        var cIdx = cand.Select(k => firstIdx + k).ToArray();
        var prev = new long[cand.Count]; var next = new long[cand.Count];
        for (var j = 0; j < cand.Count; j++) { prev[j] = j > 0 ? cIdx[j - 1] : long.MinValue; next[j] = j < cand.Count - 1 ? cIdx[j + 1] : long.MaxValue; }
        var byIdx = new List<(ActorRecord? Done, RunningActor? Run)>[cand.Count];
        for (var j = 0; j < cand.Count; j++) byIdx[j] = [];
        void See(double start, ActorRecord? done, RunningActor? run)
        {
            var si = SlotIndex(start, width);
            var j = Array.BinarySearch(cIdx, si);
            if (j >= 0) { byIdx[j].Add((done, run)); return; }
            j = ~j;
            if (j > 0 && si < next[j - 1]) next[j - 1] = si;
            if (j < cand.Count && si > prev[j]) prev[j] = si;
        }
        for (var i = 0; i < count; i++) { var a = actors[i]; if (Includes(show, a)) See(a.StartedAt, a, null); }
        if (show == ActorShow.All) foreach (var r in running) See(r.StartedAt, null, r);

        static double Start((ActorRecord? Done, RunningActor? Run) e) => e.Done?.StartedAt ?? e.Run!.StartedAt;
        static int Seq((ActorRecord? Done, RunningActor? Run) e) => e.Done?.Sequence ?? e.Run!.Sequence;
        static int Rank((ActorRecord? Done, RunningActor? Run) e) => e.Done is { } d ? (d.Failed ? 0 : d.Outcome == ActorOutcome.Slow ? 1 : 3) : 2;
        // nearest to the focus first
        var order = Enumerable.Range(0, cand.Count).ToList();
        order.Sort((x, y) => Math.Abs(Slot(cIdx[x], width) + width / 2 - focus).CompareTo(Math.Abs(Slot(cIdx[y], width) + width / 2 - focus)));
        var chosen = new SortedDictionary<int, (int Cover, List<ActorBar> Bars)>();     // by the list index of the cell's first slot
        var used = 0;
        foreach (var j in order)
        {
            var list = byIdx[j];
            if (list.Count < 2) continue;
            if (!floor && list.Max(Start) - list.Min(Start) > SameStart) continue;     // time can still split this group
            var nb = Math.Min(list.Count, perBurst);
            if (used + nb > budget) continue;
            // the room it may borrow: a share of the empty slots on each side (where there is no neighbour, as much as on the other side)
            var cs = cIdx[j];
            double? gl = prev[j] == long.MinValue ? null : cs - prev[j] - 1, gr = next[j] == long.MaxValue ? null : next[j] - cs - 1;
            var gap = Math.Min(gl ?? gr ?? 1e6, gr ?? gl ?? 1e6);
            // the decision uses the room as a continuous share (so it does not jump when the grid width changes); the cell takes whole slots
            var share = GapShare * gap;
            var m = (long)Math.Round(share);
            long mL = Math.Min(m, cs), mR = m;                                          // no room before the run's start
            if ((1 + Math.Min(share, cs) + share) * room.DecideSlotPx / nb < room.MinActorPx) continue;      // does not fit (yet)
            // it takes only as much room as bars of the widest size need (and not more than most of the chart)
            var target = room.MaxActorPx;
            if (room.ChartPx > 0) target = Math.Min(target, 0.9 * room.ChartPx / nb);
            target = Math.Max(target, room.MinActorPx);
            var want = Math.Max(1, (long)Math.Ceiling(nb * target / room.SlotPx - 1e-9));
            var hL = Math.Min(mL, (want - 1) / 2);
            var hR = Math.Min(mR, want - 1 - hL);
            hL = Math.Min(mL, want - 1 - hR);
            var from = (int)(cs - hL - firstIdx);
            if (from < 0) continue;                       // reaches out of the buffer on the left: far outside the view, stays folded there
            var cover = (int)(1 + hL + hR);
            var free = true;
            for (var q = from; q < from + cover && q < slots.Count && free; q++)
                if (q != cs - firstIdx && slots[q].Total > 0) free = false;
            foreach (var (f0, c) in chosen) if (from < f0 + c.Cover && f0 < from + cover) free = false;
            if (!free) continue;

            var shown = nb;
            var rest = list.Count - shown;
            if (rest > 0) shown--;                                                     // the trailing group bar takes the last place
            // the actors to show singly: all of them, or the most interesting ones (failed, slow, running) of a big burst
            var pick = rest > 0 ? list.OrderBy(Rank).ThenBy(Seq).Take(shown).ToList() : list;
            pick = pick.OrderBy(Start).ThenBy(Seq).ToList();
            var slot = Slot(cs, width);
            var each = (double)cover / nb;
            var bars = new List<ActorBar>(nb);
            foreach (var e in pick)
            {
                if (e.Done is { } d) bars.Add(new(1, d.Failed ? 1 : 0, d.Outcome == ActorOutcome.Slow ? 1 : 0, d.DurationMs, d.Sequence, d.StartedAt, 0, list.Count, slot, each));
                else bars.Add(new(0, 0, 0, Math.Max(0, (elapsed - e.Run!.StartedAt) * 1000), e.Run.Sequence, e.Run.StartedAt, 1, list.Count, slot, each));
            }
            if (rest > 0)
            {
                var picked = new HashSet<int>(pick.Select(Seq));
                int n = 0, f = 0, sl = 0, run = 0; double ms = 0;
                foreach (var e in list)
                {
                    if (picked.Contains(Seq(e))) continue;
                    if (e.Done is { } d) { n++; if (d.Failed) f++; else if (d.SlowCalls > 0) sl++; ms = Math.Max(ms, d.DurationMs); }
                    else { run++; ms = Math.Max(ms, (elapsed - e.Run!.StartedAt) * 1000); }
                }
                bars.Add(new(n, f, sl, ms, -1, slot, run, list.Count, slot, each));
            }
            chosen[from] = (cover, bars); used += nb;
            bursts.Add(new BurstSpan(slot, list.Count));
        }
        if (chosen.Count == 0) return slots;
        bursts.Sort((x, y) => x.Slot.CompareTo(y.Slot));
        var result = new List<ActorBar>(slots.Count + used);
        for (var k = 0; k < slots.Count;)
        {
            if (chosen.TryGetValue(k, out var c)) { result.AddRange(c.Bars); k += c.Cover; }
            else result.Add(slots[k++]);
        }
        return result;
    }

    /// <summary>
    /// The make-up of a group bar as stripes (brain 0037): failed, slow, ok, running, in that order, zero counts left out. Each share is in percent of
    /// the bar, proportional to the count, but at least <paramref name="minShare"/> percent so a single failed actor in a big group stays visible;
    /// the shares add up to 100.
    /// </summary>
    public static List<StripeSegment> Stripes(ActorBar bar, double minShare = 4)
    {
        var ok = Math.Max(0, bar.Count - bar.Failed - bar.Slow);
        var parts = new List<StripeSegment>(4);
        if (bar.Failed > 0) parts.Add(new("bad", bar.Failed, 0));
        if (bar.Slow > 0) parts.Add(new("warn", bar.Slow, 0));
        if (ok > 0) parts.Add(new("good", ok, 0));
        if (bar.Running > 0) parts.Add(new("run", bar.Running, 0));
        if (parts.Count == 0) return parts;
        minShare = Math.Clamp(minShare, 0, 100.0 / parts.Count);
        // segments below the minimum get the minimum; the others share what is left in proportion to their counts (repeat until stable)
        var fixedMin = new bool[parts.Count];
        for (var pass = 0; pass < parts.Count; pass++)
        {
            var left = 100 - minShare * fixedMin.Count(f => f);
            var free = 0;
            for (var i = 0; i < parts.Count; i++) if (!fixedMin[i]) free += parts[i].Count;
            var again = false;
            for (var i = 0; i < parts.Count; i++)
                if (!fixedMin[i] && parts[i].Count * left / free < minShare) { fixedMin[i] = true; again = true; }
            if (!again) break;
        }
        var rest = 100 - minShare * fixedMin.Count(f => f);
        var freeCount = 0;
        for (var i = 0; i < parts.Count; i++) if (!fixedMin[i]) freeCount += parts[i].Count;
        for (var i = 0; i < parts.Count; i++)
            parts[i] = parts[i] with { Share = fixedMin[i] ? minShare : parts[i].Count * rest / freeCount };
        return parts;
    }

    /// <summary>The stripes as a compact attribute for the script: "bad:12.5 good:87.5".</summary>
    public static string StripeData(ActorBar bar) =>
        string.Join(' ', Stripes(bar).Select(p => p.Kind + ":" + p.Share.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));

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
