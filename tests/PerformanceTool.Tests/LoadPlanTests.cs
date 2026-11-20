using System.Text.Json;
using PerformanceTool.Services;
using PerformanceTool.Services.Scenarios;
using PerformanceTool.Services.Scenarios.BuiltIn;

namespace PerformanceTool.Tests;

public class LoadPlanTests
{
    private static readonly ScenarioCatalog Catalog = new(
        [new HttpGetScenario(), new HttpPostJsonScenario(), new CustomRequestScenario(), new UserJourneyScenario()]);

    // Shape of a preset saved before parameters were typed (all values were strings).
    private const string LegacyPresetPlan = """
    {
      "scenarios": [
        { "scenarioId": "http-custom", "label": "Flaky API", "ratePerMinute": 180,
          "parameters": { "method": "GET", "url": "http://localhost:8099/flaky", "headers": "Accept: */*", "contentType": "application/json", "body": "" } },
        { "scenarioId": "user-journey", "label": "User journey", "ratePerMinute": 120,
          "parameters": { "urls": "http://localhost:8099/fast\nhttp://localhost:8099/slow" } }
      ],
      "durationSeconds": 8, "maxConcurrency": 50, "timeoutSeconds": 30
    }
    """;

    [Fact]
    public void Legacy_presets_load_and_validate_after_normalising()
    {
        var plan = JsonSerializer.Deserialize<LoadPlan>(LegacyPresetPlan, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var problems = new List<string>();
        foreach (var entry in plan.Scenarios) entry.Normalize(Catalog.Find(entry.ScenarioId)!, problems);

        Assert.Empty(problems);
        Assert.Empty(plan.Validate(Catalog));
        Assert.Equal("*/*", plan.Scenarios[0].Parameters["headers"]!["Accept"]!.GetValue<string>());
        Assert.Equal(2, plan.Scenarios[1].Parameters["urls"]!.AsArray().Count);
    }

    [Fact]
    public void Validation_reports_scenario_label_and_field()
    {
        var plan = new LoadPlan { Scenarios = [ScenarioRunConfig.CreateFor(Catalog.Find("http-get")!)] };
        plan.Scenarios[0].Parameters["url"] = "ftp://nope";
        plan.Scenarios[0].RatePerMinute = 0;
        var errors = plan.Validate(Catalog);
        Assert.Contains(errors, e => e.StartsWith("HTTP GET:") && e.Contains("http(s) URL"));
        Assert.Contains(errors, e => e.Contains("runs per minute"));
    }

    [Fact]
    public void Clone_is_deep()
    {
        var plan = new LoadPlan { Scenarios = [ScenarioRunConfig.CreateFor(Catalog.Find("http-get")!)] };
        var copy = plan.Clone();
        copy.Scenarios[0].Parameters["url"] = "https://changed.test";
        Assert.Equal("https://example.com", plan.Scenarios[0].Parameters["url"]!.GetValue<string>());
    }
}
