using PerformanceTool.Services.Environments;

namespace PerformanceTool.Client.Services;

/// <summary>Placeholder for the server's <c>/api/environments</c> (brain 0033, 0015): every member throws until the client talks to the API.</summary>
public sealed class ApiEnvironmentSource : IEnvironmentSource
{
    private static NotSupportedException NotYet() =>
        new("ApiEnvironmentSource is not implemented yet. Set \"DataSource\": \"Fake\" in wwwroot/appsettings.json.");

    public IReadOnlyList<EnvironmentDefinition> Environments => throw NotYet();
    public string? ActiveName => throw NotYet();
    public EnvironmentDefinition? Active => throw NotYet();
    public event Action? Changed { add { } remove { } }
    public void SetActive(string? name) => throw NotYet();
    public void Save(string? originalName, EnvironmentDefinition environment) => throw NotYet();
    public void Delete(string name) => throw NotYet();
}
