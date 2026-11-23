using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;

namespace Beesharp.Engine.Tests;

public class ScenarioStepsEngineTests
{
    public sealed record StepParams(bool FailAtPay = false, int DelayMs = 0);
    public sealed record LoginStep;
    public sealed record PayStep;
    public union TwoStep(LoginStep, PayStep);

    private sealed class TwoStepScenario : Scenario<StepParams, TwoStep>
    {
        public override string Id => "two-step";
        public override string Name => "Two step";
        public override string Description => "Test";

        protected override TwoStep StartStep(StepParams p) => new LoginStep();

        protected override async Task<TwoStep?> RunStepAsync(TwoStep step, StepParams p, ScenarioContext ctx, CancellationToken ct) =>
            step switch
            {
                LoginStep => await RunLogin(ctx, ct),
                PayStep => await RunPay(p, ctx, ct),
            };

        private static async Task<TwoStep?> RunLogin(ScenarioContext ctx, CancellationToken ct)
        {
            await ctx.MeasureCallAsync("login", () => Task.Delay(5, ct));
            return new PayStep();
        }

        private static async Task<TwoStep?> RunPay(StepParams p, ScenarioContext ctx, CancellationToken ct)
        {
            await ctx.MeasureCallAsync("pay", async () =>
            {
                if (p.DelayMs > 0) await Task.Delay(p.DelayMs, ct);
                if (p.FailAtPay) throw new InvalidOperationException("card declined");
            });
            return null;
        }
    }

    [Fact]
    public async Task RunManager_tracks_steps_and_failed_per_step()
    {
        var scenario = new TwoStepScenario();
        var catalog = new ScenarioCatalog([scenario]);
        var storage = new InMemoryRunStorage();
        var envDir = Path.Combine(Path.GetTempPath(), $"beesharp-env-{Guid.NewGuid():N}");
        var envs = new EnvironmentStore(envDir);
        var manager = new RunManager(new LoadRunner(catalog), envs, storage);

        var entry = ScenarioRunConfig.CreateFor(scenario);
        entry.RatePerMinute = 60;
        entry.Parameters["failAtPay"] = true;
        var plan = new LoadPlan { DurationSeconds = 1, MaxConcurrency = 5, TimeoutSeconds = 10, Scenarios = [entry] };

        await manager.StartAsync(plan, null);
        await manager.WaitUntilFinishedAsync();

        var status = manager.GetCurrent();
        Assert.NotNull(status.Steps);
        Assert.Equal(["Login step", "Pay step"], status.Steps![0].Select(s => s.Name).ToArray());
        Assert.True(status.Scenarios[0].Failed > 0);
        Assert.Equal(0, status.Scenarios[0].FailedPerStep![0]);
        Assert.Equal(status.Scenarios[0].Failed, status.Scenarios[0].FailedPerStep![1]);
        Assert.All(status.NewBees, b => Assert.Equal(1, b.FailedStep));
    }

    [Fact]
    public async Task Stopped_run_keeps_step_of_canceled_bee()
    {
        var scenario = new TwoStepScenario();
        var catalog = new ScenarioCatalog([scenario]);
        var storage = new InMemoryRunStorage();
        var envDir = Path.Combine(Path.GetTempPath(), $"beesharp-env-stop-{Guid.NewGuid():N}");
        var envs = new EnvironmentStore(envDir);
        var manager = new RunManager(new LoadRunner(catalog), envs, storage);

        var entry = ScenarioRunConfig.CreateFor(scenario);
        entry.RatePerMinute = 60;
        entry.Parameters["delayMs"] = 5000;
        var plan = new LoadPlan { DurationSeconds = 10, MaxConcurrency = 1, TimeoutSeconds = 30, Scenarios = [entry] };

        await manager.StartAsync(plan, null);

        // Wait until a bee reaches PayStep (step index 1)
        LiveRunStatusDto? mid = null;
        for (var i = 0; i < 60; i++)
        {
            mid = manager.GetCurrent();
            if (mid.RunningBees is { Count: > 0 } rb && rb[0].Step == 1) break;
            await Task.Delay(25);
        }
        Assert.NotNull(mid?.RunningBees);
        Assert.Equal(1, mid!.RunningBees![0].Step);
        Assert.Equal(1, mid.Scenarios[0].InFlightPerStep![1]);

        await manager.StopAsync();
        await manager.WaitUntilFinishedAsync();

        var done = manager.GetCurrent();
        Assert.NotEmpty(done.NewBees);
        Assert.Equal(ExecutionResult.CanceledError, done.NewBees[0].Error);
        Assert.Equal(1, done.NewBees[0].FailedStep);
    }
}
