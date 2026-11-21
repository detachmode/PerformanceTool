using PerformanceTool.Client.Components;

namespace PerformanceTool.Tests;

/// <summary>Stripes of a highlighted group bar (brain 0037).</summary>
public class GroupStripeTests
{
    private static BeeBar Group(int count, int failed, int slow, int running = 0) => new(count, failed, slow, 1000, -1, 0, running);

    [Fact]
    public void Order_is_failed_slow_ok_running_and_zero_counts_are_left_out()
    {
        var s = BeeBars.Stripes(Group(10, 2, 3, 1));
        Assert.Equal(["bad", "warn", "good", "run"], s.Select(x => x.Kind));
        Assert.Equal([2, 3, 5, 1], s.Select(x => x.Count));
        Assert.Equal(["good"], BeeBars.Stripes(Group(8, 0, 0)).Select(x => x.Kind));
        Assert.Equal(["bad", "good"], BeeBars.Stripes(Group(8, 1, 0)).Select(x => x.Kind));
        Assert.Empty(BeeBars.Stripes(Group(0, 0, 0)));
    }

    [Fact]
    public void Shares_are_proportional_and_add_up_to_100()
    {
        var s = BeeBars.Stripes(Group(10, 2, 3, 0), minShare: 0);
        Assert.Equal(100, s.Sum(x => x.Share), 6);
        Assert.Equal(20, s[0].Share, 6);
        Assert.Equal(30, s[1].Share, 6);
        Assert.Equal(50, s[2].Share, 6);
    }

    [Fact]
    public void A_single_failed_bee_in_a_big_group_keeps_a_visible_share()
    {
        var s = BeeBars.Stripes(Group(500, 1, 2));
        Assert.Equal(100, s.Sum(x => x.Share), 6);
        Assert.Equal(4, s[0].Share, 6);         // minimum
        Assert.Equal(4, s[1].Share, 6);
        Assert.Equal(92, s[2].Share, 6);        // the ok bees get the rest
    }

    [Fact]
    public void Attribute_is_compact_and_culture_invariant()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("bad:12.5 good:87.5", BeeBars.StripeData(Group(8, 1, 0)));
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }
}
