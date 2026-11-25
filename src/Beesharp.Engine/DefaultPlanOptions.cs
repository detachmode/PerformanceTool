using System.Text.Json.Nodes;

namespace Beesharp.Services;

/// <summary>
/// Startup default plan configuration (brain 0058): can load a named preset (<see cref="DefaultPreset"/>)
/// or construct an inline <see cref="LoadPlan"/> (<see cref="Scenarios"/>), plus an optional
/// <see cref="DefaultEnvironment"/>. Typically bound from the <c>Plan</c> configuration section.
/// </summary>
public sealed class DefaultPlanOptions
{
    /// <summary>Optional name of a saved or code-defined preset to load at startup.</summary>
    public string? DefaultPreset { get; set; }

    /// <summary>Optional name of the environment to select by default when starting runs.</summary>
    public string? DefaultEnvironment { get; set; }

    /// <summary>When true, applies the default preset or inline plan even if the workspace already has entries (e.g. from a restored run).</summary>
    public bool ForceDefault { get; set; }

    /// <summary>Default run duration in seconds when building an inline plan from <see cref="Scenarios"/>.</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Default maximum concurrent executions across the plan when building an inline plan.</summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>Default per-execution timeout in seconds when building an inline plan.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>Optional preflight script ID to attach to the inline plan.</summary>
    public string? PreflightScriptId { get; set; }

    /// <summary>Optional shutdown script ID to attach to the inline plan.</summary>
    public string? ShutdownScriptId { get; set; }

    /// <summary>Inline scenario entries used to build a default <see cref="LoadPlan"/> when no <see cref="DefaultPreset"/> is loaded.</summary>
    public List<DefaultScenarioEntryOptions>? Scenarios { get; set; }
}

/// <summary>
/// Configuration for a single scenario entry inside <see cref="DefaultPlanOptions.Scenarios"/>.
/// </summary>
public sealed class DefaultScenarioEntryOptions
{
    /// <summary>Identifier of the scenario to run.</summary>
    public string ScenarioId { get; set; } = "";

    /// <summary>Optional display label for the scenario entry; defaults to the scenario name.</summary>
    public string? Label { get; set; }

    /// <summary>Optional target launch rate in bees per minute for <see cref="SpawningPattern.Linear"/> mode.</summary>
    public double? RatePerMinute { get; set; }

    /// <summary>Optional spawning pattern (<see cref="SpawningPattern.Linear"/> or <see cref="SpawningPattern.Bursts"/>).</summary>
    public SpawningPattern? Pattern { get; set; }

    /// <summary>Optional number of bees launched per burst when <see cref="Pattern"/> is <see cref="SpawningPattern.Bursts"/>.</summary>
    public int? BeesPerBurst { get; set; }

    /// <summary>Optional burst frequency in bursts per minute when <see cref="Pattern"/> is <see cref="SpawningPattern.Bursts"/>.</summary>
    public double? BurstsPerMinute { get; set; }

    /// <summary>Optional per-entry cap on concurrent in-flight bees.</summary>
    public int? MaxInFlight { get; set; }

    /// <summary>Optional scenario parameter overrides as a JSON object.</summary>
    public JsonObject? Parameters { get; set; }
}
