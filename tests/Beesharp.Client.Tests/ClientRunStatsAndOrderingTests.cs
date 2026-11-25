using System.Globalization;
using Beesharp.Client.Chart;
using Beesharp.Client.Environments;
using Beesharp.Client.Logs;
using Beesharp.Client.Runs;
using Beesharp.Client.Shared;
using Beesharp.Services;
using Beesharp.Services.Environments;

namespace Beesharp.Client.Tests;

/// <summary>
/// Stories for client-side statistics, ordering, formatting and drill-down helpers:
/// <see cref="ScenarioStats"/>, <see cref="BeeStats"/>, <see cref="BeeOrder"/>,
/// <see cref="Format"/>, <see cref="LogsLinks"/>, <see cref="Aria"/> and <see cref="EnvironmentDisplay"/>.
/// </summary>
public sealed class ClientRunStatsAndOrderingTests
{
    [Fact]
    public void Overview_scenario_stats_track_sliding_window_detect_slow_tail_and_reset_on_new_run()
    {
        var errors = new ErrorDictionary();
        var stats = new ScenarioStats();
        Assert.False(stats.HasData);
        Assert.False(stats.Judgeable);

        // Start a run with 12 bees: 10 typical ~100 ms, 2 slow outliers (350 ms, 500 ms).
        var bees = new List<BeeRecord>();
        for (var i = 0; i < 10; i++)
            bees.Add(new BeeRecord(i, 0, i * 1.0, 100 + i, 0, 0, errors));
        bees.Add(new BeeRecord(10, 0, 10.0, 350, 1, 0, errors));
        bees.Add(new BeeRecord(11, 0, 11.0, 500, 1, 0, errors));

        // Incremental updates refresh immediately on first data, stay constant if finished hasn't moved, and refresh every 5 bees.
        stats.Update(bees, finished: 2);
        Assert.True(stats.HasData);
        Assert.False(stats.Judgeable);
        stats.Update(bees, finished: 2); // no-op

        stats.Update(bees, finished: 12);
        Assert.True(stats.Judgeable);
        Assert.Equal(500, stats.Longest);
        Assert.True(stats.Median is >= 100 and <= 115);
        Assert.True(stats.P95Slow);
        Assert.True(stats.LongestSlow);

        // Starting a fresh run resets all accumulated metrics cleanly.
        var fresh = new List<BeeRecord> { new(0, 0, 0.1, 42, 0, 0, errors) };
        stats.Update(fresh, finished: 1);
        Assert.Equal(42, stats.Longest);
        Assert.Equal(42, stats.Median);
        Assert.False(stats.Judgeable);
    }

    [Fact]
    public void Popup_bee_stats_compute_window_percentiles_rates_and_drill_down_indices()
    {
        var errors = new ErrorDictionary();
        var errId = errors.Intern("HTTP 500");

        // Empty window returns BeeStats.Empty with 0 rates and -1 drill-down indices.
        Assert.Equal(BeeStats.Empty, BeeStats.Compute([], 0, 0, 10));
        Assert.Equal(0, BeeStats.Empty.ErrorRate);
        Assert.Equal(0, BeeStats.Empty.SlowRate);

        // 10 bees inside [10s, 20s) plus 2 bees outside the zoomed window.
        var bees = new List<BeeRecord>
        {
            new(0, 0, 2.0, 9999, 1, 0, errors), // outside left
            new(1, 0, 10.0, 100, 0, 0, errors),
            new(2, 0, 11.0, 105, 0, 0, errors),
            new(3, 0, 12.0, 110, 0, 0, errors),
            new(4, 0, 13.0, 115, 0, 0, errors),
            new(5, 0, 14.0, 120, 0, 0, errors),
            new(6, 0, 15.0, 125, 0, 0, errors),
            new(7, 0, 16.0, 130, 0, 0, errors),
            new(8, 0, 17.0, 135, 0, errId, errors), // failed
            new(9, 0, 18.0, 320, 1, 0, errors),     // slow
            new(10, 0, 19.0, 480, 1, 0, errors),    // longest & slow
            new(11, 0, 25.0, 50, 0, 0, errors),     // outside right
        };

        var s = BeeStats.Compute(bees, bees.Count, from: 10.0, to: 20.0);

        Assert.Equal(10, s.Count);
        Assert.Equal(1, s.Failed);
        Assert.Equal(2, s.Slow);
        Assert.Equal(10.0, s.ErrorRate);
        Assert.Equal(20.0, s.SlowRate);
        Assert.Equal(120, s.Median);
        Assert.Equal(5, s.MedianIndex);
        Assert.Equal(480, s.Longest);
        Assert.Equal(10, s.LongestIndex);
        Assert.True(s.TailIsSlow);
        Assert.True(s.LongestIsSlow);
    }

