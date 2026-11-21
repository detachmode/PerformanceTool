using PerformanceTool.Client.Components;
using PerformanceTool.Client.Services;

namespace PerformanceTool.Tests;

/// <summary>The stable time grid of the zoomed popup chart (brain 0031 update 2).</summary>
public class ActorGridTests
{
    private static List<ActorRecord> Actors(int n, double step)
    {
        var errors = new ErrorDictionary();
        var list = new List<ActorRecord>();
        for (var i = 0; i < n; i++)
            list.Add(new ActorRecord(i, 0, i * step, 100 + i % 7, i % 11 == 0 ? 1 : 0, i % 13 == 0 ? errors.Intern("boom") : ErrorDictionary.None, errors));
        return list;
    }

    [Fact]
    public void Bucket_keeps_its_actors_when_the_view_shifts()
    {
        var actors = Actors(5000, 0.37);
        var w = ActorBars.GridWidth(60, 240);
        var a = ActorBars.BuildGrid(actors, actors.Count, ActorShow.All, w, ActorBars.SlotIndex(100, w), ActorBars.SlotIndex(220, w), 120, 180, out _);
        var b = ActorBars.BuildGrid(actors, actors.Count, ActorShow.All, w, ActorBars.SlotIndex(130, w), ActorBars.SlotIndex(250, w), 150, 210, out _);
        var offset = (int)(ActorBars.SlotIndex(130, w) - ActorBars.SlotIndex(100, w));
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
        var actors = Actors(300, 0.5);
        var w = 0.2;
        var bars = ActorBars.BuildGrid(actors, actors.Count, ActorShow.All, w, 10, 60, 2, 12, out var total);
        Assert.Equal(51, bars.Count);
        Assert.Equal(20, total);
        Assert.Equal(ActorBars.Slot(10, w), bars[0].BucketStart);
        Assert.Equal(ActorBars.BucketOf(0.6, w), ActorBars.Slot(3, w));
    }

    [Fact]
    public void Width_fits_the_visible_bars()
    {
        Assert.True(60 / ActorBars.GridWidth(60, 240) < 240);
        Assert.Equal(0.01, ActorBars.GridWidth(0.5, 240));
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
        Assert.True(g.LastNow(1100) >= ActorBars.SlotIndex(1100, g.Width));
    }
}
