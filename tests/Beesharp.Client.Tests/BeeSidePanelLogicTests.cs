using Beesharp.Client.Data;
using Beesharp.Client.Logs;
using Beesharp.Client.Overview;
using Beesharp.Client.Runs;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

/// <summary>The addresses behind "drill down and come back" (brain 0069): the bee column's tab, the Logs page's bee side panel, the Overview's edit state, and finding a bee by its number.</summary>
public sealed class BeeSidePanelLogicTests
{
    // ----- the bee column's tab on the scenario page: beeTab=logs|calls, Steps is the default and not written -----

    [Fact]
    public void The_bee_tab_is_written_as_beeTab_and_steps_is_left_out()
    {
        Assert.Equal("scenario/2?bee=8", ScenarioLink.Href(1, new(Bee: 7)));
        Assert.Equal("scenario/2?bee=8&beeTab=logs", ScenarioLink.Href(1, new(Bee: 7), BeeTab.Logs));
        Assert.Equal("scenario/2?show=failed&bee=8&beeTab=calls", ScenarioLink.Href(1, new(DetailFilter.Failed, Bee: 7), BeeTab.Calls));
    }

    [Theory]
    [InlineData("?beeTab=logs", BeeTab.Logs)]
    [InlineData("?beeTab=CALLS", BeeTab.Calls)]
    [InlineData("http://localhost/scenario/3?show=failed&beeTab=calls&bee=2", BeeTab.Calls)]
    [InlineData("?beeTab=steps", BeeTab.Steps)]
    [InlineData("?beeTab=nonsense", BeeTab.Steps)]
    [InlineData("?show=failed", BeeTab.Steps)]
    [InlineData("", BeeTab.Steps)]
    [InlineData(null, BeeTab.Steps)]
    public void The_bee_tab_is_read_back_and_unknown_values_mean_steps(string? query, BeeTab expected) =>
        Assert.Equal(expected, ScenarioLink.ParseBeeTab(query));

    [Fact]
    public void Changing_the_bee_tab_keeps_the_filter_the_phone_view_and_other_parts()
    {
        Assert.Equal("scenario/3?show=failed&bee=5&tab=bees&beeTab=calls",
            ScenarioLink.WithBeeTab("http://localhost/scenario/3?show=failed&bee=5&tab=bees", BeeTab.Calls));
        Assert.Equal("scenario/3?bee=5&beeTab=logs", ScenarioLink.WithBeeTab("scenario/3?bee=5&beeTab=calls", BeeTab.Logs));
        Assert.Equal("scenario/3?bee=5", ScenarioLink.WithBeeTab("scenario/3?bee=5&beeTab=calls", BeeTab.Steps));
        Assert.Equal("scenario/3", ScenarioLink.WithBeeTab("scenario/3?beeTab=logs", BeeTab.Steps));
    }

    [Fact]
    public void A_filter_or_phone_view_change_keeps_the_bee_tab()
    {
        Assert.Equal("scenario/3?show=slow&beeTab=calls", ScenarioLink.WithFilter("scenario/3?show=failed&beeTab=calls", new(DetailFilter.Slow)));
        Assert.Equal("scenario/3?bee=5&beeTab=calls&tab=chart", ScenarioLink.WithView("scenario/3?bee=5&beeTab=calls", new PhoneView(PhoneTab.Chart)));
        Assert.Equal(new ScenarioFilter(Bee: 4), ScenarioLink.Parse("?bee=5&beeTab=calls"));
    }

    // ----- the Logs page's bee side panel: open=<bee> is its own parameter, never the bee= filter -----

    [Fact]
    public void An_open_bee_is_its_own_address_parameter_after_the_filter()
    {
        var filter = LogFilter.None.ToggleLevel("Error");
        Assert.Equal("logs", LogPanelLink.Href(LogFilter.None, LogPanel.None));
        Assert.Equal("logs?level=error", LogPanelLink.Href(filter, LogPanel.None));
        Assert.Equal("logs?level=error&open=4158", LogPanelLink.Href(filter, new LogPanel(4157)));
        Assert.Equal("logs?level=error&open=4158&beeTab=calls", LogPanelLink.Href(filter, new LogPanel(4157, BeeTab.Calls)));
        Assert.Equal("logs?open=3&beeTab=logs", LogPanelLink.Href(LogFilter.None, new LogPanel(2, BeeTab.Logs)));
    }

