using System.Text.Json;
using System.Text.Json.Nodes;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services;

/// <summary>
/// Fluent builder for <see cref="LoadPlan"/> (brain 0058). Supports typed scenario classes
/// (resolved through <see cref="ScenarioCatalog"/> so constructor-injected DI scenarios never need
/// a parameterless constructor) as well as string scenario IDs.
/// </summary>
public sealed class PlanBuilder
{
    private readonly List<Func<ScenarioCatalog?, IReadOnlyList<ScenarioRunConfig>, ScenarioRunConfig>> entries = [];
    private int durationSeconds = 30;
    private int maxConcurrency = 50;
    private int timeoutSeconds = 15;
    private string? preflightScriptId;
    private string? shutdownScriptId;

    public PlanBuilder Duration(TimeSpan duration)
    {
        durationSeconds = Math.Max(1, (int)Math.Round(duration.TotalSeconds));
        return this;
    }

    public PlanBuilder DurationSeconds(int seconds)
    {
        durationSeconds = seconds;
        return this;
    }

    public PlanBuilder MaxConcurrency(int maxConcurrency)
    {
        this.maxConcurrency = maxConcurrency;
        return this;
    }

    public PlanBuilder Timeout(TimeSpan timeout)
    {
        timeoutSeconds = Math.Max(1, (int)Math.Round(timeout.TotalSeconds));
        return this;
    }

    public PlanBuilder TimeoutSeconds(int seconds)
    {
        timeoutSeconds = seconds;
        return this;
    }

    public PlanBuilder Preflight(string? scriptId)
    {
        preflightScriptId = scriptId;
        return this;
    }

    public PlanBuilder Shutdown(string? scriptId)
    {
        shutdownScriptId = scriptId;
        return this;
    }

    /// <summary>Adds a linear-rate entry for a registered scenario using its default parameters.</summary>
    public PlanBuilder Add<TScenario>(double ratePerMinute = 60, string? label = null, int? maxInFlight = null)
        where TScenario : IScenario
    {
        entries.Add((catalog, existing) => CreateTypedEntry<TScenario>(
            catalog, existing, parameters: null, SpawningPattern.Linear, ratePerMinute, beesPerBurst: 10, burstsPerMinute: 4, label, maxInFlight));
        return this;
    }

    /// <summary>Adds a linear-rate entry for a registered scenario with typed parameters.</summary>
    public PlanBuilder Add<TScenario>(object parameters, double ratePerMinute = 60, string? label = null, int? maxInFlight = null)
        where TScenario : IScenario
    {
        ArgumentNullException.ThrowIfNull(parameters);
        entries.Add((catalog, existing) => CreateTypedEntry<TScenario>(
            catalog, existing, parameters, SpawningPattern.Linear, ratePerMinute, beesPerBurst: 10, burstsPerMinute: 4, label, maxInFlight));
        return this;
    }

    /// <summary>Adds a burst-pattern entry for a registered scenario using its default parameters.</summary>
    public PlanBuilder AddBurst<TScenario>(int beesPerBurst, double burstsPerMinute, string? label = null, int? maxInFlight = null)
        where TScenario : IScenario
    {
        entries.Add((catalog, existing) => CreateTypedEntry<TScenario>(
            catalog, existing, parameters: null, SpawningPattern.Bursts, ratePerMinute: 60, beesPerBurst, burstsPerMinute, label, maxInFlight));
        return this;
    }

    /// <summary>Adds a burst-pattern entry for a registered scenario with typed parameters.</summary>
    public PlanBuilder AddBurst<TScenario>(object parameters, int beesPerBurst, double burstsPerMinute, string? label = null, int? maxInFlight = null)
        where TScenario : IScenario
    {
        ArgumentNullException.ThrowIfNull(parameters);
        entries.Add((catalog, existing) => CreateTypedEntry<TScenario>(
            catalog, existing, parameters, SpawningPattern.Bursts, ratePerMinute: 60, beesPerBurst, burstsPerMinute, label, maxInFlight));
        return this;
    }

    /// <summary>Adds a linear-rate entry by scenario ID (for script scenarios or built-ins by string ID).</summary>
    public PlanBuilder Add(string scenarioId, double ratePerMinute = 60, JsonObject? parameters = null, string? label = null, int? maxInFlight = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        entries.Add((catalog, existing) => CreateIdEntry(
            catalog, existing, scenarioId, parameters, SpawningPattern.Linear, ratePerMinute, beesPerBurst: 10, burstsPerMinute: 4, label, maxInFlight));
        return this;
    }

