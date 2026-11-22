using Beesharp.Client.Components;
using Beesharp.Client.Services;

namespace Beesharp.Tests;

/// <summary>The stable time grid of the zoomed popup chart (brain 0031 update 2).</summary>
public class BeeGridTests
{
    private static List<BeeRecord> Bees(int n, double step)
    {
        var errors = new ErrorDictionary();
        var list = new List<BeeRecord>();
        for (var i = 0; i < n; i++)
            list.Add(new BeeRecord(i, 0, i * step, 100 + i % 7, i % 11 == 0 ? 1 : 0, i % 13 == 0 ? errors.Intern("boom") : ErrorDictionary.None, errors));
        return list;
    }

    [Fact]
    public void Bucket_keeps_its_bees_when_the_view_shifts()
    {
        var bees = Bees(5000, 0.37);
        var w = BeeBars.GridWidth(60, 240);
        var a = BeeBars.BuildGrid(bees, bees.Count, BeeShow.All, w, BeeBars.SlotIndex(100, w), BeeBars.SlotIndex(220, w), 120, 180, out _);
        var b = BeeBars.BuildGrid(bees, bees.Count, BeeShow.All, w, BeeBars.SlotIndex(130, w), BeeBars.SlotIndex(250, w), 150, 210, out _);
        var offset = (int)(BeeBars.SlotIndex(130, w) - BeeBars.SlotIndex(100, w));
        for (var k = 0; k < 80; k++)
        {
            var x = a[offset + k]; var y = b[k];
            Assert.Equal((x.Count, x.Failed, x.Slow, x.Sequence), (y.Count, y.Failed, y.Slow, y.Sequence));
            Assert.Equal(x.BucketStart, y.BucketStart);
        }
    }

    [Fact]
    public void Slots_are_contiguous_on_a_multiple_of_the_width()
    {
        var bees = Bees(300, 0.5);
        var w = 0.2;
        var bars = BeeBars.BuildGrid(bees, bees.Count, BeeShow.All, w, 10, 60, 2, 12, out var total);
        Assert.Equal(51, bars.Count);
        Assert.Equal(20, total);
        Assert.Equal(BeeBars.Slot(10, w), bars[0].BucketStart);
        Assert.Equal(BeeBars.BucketOf(0.6, w), BeeBars.Slot(3, w));
    }

    [Fact]
    public void Width_fits_the_visible_bars()
    {
        Assert.True(60 / BeeBars.GridWidth(60, 240) < 240);
        Assert.Equal(0.01, BeeBars.GridWidth(0.5, 240));
    }

    [Fact]
    public void Buffer_is_sticky_until_the_view_nears_its_edge()
    {
        var g = new ChartGrid();
        g.Ensure(1000, 1060, 5000, 240);
        var first = g.First; var w = g.Width;
        Assert.True(g.From <= 940 && g.To >= 1120);
        g.Ensure(1010, 1070, 5000, 240);
        g.Ensure(1020, 1080, 5000, 240);
        Assert.Equal((first, w), (g.First, g.Width));       // panning inside: untouched
        g.Ensure(1090, 1150, 5000, 240);
        Assert.NotEqual(first, g.First);                    // near the edge: rebuilt
    }

    [Fact]
    public void Buffer_at_the_live_end_is_open()
    {
        var g = new ChartGrid();
        g.Ensure(940, 1000, 1000, 240);
        Assert.True(g.Open);
        Assert.True(g.LastNow(1100) >= BeeBars.SlotIndex(1100, g.Width));
    }
}
