using Beesharp.Client.Services;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

/// <summary>The fake run (client simulation) keeps its bees in flight as canceled on stop (brain 0053).</summary>
public sealed class FakeRunCanceledBeesTests
{
    [Fact]
    public async Task Fake_run_turns_the_bees_in_flight_into_canceled_bees_on_stop()
    {
        var source = new FakeRunSource(new ScenarioCatalog([new HttpGetScenario(), new HttpPostJsonScenario(), new CustomRequestScenario(), new UserJourneyScenario()]));
        source.Start();
        await Task.Delay(2500);
        var inFlight = source.Current.Scenarios.Sum(s => s.InFlight);
        Assert.True(inFlight > 0, "the fake run has bees in flight");
        source.Stop();

        var s = source.Current;
        Assert.All(s.Scenarios, v => Assert.Equal(0, v.InFlight));
        var canceled = Enumerable.Range(0, s.Count).SelectMany(i => s.Store.Bees(i)).Where(b => b.Canceled).ToList();
        Assert.True(canceled.Count >= inFlight);
        Assert.All(canceled, b => { Assert.True(b.Failed); Assert.Equal(ExecutionResult.CanceledError, b.Error); });
        var bee = canceled[0];
        var detail = s.Store.Detail(bee.Sequence)!;
        Assert.NotEmpty(detail.Logs);
        Assert.All(detail.Calls.Where(c => c.Failed), c => Assert.Equal(ExecutionResult.CanceledError, c.Error));
        Assert.All(detail.Calls.Where(c => !c.Skipped), c => Assert.True(c.OffsetMs <= bee.DurationMs + 0.01));
        source.Dispose();
    }
}