    [Fact]
    public void Bee_order_ranks_failed_then_slow_then_running_then_ok_and_maps_css_and_words()
    {
        var errors = new ErrorDictionary();
        var errId = errors.Intern("timeout");
        var finished = new List<BeeRecord>
        {
            new(Sequence: 0, Entry: 0, StartedAt: 1.0, DurationMs: 90, SlowCalls: 0, ErrorId: 0, Errors: errors),
            new(Sequence: 1, Entry: 0, StartedAt: 2.0, DurationMs: 420, SlowCalls: 1, ErrorId: 0, Errors: errors),
            new(Sequence: 2, Entry: 0, StartedAt: 3.0, DurationMs: 250, SlowCalls: 0, ErrorId: errId, Errors: errors),
            new(Sequence: 3, Entry: 0, StartedAt: 1.0, DurationMs: 85, SlowCalls: 0, ErrorId: 0, Errors: errors), // tie on StartedAt with seq 0
        };
        var running = new List<RunningBee>
        {
            new(Sequence: 4, Entry: 0, StartedAt: 4.0, Calls: []),
            new(Sequence: 5, Entry: 0, StartedAt: 99.0, Calls: []), // outside window
        };

        var allRows = BeeOrder.Rows(finished, finished.Count, running, BeeShow.All, from: 0, to: 10);
        Assert.Equal([2, 1, 4, 0, 3], allRows.Select(r => r.Sequence).ToArray());
        Assert.True(allRows[2].IsRunning);

        // Filtering by minMs excludes running bees and short calls.
        var slowOnly = BeeOrder.Rows(finished, finished.Count, running, BeeShow.All, from: 0, to: 10, minMs: 200);
        Assert.Equal([2, 1], slowOnly.Select(r => r.Sequence).ToArray());

        // Outcome CSS classes and graded slowness tints (brain 0018, 0041).
        Assert.Equal("bad", BeeOrder.Css(BeeState.Failed));
        Assert.Equal("warn", BeeOrder.Css(BeeState.Slow));
        Assert.Equal("run", BeeOrder.Css(BeeState.Running));
        Assert.Equal("good", BeeOrder.Css(BeeState.Ok, tint: 0));
        Assert.Equal("good t2", BeeOrder.Css(BeeState.Ok, tint: 2));
        Assert.Equal("warn", BeeOrder.Css(BeeState.Ok, tint: SlowCallRule.MaxStep));

        Assert.Equal("failed", BeeOrder.Word(BeeState.Failed));
        Assert.Equal("slow", BeeOrder.Word(BeeState.Slow));
        Assert.Equal("active", BeeOrder.Word(BeeState.Running));
        Assert.Equal("ok", BeeOrder.Word(BeeState.Ok));
    }

    [Fact]
    public void Format_renders_durations_rates_burst_shapes_ratios_and_plurals_cleanly()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            Assert.Equal("0:00", Format.Time(-5));
            Assert.Equal("1:05", Format.Time(65.9));
            Assert.Equal("1:02:03", Format.Time(3723));
            Assert.Equal("850 ms", Format.Ms(850));
            Assert.Equal("3.4 s", Format.Ms(3400));
            Assert.Equal("1:05", Format.Ms(65_000));

            Assert.Equal("12", Format.Rate(12.4));
            Assert.Equal("4.5", Format.Rate(4.5));
            Assert.Equal("1,250", Format.Num(1250));
            Assert.Equal("1.4×", Format.Times(1.42));
            Assert.False(Format.OverTypical(1.02));
            Assert.True(Format.OverTypical(1.10));
            Assert.Equal("1 bee", Format.Plural(1, "bee", "bees"));
            Assert.Equal("3 bees", Format.Plural(3, "bee", "bees"));

            var burstEntry = new ScenarioRunConfig { Pattern = SpawningPattern.Bursts, BeesPerBurst = 10, BurstsPerMinute = 4 };
            var linearEntry = new ScenarioRunConfig { Pattern = SpawningPattern.Linear };
            Assert.Equal("10 × 4/min", Format.Shape(burstEntry));
            Assert.Equal("", Format.Shape(linearEntry));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Aria_environment_display_and_logs_links_produce_consistent_ui_attributes_and_urls()
    {
        Assert.Equal("true", Aria.Bool(true));
        Assert.Equal("false", Aria.Bool(false));
        Assert.True(EnvironmentDisplay.LooksLikeCredential("ApiToken"));
        Assert.False(EnvironmentDisplay.LooksLikeCredential("BaseUrl"));

        // LogsLinks builds the 1-based /logs URL and remembers the last navigated filter.
        var nav = new StubNavigationManager();
        var links = new LogsLinks(nav);
        links.Show(entry: 0, bee: 4, outcome: BeeShow.Failed);
        Assert.Single(nav.NavigatedTo);
        Assert.Equal("logs?scenario=1&bee=5&bees=failed", nav.NavigatedTo[0]);
        Assert.Equal(0, links.Last.Entry);
        Assert.Equal(4, links.Last.Bee);
    }
}
