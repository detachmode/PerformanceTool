using Beesharp.Client.Chart;
using Beesharp.Client.Logs;
using Beesharp.Client.Runs;

namespace Beesharp.Client.Tests;

/// <summary>The Logs page's filter, counts and text (brain 0057): public behaviour only.</summary>
public class LogFilterTests
{
    private static readonly IReadOnlyDictionary<string, string> NoScope = new Dictionary<string, string>();

    private static FakeLogEntry Line(int entry, int seq, string level, string message, int step = 0, string? exception = null, double at = 0) =>
        new(at, DateTimeOffset.UnixEpoch, level, message, exception, entry, seq, NoScope, null, step);

    /// <summary>Entry 0: B1 fails at step 1 (two errors and a warning), B2 ok. Entry 1: B3 slow.</summary>
    private static (RunStore Store, List<FakeLogEntry> Lines) Run()
    {
        var store = new RunStore(2);
        void Bee(int seq, int entry, BeeOutcome outcome) => store.Complete(
            new BeeRecord(seq, entry, seq, 100, outcome == BeeOutcome.Slow ? 1 : 0, outcome == BeeOutcome.Failed ? store.Errors.Intern("boom") : ErrorDictionary.None, store.Errors),
            new BeeDetail(seq, entry, [], () => []));
        Bee(0, 0, BeeOutcome.Failed); Bee(1, 0, BeeOutcome.Ok); Bee(2, 1, BeeOutcome.Slow);
        return (store,
        [
            Line(0, 0, "Information", "log in", 0),
            Line(0, 0, "Error", "GET /a 503", 1, "System.Exception: bad"),
            Line(0, 0, "Error", "GET /b 503", 1),
            Line(0, 0, "Warning", "slow", 1),
            Line(0, 1, "Information", "hello", 0),
            Line(1, 2, "Warning", "slow call", 0),
        ]);
    }

    [Fact]
    public void An_empty_filter_keeps_every_line_and_knows_it_is_empty()
    {
        var (store, lines) = Run();
        Assert.True(LogFilter.None.IsEmpty);
        Assert.Equal(6, LogCounts.Filter(lines, LogFilter.None, store).Count);
    }

    [Fact]
    public void Level_scenario_and_step_narrow_the_lines_together()
    {
        var (store, lines) = Run();
        var f = LogFilter.None.ToggleLevel("Error").WithEntry(0).WithStep(1);
        var hit = LogCounts.Filter(lines, f, store);
        Assert.Equal(2, hit.Count);
        Assert.All(hit, l => Assert.Equal("Error", l.Level));
        Assert.Empty(LogCounts.Filter(lines, f.WithStep(0), store));
    }

    [Fact]
    public void A_step_needs_its_scenario_and_changing_the_scenario_drops_it()
    {
        Assert.Null(LogFilter.None.WithStep(2).Step);
        var f = LogFilter.None.WithEntry(0).WithStep(2);
        Assert.Equal(2, f.Step);
        Assert.Null(f.WithEntry(1).Step);
        Assert.Equal(2, f.WithEntry(0).Step);
        Assert.Null(f.ToggleEntry(0).Entry);
    }

    [Fact]
    public void Levels_toggle_in_order_and_toggle_off_again()
    {
        var f = LogFilter.None.ToggleLevel("Error").ToggleLevel("Warning");
        Assert.Equal(["Warning", "Error"], f.Levels);
        Assert.Equal(["Warning"], f.ToggleLevel("Error").Levels);
    }

    [Fact]
    public void A_search_looks_into_the_message_the_exception_and_the_bee_name()
    {
        var (store, lines) = Run();
        Assert.Equal(2, LogCounts.Filter(lines, LogFilter.None.WithSearch("503"), store).Count);
        Assert.Single(LogCounts.Filter(lines, LogFilter.None.WithSearch("system.exception"), store));
        Assert.Single(LogCounts.Filter(lines, LogFilter.None.WithSearch("HELLO"), store));
    }

    [Fact]
    public void A_search_that_is_a_bee_number_filters_by_that_bee()
    {
        var (store, lines) = Run();
        var f = LogFilter.None.WithSearch(" b2 ");
        Assert.Equal(1, f.EffectiveBee);
        Assert.Equal(("hello"), Assert.Single(LogCounts.Filter(lines, f, store)).Message);
        Assert.Equal(4, LogCounts.Filter(lines, LogFilter.None.WithBee(0), store).Count);
        Assert.Empty(LogCounts.Filter(lines, LogFilter.None.WithBee(0).WithSearch("B2"), store));
    }

    [Fact]
    public void The_outcome_keeps_only_the_logs_of_failed_or_slow_bees()
    {
        var (store, lines) = Run();
        Assert.Equal(4, LogCounts.Filter(lines, LogFilter.None.WithOutcome(BeeShow.Failed), store).Count);
        Assert.Equal("slow call", Assert.Single(LogCounts.Filter(lines, LogFilter.None.WithOutcome(BeeShow.Slow), store)).Message);
    }

