using Beesharp.Client.Components;
using Beesharp.Client.Services;

namespace Beesharp.Tests;

/// <summary>Unfolding bursts into single-bee bars (brain 0038), with the pixel rule, borrowed room and hysteresis of brain 0040.</summary>
public class BurstUnfoldTests
{
    private static readonly ErrorDictionary Errors = new();
    private static BeeRecord A(int seq, double start, bool failed = false) =>
        new(seq, 0, start, 100, 0, failed ? Errors.Intern("boom") : ErrorDictionary.None, Errors);

    // a burst of n bees at 10.0 s, one lone bee at 10.5 s and another burst of m at 12.0 s (the third of it failed)
    private static List<BeeRecord> Run(int n, int m)
    {
        var l = new List<BeeRecord>();
        var s = 0;
        for (var i = 0; i < n; i++) l.Add(A(s++, 10.0));
        l.Add(A(s++, 10.5));
        for (var i = 0; i < m; i++) l.Add(A(s++, 12.0, i == 2));
        return l;
    }

    // bursts of `size` bees every `every` seconds from 0 to `until`
    private static List<BeeRecord> Periodic(int size, double every, double until)
    {
        var l = new List<BeeRecord>();
        var s = 0;
        for (var t = 0.0; t < until; t += every)
            for (var i = 0; i < size; i++) l.Add(A(s++, t));
        return l;
    }

    private static List<BeeBar> Grid(List<BeeRecord> a, double w, out long first, BeeShow show = BeeShow.All, double from = 9, double to = 13)
    {
        first = BeeBars.SlotIndex(from, w);
        return BeeBars.BuildGrid(a, a.Count, show, w, first, BeeBars.SlotIndex(to, w), from, to, out _);
    }

    /// <summary>Plenty of room: 100 px per slot.</summary>
    private static UnfoldRoom Wide => new(100, 100, 4, 48, 900);

    private static List<BeeBar> Unfold(List<BeeBar> slots, List<BeeRecord> a, double w, long first, UnfoldRoom room, out List<BurstSpan> bursts,
        double focus = 11, int perBurst = 60, int budget = 240, BeeShow show = BeeShow.All, IReadOnlyList<RunningBee>? running = null) =>
        BeeBars.Unfold(slots, a, a.Count, show, running ?? [], w, first, 20, focus, perBurst, budget, room, out bursts);

