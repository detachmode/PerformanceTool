namespace PerformanceTool.Services.Scenarios;

/// <summary>All scenarios registered in DI, looked up by <see cref="IScenario.Id"/>.</summary>
public sealed class ScenarioCatalog
{
    private readonly Dictionary<string, IScenario> byId;

    public ScenarioCatalog(IEnumerable<IScenario> scenarios)
    {
        All = scenarios.OrderBy(s => s.Name).ToList();
        byId = new Dictionary<string, IScenario>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in All)
            if (!byId.TryAdd(s.Id, s))
                throw new InvalidOperationException($"Duplicate scenario id '{s.Id}'.");
    }

    public IReadOnlyList<IScenario> All { get; }

    public IScenario? Find(string id) => byId.GetValueOrDefault(id);
}

public static class ScenarioServiceCollectionExtensions
{
    public static IServiceCollection AddScenario<T>(this IServiceCollection services) where T : class, IScenario =>
        services.AddSingleton<IScenario, T>();
}
