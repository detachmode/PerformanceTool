using Beesharp.Client.Components;
using Beesharp.Client.Components.Assistant;
using Beesharp.Client.Services;
using Beesharp.Services;

namespace Beesharp.Tests;

/// <summary>The assistant's bee timeline (brain 0049) feeds the popup chart with a hand-picked set of bees shaped like one scenario entry.</summary>
public class BeeSetTests
{
    private static readonly AssistantBeeDto[] Bees =
    [
        new(40, 1, 30.0, 300, 500, 0, "HTTP 500 on /pay", 0),
        new(7, 0, 4.0, 2400, 200, 10, null, 2),
        new(12, 0, 9.5, 120, 200, 10, null, 0),
    ];

    [Fact]
    public void The_set_is_one_entry_with_the_bees_in_time_order_and_their_outcomes()
    {
        var set = BeeSet.Build("Slow search and failed pay", "run-1", DateTimeOffset.UtcNow, Bees);
        var s = set.Snapshot;

        Assert.Equal(1, s.Count);
        Assert.Equal("Slow search and failed pay", s.Labels[0]);
        Assert.Equal(FakeRunState.Finished, s.State);
        Assert.Equal(new ScenarioView(3, 1, 1, 2, 0, 3), s.Scenarios[0]);
        var bees = s.Store.Bees(0);
        Assert.Equal([7, 12, 40], bees.Select(b => b.Sequence));
        Assert.Equal([BeeOutcome.Slow, BeeOutcome.Ok, BeeOutcome.Failed], bees.Select(b => b.Outcome));
        Assert.All(bees, b => Assert.Equal(0, b.Entry));
        Assert.Equal("HTTP 500 on /pay", bees[2].Error);
        Assert.True(s.Elapsed >= 30.3); // the axis reaches the end of the last bee
    }

    [Fact]
    public void Real_entries_are_kept_and_every_bee_has_a_placeholder_until_its_detail_arrives()
    {
        var set = BeeSet.Build("t", "run-1", DateTimeOffset.UtcNow, Bees);
        Assert.Equal(1, set.Entries[40]);
        Assert.Equal(0, set.Entries[7]);
        var placeholder = set.Snapshot.Store.Detail(7);
        Assert.NotNull(placeholder);
        Assert.Single(placeholder!.Calls);

        set.ApplyDetail(new ServerBeeDetailDto("run-1", 7, 0, 4.0, 2400, 200, 10, null,
            [new RecordedCall("GET /a", 0, 1200, 200)], []));
        Assert.Equal("GET /a", set.Snapshot.Store.Detail(7)!.Calls[0].Name);
    }

    [Fact]
    public void The_popup_chart_draws_the_set_with_the_same_bars_as_a_scenario()
    {
        var set = BeeSet.Build("t", "run-1", DateTimeOffset.UtcNow, Bees);
        var bars = BeeBars.Build(set.Snapshot.Store.Bees(0), 3, BeeShow.All, out var total, out var width, maxBars: 60);
        Assert.Equal(3, total);
        Assert.Equal(0, width); // one bar per bee
        Assert.Equal([7, 12, 40], bars.Select(b => b.Sequence));
        Assert.Equal(1, bars[2].Failed);
        Assert.Equal(1, bars[0].Slow);
    }
}
