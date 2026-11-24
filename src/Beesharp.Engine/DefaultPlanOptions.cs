using System.Text.Json.Nodes;

namespace Beesharp.Services;

/// <summary>
/// Startup default plan configuration (brain 0058): can load a named preset (<see cref="DefaultPreset"/>)
/// or construct an inline <see cref="LoadPlan"/> (<see cref="Scenarios"/>), plus an optional
/// <see cref="DefaultEnvironment"/>. Typically bound from the <c>Plan</c> configuration section.
/// </summary>
public sealed class DefaultPlanOptions
{
    public string? DefaultPreset { get; set; }
    public string? DefaultEnvironment { get; set; }
    /// <summary>When true, applies the default preset or inline plan even if the workspace already has entries (e.g. from a restored run).</summary>
    public bool ForceDefault { get; set; }

    public int? DurationSeconds { get; set; }
    public int? MaxConcurrency { get; set; }
    public int? TimeoutSeconds { get; set; }
    public string? PreflightScriptId { get; set; }
    public string? ShutdownScriptId { get; set; }
    public List<DefaultScenarioEntryOptions>? Scenarios { get; set; }
}

public sealed class DefaultScenarioEntryOptions
{
    public string ScenarioId { get; set; } = "";
    public string? Label { get; set; }
    public double? RatePerMinute { get; set; }
    public SpawningPattern? Pattern { get; set; }
    public int? BeesPerBurst { get; set; }
    public double? BurstsPerMinute { get; set; }
    public int? MaxInFlight { get; set; }
    public JsonObject? Parameters { get; set; }
}
