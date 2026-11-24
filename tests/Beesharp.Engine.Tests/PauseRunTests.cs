using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;

namespace Beesharp.Engine.Tests;

/// <summary>Pausing a run holds it: no new bees start and the run time stands still; bees in flight finish; stop still ends it (brain 0027).</summary>
public sealed class PauseRunTests
{
    [Fact]
    public async Task Paused_time_does_not_count_as_run_time()
    {
        var clock = new RunClock();
        clock.Start();
        await Task.Delay(50);
        Assert.True(clock.Pause());
        var atPause = clock.Elapsed;
        await Task.Delay(200);
        Assert.Equal(atPause, clock.Elapsed);
        Assert.True(clock.Resume());
        Assert.True(clock.Elapsed < atPause + TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task A_paused_run_starts_no_new_bees_and_lets_the_bees_in_flight_finish()
    {
        await using var storage = new InMemoryRunStorage();
        var manager = new RunManager(new LoadRunner(new ScenarioCatalog([new SlowScenario()])), null!, storage);
        await manager.StartAsync(Plan(), null);
        await WaitFor(() => manager.GetCurrent().Scenarios[0].InFlight >= 2);

        var paused = manager.Pause();
        Assert.Equal("Paused", paused.State);
        var startsAtPause = paused.Scenarios[0].Starts;

        // The bees in flight finish (not canceled), and no new ones start while paused.
        await WaitFor(() => manager.GetCurrent().Scenarios[0].InFlight == 0);
        await Task.Delay(400);
        var whilePaused = manager.GetCurrent();
        Assert.Equal("Paused", whilePaused.State);
        Assert.Equal(startsAtPause, whilePaused.Scenarios[0].Starts);
        Assert.Equal(0, whilePaused.Scenarios[0].Failed);

        // Resume: bees start again.
        Assert.Equal("Running", manager.Resume().State);
        await WaitFor(() => manager.GetCurrent().Scenarios[0].Starts > startsAtPause);

        await manager.StopAsync();
        await WaitFor(() => manager.GetCurrent().State == "Finished");
    }

    [Fact]
    public async Task Stopping_a_paused_run_ends_it()
    {
        await using var storage = new InMemoryRunStorage();
        var manager = new RunManager(new LoadRunner(new ScenarioCatalog([new SlowScenario()])), null!, storage);
        await manager.StartAsync(Plan(), null);
        await WaitFor(() => manager.GetCurrent().Scenarios[0].Starts >= 1);
        manager.Pause();

        await manager.StopAsync();
        await WaitFor(() => manager.GetCurrent().State == "Finished");
        Assert.Equal("Finished", manager.GetCurrent().State);
    }

    private static LoadPlan Plan() => new()
    {
        DurationSeconds = 60,
        MaxConcurrency = 20,
        TimeoutSeconds = 20,
        Scenarios = [new ScenarioRunConfig { ScenarioId = "slow", Label = "Slow", RatePerMinute = 600 }],
    };

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    public sealed class NoParams;

    /// <summary>A bee that takes 300 ms, so several are in flight at any time at 10 bees a second.</summary>
    private sealed class SlowScenario : Scenario<NoParams>
    {
        public override string Id => "slow";
        public override string Name => "Slow";
        public override string Description => "Test scenario.";

        protected override async Task<ScenarioOutcome> ExecuteAsync(NoParams p, ScenarioContext ctx, CancellationToken ct)
        {
            await Task.Delay(300, ct);
            return new ScenarioOutcome(200, 0);
        }
    }
}
