using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Services;

/// <summary>
/// What to run: a mix of scenarios, each with its own parameters and target rate. This is also the shape
/// stored in presets, so keep it a plain, JSON-serialisable class. See docs/brain/0004 and 0005.
/// </summary>
public sealed class LoadPlan
{
    public const int MaxEntries = 7;
    public const double MaxRatePerMinute = 60_000;
    public const int MaxActorsPerBurst = 1000;

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
            if (entry.Pattern == SpawningPattern.Bursts)
            {
                if (entry.ActorsPerBurst is < 1 or > MaxActorsPerBurst)
                    errors.Add($"{label}: actors per burst must be between 1 and {MaxActorsPerBurst:N0}.");
                if (entry.BurstsPerMinute <= 0 || double.IsNaN(entry.BurstsPerMinute))
                    errors.Add($"{label}: bursts per minute must be greater than 0.");
                else if (entry.EffectiveRatePerMinute() > MaxRatePerMinute)
                    errors.Add($"{label}: actors per burst × bursts per minute must not exceed {MaxRatePerMinute:N0} actors per minute.");
            }
            else if (entry.RatePerMinute is <= 0 or > MaxRatePerMinute || double.IsNaN(entry.RatePerMinute))
                errors.Add($"{label}: actors per minute must be between 0 and {MaxRatePerMinute:N0}.");
            foreach (var err in scenario.Spec.Validate(entry.Parameters))
                errors.Add($"{label}: {err}");
        }
        return errors;
    }
}

/// <summary>How a scenario entry starts new actors over time (brain 0034).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpawningPattern
{
    /// <summary>Evenly spread: <see cref="ScenarioRunConfig.RatePerMinute"/> actors per minute. The default.</summary>
    Linear,
    /// <summary>In clumps: <see cref="ScenarioRunConfig.ActorsPerBurst"/> actors at once, <see cref="ScenarioRunConfig.BurstsPerMinute"/> times per minute.</summary>
    Bursts,
}

public sealed class ScenarioRunConfig
{
    public string ScenarioId { get; set; } = "";
    public string Label { get; set; } = "";
    public double RatePerMinute { get; set; } = 60;
    /// <summary>Spawning pattern (brain 0034). Missing in old presets = <see cref="SpawningPattern.Linear"/>.</summary>
    public SpawningPattern Pattern { get; set; } = SpawningPattern.Linear;
    /// <summary>Bursts only: actors started together by each burst.</summary>
    public int ActorsPerBurst { get; set; } = 10;
    /// <summary>Bursts only: how many bursts per minute.</summary>
    public double BurstsPerMinute { get; set; } = 4;

    /// <summary>Actors per minute the entry aims at: <see cref="RatePerMinute"/> for linear, actors per burst × bursts per minute for bursts.</summary>
    public double EffectiveRatePerMinute() => Pattern == SpawningPattern.Bursts ? ActorsPerBurst * BurstsPerMinute : RatePerMinute;
    /// <summary>Parameter values as stored in presets. Shape is defined by the scenario's parameter class (brain 0007).</summary>
    public JsonObject Parameters { get; set; } = new();

    public ScenarioRunConfig Clone() => new()
    {
        ScenarioId = ScenarioId,
        Label = Label,
        RatePerMinute = RatePerMinute,
        Pattern = Pattern,
        ActorsPerBurst = ActorsPerBurst,
        BurstsPerMinute = BurstsPerMinute,
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
