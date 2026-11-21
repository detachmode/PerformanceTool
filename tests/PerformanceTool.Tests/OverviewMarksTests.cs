using PerformanceTool.Client.Components;
using PerformanceTool.Client.Services;

namespace PerformanceTool.Tests;

/// <summary>The overview strip's marks (brain 0035 update 1).</summary>
public class OverviewMarksTests
{
    [Fact]
    public void Marks_are_one_character_per_bin_red_before_amber()
    {
        var errors = new ErrorDictionary();
        var list = new List<ActorRecord>
        {
            new(0, 0, 5, 100, 0, ErrorDictionary.None, errors),            // ok
            new(1, 0, 25, 100, 2, ErrorDictionary.None, errors),           // slow, bin 2
            new(2, 0, 55, 100, 0, errors.Intern("boom"), errors),          // failed, bin 5
            new(3, 0, 55, 100, 3, ErrorDictionary.None, errors),           // slow in the same bin: red stays
        };
        Assert.Equal("..s..f....", ActorBars.OverviewMarks(list, list.Count, 100, 10));
        Assert.Equal(ActorBars.OverviewBins, ActorBars.OverviewMarks(list, list.Count, 100).Length);
        Assert.Equal("", ActorBars.OverviewMarks(list, list.Count, 0));
    }
}
