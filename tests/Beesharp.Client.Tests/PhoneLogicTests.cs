using Beesharp.Client.Runs;
using Beesharp.Client.Shared;

namespace Beesharp.Client.Tests;

/// <summary>The phone layouts' rules that are not markup (brain 0059): the tab and the sheet in the address, where a bottom sheet settles.</summary>
public class PhoneLogicTests
{
    [Fact]
    public void The_phone_view_is_part_of_the_address()
    {
        Assert.Equal("scenario/2", ScenarioLink.WithView("http://localhost/scenario/2", PhoneView.Default));
        Assert.Equal("scenario/2?tab=bees", ScenarioLink.WithView("http://localhost/scenario/2", new(PhoneTab.Bees)));
        Assert.Equal("scenario/2?tab=chart&sheet=1", ScenarioLink.WithView("scenario/2", new(PhoneTab.Chart, true)));
    }

    [Fact]
    public void Changing_the_view_keeps_the_filter_and_other_parts_and_the_other_way_round()
    {
        Assert.Equal("scenario/2?show=failed&bee=7&stepcount=1,8&tab=bees", ScenarioLink.WithView("scenario/2?show=failed&bee=7&stepcount=1,8", new(PhoneTab.Bees)));
        Assert.Equal("scenario/2?show=slow&tab=bees&sheet=1", ScenarioLink.WithFilter("scenario/2?tab=bees&sheet=1&show=failed", new(DetailFilter.Slow)));
        Assert.Equal("scenario/2?bee=3", ScenarioLink.WithView("scenario/2?bee=3&tab=bees&sheet=1", PhoneView.Default));
    }

    [Theory]
    [InlineData("scenario/2", PhoneTab.Steps, false)]
    [InlineData("scenario/2?tab=chart", PhoneTab.Chart, false)]
    [InlineData("scenario/2?show=failed&tab=BEES&sheet=1", PhoneTab.Bees, true)]
    [InlineData("scenario/2?tab=nonsense&sheet=no", PhoneTab.Steps, false)]
    public void The_phone_view_is_read_back_from_the_address(string uri, PhoneTab tab, bool sheet)
        => Assert.Equal(new PhoneView(tab, sheet), ScenarioLink.ParseView(uri));

    [Fact]
    public void A_sheet_settles_at_the_nearest_stop()
    {
        const double half = 480, full = 800;
        Assert.Equal(SheetStop.Half, SheetSnap.Decide(430, 0, half, full, true));          // a short drag falls back
        Assert.Equal(SheetStop.Full, SheetSnap.Decide(700, 0, half, full, true));          // dragged most of the way up
        Assert.Equal(SheetStop.Closed, SheetSnap.Decide(200, 0, half, full, true));        // dragged down far enough
    }

    [Fact]
    public void A_flick_carries_the_sheet_on()
    {
        const double half = 480, full = 800;
        Assert.Equal(SheetStop.Full, SheetSnap.Decide(500, 1.5, half, full, true));
        Assert.Equal(SheetStop.Closed, SheetSnap.Decide(480, -2.5, half, full, true));
        Assert.Equal(SheetStop.Half, SheetSnap.Decide(800, -1.5, half, full, true));        // a gentle swipe from full goes to half first
    }

    [Fact]
    public void A_sheet_that_cannot_grow_never_goes_full()
        => Assert.Equal(SheetStop.Half, SheetSnap.Decide(500, 3, 480, 800, false));
}
