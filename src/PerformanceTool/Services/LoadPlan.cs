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
                errors.Add($"{label}: runs per minute must be between 0 and {MaxRatePerMinute:N0}.");
            foreach (var p in scenario.Parameters)
                if (p.Validate(entry.Parameters.GetValueOrDefault(p.Key)) is { } err)
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
    public Dictionary<string, string> Parameters { get; set; } = [];

    public ScenarioRunConfig Clone() => new()
    {
        ScenarioId = ScenarioId,
        Label = Label,
        RatePerMinute = RatePerMinute,
        Parameters = new Dictionary<string, string>(Parameters),
    };

    public static ScenarioRunConfig CreateFor(IScenario scenario) => new()
    {
        ScenarioId = scenario.Id,
        Label = scenario.Name,
        Parameters = scenario.Parameters.ToDictionary(p => p.Key, p => p.DefaultValue),
    };

    /// <summary>Adds defaults for parameters missing from an older preset and drops ones the scenario no longer has.</summary>
    public void Normalize(IScenario scenario) =>
        Parameters = scenario.Parameters.ToDictionary(p => p.Key, p => Parameters.GetValueOrDefault(p.Key) ?? p.DefaultValue);
}
