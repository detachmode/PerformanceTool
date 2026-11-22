using Beesharp.Services.Environments;

namespace Beesharp.Client.Services;

/// <summary>In-browser environments for the demo (brain 0033): three environments, kept only until the page is reloaded.</summary>
public sealed class FakeEnvironmentSource : IEnvironmentSource
{
    private readonly List<EnvironmentDefinition> list =
    [
        new() { Name = "Local", Variables = [new("BaseUrl", "https://localhost:5001"), new("Tenant", "dev"), new("ApiToken", "dev-token-7f3a91")] },
        new() { Name = "Staging", Variables = [new("BaseUrl", "https://shop.example.com"), new("Tenant", "acme-test"), new("ApiToken", "stg-9c41d8e2b6a05f17")] },
        new() { Name = "Load lab", Variables = [new("BaseUrl", "https://lab.shop.example.com"), new("Tenant", "load"), new("ApiToken", "lab-51be0a77c9d3")] },
    ];

    public IReadOnlyList<EnvironmentDefinition> Environments => list;
    public string? ActiveName { get; private set; } = "Staging";
    public EnvironmentDefinition? Active => ActiveName is null ? null : list.FirstOrDefault(e => e.Name == ActiveName);
    public event Action? Changed;

    public void SetActive(string? name)
    {
        ActiveName = name is not null && list.Any(e => e.Name == name) ? name : null;
        Changed?.Invoke();
    }

    public void Save(string? originalName, EnvironmentDefinition environment)
    {
        var clean = environment.Clone();
        clean.Name = clean.Name.Trim();
        clean.Variables = clean.Variables.Where(v => v.Key.Trim().Length > 0 || v.Value.Length > 0)
            .Select(v => new EnvironmentVariable(v.Key.Trim(), v.Value)).ToList();
        var index = originalName is null ? -1 : list.FindIndex(e => e.Name == originalName);
        if (index >= 0) list[index] = clean; else list.Add(clean);
        if (originalName is not null && ActiveName == originalName) ActiveName = clean.Name; // a rename keeps it active
        Changed?.Invoke();
    }

    public void Delete(string name)
    {
        list.RemoveAll(e => e.Name == name);
        if (ActiveName == name) ActiveName = null;
        Changed?.Invoke();
    }
}
