using System.Text.Json.Nodes;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Services;

/// <summary>
/// What to run: a mix of scenarios, each with its own parameters and target rate. This is also the shape
/// stored in presets, so keep it a plain, JSON-serialisable class. See docs/brain/0004 and 0005.
/// </summary>
public sealed class LoadPlan
{
    public const int MaxEntries = 7; // one per categorical colour slot, see docs/brain/0002
    public const double MaxRatePerMinute = 60_000;

    public List<ScenarioRunConfig> Scenarios { get; set; } = [];
    public int DurationSeconds { get; set; } = 30;
    public int MaxConcurrency { get; set; } = 50;
    public int TimeoutSeconds { get; set; } = 30;

    public LoadPlan Clone() => new()
    {
        Scenarios = Scenarios.Select(s => s.Clone()).ToList(),
        DurationSeconds = DurationSeconds,
        MaxConcurrency = MaxConcurrency,
        TimeoutSeconds = TimeoutSeconds,
    };

    public List<string> Validate(ScenarioCatalog catalog)
    {
        var errors = new List<string>();
        if (Scenarios.Count == 0) errors.Add("Add at least one scenario.");
        if (Scenarios.Count > MaxEntries) errors.Add($"At most {MaxEntries} scenarios per plan.");
        if (DurationSeconds is < 1 or > 24 * 3600) errors.Add("Duration must be between 1 second and 24 hours.");
        if (MaxConcurrency is < 1 or > 1000) errors.Add("Max in-flight must be between 1 and 1000.");
        if (TimeoutSeconds is < 1 or > 300) errors.Add("Timeout must be between 1 and 300 seconds.");

        foreach (var entry in Scenarios)
        {
            var scenario = catalog.Find(entry.ScenarioId);
            var label = string.IsNullOrWhiteSpace(entry.Label) ? entry.ScenarioId : entry.Label;
            if (scenario is null) { errors.Add($"{label}: unknown scenario '{entry.ScenarioId}'."); continue; }
            if (entry.RatePerMinute is <= 0 or > MaxRatePerMinute || double.IsNaN(entry.RatePerMinute))
                errors.Add($"{label}: actors per minute must be between 0 and {MaxRatePerMinute:N0}.");
            foreach (var err in scenario.Spec.Validate(entry.Parameters))
                errors.Add($"{label}: {err}");
        }
        return errors;
    }
}

public sealed class ScenarioRunConfig
{
    public string ScenarioId { get; set; } = "";
    public string Label { get; set; } = "";
    public double RatePerMinute { get; set; } = 60;
    /// <summary>Parameter values as stored in presets. Shape is defined by the scenario's parameter class (brain 0007).</summary>
    public JsonObject Parameters { get; set; } = new();

    public ScenarioRunConfig Clone() => new()
    {
        ScenarioId = ScenarioId,
        Label = Label,
        RatePerMinute = RatePerMinute,
        Parameters = (JsonObject)Parameters.DeepClone(),
    };

    public static ScenarioRunConfig CreateFor(IScenario scenario) => new()
    {
        ScenarioId = scenario.Id,
        Label = scenario.Name,
        Parameters = scenario.Spec.DefaultJson(),
    };

    /// <summary>
    /// Brings stored values into the scenario's current shape: adds defaults for new parameters, drops removed ones
    /// and converts legacy values (e.g. a multiline string into a list). Anything unreadable goes to <paramref name="problems"/>.
    /// </summary>
    public void Normalize(IScenario scenario, List<string>? problems = null) =>
        Parameters = scenario.Spec.Normalize(Parameters, problems);
}
