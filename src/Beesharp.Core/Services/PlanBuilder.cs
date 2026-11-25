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
    private Func<IEnumerable<IPreflightProgram>?, string?>? preflightResolver;
    private string? shutdownScriptId;
    private Func<IEnumerable<IShutdownProgram>?, string?>? shutdownResolver;

    /// <summary>Sets the total run duration from a <see cref="TimeSpan"/>, rounded to at least 1 second.</summary>
    /// <param name="duration">Total run duration.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder Duration(TimeSpan duration)
    {
        durationSeconds = Math.Max(1, (int)Math.Round(duration.TotalSeconds));
        return this;
    }

    /// <summary>Sets the total run duration in seconds.</summary>
    /// <param name="seconds">Run duration in seconds.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder DurationSeconds(int seconds)
    {
        durationSeconds = seconds;
        return this;
    }

    /// <summary>Sets the plan-wide maximum number of concurrent bees in flight.</summary>
    /// <param name="maxConcurrency">Maximum concurrent executions allowed across the run.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder MaxConcurrency(int maxConcurrency)
    {
        this.maxConcurrency = maxConcurrency;
        return this;
    }

    /// <summary>Sets the per-call/per-execution timeout from a <see cref="TimeSpan"/>, rounded to at least 1 second.</summary>
    /// <param name="timeout">Timeout duration.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder Timeout(TimeSpan timeout)
    {
        timeoutSeconds = Math.Max(1, (int)Math.Round(timeout.TotalSeconds));
        return this;
    }

    /// <summary>Sets the per-call/per-execution timeout in seconds.</summary>
    /// <param name="seconds">Timeout duration in seconds.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder TimeoutSeconds(int seconds)
    {
        timeoutSeconds = seconds;
        return this;
    }

    /// <summary>Sets the optional preflight script ID to execute before scenarios start.</summary>
    /// <param name="scriptId">Preflight program or script ID, or <see langword="null"/> to clear.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder Preflight(string? scriptId)
    {
        preflightScriptId = scriptId;
        preflightResolver = null;
        return this;
    }

    /// <summary>Sets the preflight program by compiled C# class (resolved from registered <see cref="IPreflightProgram"/> instances).</summary>
    /// <typeparam name="TPreflight">Registered preflight program type.</typeparam>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder Preflight<TPreflight>() where TPreflight : class, IPreflightProgram
    {
        preflightScriptId = null;
        preflightResolver = programs => ResolveProgramId<TPreflight, IPreflightProgram>(programs, "Preflight", "AddPreflight");
        return this;
    }

    /// <summary>Sets the optional shutdown script ID to execute at the end of the run.</summary>
    /// <param name="scriptId">Shutdown program or script ID, or <see langword="null"/> to clear.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder Shutdown(string? scriptId)
    {
        shutdownScriptId = scriptId;
        shutdownResolver = null;
        return this;
    }

    /// <summary>Sets the shutdown program by compiled C# class (resolved from registered <see cref="IShutdownProgram"/> instances).</summary>
    /// <typeparam name="TShutdown">Registered shutdown program type.</typeparam>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder Shutdown<TShutdown>() where TShutdown : class, IShutdownProgram
    {
        shutdownScriptId = null;
        shutdownResolver = programs => ResolveProgramId<TShutdown, IShutdownProgram>(programs, "Shutdown", "AddShutdown");
        return this;
    }

    /// <summary>Adds a linear-rate entry for a registered scenario using its default parameters.</summary>
    /// <typeparam name="TScenario">Registered scenario type.</typeparam>
    /// <param name="ratePerMinute">Target arrival rate in bees per minute.</param>
    /// <param name="label">Optional display label for the entry; when omitted, derived from the scenario name.</param>
    /// <param name="maxInFlight">Optional concurrency cap for this entry; <see langword="null"/> uses the plan default.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder Add<TScenario>(double ratePerMinute = 60, string? label = null, int? maxInFlight = null)
        where TScenario : IScenario
    {
        entries.Add((catalog, existing) => CreateTypedEntry<TScenario>(
            catalog, existing, parameters: null, SpawningPattern.Linear, ratePerMinute, beesPerBurst: 10, burstsPerMinute: 4, label, maxInFlight));
        return this;
    }

    /// <summary>Adds a linear-rate entry for a registered scenario with typed parameters.</summary>
    /// <typeparam name="TScenario">Registered scenario type.</typeparam>
    /// <param name="parameters">Typed parameter object serialized to the entry's JSON configuration.</param>
    /// <param name="ratePerMinute">Target arrival rate in bees per minute.</param>
    /// <param name="label">Optional display label for the entry; when omitted, derived from the scenario name.</param>
    /// <param name="maxInFlight">Optional concurrency cap for this entry; <see langword="null"/> uses the plan default.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder Add<TScenario>(object parameters, double ratePerMinute = 60, string? label = null, int? maxInFlight = null)
        where TScenario : IScenario
    {
        ArgumentNullException.ThrowIfNull(parameters);
        entries.Add((catalog, existing) => CreateTypedEntry<TScenario>(
            catalog, existing, parameters, SpawningPattern.Linear, ratePerMinute, beesPerBurst: 10, burstsPerMinute: 4, label, maxInFlight));
        return this;
    }

    /// <summary>Adds a burst-pattern entry for a registered scenario using its default parameters.</summary>
    /// <typeparam name="TScenario">Registered scenario type.</typeparam>
    /// <param name="beesPerBurst">Number of bees spawned simultaneously in each burst.</param>
    /// <param name="burstsPerMinute">Number of bursts triggered per minute.</param>
    /// <param name="label">Optional display label for the entry; when omitted, derived from the scenario name.</param>
    /// <param name="maxInFlight">Optional concurrency cap for this entry; <see langword="null"/> uses the plan default.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder AddBurst<TScenario>(int beesPerBurst, double burstsPerMinute, string? label = null, int? maxInFlight = null)
        where TScenario : IScenario
    {
        entries.Add((catalog, existing) => CreateTypedEntry<TScenario>(
            catalog, existing, parameters: null, SpawningPattern.Bursts, ratePerMinute: 60, beesPerBurst, burstsPerMinute, label, maxInFlight));
        return this;
    }

    /// <summary>Adds a burst-pattern entry for a registered scenario with typed parameters.</summary>
    /// <typeparam name="TScenario">Registered scenario type.</typeparam>
    /// <param name="parameters">Typed parameter object serialized to the entry's JSON configuration.</param>
    /// <param name="beesPerBurst">Number of bees spawned simultaneously in each burst.</param>
    /// <param name="burstsPerMinute">Number of bursts triggered per minute.</param>
    /// <param name="label">Optional display label for the entry; when omitted, derived from the scenario name.</param>
    /// <param name="maxInFlight">Optional concurrency cap for this entry; <see langword="null"/> uses the plan default.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder AddBurst<TScenario>(object parameters, int beesPerBurst, double burstsPerMinute, string? label = null, int? maxInFlight = null)
        where TScenario : IScenario
    {
        ArgumentNullException.ThrowIfNull(parameters);
        entries.Add((catalog, existing) => CreateTypedEntry<TScenario>(
            catalog, existing, parameters, SpawningPattern.Bursts, ratePerMinute: 60, beesPerBurst, burstsPerMinute, label, maxInFlight));
        return this;
    }

    /// <summary>Adds a linear-rate entry by scenario ID (for script scenarios or built-ins by string ID).</summary>
    /// <param name="scenarioId">Identifier of the scenario to run.</param>
    /// <param name="ratePerMinute">Target arrival rate in bees per minute.</param>
    /// <param name="parameters">Optional JSON parameter overrides; cloned when provided.</param>
    /// <param name="label">Optional display label for the entry; when omitted, derived from the scenario name or ID.</param>
    /// <param name="maxInFlight">Optional concurrency cap for this entry; <see langword="null"/> uses the plan default.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder Add(string scenarioId, double ratePerMinute = 60, JsonObject? parameters = null, string? label = null, int? maxInFlight = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        entries.Add((catalog, existing) => CreateIdEntry(
            catalog, existing, scenarioId, parameters, SpawningPattern.Linear, ratePerMinute, beesPerBurst: 10, burstsPerMinute: 4, label, maxInFlight));
        return this;
    }

    /// <summary>Adds a burst-pattern entry by scenario ID.</summary>
    /// <param name="scenarioId">Identifier of the scenario to run.</param>
    /// <param name="beesPerBurst">Number of bees spawned simultaneously in each burst.</param>
    /// <param name="burstsPerMinute">Number of bursts triggered per minute.</param>
    /// <param name="parameters">Optional JSON parameter overrides; cloned when provided.</param>
    /// <param name="label">Optional display label for the entry; when omitted, derived from the scenario name or ID.</param>
    /// <param name="maxInFlight">Optional concurrency cap for this entry; <see langword="null"/> uses the plan default.</param>
    /// <returns>This builder instance for chaining.</returns>
    public PlanBuilder AddBurst(string scenarioId, int beesPerBurst, double burstsPerMinute, JsonObject? parameters = null, string? label = null, int? maxInFlight = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        entries.Add((catalog, existing) => CreateIdEntry(
            catalog, existing, scenarioId, parameters, SpawningPattern.Bursts, ratePerMinute: 60, beesPerBurst, burstsPerMinute, label, maxInFlight));
        return this;
    }

    /// <summary>Materializes the configured <see cref="LoadPlan"/>, resolving any typed scenarios or program classes.</summary>
    /// <param name="catalog">Scenario catalog used to resolve typed scenario entries and normalize parameters.</param>
    /// <param name="preflights">Registered preflight programs used when <see cref="Preflight{TPreflight}"/> was called.</param>
    /// <param name="shutdowns">Registered shutdown programs used when <see cref="Shutdown{TShutdown}"/> was called.</param>
    /// <returns>The constructed <see cref="LoadPlan"/>.</returns>
    public LoadPlan Build(
        ScenarioCatalog? catalog = null,
        IEnumerable<IPreflightProgram>? preflights = null,
        IEnumerable<IShutdownProgram>? shutdowns = null)
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
            PreflightScriptId = preflightResolver is not null ? preflightResolver(preflights) : preflightScriptId,
            ShutdownScriptId = shutdownResolver is not null ? shutdownResolver(shutdowns) : shutdownScriptId,
        };
    }

    private static string ResolveProgramId<TProgram, TInterface>(
        IEnumerable<TInterface>? programs,
        string kindLabel,
        string registrationMethod)
        where TProgram : class, TInterface
        where TInterface : IRunProgram
    {
        if (programs is null)
            throw new InvalidOperationException($"Registered {kindLabel.ToLowerInvariant()} programs are required to build a plan referencing '{typeof(TProgram).Name}'.");

        var list = programs as IReadOnlyCollection<TInterface> ?? programs.ToList();
        var program = list.FirstOrDefault(p => p.GetType() == typeof(TProgram))
            ?? list.FirstOrDefault(p => typeof(TProgram).IsAssignableFrom(p.GetType()))
            ?? throw new InvalidOperationException($"{kindLabel} program '{typeof(TProgram).Name}' is not registered. Call builder.{registrationMethod}<{typeof(TProgram).Name}>().");

        return program.Id;
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