    /// <summary>Adds a burst-pattern entry by scenario ID.</summary>
    public PlanBuilder AddBurst(string scenarioId, int beesPerBurst, double burstsPerMinute, JsonObject? parameters = null, string? label = null, int? maxInFlight = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        entries.Add((catalog, existing) => CreateIdEntry(
            catalog, existing, scenarioId, parameters, SpawningPattern.Bursts, ratePerMinute: 60, beesPerBurst, burstsPerMinute, label, maxInFlight));
        return this;
    }

    public LoadPlan Build(ScenarioCatalog? catalog = null)
    {
        var scenarios = new List<ScenarioRunConfig>(entries.Count);
        foreach (var entryFactory in entries)
            scenarios.Add(entryFactory(catalog, scenarios));

        return new LoadPlan
        {
            Scenarios = scenarios,
            DurationSeconds = durationSeconds,
            MaxConcurrency = maxConcurrency,
            TimeoutSeconds = timeoutSeconds,
            PreflightScriptId = preflightScriptId,
            ShutdownScriptId = shutdownScriptId,
        };
    }

    private static ScenarioRunConfig CreateTypedEntry<TScenario>(
        ScenarioCatalog? catalog,
        IReadOnlyList<ScenarioRunConfig> existing,
        object? parameters,
        SpawningPattern pattern,
        double ratePerMinute,
        int beesPerBurst,
        double burstsPerMinute,
        string? label,
        int? maxInFlight)
        where TScenario : IScenario
    {
        if (catalog is null)
            throw new InvalidOperationException($"A ScenarioCatalog is required to build a plan referencing '{typeof(TScenario).Name}'.");

        var scenario = catalog.All.FirstOrDefault(s => s.GetType() == typeof(TScenario))
            ?? catalog.All.FirstOrDefault(s => typeof(TScenario).IsAssignableFrom(s.GetType()))
            ?? throw new InvalidOperationException($"Scenario '{typeof(TScenario).Name}' is not registered. Call builder.AddScenario<{typeof(TScenario).Name}>().");

        var json = parameters is null
            ? scenario.Spec.DefaultJson()
            : JsonSerializer.SerializeToNode(parameters, parameters.GetType(), ParameterSpec.JsonOptions) as JsonObject
              ?? throw new ArgumentException($"Parameters for '{typeof(TScenario).Name}' must serialize to a JSON object.", nameof(parameters));

        var config = new ScenarioRunConfig
        {
            ScenarioId = scenario.Id,
            Label = ResolveLabel(label, scenario.Name, scenario.Id, existing),
            Pattern = pattern,
            RatePerMinute = ratePerMinute,
            BeesPerBurst = beesPerBurst,
            BurstsPerMinute = burstsPerMinute,
            MaxInFlight = maxInFlight,
            Parameters = json,
        };
        config.Normalize(scenario);
        return config;
    }

    private static ScenarioRunConfig CreateIdEntry(
        ScenarioCatalog? catalog,
        IReadOnlyList<ScenarioRunConfig> existing,
        string scenarioId,
        JsonObject? parameters,
        SpawningPattern pattern,
        double ratePerMinute,
        int beesPerBurst,
        double burstsPerMinute,
        string? label,
        int? maxInFlight)
    {
        var scenario = catalog?.Find(scenarioId);
        var defaultName = scenario?.Name ?? scenarioId;
        var json = parameters is not null
            ? (JsonObject)parameters.DeepClone()
            : scenario?.Spec.DefaultJson() ?? new JsonObject();

        var config = new ScenarioRunConfig
        {
            ScenarioId = scenario?.Id ?? scenarioId,
            Label = ResolveLabel(label, defaultName, scenario?.Id ?? scenarioId, existing),
            Pattern = pattern,
            RatePerMinute = ratePerMinute,
            BeesPerBurst = beesPerBurst,
            BurstsPerMinute = burstsPerMinute,
            MaxInFlight = maxInFlight,
            Parameters = json,
        };
        if (scenario is not null) config.Normalize(scenario);
        return config;
    }

    private static string ResolveLabel(string? explicitLabel, string defaultName, string scenarioId, IReadOnlyList<ScenarioRunConfig> existing)
    {
        if (!string.IsNullOrWhiteSpace(explicitLabel)) return explicitLabel.Trim();
        var same = existing.Count(s => string.Equals(s.ScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase));
        return same == 0 ? defaultName : $"{defaultName} #{same + 1}";
    }
}
