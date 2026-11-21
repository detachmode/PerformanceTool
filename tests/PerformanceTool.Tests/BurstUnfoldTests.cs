using PerformanceTool.Client.Components;
using PerformanceTool.Client.Services;

namespace PerformanceTool.Tests;

/// <summary>Unfolding bursts into single-actor bars (brain 0038), with the pixel rule, borrowed room and hysteresis of brain 0040.</summary>
public class BurstUnfoldTests
{
    private static readonly ErrorDictionary Errors = new();
    private static ActorRecord A(int seq, double start, bool failed = false) =>
        new(seq, 0, start, 100, 0, failed ? Errors.Intern("boom") : ErrorDictionary.None, Errors);

    // a burst of n actors at 10.0 s, one lone actor at 10.5 s and another burst of m at 12.0 s (the third of it failed)
    private static List<ActorRecord> Run(int n, int m)
    {
        var l = new List<ActorRecord>();
        var s = 0;
        for (var i = 0; i < n; i++) l.Add(A(s++, 10.0));
        l.Add(A(s++, 10.5));
        for (var i = 0; i < m; i++) l.Add(A(s++, 12.0, i == 2));
        return l;
    }

    // bursts of `size` actors every `every` seconds from 0 to `until`
    private static List<ActorRecord> Periodic(int size, double every, double until)
    {
        var l = new List<ActorRecord>();
        var s = 0;
        for (var t = 0.0; t < until; t += every)
            for (var i = 0; i < size; i++) l.Add(A(s++, t));
        return l;
    }

    private static List<ActorBar> Grid(List<ActorRecord> a, double w, out long first, ActorShow show = ActorShow.All, double from = 9, double to = 13)
    {
        first = ActorBars.SlotIndex(from, w);
        return ActorBars.BuildGrid(a, a.Count, show, w, first, ActorBars.SlotIndex(to, w), from, to, out _);
    }

    /// <summary>Plenty of room: 100 px per slot.</summary>
    private static UnfoldRoom Wide => new(100, 100, 4, 48, 900);

    private static List<ActorBar> Unfold(List<ActorBar> slots, List<ActorRecord> a, double w, long first, UnfoldRoom room, out List<BurstSpan> bursts,
        double focus = 11, int perBurst = 60, int budget = 240, ActorShow show = ActorShow.All, IReadOnlyList<RunningActor>? running = null) =>
        ActorBars.Unfold(slots, a, a.Count, show, running ?? [], w, first, 20, focus, perBurst, budget, room, out bursts);

    [Fact]
    public void A_burst_becomes_one_bar_per_actor_in_the_room_it_borrows_and_x_stays_linear_in_time()
    {
        var a = Run(5, 3);
        var slots = Grid(a, 0.1, out var first);
        var bars = Unfold(slots, a, 0.1, first, Wide, out var bursts);
        Assert.Equal(2, bursts.Count);
        Assert.Equal(new BurstSpan(10.0, 5), bursts[0]);
        var one = bars.Where(b => b.InBurst && b.Slot == 10.0).ToList();
        Assert.Equal(5, one.Count);
        Assert.All(one, b => { Assert.False(b.IsBucket); Assert.Equal(5, b.Burst); });
        Assert.Equal([0, 1, 2, 3, 4], one.Select(b => b.Sequence));
        // the bars still cover exactly the grid's slots, so every other bar keeps its place on the time axis
        Assert.Equal(slots.Count, bars.Sum(b => b.Cover), 6);
        double x = 0;
        foreach (var b in bars)
        {
            if (!b.InBurst && b.Total > 0) Assert.Equal(ActorBars.SlotIndex(b.BucketStart, 0.1) - first, x, 6);
            x += b.Cover;
        }
        Assert.Equal(1, bars.Count(b => b.Sequence == 5 && !b.InBurst));
        Assert.Equal(1, bars.Count(b => b.Failed > 0));
    }

    [Fact]
    public void A_burst_borrows_at_most_a_share_of_the_quiet_time_to_its_neighbours()
    {
        var a = Run(5, 3);
        var slots = Grid(a, 0.1, out var first);
        var bars = Unfold(slots, a, 0.1, first, new UnfoldRoom(4, 4, 4, 48, 900), out _);
        // burst at 10.0: 4 empty slots to the lone actor at 10.5 and more to the left; 35 % of 4 = 1.4 slots each side: 3.8 slots for 5 bars
        var one = bars.Where(b => b.InBurst && b.Slot == 10.0).ToList();
        Assert.Empty(one);      // 3.8 slots * 4 px / 5 bars is under the 4 px minimum: it stays folded
        var wider = Unfold(slots, a, 0.1, first, new UnfoldRoom(8, 8, 4, 48, 900), out _);
        var burst = wider.Where(b => b.InBurst && b.Slot == 10.0).ToList();
        Assert.Equal(5, burst.Count);
        Assert.Equal(3, burst.Sum(b => b.Cover), 6);      // it takes 1 whole slot each side: 3 slots * 8 px / 5 = 4.8 px per actor
    }

