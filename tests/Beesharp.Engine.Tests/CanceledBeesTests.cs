using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;
using Beesharp.Services.Storage;

namespace Beesharp.Engine.Tests;

/// <summary>Bees in flight when a run is stopped are kept as canceled, with their logs and calls (brain 0053).</summary>
public sealed class CanceledBeesTests
{
    public sealed class StepParams
    {
        public int LongMs { get; set; } = 5000;
    }

    private sealed class StepScenario(Action? onEnteredLong = null) : Scenario<StepParams>
    {
        public override string Id => "step";
        public override string Name => "Step";
        public override string Description => "Test scenario.";

        protected override async Task<ScenarioOutcome> ExecuteAsync(StepParams p, ScenarioContext ctx, CancellationToken ct)
        {
            ctx.Log("first");
            await ctx.MeasureCallAsync("quick", () => Task.Delay(20, ct));
            await ctx.MeasureCallAsync("long", () =>
            {
                onEnteredLong?.Invoke();
                return Task.Delay(p.LongMs, ct);
            });
            return new ScenarioOutcome(200, 0);
        }
    }

    private static LoadPlan Plan() => new()
    {
        DurationSeconds = 30,
        MaxConcurrency = 10,
        TimeoutSeconds = 20,
        Scenarios = [new ScenarioRunConfig { ScenarioId = "step", Label = "Step", RatePerMinute = 120 }],
    };

    private static async Task<StoredBeeDetail> StopAndReadBack(IRunStorage storage)
    {
        var inLong = 0;
        var twoInLong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RunManager(new LoadRunner(new ScenarioCatalog([new StepScenario(() => { if (Interlocked.Increment(ref inLong) >= 2) twoInLong.TrySetResult(); })])), null!, storage);
        await manager.StartAsync(Plan(), null);
        for (var i = 0; i < 100 && manager.GetCurrent().Scenarios[0].InFlight < 2; i++) await Task.Delay(50);
        await twoInLong.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(20);
        await manager.StopAsync();
        for (var i = 0; i < 200 && manager.GetCurrent().State != "Finished"; i++) await Task.Delay(50);
        var status = manager.GetCurrent();
        Assert.Equal("Finished", status.State);
        Assert.Equal(0, status.Scenarios[0].InFlight);
        // They count like failed: in the errors, with the one error text.
        Assert.True(status.Scenarios[0].Failed >= 2);
        Assert.All(status.NewBees, b => Assert.Equal(ExecutionResult.CanceledError, b.Error));
        Assert.Equal(status.Scenarios[0].Starts, status.NewBees.Count);

        await storage.FlushAsync();
        var run = await storage.GetRunAsync(status.RunId);
        Assert.NotNull(run);
        var detail = await storage.GetBeeDetailAsync(status.RunId, 0);
        Assert.NotNull(detail);
        return detail!;
    }

    private static void AssertCanceled(StoredBeeDetail d)
    {
        Assert.True(ExecutionResult.IsCanceledError(d.Error));
        Assert.Contains(d.Logs, l => l.Message == "first");
        Assert.Equal(2, d.Calls.Count);
        Assert.Null(d.Calls[0].Error);
        Assert.Equal(ExecutionResult.CanceledError, d.Calls[1].Error);
        Assert.True(d.Calls[1].DurationMs > 0);
        Assert.True(d.DurationMs >= d.Calls[1].OffsetMs);
    }

    [Fact]
    public async Task Canceled_bee_is_stored_and_read_back_from_memory()
    {
        await using var s = new InMemoryRunStorage();
        AssertCanceled(await StopAndReadBack(s));
    }

    [Fact]
    public async Task Canceled_bee_is_stored_and_read_back_from_duckdb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"perftool-cancel-{Guid.NewGuid():N}.duckdb");
        try
        {
            await using var s = new DuckDbRunStorage(path);
            AssertCanceled(await StopAndReadBack(s));
        }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
            try { File.Delete(f); } catch (IOException) { }
    }
}
