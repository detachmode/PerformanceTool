using Beesharp.Cli;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Cli.Tests;

public class CliStepsSummaryTests
{
    [Fact]
    public void Summary_text_and_json_include_failed_per_step()
    {
        var plan = new LoadPlan
        {
            DurationSeconds = 5,
            Scenarios = [new ScenarioRunConfig { ScenarioId = "checkout", Label = "Checkout", RatePerMinute = 60 }],
        };
        var status = new LiveRunStatusDto(
            "run-1", "Finished", 5.0, DateTimeOffset.UtcNow, plan,
            [new LiveScenarioViewDto(5, 5, 0, 2, 0, 0, 0, FailedPerStep: [0, 2])],
            [], 4, 0, "Memory", null,
            Steps: [[new StepInfo("Log in"), new StepInfo("Pay")]]);
        var bees = new[]
        {
            new LiveBeeDto(0, 0, 0.1, 50, 200, 10, null, 0),
            new LiveBeeDto(1, 0, 0.2, 60, 500, 0, "declined", 0, FailedStep: 1, FailedRep: 0),
            new LiveBeeDto(2, 0, 0.3, 60, 500, 0, "declined", 0, FailedStep: 1, FailedRep: 0),
        };

        var summary = RunSummary.Build("smoke", null, status, bees, stopped: false, savedTo: null);
        var text = summary.ToText();
        var json = summary.ToJson();

        Assert.Contains("failed at Pay: 2", text);
        Assert.Contains("\"failedPerStep\"", json);
        Assert.Equal("Pay", summary.Entries[0].FailedPerStep![0].Step);
        Assert.Equal(2, summary.Entries[0].FailedPerStep![0].Count);
    }
}
