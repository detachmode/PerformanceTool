using PerformanceTool.Services;
using PerformanceTool.Services.Scenarios;
using PerformanceTool.Services.Storage;
using Xunit;

namespace PerformanceTool.Tests;

public sealed class PreflightTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"perftool-preflight-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task Preflight_Success_Captures_Logs_And_Starts_Scenarios()
    {
        var preflights = new PreflightStore(tempDir);
        var compiled = await preflights.CompileAndSaveAsync("ok-check", "ok-check.cs", """
            Log("Checking target...");
            await Task.Delay(10, CancellationToken);
            Log("Ready!");
            """);
        Assert.True(compiled.Success, compiled.Error);

        var catalog = new ScenarioCatalog([new FastStubScenario()]);
        await using var storage = new InMemoryRunStorage();
        var runner = new LoadRunner(catalog);
        var manager = new RunManager(runner, null!, storage, preflights);

        var plan = new LoadPlan
        {
            DurationSeconds = 1,
            MaxConcurrency = 5,
            TimeoutSeconds = 5,
            PreflightScriptId = "ok-check",
            Scenarios = [new ScenarioRunConfig { ScenarioId = "fast-stub", Label = "Fast", RatePerMinute = 120 }],
        };

        await manager.StartAsync(plan, null);

        LiveRunStatusDto status = manager.GetCurrent();
        for (var i = 0; i < 60 && status.State is "Preflight" or "Running"; i++)
        {
            await Task.Delay(50);
            status = manager.GetCurrent();
        }

        Assert.Equal("Finished", status.State);
        Assert.Equal("Succeeded", status.PreflightState);
        Assert.Null(status.PreflightError);
        Assert.Contains(status.PreflightLogs!, l => l.Contains("Checking target..."));
        Assert.Contains(status.PreflightLogs!, l => l.Contains("Ready!"));
        Assert.True(status.Scenarios[0].Finished > 0, "Scenarios should run after preflight succeeds.");
    }

    [Fact]
    public async Task Preflight_Failure_Aborts_Before_Scenarios_Start()
    {
        var preflights = new PreflightStore(tempDir);
        var compiled = await preflights.CompileAndSaveAsync("fail-check", "fail-check.cs", """
            Log("Connecting to DB...");
            Fail("Database is not reachable");
            """);
        Assert.True(compiled.Success, compiled.Error);

        var catalog = new ScenarioCatalog([new FastStubScenario()]);
        await using var storage = new InMemoryRunStorage();
        var runner = new LoadRunner(catalog);
        var manager = new RunManager(runner, null!, storage, preflights);

        var plan = new LoadPlan
        {
            DurationSeconds = 2,
            MaxConcurrency = 5,
            TimeoutSeconds = 5,
            PreflightScriptId = "fail-check",
            Scenarios = [new ScenarioRunConfig { ScenarioId = "fast-stub", Label = "Fast", RatePerMinute = 120 }],
        };

        await manager.StartAsync(plan, null);

        LiveRunStatusDto status = manager.GetCurrent();
        for (var i = 0; i < 40 && status.State is "Preflight" or "Running"; i++)
        {
            await Task.Delay(50);
            status = manager.GetCurrent();
        }

        Assert.Equal("Finished", status.State);
        Assert.Equal("Failed", status.PreflightState);
        Assert.Contains("Database is not reachable", status.PreflightError);
        Assert.Contains(status.PreflightLogs!, l => l.Contains("Connecting to DB..."));
        Assert.Equal(0, status.Scenarios[0].Starts);
        Assert.Equal(0, status.Scenarios[0].Finished);
    }

    private sealed class EmptyParams { }

    private sealed class FastStubScenario : Scenario<EmptyParams>
    {
        public override string Id => "fast-stub";
        public override string Name => "Fast Stub";
        public override string Description => "Test scenario";
        protected override Task<ScenarioOutcome> ExecuteAsync(EmptyParams parameters, ScenarioContext context, CancellationToken ct) =>
            Task.FromResult(new ScenarioOutcome(200, 10, null));
    }
}
