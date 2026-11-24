using Beesharp.Client.Runs;

namespace Beesharp.Client.Tests;

/// <summary>The logic behind the scenario page (brain 0057): which bee is next, the address of a filter, the line after a bee.</summary>
public sealed class ScenarioPageLogicTests
{
    [Fact]
    public void Next_and_previous_walk_the_list_and_stop_at_its_ends()
    {
        int[] list = [7, 12, 40];
        Assert.Equal(12, BeeNavigator.Neighbour(list, 7, +1));
        Assert.Equal(40, BeeNavigator.Neighbour(list, 12, +1));
        Assert.Null(BeeNavigator.Neighbour(list, 40, +1));
        Assert.Equal(7, BeeNavigator.Neighbour(list, 12, -1));
        Assert.Null(BeeNavigator.Neighbour(list, 7, -1));
    }

    [Fact]
    public void A_bee_that_is_not_in_the_list_goes_to_the_first_for_next_and_the_last_for_previous()
    {
        int[] list = [7, 12, 40];
        Assert.Equal(7, BeeNavigator.Neighbour(list, 99, +1));
        Assert.Equal(40, BeeNavigator.Neighbour(list, 99, -1));
        Assert.Equal(7, BeeNavigator.Neighbour(list, null, +1));
        Assert.Null(BeeNavigator.Neighbour([], 5, +1));
    }

    [Fact]
    public void A_filter_becomes_an_address_with_the_numbers_the_user_reads()
    {
        Assert.Equal("scenario/1", ScenarioLink.Href(0));
        Assert.Equal("scenario/3?show=failed", ScenarioLink.Href(2, new(DetailFilter.Failed)));
        Assert.Equal("scenario/2?show=slowest", ScenarioLink.Href(1, new(DetailFilter.Slowest5Pct)));
        Assert.Equal("scenario/2?failedStep=3", ScenarioLink.Href(1, new(FailedStep: 2)));
        Assert.Equal("scenario/2?nowStep=1&bee=8", ScenarioLink.Href(1, new(NowStep: 0, Bee: 7)));
    }

    [Theory]
    [InlineData("?show=failed", DetailFilter.Failed, null, null, null)]
    [InlineData("show=slow", DetailFilter.Slow, null, null, null)]
    [InlineData("?show=slowest&bee=8", DetailFilter.Slowest5Pct, null, null, 7)]
    [InlineData("?failedStep=3", DetailFilter.All, 2, null, null)]
    [InlineData("?nowStep=2", DetailFilter.All, null, 1, null)]
    [InlineData("?failedStep=2&nowStep=3", DetailFilter.All, 1, null, null)]
    [InlineData("scenario/4?SHOW=Failed", DetailFilter.Failed, null, null, null)]
    [InlineData("?show=nonsense&failedStep=0&bee=x&other=1", DetailFilter.All, null, null, null)]
    [InlineData("", DetailFilter.All, null, null, null)]
    public void An_address_becomes_the_filter_again(string query, DetailFilter show, int? failed, int? now, int? bee)
    {
        Assert.Equal(new ScenarioFilter(show, failed, now, bee), ScenarioLink.Parse(query));
    }

    [Fact]
    public void Building_an_address_and_reading_it_back_gives_the_same_filter()
    {
        foreach (var f in new[] { new ScenarioFilter(), new(DetailFilter.Slow), new(FailedStep: 4, Bee: 11), new(NowStep: 0), new(DetailFilter.Failed, Bee: 0) })
            Assert.Equal(f, ScenarioLink.Parse(ScenarioLink.Href(3, f)));
    }

    [Fact]
    public void Changing_the_filter_of_an_address_keeps_the_other_query_parts()
    {
        Assert.Equal("scenario/3?show=failed", ScenarioLink.WithFilter("http://localhost:5321/scenario/3?show=slow&bee=4", new(DetailFilter.Failed)));
        Assert.Equal("scenario/3?bee=5&stepcount=1,8,24", ScenarioLink.WithFilter("http://localhost/scenario/3?stepcount=1,8,24&failedStep=2", new(Bee: 4)));
        Assert.Equal("scenario/3?stepcount=1,8", ScenarioLink.WithFilter("scenario/3?show=failed&stepcount=1,8", ScenarioFilter.Default));
    }

    [Theory]
    [InlineData("/scenario/2", 1)]
    [InlineData("scenario/1?show=failed", 0)]
    [InlineData("http://localhost:5321/scenario/12/", 11)]
    [InlineData("/scenario/0", null)]
    [InlineData("/scenario/x", null)]
    [InlineData("/editor", null)]
    public void The_scenario_number_is_read_from_the_path(string path, int? index) => Assert.Equal(index, ScenarioLink.IndexOf(path));

    [Fact]
    public void The_line_after_a_failed_bee_is_its_error_and_the_step_it_failed_at()
    {
        var errors = new ErrorDictionary();
        var steps = new ScenarioSteps(["Log in", "Search", "Pay"]);
        var failed = new BeeRecord(5, 0, 12, 3100, 0, errors.Intern("HTTP 422 Payment declined\nat Pay.cs:12"), errors, FailedStep: 2);
        Assert.Equal("HTTP 422 Payment declined · at Pay", BeeLine.Of(failed, null, steps));
        Assert.Equal("HTTP 422 Payment declined", BeeLine.Of(failed, null, ScenarioSteps.None));
    }

    [Fact]
    public void The_line_after_a_slow_bee_names_its_slowest_slow_call_and_a_good_bee_says_nothing()
    {
        var slow = new BeeRecord(1, 0, 3, 9600, 1, ErrorDictionary.None, null);
        var detail = new BeeDetail(1, 0, [
            new FakeCall(0, "GET /home", 0, 100, 200, ErrorDictionary.None, false, 0),
            new FakeCall(1, "GET /search", 100, 4100, 200, ErrorDictionary.None, false, 0, Slow: true),
        ], () => []);
        Assert.Equal("slow: GET /search " + Beesharp.Client.Shared.Format.Ms(4100), BeeLine.Of(slow, detail, ScenarioSteps.None));
        Assert.Equal("slow", BeeLine.Of(slow, null, ScenarioSteps.None));
        Assert.Null(BeeLine.Of(new BeeRecord(2, 0, 4, 400, 0, ErrorDictionary.None, null), null, ScenarioSteps.None));
    }

    [Fact]
    public void The_line_after_a_bee_in_flight_is_its_step_or_its_current_call()
    {
        var steps = new ScenarioSteps(["Log in", "Search"]);
        var calls = new List<FakeCall>
        {
            new(0, "POST /login", 0, 100, 200, ErrorDictionary.None, false, 0, Step: 0),
            new(1, "GET /search", 500, 100, 200, ErrorDictionary.None, false, 0, Step: 1),
        };
        var bee = new RunningBee(3, 0, 10, calls);
        Assert.Equal("Step 2 of 2 · Search", BeeLine.Of(bee, steps, 11));
        Assert.Equal("Step 1 of 2 · Log in", BeeLine.Of(bee, steps, 10.2));
        Assert.Equal("GET /search", BeeLine.Of(bee, ScenarioSteps.None, 11));
    }
}
