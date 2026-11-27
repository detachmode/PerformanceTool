using Beesharp.Client.Calls;
using Beesharp.Client.Runs;

namespace Beesharp.Client.Tests;

/// <summary>The address of the Calls page beyond its filter (brain 0070): the selected call, where the user came from, and the bee side panel.</summary>
public sealed class CallsAddressTests
{
    private static CallsFilter Filter => CallsFilter.None.WithEndpoint("GET /cart").WithEntry(0).WithSort("time");

    [Fact]
    public void A_call_key_names_bee_step_repetition_and_offset_and_is_the_same_from_either_side()
    {
        Assert.Equal("4668.2.0.153120", CallKey.Of(4667, 2, 0, 15312.0));
        // rounded to a tenth of a millisecond, so a double that went through storage still matches
        Assert.Equal(CallKey.Of(4667, 2, 0, 15312.0), CallKey.Of(4667, 2, 0, 15312.0000004));
        var sample = new CallSampleDto(99, 4667, "B4668", 0, 2, 0, 12, 15312.0, 40, 200, 0, null, "GET /cart", null, "GET /cart", false, null, null);
        var inBee = new FakeCall(3, "GET /cart", 15312.0, 40, 200, 0, false, 0, Step: 2, Rep: 0);
        Assert.Equal(CallKey.Of(sample), CallKey.Of(4667, inBee));
    }

    [Theory]
    [InlineData("4668.2.0.153120", true)]
    [InlineData("1.0.0.0", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("4668", false)]
    [InlineData("4668.2.0", false)]
    [InlineData("a.b.c.d", false)]
    [InlineData("1.2.3.-4", false)]
    public void Only_a_well_formed_call_key_is_taken_from_the_address(string? key, bool valid) =>
        Assert.Equal(valid, CallKey.IsValid(key));

    [Fact]
    public void The_selected_call_follows_the_filter_in_the_address()
    {
        var href = CallsAddress.Href(Filter, new CallsView("4668.2.0.153120"));
        Assert.Equal("calls?endpoint=GET%20%2Fcart&scenario=1&sort=time&call=4668.2.0.153120", href);
        Assert.Equal("calls", CallsAddress.Href(CallsFilter.None, CallsView.None));
        Assert.Equal("calls?call=1.0.0.5", CallsAddress.Href(CallsFilter.None, new CallsView("1.0.0.5")));
    }

    [Fact]
    public void The_sort_order_is_part_of_the_address_unless_it_is_the_default()
    {
        Assert.DoesNotContain("sort", CallsAddress.Href(CallsFilter.None.WithEndpoint("GET /a"), CallsView.None));
        Assert.Contains("sort=failed", CallsAddress.Href(CallsFilter.None.WithEndpoint("GET /a").WithSort("failed"), CallsView.None));
    }

    [Fact]
    public void The_view_survives_a_round_trip_through_the_address_parts()
    {
        var view = new CallsView("4668.2.0.153120", new CallsOrigin(0, 4667), 4667, BeeTab.Calls);
        var href = CallsAddress.Href(Filter, view);
        Assert.EndsWith("&call=4668.2.0.153120&origin=scenario:1:4668&open=4668&beeTab=calls", href);
        Assert.Equal(view, CallsAddress.Parse("4668.2.0.153120", "scenario:1:4668", "4668", "calls"));
    }

    [Fact]
    public void The_bee_tab_is_only_written_with_an_open_bee_and_steps_is_the_default()
    {
        Assert.Equal("calls?open=7", CallsAddress.Href(CallsFilter.None, new CallsView(OpenBee: 6)));
        Assert.Equal("calls?open=7&beeTab=logs", CallsAddress.Href(CallsFilter.None, new CallsView(OpenBee: 6, Tab: BeeTab.Logs)));
        Assert.Equal("calls", CallsAddress.Href(CallsFilter.None, new CallsView(Tab: BeeTab.Logs)));
        Assert.Equal(BeeTab.Steps, CallsAddress.Parse(null, null, null, "calls").Tab);
    }

    [Theory]
    [InlineData("scenario:1:4668", 0, 4667)]
    [InlineData("scenario:3:1", 2, 0)]
    public void An_origin_names_the_scenario_and_the_bee_as_the_UI_numbers_them(string param, int entry, int bee)
    {
        var o = CallsOrigin.Parse(param);
        Assert.Equal(new CallsOrigin(entry, bee), o);
        Assert.Equal(param, o!.Value.ToParam());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("scenario:0:5")]
    [InlineData("scenario:1:0")]
    [InlineData("scenario:1")]
    [InlineData("logs:1:5")]
    [InlineData("scenario:x:y")]
    public void A_malformed_origin_means_none(string? param) => Assert.Null(CallsOrigin.Parse(param));

    [Fact]
    public void The_way_back_without_history_is_the_scenario_page_on_that_bees_calls_tab() =>
        Assert.Equal("scenario/1?bee=4668&beeTab=calls", new CallsOrigin(0, 4667).ScenarioHref());

    [Fact]
    public void Malformed_view_parts_mean_not_there()
    {
        var v = CallsAddress.Parse("oops", null, "zero", null);
        Assert.Null(v.Call);
        Assert.Null(v.OpenBee);
        Assert.False(v.PanelOpen);
    }

    [Fact]
    public void An_endpoint_of_a_bees_call_is_its_own_endpoint_or_the_normalized_call_name()
    {
        var named = new FakeCall(0, "GET /a/1", 0, 5, 200, 0, false, 0, Endpoint: "GET /a/{id}");
        var plain = new FakeCall(1, "GET /b", 5, 5, 200, 0, false, 0);
        Assert.Equal("GET /a/{id}", CallKey.EndpointOf(named));
        Assert.False(string.IsNullOrWhiteSpace(CallKey.EndpointOf(plain)));
    }

    [Fact]
    public void Opening_a_call_of_a_bee_leads_to_the_calls_page_with_the_call_and_the_origin()
    {
        var nav = new StubNavigationManager();
        var links = new CallsLinks(nav);
        var origin = new CallsOrigin(1, 4667);
        links.ShowCall(Filter, "4668.2.0.153120", origin);
        Assert.Equal("calls?endpoint=GET%20%2Fcart&scenario=1&sort=time&call=4668.2.0.153120&origin=scenario:2:4668", Assert.Single(nav.NavigatedTo));
        Assert.Equal("4668.2.0.153120", links.LastCall);
        Assert.Equal(origin, links.TakeArrival());
        Assert.Null(links.TakeArrival());   // read once: a reload no longer knows where history goes
    }
}
