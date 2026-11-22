using Beesharp.Client.Runs;

namespace Beesharp.Client.Tests;

/// <summary>The popup's boxes describe the whole run, independent of the zoom (brain 0056).</summary>
public class BeeStatsWholeRunTests
{
    private static List<BeeRecord> Run(ErrorDictionary e)
    {
        var l = new List<BeeRecord>();
        for (var i = 0; i < 100; i++)
            l.Add(new(i, 0, i, 100 + i, i % 10 == 0 ? 1 : 0, i % 4 == 0 ? e.Intern("boom") : ErrorDictionary.None, e));
        return l;
    }

    [Fact]
    public void Whole_run_stats_ignore_any_zoom_range()
    {
        var list = Run(new ErrorDictionary());
        var whole = BeeStats.Compute(list, list.Count);
        Assert.Equal(100, whole.Count);
        Assert.Equal(25, whole.Failed);
        Assert.Equal(25.0, whole.ErrorRate);
        Assert.Equal(149, whole.Median);               // lower median of 100..199
        Assert.Equal(whole, BeeStats.Compute(list, list.Count, double.NegativeInfinity, double.PositiveInfinity));
    }

    [Fact]
    public void Zoomed_stats_differ_from_whole_run_so_the_boxes_must_not_use_them()
    {
        var list = Run(new ErrorDictionary());
        var zoomed = BeeStats.Compute(list, list.Count, 1, 4);   // bees 1..3: no failures
        Assert.Equal(0, zoomed.ErrorRate);
        Assert.NotEqual(BeeStats.Compute(list, list.Count).ErrorRate, zoomed.ErrorRate);
    }

    [Fact]
    public void Median_index_points_at_the_whole_run_median_bee_outside_a_zoom()
    {
        var list = Run(new ErrorDictionary());
        var whole = BeeStats.Compute(list, list.Count);
        Assert.Equal(49, whole.MedianIndex);
        Assert.True(list[whole.MedianIndex].StartedAt is < 1 or >= 4);
    }
}
