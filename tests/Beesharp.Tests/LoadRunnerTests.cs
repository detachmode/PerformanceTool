using System.ComponentModel.DataAnnotations;
using System.Collections.Concurrent;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Tests;

public class LoadRunnerTests
{
    public sealed class DelayParameters
    {
        [Range(0, 10_000)] public int DelayMs { get; set; }
        public bool Fail { get; set; }
    }

    /// <summary>No network: waits, then succeeds or fails. Lets us test scheduling in isolation.</summary>
    private sealed class DelayScenario : Scenario<DelayParameters>
    {
        public override string Id => "delay";
        public override string Name => "Delay";
        public override string Description => "Test scenario.";

        protected override async Task<ScenarioOutcome> ExecuteAsync(DelayParameters p, ScenarioContext ctx, CancellationToken ct)
        {
            await Task.Delay(p.DelayMs, ct);
            return p.Fail ? ScenarioOutcome.Failed("boom") : new ScenarioOutcome(200, 0);
        }
    }

    private static readonly ScenarioCatalog Catalog = new([new DelayScenario()]);

    private static ScenarioRunConfig Entry(double ratePerMinute, int delayMs = 0, bool fail = false)
    {
        var e = ScenarioRunConfig.CreateFor(Catalog.Find("delay")!);
        e.RatePerMinute = ratePerMinute;
        e.Parameters["delayMs"] = delayMs;
        e.Parameters["fail"] = fail;
        return e;
    }

    private static async Task<(List<ExecutionResult> Results, int[] Dropped)> Run(LoadPlan plan)
    {
        var results = new ConcurrentBag<ExecutionResult>();
        var dropped = new int[plan.Scenarios.Count];
        await new LoadRunner(Catalog).RunAsync(plan,
            r => { results.Add(r); return ValueTask.CompletedTask; },
            i => { Interlocked.Increment(ref dropped[i]); return ValueTask.CompletedTask; },
            CancellationToken.None);
        return (results.ToList(), dropped);
    }

    [Fact]
    public async Task Each_entry_runs_exactly_rate_times_duration()
    {
        // 1800/min = 30/s and 600/min = 10/s for 1 s. The rounding bug used to add an extra run.
        var plan = new LoadPlan { DurationSeconds = 1, Scenarios = [Entry(1800), Entry(600)] };
        var (results, dropped) = await Run(plan);
        Assert.Equal(30, results.Count(r => r.Entry == 0));
        Assert.Equal(10, results.Count(r => r.Entry == 1));
        Assert.All(dropped, d => Assert.Equal(0, d));
    }

    [Fact]
    public async Task Runs_are_dropped_not_queued_when_max_in_flight_is_reached()
    {
        // 10/s, each taking 400 ms, but only one may be in flight.
        var plan = new LoadPlan { DurationSeconds = 1, MaxConcurrency = 1, Scenarios = [Entry(600, delayMs: 400)] };
        var (results, dropped) = await Run(plan);
        Assert.Equal(10, results.Count + dropped[0]);
        Assert.InRange(results.Count, 2, 4);
    }

    [Fact]
    public async Task Failures_and_timeouts_are_reported_as_errors()
    {
        var plan = new LoadPlan { DurationSeconds = 1, TimeoutSeconds = 1, Scenarios = [Entry(60, fail: true), Entry(60, delayMs: 3000)] };
        var (results, _) = await Run(plan);
        Assert.Equal("boom", Assert.Single(results, r => r.Entry == 0).Error);
        Assert.StartsWith("Timeout", Assert.Single(results, r => r.Entry == 1).Error);
    }

    private static async Task<(List<ExecutionResult> Results, List<ExecutionActivity> Activity)> RunWithActivity(LoadPlan plan, CancellationToken ct = default)
    {
        var results = new ConcurrentBag<ExecutionResult>();
        var activity = new List<ExecutionActivity>();
        try
        {
            await new LoadRunner(Catalog).RunAsync(plan,
                r => { results.Add(r); return ValueTask.CompletedTask; },
                _ => ValueTask.CompletedTask,
                ct,
                a => { lock (activity) activity.Add(a); return ValueTask.CompletedTask; });
        }
        catch (OperationCanceledException) { }
        return (results.ToList(), activity);
    }

    [Fact]
    public async Task Activity_reports_starts_and_in_flight_returning_to_zero()
    {
        // 10/s for 1 s, each taking 250 ms: several executions overlap.
        var plan = new LoadPlan { DurationSeconds = 1, Scenarios = [Entry(600, delayMs: 250)] };
        var (results, activity) = await RunWithActivity(plan);

        Assert.Equal(10, activity.Count(a => a.Started));
        Assert.Equal(results.Count, activity.Count(a => !a.Started));
        Assert.Equal(10, results.Count);
        Assert.InRange(activity.Max(a => a.InFlight), 2, 10);
        Assert.Equal(0, activity.Last(a => !a.Started).InFlight);
        Assert.All(activity, a => Assert.InRange(a.InFlight, 0, 10));
        Assert.All(activity, a => Assert.Equal(a.InFlight, a.TotalInFlight)); // single entry
    }