    [Fact]
    public void The_panel_is_read_back_and_does_not_touch_the_filter()
    {
        Assert.Equal(new LogPanel(4157, BeeTab.Calls), LogPanelLink.Parse("4158", "calls"));
        Assert.Equal(new LogPanel(0, BeeTab.Steps), LogPanelLink.Parse("1", null));
        Assert.False(LogPanelLink.Parse(null, "calls").IsOpen);
        Assert.False(LogPanelLink.Parse("0", null).IsOpen);
        Assert.False(LogPanelLink.Parse("abc", null).IsOpen);
        Assert.False(LogPanelLink.Parse("-4", null).IsOpen);
        // opening a bee leaves the filter exactly as it was: no bee in it
        var filter = LogFilter.FromQuery(null, null, null, "error", null, null);
        Assert.Null(filter.Bee);
        Assert.True(filter.SameAs(LogFilter.FromQuery(null, null, null, "error", null, null)));
    }

    [Fact]
    public void Filtering_by_a_bee_still_works_through_bee_and_the_search()
    {
        Assert.Equal("logs?bee=199", LogFilter.None.WithBee(198).Href());
        Assert.Equal("logs?bee=199&open=7", LogPanelLink.Href(LogFilter.None.WithBee(198), new LogPanel(6)));
        Assert.Equal(198, LogFilter.None.WithSearch("B199").EffectiveBee);
    }

    // ----- the Overview's edit mode in the address: /?edit=1&entry=3 -----

    [Fact]
    public void Edit_mode_and_the_open_entry_are_written_with_numbers_from_one()
    {
        Assert.Equal("", OverviewLink.Href(OverviewView.Viewing));
        Assert.Equal("", OverviewLink.Href(new OverviewView(false, 2)));
        Assert.Equal("?edit=1", OverviewLink.Href(new OverviewView(true)));
        Assert.Equal("?edit=1&entry=3", OverviewLink.Href(new OverviewView(true, 2)));
    }

    [Fact]
    public void An_address_tells_edit_mode_and_the_entry_or_says_nothing()
    {
        Assert.Equal(new OverviewView(true, 2), OverviewLink.Parse("http://localhost/?edit=1&entry=3"));
        Assert.Equal(new OverviewView(true, null), OverviewLink.Parse("http://localhost/?edit=1"));
        Assert.Equal(new OverviewView(true, null), OverviewLink.Parse("?edit=1&entry=0"));
        Assert.Equal(new OverviewView(true, null), OverviewLink.Parse("?edit=1&entry=x"));
        Assert.Equal(new OverviewView(true, 0), OverviewLink.Parse("?entry=1&edit=abc123"));   // the assistant's older ?edit=<anything> links
        Assert.Equal(OverviewView.Viewing, OverviewLink.Parse("?edit=0"));
        Assert.Equal(OverviewView.Viewing, OverviewLink.Parse("?edit=false"));
        Assert.Null(OverviewLink.Parse("http://localhost/"));
        Assert.Null(OverviewLink.Parse("http://localhost/?other=1"));
        Assert.Null(OverviewLink.Parse(null));
    }

    // ----- finding a bee by its number (the side panel only knows B7 from the address) -----

    [Fact]
    public void A_bee_is_found_by_its_number_with_its_scenario_entry()
    {
        using var source = new FakeRunSource(new ScenarioCatalog([new HttpGetScenario(), new HttpPostJsonScenario(), new CustomRequestScenario(), new UserJourneyScenario()]), simulateSeconds: 30);
        var s = source.Current;
        var entry = Enumerable.Range(0, s.Count).First(e => s.Scenarios[e].Finished > 0 && s.Store.Bees(e).Count > 0);
        var bee = s.Store.Bees(entry)[0];

        var found = BeeLookup.Find(s, bee.Sequence);
        Assert.NotNull(found);
        Assert.Equal(entry, found.Value.Entry);
        Assert.Equal(bee.Sequence, found.Value.Finished!.Value.Sequence);
        Assert.Null(found.Value.Running);

        Assert.Null(BeeLookup.Find(s, -1));
        Assert.Null(BeeLookup.Find(s, int.MaxValue));
    }
}
