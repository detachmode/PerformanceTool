using PerformanceTool.Client.Components;
using PerformanceTool.Client.Services;

namespace PerformanceTool.Tests;

/// <summary>Unfolding bursts into single-actor bars (brain 0038).</summary>
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

    private static List<ActorBar> Grid(List<ActorRecord> a, double w, out long first, ActorShow show = ActorShow.All)
    {
        first = ActorBars.SlotIndex(9, w);
        return ActorBars.BuildGrid(a, a.Count, show, w, first, ActorBars.SlotIndex(13, w), 9, 13, out _);
    }

    [Fact]
    public void A_burst_becomes_one_bar_per_actor_in_its_place()
    {
        var a = Run(5, 3);
        var slots = Grid(a, 0.1, out var first);
        var bars = ActorBars.Unfold(slots, a, a.Count, ActorShow.All, [], 0.1, first, 20, 11, 60, 240, out var bursts);
        Assert.Equal(slots.Count + 4 + 2, bars.Count);
        Assert.Equal(2, bursts.Count);
        Assert.Equal(new BurstSpan(10.0, 5), bursts[0]);
        var one = bars.Where(b => b.InBurst && b.Slot == 10.0).ToList();
        Assert.Equal(5, one.Count);
        Assert.All(one, b => { Assert.False(b.IsBucket); Assert.Equal(5, b.Burst); });
        Assert.Equal([0, 1, 2, 3, 4], one.Select(b => b.Sequence));
        // the lone actor and the empty slots keep their place and order
        Assert.Equal(slots.Count(b => b.Total == 0), bars.Count(b => b.Total == 0));
        Assert.Equal(1, bars.Count(b => b.Sequence == 5 && !b.InBurst));
        Assert.Equal(1, bars.Count(b => b.Failed > 0));
    }

    [Fact]
    public void Without_room_in_the_budget_nothing_unfolds_and_the_list_is_the_grid()
    {
        var a = Run(5, 3);
        var slots = Grid(a, 0.1, out var first);
        var bars = ActorBars.Unfold(slots, a, a.Count, ActorShow.All, [], 0.1, first, 20, 11, 60, 1, out var bursts);
        Assert.Same(slots, bars);
        Assert.Empty(bursts);
    }

    [Fact]
    public void A_group_with_different_start_times_unfolds_only_at_the_smallest_grid_width()
    {
        var a = new List<ActorRecord> { A(0, 10.0), A(1, 10.004) };
        var slots = Grid(a, 0.1, out var first);
        Assert.Same(slots, ActorBars.Unfold(slots, a, a.Count, ActorShow.All, [], 0.1, first, 20, 11, 60, 240, out _));
        var fine = Grid(a, 0.01, out var f2);
        var bars = ActorBars.Unfold(fine, a, a.Count, ActorShow.All, [], 0.01, f2, 20, 11, 60, 240, out var b2);
        Assert.Single(b2);
        Assert.Equal(2, bars.Count(b => b.InBurst));
    }

    [Fact]
    public void A_huge_burst_is_bounded_and_keeps_the_interesting_actors()
    {
        var a = new List<ActorRecord>();
        for (var i = 0; i < 500; i++) a.Add(A(i, 10.0, i == 400));
        var slots = Grid(a, 0.1, out var first);
        var bars = ActorBars.Unfold(slots, a, a.Count, ActorShow.All, [], 0.1, first, 20, 10, 60, 240, out var bursts);
        var burst = bars.Where(b => b.InBurst).ToList();
        Assert.Equal(60, burst.Count);                       // 59 single bars and the trailing group bar
        Assert.Equal(500, bursts[0].Actors);
        Assert.Contains(burst, b => !b.IsBucket && b.Sequence == 400 && b.Failed == 1);
        var tail = burst[^1];
        Assert.True(tail.IsBucket);
        Assert.Equal(441, tail.Count);
        Assert.Equal(500, burst.Where(b => !b.IsBucket).Sum(b => b.Count) + tail.Count);
        Assert.Equal(slots.Count - 1 + 60, bars.Count);
    }

    [Fact]
    public void The_budget_unfolds_the_bursts_nearest_to_the_view_first()
    {
        var a = Run(30, 30);
        var slots = Grid(a, 0.1, out var first);
        var bars = ActorBars.Unfold(slots, a, a.Count, ActorShow.All, [], 0.1, first, 20, 12.0, 60, 35, out var bursts);
        Assert.Single(bursts);
        Assert.Equal(12.0, bursts[0].Slot);
        Assert.True(bars.Count <= slots.Count + 35);
    }

    [Fact]
    public void Running_actors_of_a_burst_unfold_too_and_the_filter_applies()
    {
        var a = new List<ActorRecord> { A(0, 10.0, true), A(1, 10.0), A(2, 10.0, true) };
        var running = new List<RunningActor> { new(3, 0, 10.0, []) };
        var slots = ActorBars.WithRunning(Grid(a, 0.1, out var first), running, 0.1, 10.5, ActorBars.Slot(first, 0.1));
        var all = ActorBars.Unfold(slots, a, a.Count, ActorShow.All, running, 0.1, first, 10.5, 10, 60, 240, out _);
        Assert.Equal(4, all.Count(b => b.InBurst));
        Assert.Single(all, b => b.IsRunningActor);
        var failedSlots = Grid(a, 0.1, out _, ActorShow.Failed);
        var failed = ActorBars.Unfold(failedSlots, a, a.Count, ActorShow.Failed, running, 0.1, first, 10.5, 10, 60, 240, out _);
        Assert.Equal(2, failed.Count(b => b.InBurst));
    }

    [Fact]
    public void Unfolding_starts_at_a_share_of_the_max_bars_setting()
    {
        Assert.Equal(96, ActorBars.UnfoldMaxSlots(240));
        Assert.True(ActorBars.UnfoldPerBurst(240) < 240 && ActorBars.UnfoldBudget(240) <= 240);
    }
}
