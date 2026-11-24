using Beesharp.Client.Chart;
using Beesharp.Client.Runs;
using Beesharp.Services;


namespace Beesharp.Client.Tests;

/// <summary>The graded slow colour (brain 0041): ratio to the typical time, colour step, compact storage, group bars.</summary>
public class GradedSlowTests
{
    [Fact]
    public void Ratio_is_one_at_or_below_typical_or_without_a_typical()
    {
        Assert.Equal(1, SlowCallRule.Ratio(80, 100));
        Assert.Equal(1, SlowCallRule.Ratio(100, 100));
        Assert.Equal(1, SlowCallRule.Ratio(500, null));
        Assert.Equal(1.4, SlowCallRule.Ratio(140, 100), 6);
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1.0, 0)]     // at typical: pure green
    [InlineData(1.01, 1)]    // anything slower than typical gets a tint
    [InlineData(1.25, 1)]
    [InlineData(1.26, 2)]
    [InlineData(1.5, 2)]
    [InlineData(1.75, 3)]
    [InlineData(1.99, 3)]    // just under the slow line is never full amber
    [InlineData(2.0, 4)]     // at the slow line and beyond: full amber
    [InlineData(9.0, 4)]
    public void Step_rises_from_green_at_typical_to_amber_at_the_slow_line(double ratio, int step) =>
        Assert.Equal(step, SlowCallRule.Step(ratio));

    [Fact]
    public void Slow_calls_are_always_full_amber()
    {
        for (var d = 100.0; d < 400; d += 3.7)
            if (SlowCallRule.IsSlow(d, 100))
                Assert.Equal(SlowCallRule.MaxStep, SlowCallRule.Step(SlowCallRule.Ratio(d, 100)));
    }

    [Fact]
    public void Ratio_fits_one_byte_and_never_rounds_up_over_the_slow_line()
    {
        Assert.Equal((byte)0, SlowCallRule.Encode(1));
        Assert.Equal(1.0, SlowCallRule.Decode(0));
        Assert.Equal(1.4, SlowCallRule.Decode(SlowCallRule.Encode(1.4)), 1);
        Assert.True(SlowCallRule.Decode(SlowCallRule.Encode(1.999)) < SlowCallRule.Factor);
        Assert.Equal((byte)255, SlowCallRule.Encode(100));
    }

    private static BeeRecord Bee(double ratio, int slowCalls = 0, int errorId = ErrorDictionary.None) =>
        new(0, 0, 0, 100, slowCalls, errorId, null, SlowCallRule.Encode(ratio));

    [Fact]
    public void Bee_tint_follows_its_worst_call_and_a_slow_bee_is_always_amber()
    {
        Assert.Equal(0, Bee(1).Tint);
        Assert.Equal(2, Bee(1.4).Tint);
        Assert.Equal(SlowCallRule.MaxStep, Bee(2.6, slowCalls: 1).Tint);
        Assert.Equal("good", BeeOrder.Good(0));
        Assert.Equal("good t2", BeeOrder.Good(2));
        Assert.Equal("warn", BeeOrder.Good(SlowCallRule.MaxStep));
        Assert.Equal("good t1", BeeOrder.Css(BeeState.Ok, 1));
        Assert.Equal("bad", BeeOrder.Css(BeeState.Failed, 3));
    }

    [Fact]
    public void A_group_bar_takes_the_step_of_its_worst_ok_bee_and_ignores_failed_bees()
    {
        var bees = new[]
        {
            Bee(1.0) with { Sequence = 0, StartedAt = 1 },
            Bee(1.6) with { Sequence = 1, StartedAt = 1.2 },
            Bee(1.2) with { Sequence = 2, StartedAt = 1.4 },
            Bee(1.9, errorId: 1) with { Sequence = 3, StartedAt = 1.5 },
        };
        var bar = BeeBars.BuildGrid(bees, bees.Length, BeeShow.All, 10, 0, 0, 0, 10, out _).Single();
        Assert.Equal(1, bar.Failed);
        Assert.Equal(SlowCallRule.Step(1.6), SlowCallRule.Step(bar.Ratio));
        var ok = BeeBars.BuildGrid(bees, 3, BeeShow.All, 10, 0, 0, 0, 10, out _).Single();
        Assert.Equal(SlowCallRule.Step(1.6), ok.Step);
    }

    [Fact]
    public void Single_ok_bar_has_its_own_step_and_ratio()
    {
        var bars = BeeBars.Build([Bee(1.3)], 1, BeeShow.All, out _, out _);
        Assert.Equal(2, bars[0].Step);
        Assert.Equal(1.3, bars[0].Ratio, 1);
    }

    [Fact]
    public void Store_keeps_the_ratio_per_bee()
    {
        var store = new RunStore(1);
        var a = Bee(1.5) with { Sequence = 7 };
        store.Complete(a, new BeeDetail(7, 0, [], () => []));
        Assert.Equal(2, store.TintOf(7));
        Assert.Equal(a.Ratio, store.RatioOf(7), 6);
        Assert.Equal(0, store.TintOf(99));
    }
}