    [Fact]
    public async Task In_flight_is_counted_per_entry()
    {
        var plan = new LoadPlan { DurationSeconds = 1, Scenarios = [Entry(600, delayMs: 300), Entry(60)] };
        var (_, activity) = await RunWithActivity(plan);
        Assert.Equal(10, activity.Count(a => a.Started && a.Entry == 0));
        Assert.Equal(1, activity.Count(a => a.Started && a.Entry == 1));
        Assert.True(activity.Any(a => a.TotalInFlight > a.InFlight), "entries overlap, so the total exceeds a single entry's count");
        Assert.All(new[] { 0, 1 }, e => Assert.Equal(0, activity.Last(a => !a.Started && a.Entry == e).InFlight));
    }

    [Fact]
    public async Task Started_equals_results_plus_cancelled_when_the_run_is_stopped()
    {
        using var cts = new CancellationTokenSource();
        var plan = new LoadPlan { DurationSeconds = 5, Scenarios = [Entry(1200, delayMs: 800)] };
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        var (results, activity) = await RunWithActivity(plan, cts.Token);

        var started = activity.Count(a => a.Started);
        var ended = activity.Count(a => !a.Started);
        Assert.True(started > 0);
        Assert.Equal(started, ended);                    // every start has an end, results or cancelled
        Assert.True(results.Count < started);            // the in-flight ones were cancelled, not reported
        Assert.Equal(0, activity.Last(a => !a.Started).TotalInFlight);
    }

    [Fact]
    public async Task Activity_events_arrive_in_counter_order_even_when_many_end_at_once()
    {
        // Many overlapping bees cancelled at the same moment: every event must change the total by exactly one,
        // in arrival order, so a UI that shows the latest event's count is never stale.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var cts = new CancellationTokenSource();
            var plan = new LoadPlan { DurationSeconds = 5, MaxConcurrency = 200, Scenarios = [Entry(6000, delayMs: 2000)] };
            cts.CancelAfter(TimeSpan.FromMilliseconds(400));
            var (_, activity) = await RunWithActivity(plan, cts.Token);

            var expected = 0;
            foreach (var a in activity)
            {
                expected += a.Started ? 1 : -1;
                Assert.Equal(expected, a.TotalInFlight);
            }
            Assert.Equal(0, expected);
        }
    }

    [Fact]
    public async Task Bursts_start_their_bees_together()
    {
        // 5 bees per burst, 120 bursts/min = one burst every 0.5 s, for 2 s: 4 bursts = 20 bees, in clumps of 5.
        var e = Entry(1);
        e.Pattern = SpawningPattern.Bursts;
        e.BeesPerBurst = 5;
        e.BurstsPerMinute = 120;
        var (results, dropped) = await Run(new LoadPlan { DurationSeconds = 2, Scenarios = [e] });
        Assert.Equal(20, results.Count);
        Assert.Equal(0, dropped[0]);
        var clumps = results.GroupBy(r => Math.Round(r.StartedAtSeconds * 2)).ToList();
        Assert.Equal(4, clumps.Count);
        Assert.All(clumps, g => Assert.Equal(5, g.Count()));
    }

    [Fact]
    public async Task Bursts_respect_max_in_flight()
    {
        var e = Entry(1, delayMs: 300);
        e.Pattern = SpawningPattern.Bursts;
        e.BeesPerBurst = 8;
        e.BurstsPerMinute = 60; // one burst at t=0, duration 1 s -> only the first burst
        var (results, dropped) = await Run(new LoadPlan { DurationSeconds = 1, MaxConcurrency = 5, Scenarios = [e] });
        Assert.Equal(5, results.Count);
        Assert.Equal(3, dropped[0]);
    }

    private sealed class ThrowingScenario : Scenario<DelayParameters>
    {
        public override string Id => "throw";
        public override string Name => "Throw";
        public override string Description => "Throws an unhandled exception.";

        protected override Task<ScenarioOutcome> ExecuteAsync(DelayParameters p, ScenarioContext ctx, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Unhandled_exception_is_logged_with_full_exception_tostring()
    {
        var liveLogs = new List<LiveLogEvent>();
        var results = new List<ExecutionResult>();
        var sc = new ThrowingScenario();
        var runner = new LoadRunner(new ScenarioCatalog([sc]));
        var entry = ScenarioRunConfig.CreateFor(sc);
        entry.RatePerMinute = 60;
        var plan = new LoadPlan { DurationSeconds = 1, MaxConcurrency = 2, Scenarios = [entry] };
        await runner.RunAsync(
            plan,
            r => { lock (results) results.Add(r); return ValueTask.CompletedTask; },
            _ => ValueTask.CompletedTask,
            CancellationToken.None,
            onLiveLog: l => { lock (liveLogs) liveLogs.Add(l); });

        Assert.NotEmpty(results);
        var r = results[0];
        Assert.NotNull(r.Logs);
        var errLog = Assert.Single(r.Logs!, l => l.Level == "Error");
        Assert.Contains("boom", errLog.Message);
        Assert.NotNull(errLog.Exception);
        Assert.Contains(nameof(InvalidOperationException), errLog.Exception!);
        Assert.Contains(nameof(ThrowingScenario), errLog.Exception!);
        Assert.DoesNotContain("Beesharp.Services.LoadRunner", errLog.Exception!);
        Assert.Contains(liveLogs, l => l.Log.Level == "Error" && l.Log.Exception?.Contains(nameof(InvalidOperationException)) == true);
    }
}