    [Fact]
    public void A_burst_becomes_one_bar_per_bee_in_the_room_it_borrows_and_x_stays_linear_in_time()
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
            if (!b.InBurst && b.Total > 0) Assert.Equal(BeeBars.SlotIndex(b.BucketStart, 0.1) - first, x, 6);
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
        // burst at 10.0: 4 empty slots to the lone bee at 10.5 and more to the left; 35 % of 4 = 1.4 slots each side: 3.8 slots for 5 bars
        var one = bars.Where(b => b.InBurst && b.Slot == 10.0).ToList();
        // 3.8 slots * 4 px is room for 3 bars of the 4 px minimum, not for 5: three even groups (2, 2, 1) instead of five single bars
        Assert.Equal(3, one.Count);
        Assert.Equal([2, 2, 1], one.Select(b => b.Total));
        var wider = Unfold(slots, a, 0.1, first, new UnfoldRoom(8, 8, 4, 48, 900), out _);
        var burst = wider.Where(b => b.InBurst && b.Slot == 10.0).ToList();
        Assert.Equal(5, burst.Count);
        Assert.Equal(3, burst.Sum(b => b.Cover), 6);      // it takes 1 whole slot each side: 3 slots * 8 px / 5 = 4.8 px per bee
    }

    [Fact]
    public void Without_room_nothing_unfolds_and_the_list_is_the_grid()
    {
        var a = Run(5, 3);
        var slots = Grid(a, 0.1, out var first);
        Assert.Same(slots, Unfold(slots, a, 0.1, first, Wide, out var b1, budget: 1));      // not even two bars may be shown
        Assert.Empty(b1);
        Assert.Same(slots, Unfold(slots, a, 0.1, first, new UnfoldRoom(0.3, 0.3, 4), out var b2));
        Assert.Empty(b2);
        Assert.Same(slots, Unfold(slots, a, 0.1, first, default, out _));      // chart width not known yet
    }

    [Fact]
    public void A_group_with_different_start_times_unfolds_only_at_the_smallest_grid_width()
    {
        var a = new List<BeeRecord> { A(0, 10.0), A(1, 10.004) };
        var slots = Grid(a, 0.1, out var first);
        Assert.Same(slots, Unfold(slots, a, 0.1, first, Wide, out _));
        var fine = Grid(a, 0.01, out var f2);
        var bars = Unfold(fine, a, 0.01, f2, Wide, out var b2);
        Assert.Single(b2);
        Assert.Equal(2, bars.Count(b => b.InBurst));
    }

    [Fact]
    public void A_huge_burst_splits_into_even_groups_and_failed_bees_stay_in_their_group()
    {
        var a = new List<BeeRecord>();
        for (var i = 0; i < 500; i++) a.Add(A(i, 10.0, i == 400));
        var slots = Grid(a, 0.1, out var first);
        var bars = Unfold(slots, a, 0.1, first, Wide, out var bursts, focus: 10);
        var burst = bars.Where(b => b.InBurst).ToList();
        Assert.Equal(500, bursts[0].Bees);
        Assert.Equal(10, bursts[0].GroupSize);               // at most 60 bars: 500 / 10 = 50 groups of 10
        Assert.Equal(50, burst.Count);
        Assert.All(burst, b => { Assert.True(b.IsBucket); Assert.True(b.IsBurstGroup); Assert.Equal(10, b.Total); });
        var red = Assert.Single(burst, b => b.Failed > 0);   // the failed bee is not shown singly: it turns its group red
        Assert.Equal((400, 409), (red.GroupLo, red.GroupHi));
        Assert.Equal(500, burst.Sum(b => b.Total));
        Assert.Equal(slots.Count, bars.Sum(b => b.Cover), 6);
    }

    [Theory]
    [InlineData(100, 1, 100)]
    [InlineData(100, 2, 50)]
    [InlineData(100, 3, 50)]
    [InlineData(100, 5, 20)]
    [InlineData(100, 10, 10)]
    [InlineData(100, 19, 10)]
    [InlineData(100, 20, 5)]
    [InlineData(100, 50, 2)]
    [InlineData(100, 99, 2)]
    [InlineData(100, 100, 1)]
    [InlineData(1000, 60, 20)]
    [InlineData(1000, 240, 5)]
    public void Group_sizes_are_nice_and_just_small_enough_for_the_room(int bees, int fit, int expected) =>
        Assert.Equal(expected, BeeBars.NiceGroupSize(bees, fit));

    [Fact]
    public void A_burst_of_100_refines_as_the_room_grows_until_every_bee_has_a_bar()
    {
        var a = new List<BeeRecord>();
        for (var i = 0; i < 100; i++) a.Add(A(i, 10.0, i == 37));
        a.Add(A(100, 8.0)); a.Add(A(101, 12.5));        // quiet time around the burst to borrow
        var slots = Grid(a, 0.1, out var first);
        var sizes = new List<int>();
        foreach (var px in new[] { 0.5, 1.0, 2, 4, 8, 16, 32, 64 })
        {
            var bars = Unfold(slots, a, 0.1, first, new UnfoldRoom(px, px, 4, 48, 3000), out var bursts, perBurst: 240, budget: 240);
            sizes.Add(bursts.Count == 0 ? 100 : bursts[0].GroupSize);
            var burst = bars.Where(b => b.InBurst).ToList();
            if (bursts.Count == 0) continue;
            Assert.True(burst.Count <= 100);
            Assert.Equal(100, burst.Sum(b => b.Total));
            Assert.Single(burst, b => b.Failed > 0);          // always exactly one red bar: the group (or bee) of #38
            var g = bursts[0].GroupSize;
            Assert.All(burst.Take(burst.Count - 1), b => Assert.Equal(g, b.Total));      // even groups
            Assert.Equal(slots.Count, bars.Sum(b => b.Cover), 6);
        }
        // never coarser when there is more room, and it ends with one bar per bee
        for (var i = 1; i < sizes.Count; i++) Assert.True(sizes[i] <= sizes[i - 1], string.Join(",", sizes));
        Assert.Equal(1, sizes[^1]);
        Assert.Contains(10, sizes);
        Assert.Contains(50, sizes);
    }

    [Fact]
    public void Groups_are_slices_by_bee_number_and_hysteresis_keeps_them_while_zooming_out_a_little()
    {
        var a = new List<BeeRecord>();
        for (var i = 0; i < 100; i++) a.Add(A(i, 10.0));
        a.Add(A(100, 8.0)); a.Add(A(101, 12.5));
        var slots = Grid(a, 0.1, out var first);
        var tight = Unfold(slots, a, 0.1, first, new UnfoldRoom(4, 4, 4, 48, 3000), out var b1, perBurst: 240);
        var groups = tight.Where(b => b.InBurst).ToList();
        for (var i = 0; i < groups.Count; i++) Assert.Equal((i * b1[0].GroupSize, (i + 1) * b1[0].GroupSize - 1), (groups[i].GroupLo, groups[i].GroupHi));
        // the real width shrank (zooming out) but the decision width still is the old one: same groups
        Unfold(slots, a, 0.1, first, new UnfoldRoom(3, 4, 4, 48, 3000), out var b2, perBurst: 240);
        Assert.Equal(b1[0].GroupSize, b2[0].GroupSize);
    }

    [Fact]
    public void Running_bees_count_into_their_group_as_running()
    {
        var a = new List<BeeRecord>();
        for (var i = 0; i < 20; i++) a.Add(A(i, 10.0));
        var running = new List<RunningBee>();
        for (var i = 20; i < 30; i++) running.Add(new(i, 0, 10.0, []));
        var slots = BeeBars.WithRunning(Grid(a, 0.1, out var first), running, 0.1, 10.5, BeeBars.Slot(first, 0.1));
        var bars = Unfold(slots, a, 0.1, first, Wide, out var bursts, running: running, focus: 10, perBurst: 10);
        Assert.Single(bursts);
        var burst = bars.Where(b => b.InBurst).ToList();
        Assert.Equal(30, burst.Sum(b => b.Total));
        Assert.Equal(10, burst.Sum(b => b.Running));
    }

    [Fact]
    public void A_burst_of_1000_stays_bounded_at_every_zoom()
    {
        var a = new List<BeeRecord>();
        for (var i = 0; i < 1000; i++) a.Add(A(i, 10.0, i % 97 == 0));
        a.Add(A(1000, 8.0)); a.Add(A(1001, 12.5));
        var slots = Grid(a, 0.1, out var first);
        foreach (var px in new[] { 2.0, 10, 50, 300, 1500, 8000 })
        {
            var bars = Unfold(slots, a, 0.1, first, new UnfoldRoom(px, px, 5.6, 48, 900), out var bursts, perBurst: 240, budget: 240);
            var burst = bars.Where(b => b.InBurst).ToList();
            Assert.True(burst.Count <= 240, $"{px}: {burst.Count}");
            Assert.True(bars.Count <= slots.Count + 240);
            if (bursts.Count > 0) { Assert.Equal(1000, burst.Sum(b => b.Total)); Assert.Equal(11, burst.Sum(b => b.Failed)); }
        }
    }

    [Fact]
    public void The_budget_unfolds_the_bursts_nearest_to_the_view_first()
    {
        var a = Run(30, 30);
        var slots = Grid(a, 0.1, out var first);
        var bars = Unfold(slots, a, 0.1, first, Wide, out var bursts, focus: 12.0, budget: 35);
        Assert.Equal(2, bursts.Count);
        Assert.Equal(1, bursts.Single(b => b.Slot == 12.0).GroupSize);      // the burst nearest to the focus gets single bars
        Assert.True(bursts.Single(b => b.Slot == 10.0).GroupSize > 1);      // the other makes do with what is left: coarser groups
        Assert.True(bars.Count(b => b.InBurst) <= 35);
        Assert.Equal(60, bars.Where(b => b.InBurst).Sum(b => b.Total));
    }

    [Fact]
    public void Running_bees_of_a_burst_unfold_too_and_the_filter_applies()
    {
        var a = new List<BeeRecord> { A(0, 10.0, true), A(1, 10.0), A(2, 10.0, true) };
        var running = new List<RunningBee> { new(3, 0, 10.0, []) };
        var slots = BeeBars.WithRunning(Grid(a, 0.1, out var first), running, 0.1, 10.5, BeeBars.Slot(first, 0.1));
        var all = Unfold(slots, a, 0.1, first, Wide, out _, focus: 10, running: running);
        Assert.Equal(4, all.Count(b => b.InBurst));
        Assert.Single(all, b => b.IsRunningBee);
        var failedSlots = Grid(a, 0.1, out _, BeeShow.Failed);
        var failed = Unfold(failedSlots, a, 0.1, first, Wide, out _, focus: 10, show: BeeShow.Failed, running: running);
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
    private static HashSet<double> UnfoldedInView(List<BeeRecord> a, double viewFrom, double span, double px, int maxBars = 240, double? decide = null, bool singles = true)
    {
        var g = new ChartGrid();
        g.Ensure(viewFrom, viewFrom + span, 1800, maxBars);
        var slots = BeeBars.BuildGrid(a, a.Count, BeeShow.All, g.Width, g.First, g.LastNow(1800), viewFrom, viewFrom + span, out _);
        var room = UnfoldRoom.For(px, span, decide ?? span, g.Width, maxBars);
        BeeBars.Unfold(slots, a, a.Count, BeeShow.All, [], g.Width, g.First, 1800, viewFrom + span / 2,
            BeeBars.UnfoldPerBurst(maxBars), BeeBars.UnfoldBudget(maxBars), room, out var bursts);
        return bursts.Where(b => (!singles || b.GroupSize == 1) && b.Slot >= viewFrom - 1e-9 && b.Slot < viewFrom + span).Select(b => b.Slot).ToHashSet();
    }

    [Fact]
    public void Bursts_unfold_as_soon_as_their_bees_fit_not_only_at_the_smallest_grid()
    {
        // the seeded "Browse categories" entry: 15 bees every 10 s
        var a = Periodic(15, 10, 1800);
        // phone chart (360 px): 15 bars of at least 4 px need 60 px; a burst may borrow 35 % of the 10 s on each side
        Assert.NotEmpty(UnfoldedInView(a, 600, 30, 360));
        Assert.Empty(UnfoldedInView(a, 600, 60, 360));
        // desktop chart (900 px, 5.6 px per bee): already at a one minute view
        Assert.NotEmpty(UnfoldedInView(a, 600, 60, 900));
        Assert.Empty(UnfoldedInView(a, 600, 120, 900));
        // the old rule only unfolded at a view of about one second; and before the single bars fit, the burst already splits into even groups
        Assert.NotEmpty(UnfoldedInView(a, 600, 60, 360, singles: false));
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
            var slots = BeeBars.BuildGrid(a, a.Count, BeeShow.All, g.Width, g.First, g.LastNow(1800), 600, 600 + span, out _);
            var bars = BeeBars.Unfold(slots, a, a.Count, BeeShow.All, [], g.Width, g.First, 1800, 600 + span / 2, 60, 240,
                UnfoldRoom.For(360, span, span, g.Width, 240), out _);
            Assert.True(bars.Count <= slots.Count + 240);
            // x stays linear in time: every slot bar sits at its slot index (a cell at the buffer's end may reach a little beyond it)
            var x = 0.0;
            foreach (var b in bars) { if (!b.InBurst) Assert.Equal(BeeBars.SlotIndex(b.BucketStart, g.Width) - g.First, x, 6); x += b.Cover; }
            Assert.True(x >= slots.Count - 1e-6);
            // an unfolded bar is never much narrower than the minimum (the borrowed room is whole slots)
            var px = 360 * g.Width / span;
            Assert.All(bars.Where(b => b.InBurst), b => Assert.True(b.Cover * px >= 0.85 * 4));
        }
    }
}