    [Fact]
    public void Without_room_nothing_unfolds_and_the_list_is_the_grid()
    {
        var a = Run(5, 3);
        var slots = Grid(a, 0.1, out var first);
        Assert.Same(slots, Unfold(slots, a, 0.1, first, Wide, out var b1, budget: 1));
        Assert.Empty(b1);
        Assert.Same(slots, Unfold(slots, a, 0.1, first, new UnfoldRoom(1, 1, 4), out var b2));
        Assert.Empty(b2);
        Assert.Same(slots, Unfold(slots, a, 0.1, first, default, out _));      // chart width not known yet
    }

    [Fact]
    public void A_group_with_different_start_times_unfolds_only_at_the_smallest_grid_width()
    {
        var a = new List<ActorRecord> { A(0, 10.0), A(1, 10.004) };
        var slots = Grid(a, 0.1, out var first);
        Assert.Same(slots, Unfold(slots, a, 0.1, first, Wide, out _));
        var fine = Grid(a, 0.01, out var f2);
        var bars = Unfold(fine, a, 0.01, f2, Wide, out var b2);
        Assert.Single(b2);
        Assert.Equal(2, bars.Count(b => b.InBurst));
    }

    [Fact]
    public void A_huge_burst_is_bounded_and_keeps_the_interesting_actors()
    {
        var a = new List<ActorRecord>();
        for (var i = 0; i < 500; i++) a.Add(A(i, 10.0, i == 400));
        var slots = Grid(a, 0.1, out var first);
        var bars = Unfold(slots, a, 0.1, first, Wide, out var bursts, focus: 10);
        var burst = bars.Where(b => b.InBurst).ToList();
        Assert.Equal(60, burst.Count);                       // 59 single bars and the trailing group bar
        Assert.Equal(500, bursts[0].Actors);
        Assert.Contains(burst, b => !b.IsBucket && b.Sequence == 400 && b.Failed == 1);
        var tail = burst[^1];
        Assert.True(tail.IsBucket);
        Assert.Equal(441, tail.Count);
        Assert.Equal(500, burst.Where(b => !b.IsBucket).Sum(b => b.Count) + tail.Count);
        Assert.Equal(slots.Count, bars.Sum(b => b.Cover), 6);
    }

    [Fact]
    public void The_budget_unfolds_the_bursts_nearest_to_the_view_first()
    {
        var a = Run(30, 30);
        var slots = Grid(a, 0.1, out var first);
        var bars = Unfold(slots, a, 0.1, first, Wide, out var bursts, focus: 12.0, budget: 35);
        Assert.Single(bursts);
        Assert.Equal(12.0, bursts[0].Slot);
        Assert.True(bars.Count(b => b.InBurst) <= 35);
    }

    [Fact]
    public void Running_actors_of_a_burst_unfold_too_and_the_filter_applies()
    {
        var a = new List<ActorRecord> { A(0, 10.0, true), A(1, 10.0), A(2, 10.0, true) };
        var running = new List<RunningActor> { new(3, 0, 10.0, []) };
        var slots = ActorBars.WithRunning(Grid(a, 0.1, out var first), running, 0.1, 10.5, ActorBars.Slot(first, 0.1));
        var all = Unfold(slots, a, 0.1, first, Wide, out _, focus: 10, running: running);
        Assert.Equal(4, all.Count(b => b.InBurst));
        Assert.Single(all, b => b.IsRunningActor);
        var failedSlots = Grid(a, 0.1, out _, ActorShow.Failed);
        var failed = Unfold(failedSlots, a, 0.1, first, Wide, out _, focus: 10, show: ActorShow.Failed, running: running);
        Assert.Equal(2, failed.Count(b => b.InBurst));
    }

    [Fact]
    public void A_burst_at_the_runs_start_only_borrows_room_after_it()
    {
        var a = Periodic(6, 10, 30);
        var slots = Grid(a, 0.5, out var first, from: 0, to: 30);
        var bars = Unfold(slots, a, 0.5, first, new UnfoldRoom(10, 10, 4, 48, 900), out var bursts, focus: 0);
        Assert.Contains(bursts, b => b.Slot == 0);
        var x = 0.0;
        foreach (var b in bars) { if (b.InBurst && b.Slot == 0) Assert.True(x < 1e-9 || x > 0); x += b.Cover; }
        Assert.True(bars[0].InBurst);      // the first cell starts at slot 0, nothing before the run
        Assert.Equal(slots.Count, bars.Sum(b => b.Cover), 6);
    }

