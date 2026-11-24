using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;
using Xunit;

namespace Beesharp.Engine.Tests;

/// <summary>Bees finish out of sequence order; a client polling the live run must still get every bee exactly once (the scenario page counts on it).</summary>
public sealed class LiveBeeCursorTests
{
    private static RunManager Manager(IRunStorage storage) =>
        new(new LoadRunner(new ScenarioCatalog([new VariableDelayScenario()])), null!, storage, null, null);

    [Fact]
    public async Task Polling_with_the_bee_index_delivers_every_bee_once_although_they_finish_out_of_order()
    {
        await using var storage = new InMemoryRunStorage();
        var manager = Manager(storage);
        var plan = new LoadPlan
        {
            DurationSeconds = 2, MaxConcurrency = 50, TimeoutSeconds = 5,
            Scenarios = [new ScenarioRunConfig { ScenarioId = "variable-delay", Label = "Variable", RatePerMinute = 900 }],
        };
        await manager.StartAsync(plan, null);

        var received = new List<int>();
        var next = 0;
        string? runId = null;
        LiveRunStatusDto status;
        var finishedOrderDiffers = false;
        for (var i = 0; i < 400; i++)
        {
            status = manager.GetCurrent(afterSeq: received.Count == 0 ? -1 : received.Max(), maxBees: 7, afterIndex: next, forRunId: runId);
            runId = status.RunId;
            foreach (var b in status.NewBees)
            {
                if (received.Count > 0 && b.Sequence < received.Max()) finishedOrderDiffers = true;
                received.Add(b.Sequence);
            }
            next = Math.Max(next, status.NextBeeIndex);
            if (status.State == "Finished" && received.Count == status.Scenarios[0].Finished) break;
            await Task.Delay(30);
        }

        status = manager.GetCurrent();
        Assert.Equal("Finished", status.State);
        Assert.True(received.Count > 10);
        Assert.True(finishedOrderDiffers, "the test needs bees that finish out of order");
        Assert.Equal(status.Scenarios[0].Finished, received.Count);
        Assert.Equal(received.Count, received.Distinct().Count());
    }

    [Fact]
    public async Task An_index_from_another_run_starts_at_the_first_bee()
    {
        await using var storage = new InMemoryRunStorage();
        var manager = Manager(storage);
        await manager.StartAsync(new LoadPlan
        {
            DurationSeconds = 1, MaxConcurrency = 20, TimeoutSeconds = 5,
            Scenarios = [new ScenarioRunConfig { ScenarioId = "variable-delay", Label = "Variable", RatePerMinute = 600 }],
        }, null);
        for (var i = 0; i < 100 && manager.GetCurrent().State != "Finished"; i++) await Task.Delay(50);

        var all = manager.GetCurrent(afterIndex: 0).NewBees.Count;
        var stale = manager.GetCurrent(afterIndex: 9999, forRunId: "an-older-run");
        Assert.True(all > 0);
        Assert.Equal(all, stale.NewBees.Count);
    }

    private sealed class EmptyParams { }

    private sealed class VariableDelayScenario : Scenario<EmptyParams>
    {
        public override string Id => "variable-delay";
        public override string Name => "Variable delay";
        public override string Description => "Test scenario";

        protected override async Task<ScenarioOutcome> ExecuteAsync(EmptyParams parameters, ScenarioContext context, CancellationToken ct)
        {
            await Task.Delay(Random.Shared.Next(5, 400), ct);
            return new ScenarioOutcome(200, 10, null);
        }
    }
}
