using System.Net.Http.Json;
using System.Text.Json;
using Beesharp.Services.Environments;

namespace Beesharp.Client.Data;

/// <summary>Server-backed environments source talking to <c>/api/environments</c> (brain 0033, 0015).</summary>
public sealed class ApiEnvironmentSource(HttpClient http) : IEnvironmentSource
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private List<EnvironmentDefinition> items = [];
    private string? activeName;

    public IReadOnlyList<EnvironmentDefinition> Environments => items;
    public string? ActiveName => activeName;
    public EnvironmentDefinition? Active => activeName is null ? null : items.FirstOrDefault(e => e.Name == activeName);
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        try
        {
            var list = await http.GetFromJsonAsync<List<EnvironmentDefinition>>("api/environments", JsonOpts);
            if (list is not null)
            {
                items = list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (activeName is not null && !items.Any(e => e.Name == activeName))
                    activeName = null;
                Changed?.Invoke();
            }
        }
        catch
        {
            // Keep current list if server is unreachable.
        }
    }

    public void SetActive(string? name)
    {
        activeName = name is not null && items.Any(e => e.Name == name) ? name : null;
        Changed?.Invoke();
    }

    public void Save(string? originalName, EnvironmentDefinition environment)
    {
        var copy = environment.Clone();
        var idx = originalName is null ? -1 : items.FindIndex(e => e.Name == originalName);
        if (idx >= 0) items[idx] = copy;
        else items.Add(copy);
        items = items.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (originalName is not null && activeName == originalName) activeName = copy.Name;
        Changed?.Invoke();
        _ = SaveRemoteAsync(originalName, copy);
    }

    public void Delete(string name)
    {
        items.RemoveAll(e => e.Name == name);
        if (activeName == name) activeName = null;
        Changed?.Invoke();
        _ = DeleteRemoteAsync(name);
    }

    private async Task SaveRemoteAsync(string? originalName, EnvironmentDefinition env)
    {
        try
        {
            await http.PutAsJsonAsync($"api/environments/{Uri.EscapeDataString(env.Name)}", env, JsonOpts);
            if (originalName is not null && !string.Equals(originalName, env.Name, StringComparison.OrdinalIgnoreCase))
                await http.DeleteAsync($"api/environments/{Uri.EscapeDataString(originalName)}");
        }
        catch { }
    }

    private async Task DeleteRemoteAsync(string name)
    {
        try { await http.DeleteAsync($"api/environments/{Uri.EscapeDataString(name)}"); }
        catch { }
    }
}
