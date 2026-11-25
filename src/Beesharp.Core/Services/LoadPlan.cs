using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Beesharp.Services.Scenarios;

namespace Beesharp.Services;

/// <summary>
/// What to run: a mix of scenarios, each with its own parameters and target rate. This is also the shape
/// stored in presets, so keep it a plain, JSON-serialisable class. See docs/brain/0004 and 0005.
/// </summary>
public sealed class LoadPlan
{
    /// <summary>Maximum allowed target rate per minute for a scenario entry.</summary>
    public const double MaxRatePerMinute = 60_000;
    /// <summary>Maximum allowed number of bees started together in a single burst.</summary>
    public const int MaxBeesPerBurst = 1000;

    /// <summary>Scenario entries included in this plan.</summary>
    public List<ScenarioRunConfig> Scenarios { get; set; } = [];
    /// <summary>Total run duration in seconds.</summary>
    public int DurationSeconds { get; set; } = 30;
    /// <summary>Plan-wide default maximum number of concurrent bees in flight.</summary>
    public int MaxConcurrency { get; set; } = 50;
    /// <summary>Per-call/per-execution timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Default request/response header allowlist captured on slow or failed HTTP calls when <see cref="CaptureHeaders"/> is null.</summary>
    public static readonly IReadOnlyList<string> DefaultCaptureHeaders = ["X-Correlation-Id", "traceparent", "Server-Timing"];

    /// <summary>Optional preflight script ID that must complete successfully before scenarios start.</summary>
    public string? PreflightScriptId { get; set; }
    /// <summary>Optional shutdown program script ID. It always runs at the end of the run (brain 0052): after a normal finish, after a stop, and after a failed preflight.</summary>
    public string? ShutdownScriptId { get; set; }
    /// <summary>
    /// Optional allowlist of request/response header names captured on slow or failed HTTP calls.
    /// Null uses <see cref="DefaultCaptureHeaders"/>; an empty list disables header capture.
    /// </summary>
    public List<string>? CaptureHeaders { get; set; }
    /// <summary>
    /// When true, <see cref="RecordingHttpHandler"/> measures time-to-first-byte (<c>TtfbMs</c>) separately from response body read (<c>ResponseReadMs</c>).
    /// </summary>
    public bool MeasureHttpTimingSplit { get; set; }

    /// <summary>Creates a deep copy of this load plan and all its scenario entries.</summary>
    /// <returns>A cloned <see cref="LoadPlan"/> instance.</returns>
    public LoadPlan Clone() => new()
    {
        Scenarios = Scenarios.Select(s => s.Clone()).ToList(),
        DurationSeconds = DurationSeconds,
        MaxConcurrency = MaxConcurrency,
        TimeoutSeconds = TimeoutSeconds,
        PreflightScriptId = PreflightScriptId,
        ShutdownScriptId = ShutdownScriptId,
        CaptureHeaders = CaptureHeaders is null ? null : [.. CaptureHeaders],
        MeasureHttpTimingSplit = MeasureHttpTimingSplit,
    };

    /// <summary>Validates this load plan and all scenario entries against the supplied catalog.</summary>
    /// <param name="catalog">Scenario catalog used to look up scenarios and validate entry parameters.</param>
    /// <returns>A list of validation error messages; empty when the plan is valid.</returns>
    public List<string> Validate(ScenarioCatalog catalog)
    {
        var errors = new List<string>();
        if (Scenarios.Count == 0) errors.Add("Add at least one scenario.");
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
                if (entry.BeesPerBurst is < 1 or > MaxBeesPerBurst)
                    errors.Add($"{label}: bees per burst must be between 1 and {MaxBeesPerBurst:N0}.");
                if (entry.BurstsPerMinute <= 0 || double.IsNaN(entry.BurstsPerMinute))
                    errors.Add($"{label}: bursts per minute must be greater than 0.");
                else if (entry.EffectiveRatePerMinute() > MaxRatePerMinute)
                    errors.Add($"{label}: bees per burst × bursts per minute must not exceed {MaxRatePerMinute:N0} bees per minute.");
            }
            else if (entry.RatePerMinute is <= 0 or > MaxRatePerMinute || double.IsNaN(entry.RatePerMinute))
                errors.Add($"{label}: bees per minute must be between 0 and {MaxRatePerMinute:N0}.");
            if (entry.MaxInFlight is < 1 or > 1000)
                errors.Add($"{label}: max in-flight must be between 1 and 1000.");
            foreach (var err in scenario.Spec.Validate(entry.Parameters))
                errors.Add($"{label}: {err}");
        }
        return errors;
    }
}

