using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Services.Scenarios;

/// <summary>
/// All scenarios registered in DI plus any dynamically compiled C# script scenarios (brain 0040),
/// looked up by <see cref="IScenario.Id"/>.
/// </summary>
public sealed class ScenarioCatalog
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, IScenario> byId;
    private readonly HashSet<string> builtInIds;
    private IReadOnlyList<IScenario> ordered;

    /// <summary>Initializes the catalog with the built-in scenarios registered in dependency injection.</summary>
    /// <param name="scenarios">Built-in scenario instances; duplicate <see cref="IScenario.Id"/> values throw <see cref="InvalidOperationException"/>.</param>
    public ScenarioCatalog(IEnumerable<IScenario> scenarios)
    {
        byId = new Dictionary<string, IScenario>(StringComparer.OrdinalIgnoreCase);
        builtInIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in scenarios)
        {
            if (!byId.TryAdd(s.Id, s))
                throw new InvalidOperationException($"Duplicate scenario id '{s.Id}'.");
            builtInIds.Add(s.Id);
        }
        ordered = byId.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Raised whenever custom scenarios are registered, unregistered, or replaced.</summary>
    public event Action? Changed;

    /// <summary>Gets all built-in and custom scenarios sorted case-insensitively by <see cref="IScenario.Name"/>.</summary>
    public IReadOnlyList<IScenario> All
    {
        get { lock (sync) return ordered; }
    }

    /// <summary>Looks up a scenario by its <see cref="IScenario.Id"/> (case-insensitive).</summary>
    /// <param name="id">Scenario identifier to search for.</param>
    /// <returns>The matching <see cref="IScenario"/>, or <c>null</c> if not found.</returns>
    public IScenario? Find(string id)
    {
        lock (sync) return byId.GetValueOrDefault(id);
    }

    /// <summary>Returns whether <paramref name="id"/> refers to a runtime-registered custom scenario rather than a built-in one.</summary>
    /// <param name="id">Scenario identifier to check.</param>
    /// <returns><c>true</c> if <paramref name="id"/> is currently registered and not built-in; otherwise <c>false</c>.</returns>
    public bool IsCustom(string id)
    {
        lock (sync) return byId.ContainsKey(id) && !builtInIds.Contains(id);
    }

    /// <summary>Adds or replaces a custom (non-built-in) scenario at runtime.</summary>
    /// <param name="scenario">Custom scenario instance to register.</param>
    public void RegisterCustom(IScenario scenario)
    {
        lock (sync)
        {
            if (builtInIds.Contains(scenario.Id))
                throw new InvalidOperationException($"Cannot override built-in scenario id '{scenario.Id}'.");
            byId[scenario.Id] = scenario;
            ordered = byId.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        Changed?.Invoke();
    }

    /// <summary>Removes a custom scenario by id. Built-in scenarios are never removed.</summary>
    /// <param name="id">Identifier of the custom scenario to remove.</param>
    /// <returns><c>true</c> if a custom scenario with <paramref name="id"/> was removed; otherwise <c>false</c>.</returns>
    public bool UnregisterCustom(string id)
    {
        bool removed;
        lock (sync)
        {
            if (builtInIds.Contains(id)) return false;
            removed = byId.Remove(id);
            if (removed) ordered = byId.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Replaces all custom scenarios in one atomic update (used by the WASM client when syncing from <c>/api/scenarios</c>).</summary>
    /// <param name="customScenarios">Replacement set of custom scenarios; entries colliding with built-in ids are ignored.</param>
    public void ReplaceCustomScenarios(IEnumerable<IScenario> customScenarios)
    {
        lock (sync)
        {
            foreach (var key in byId.Keys.Where(k => !builtInIds.Contains(k)).ToList())
                byId.Remove(key);
            foreach (var s in customScenarios)
                if (!builtInIds.Contains(s.Id))
                    byId[s.Id] = s;
            ordered = byId.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        Changed?.Invoke();
    }
}

/// <summary>Dependency injection helpers for registering <see cref="IScenario"/> implementations.</summary>
public static class ScenarioServiceCollectionExtensions
{
    /// <summary>Registers a scenario implementation as a singleton <see cref="IScenario"/> service.</summary>
    /// <typeparam name="T">Concrete scenario type implementing <see cref="IScenario"/>.</typeparam>
    /// <param name="services">Target service collection.</param>
    /// <returns>The updated <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddScenario<T>(this IServiceCollection services) where T : class, IScenario =>
        services.AddSingleton<IScenario, T>();
}
