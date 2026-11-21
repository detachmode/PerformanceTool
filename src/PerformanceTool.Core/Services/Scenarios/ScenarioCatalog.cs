using Microsoft.Extensions.DependencyInjection;

namespace PerformanceTool.Services.Scenarios;

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

    public event Action? Changed;

    public IReadOnlyList<IScenario> All
    {
        get { lock (sync) return ordered; }
    }

    public IScenario? Find(string id)
    {
        lock (sync) return byId.GetValueOrDefault(id);
    }

    public bool IsCustom(string id)
    {
        lock (sync) return byId.ContainsKey(id) && !builtInIds.Contains(id);
    }

    /// <summary>Adds or replaces a custom (non-built-in) scenario at runtime.</summary>
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

public static class ScenarioServiceCollectionExtensions
{
    public static IServiceCollection AddScenario<T>(this IServiceCollection services) where T : class, IScenario =>
        services.AddSingleton<IScenario, T>();
}