    /// <summary>Unfolded bursts in a view of <paramref name="span"/> s on a chart <paramref name="px"/> wide, buffer as the dialog builds it.</summary>
    private static HashSet<double> UnfoldedInView(List<ActorRecord> a, double viewFrom, double span, double px, int maxBars = 240, double? decide = null)
    {
        var g = new ChartGrid();
        g.Ensure(viewFrom, viewFrom + span, 1800, maxBars);
        var slots = ActorBars.BuildGrid(a, a.Count, ActorShow.All, g.Width, g.First, g.LastNow(1800), viewFrom, viewFrom + span, out _);
        var room = UnfoldRoom.For(px, span, decide ?? span, g.Width, maxBars);
        ActorBars.Unfold(slots, a, a.Count, ActorShow.All, [], g.Width, g.First, 1800, viewFrom + span / 2,
            ActorBars.UnfoldPerBurst(maxBars), ActorBars.UnfoldBudget(maxBars), room, out var bursts);
        return bursts.Where(b => b.Slot >= viewFrom - 1e-9 && b.Slot < viewFrom + span).Select(b => b.Slot).ToHashSet();
    }

    [Fact]
    public void Bursts_unfold_as_soon_as_their_actors_fit_not_only_at_the_smallest_grid()
    {
        // the seeded "Browse categories" entry: 15 actors every 10 s
        var a = Periodic(15, 10, 1800);
        // phone chart (360 px): 15 bars of at least 4 px need 60 px; a burst may borrow 35 % of the 10 s on each side
        Assert.NotEmpty(UnfoldedInView(a, 600, 30, 360));
        Assert.Empty(UnfoldedInView(a, 600, 60, 360));
        // desktop chart (900 px, 5.6 px per actor): already at a one minute view
        Assert.NotEmpty(UnfoldedInView(a, 600, 60, 900));
        Assert.Empty(UnfoldedInView(a, 600, 120, 900));
        // the old rule only unfolded at a view of about one second
    }

    [Fact]
    public void Panning_does_not_fold_or_unfold_a_burst()
    {
        var a = Periodic(15, 10, 1800);
        foreach (var span in new[] { 25.0, 38.0, 42.0, 70.0 })
        {
            int? n = null;
            for (var from = 300.0; from < 420; from += 3.7)
            {
                var inView = UnfoldedInView(a, from, span, 360);
                var total = (int)(Math.Floor((from + span - 1e-9) / 10) - Math.Ceiling(from / 10) + 1);
                var c = inView.Count == 0 ? 0 : total == inView.Count ? 1 : -1;
                Assert.NotEqual(-1, c);      // all bursts in view are unfolded, or none
                n ??= c;
                Assert.Equal(n, c);          // and that does not change while panning
            }
        }
    }

    [Fact]
    public void Hysteresis_folds_only_when_clearly_narrower_than_where_it_unfolded()
    {
        var h = new UnfoldHysteresis();
        Assert.Equal(60, h.Update(60));
        Assert.Equal(40, h.Update(40));      // zooming in follows at once
        Assert.Equal(40, h.Update(50));      // zooming out a little keeps the decision
        Assert.Equal(40, h.Update(56));
        Assert.Equal(60 / UnfoldHysteresis.Play, h.Update(60), 6);
        h.Reset();
        Assert.Equal(90, h.Update(90));

        // the burst unfolds at about 42 s on a phone; zooming out to 50 s keeps it, zooming in again from far out to 50 s does not unfold it
        var a = Periodic(15, 10, 1800);
        var play = new UnfoldHysteresis();
        Assert.NotEmpty(UnfoldedInView(a, 600, 40, 360, decide: play.Update(40)));
        Assert.NotEmpty(UnfoldedInView(a, 600, 50, 360, decide: play.Update(50)));
        play.Reset(); play.Update(200);
        Assert.Empty(UnfoldedInView(a, 600, 50, 360, decide: play.Update(50)));
    }

    [Fact]
    public void Unfolded_bars_are_bounded_by_the_chart_width()
    {
        var a = Periodic(15, 10, 1800);
        for (var span = 8.0; span < 200; span *= 1.3)
        {
            var g = new ChartGrid();
            g.Ensure(600, 600 + span, 1800, 240);
            var slots = ActorBars.BuildGrid(a, a.Count, ActorShow.All, g.Width, g.First, g.LastNow(1800), 600, 600 + span, out _);
            var bars = ActorBars.Unfold(slots, a, a.Count, ActorShow.All, [], g.Width, g.First, 1800, 600 + span / 2, 60, 240,
                UnfoldRoom.For(360, span, span, g.Width, 240), out _);
            Assert.True(bars.Count <= slots.Count + 240);
            // x stays linear in time: every slot bar sits at its slot index (a cell at the buffer's end may reach a little beyond it)
            var x = 0.0;
            foreach (var b in bars) { if (!b.InBurst) Assert.Equal(ActorBars.SlotIndex(b.BucketStart, g.Width) - g.First, x, 6); x += b.Cover; }
            Assert.True(x >= slots.Count - 1e-6);
            // an unfolded bar is never much narrower than the minimum (the borrowed room is whole slots)
            var px = 360 * g.Width / span;
            Assert.All(bars.Where(b => b.InBurst), b => Assert.True(b.Cover * px >= 0.85 * 4));
        }
    }
}