    [Fact]
    public void Counts_follow_the_same_rules_as_the_server_facets()
    {
        var (store, lines) = Run();

        var all = LogCounts.Count(lines, LogFilter.None, store);
        Assert.Equal(6, all.Total);
        Assert.Equal(2, all.Levels["Error"]);
        Assert.Equal(2, all.Levels["Warning"]);
        Assert.Equal([5L, 1L], all.Entries);
        Assert.Empty(all.Steps);

        // the level group ignores its own filter, the others do not
        var errors = LogCounts.Count(lines, LogFilter.None.ToggleLevel("Error").WithEntry(0), store);
        Assert.Equal(2, errors.Total);
        Assert.Equal(5, errors.Levels.Values.Sum());
        // the scenario group ignores the scenario (and step) but keeps the level
        Assert.Equal([2L], errors.Entries);
        // the steps of the chosen scenario: lines and error lines, ignoring the level filter
        Assert.Equal([new LogStepCount(0, 2, 0), new LogStepCount(1, 3, 2)], errors.Steps);
    }

    [Fact]
    public void The_filter_goes_to_the_address_and_back()
    {
        var f = LogFilter.None.WithEntry(1).WithStep(2).WithBee(198).ToggleLevel("Error").ToggleLevel("Warning").WithOutcome(BeeShow.Failed).WithSearch("a b&c");
        Assert.Equal("scenario=2&step=3&bee=199&level=warning%2Cerror&bees=failed&q=a%20b%26c", f.ToQueryString());
        Assert.Equal("logs?" + f.ToQueryString(), f.Href());
        var back = LogFilter.FromQuery("2", "3", "199", "warning,error", "failed", "a b&c");
        Assert.True(back.SameAs(f));
        Assert.Equal("logs", LogFilter.None.Href());
    }

    [Fact]
    public void Odd_addresses_are_read_forgivingly()
    {
        var f = LogFilter.FromQuery("0", "x", "-3", "warn,INF,nonsense", "nope", null);
        Assert.Null(f.Entry);
        Assert.Null(f.Bee);
        Assert.Equal(["Information", "Warning"], f.Levels);
        Assert.Equal(BeeShow.All, f.Outcome);
        Assert.Null(LogFilter.FromQuery(null, "2", null, null, null, null).Step); // a step without its scenario
    }

    [Theory]
    [InlineData(0.0, "0:00.0")]
    [InlineData(5.96, "0:05.9")]
    [InlineData(65, "1:05.0")]
    [InlineData(3723.4, "1:02:03.4")]
    public void Time_has_a_tenth_of_a_second(double seconds, string expected) => Assert.Equal(expected, LogText.Time(seconds));

    [Fact]
    public void A_line_reads_like_the_console_and_all_lines_join()
    {
        var (_, lines) = Run();
        Assert.Equal("[0:00.0] ERR Checkout B1 GET /a 503\nSystem.Exception: bad", LogText.Line(lines[1], "Checkout"));
        Assert.Equal("[0:00.0] INF B2 hello", LogText.Line(lines[4], ""));
        Assert.Equal(2, LogText.All(lines.Take(2), _ => "S").Split("] ").Length - 1 );
    }

    [Fact]
    public void A_paged_list_reads_as_placeholders_until_its_page_arrives_and_asks_for_the_pages_in_view()
    {
        var list = new PagedLogList();
        list.SetTotal(1000);
        Assert.Equal(1000, list.Count);
        Assert.Same(PagedLogList.Placeholder, list[10]);
        Assert.Equal([0, 1], list.Missing(0, 20)); // the page in view and one beyond
        Assert.Equal([4, 3], list.Missing(850, 870)); // the page in view first, then the one before (none after the last)

        list.SetPage(0, Enumerable.Range(0, PagedLogList.PageSize).Select(i => Line(0, i, "Information", $"m{i}")).ToList());
        Assert.Equal("m10", list[10].Message);
        Assert.DoesNotContain(0, list.Missing(0, 20));
    }

    [Fact]
    public void A_paged_list_that_grows_reloads_its_last_partial_page_and_one_that_shrinks_forgets_pages()
    {
        var list = new PagedLogList();
        list.SetTotal(250);
        list.SetPage(1, Enumerable.Range(0, 50).Select(i => Line(0, 200 + i, "Information", "x")).ToList());
        list.SetTotal(260);
        Assert.False(list.IsLoaded(1)); // the last page had room: it may have grown
        list.SetPage(0, Enumerable.Range(0, 200).Select(i => Line(0, i, "Information", "x")).ToList());
        list.SetTotal(300); // page 0 was full: stays
        Assert.True(list.IsLoaded(0));
        list.SetTotal(100);
        Assert.False(list.IsLoaded(1));
        Assert.Equal(100, list.Count);
    }

    [Fact]
    public void A_paged_list_keeps_a_bounded_number_of_pages_nearest_to_the_window()
    {
        var list = new PagedLogList(maxPages: 4);
        list.SetTotal(PagedLogList.PageSize * 10);
        for (var p = 0; p < 10; p++) list.SetPage(p, [Line(0, p, "Information", "x")]);
        list.Trim(first: 8 * PagedLogList.PageSize, last: 9 * PagedLogList.PageSize);
        Assert.Equal(4, list.LoadedPages);
        Assert.True(list.IsLoaded(8));
        Assert.False(list.IsLoaded(0));
    }
}
