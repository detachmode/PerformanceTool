using System.ComponentModel.DataAnnotations;
using System.Collections.Concurrent;
using PerformanceTool.Services;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Tests;

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
}