/// <summary>How a scenario entry starts new bees over time (brain 0034).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpawningPattern
{
    /// <summary>Evenly spread: <see cref="ScenarioRunConfig.RatePerMinute"/> bees per minute. The default.</summary>
    Linear,
    /// <summary>In clumps: <see cref="ScenarioRunConfig.BeesPerBurst"/> bees at once, <see cref="ScenarioRunConfig.BurstsPerMinute"/> times per minute.</summary>
    Bursts,
}

/// <summary>Configuration for one scenario entry inside a <see cref="LoadPlan"/>.</summary>
public sealed class ScenarioRunConfig
{
    /// <summary>Identifier of the scenario to execute.</summary>
    public string ScenarioId { get; set; } = "";
    /// <summary>Display label for this entry in the plan and run views.</summary>
    public string Label { get; set; } = "";
    /// <summary>Target arrival rate in bees per minute when <see cref="Pattern"/> is <see cref="SpawningPattern.Linear"/>.</summary>
    public double RatePerMinute { get; set; } = 60;
    /// <summary>Spawning pattern (brain 0034). Missing in old presets = <see cref="SpawningPattern.Linear"/>.</summary>
    public SpawningPattern Pattern { get; set; } = SpawningPattern.Linear;
    /// <summary>Bursts only: bees started together by each burst.</summary>
    public int BeesPerBurst { get; set; } = 10;
    /// <summary>Bursts only: how many bursts per minute.</summary>
    public double BurstsPerMinute { get; set; } = 4;
    /// <summary>Maximum concurrent bees allowed in flight for this scenario (null = fall back to plan default).</summary>
    public int? MaxInFlight { get; set; }

    /// <summary>Bees per minute the entry aims at: <see cref="RatePerMinute"/> for linear, bees per burst × bursts per minute for bursts.</summary>
    /// <returns>Effective target rate in bees per minute.</returns>
    public double EffectiveRatePerMinute() => Pattern == SpawningPattern.Bursts ? BeesPerBurst * BurstsPerMinute : RatePerMinute;
    /// <summary>Parameter values as stored in presets. Shape is defined by the scenario's parameter class (brain 0007).</summary>
    public JsonObject Parameters { get; set; } = new();

    /// <summary>Creates a deep copy of this scenario entry configuration.</summary>
    /// <returns>A cloned <see cref="ScenarioRunConfig"/> instance.</returns>
    public ScenarioRunConfig Clone() => new()
    {
        ScenarioId = ScenarioId,
        Label = Label,
        RatePerMinute = RatePerMinute,
        Pattern = Pattern,
        BeesPerBurst = BeesPerBurst,
        BurstsPerMinute = BurstsPerMinute,
        MaxInFlight = MaxInFlight,
        Parameters = (JsonObject)Parameters.DeepClone(),
    };

    /// <summary>Creates a default <see cref="ScenarioRunConfig"/> for the specified scenario.</summary>
    /// <param name="scenario">Scenario whose ID, name, and default parameters populate the entry.</param>
    /// <returns>A new <see cref="ScenarioRunConfig"/> initialized with the scenario's defaults.</returns>
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
    /// <param name="scenario">Scenario defining the target parameter schema.</param>
    /// <param name="problems">Optional list that receives messages for unreadable or dropped parameter values.</param>
    public void Normalize(IScenario scenario, List<string>? problems = null) =>
        Parameters = scenario.Spec.Normalize(Parameters, problems);
}
