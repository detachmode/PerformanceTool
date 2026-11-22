using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.JSInterop;
using Beesharp.Services.Scenarios;

namespace Beesharp.Client.Data;

/// <summary>
/// Controls whether the UI runs against the in-browser fake simulator (<see cref="FakeRunSource"/>)
/// or the live ASP.NET Core server API (<see cref="ApiRunSource"/>). Persisted in <c>localStorage</c>
/// and switchable at runtime from the top bar (brain 0040).
/// </summary>
public sealed class DataModeState(IJSRuntime js, bool initialFakeMode = true)
{
    private const string StorageKey = "perftool.fakeMode";

    public bool IsFakeMode { get; private set; } = initialFakeMode;
    public event Action? Changed;

    public async Task LoadAsync()
    {
        try
        {
            var raw = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (bool.TryParse(raw, out var stored) && stored != IsFakeMode)
            {
                IsFakeMode = stored;
                Changed?.Invoke();
            }
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }

    public void Toggle() => SetFakeMode(!IsFakeMode);

    public void SetFakeMode(bool fake)
    {
        if (IsFakeMode == fake) return;
        IsFakeMode = fake;
        Changed?.Invoke();
        _ = PersistAsync(fake);
    }

    private async Task PersistAsync(bool fake)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", StorageKey, fake ? "true" : "false"); }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }
}

/// <summary>Open/close state for the Custom C# Scripts popup.</summary>
public sealed class CustomScriptsPopupState
{
    public bool IsOpen { get; private set; }
    public string? SelectScenarioId { get; private set; }
    public int Version { get; private set; }
    public event Action? Changed;

    public void Show(string? scenarioId = null)
    {
        IsOpen = true;
        SelectScenarioId = scenarioId;
        Version++;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        SelectScenarioId = null;
        Changed?.Invoke();
    }
}

/// <summary>HTTP client for <c>/api/scenarios</c> and <c>/api/scripts</c>, keeping the client <see cref="ScenarioCatalog"/> synced.</summary>
public sealed class ScriptApiClient(HttpClient http, ScenarioCatalog catalog)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task SyncCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await http.GetFromJsonAsync<List<ScenarioInfoDto>>("api/scenarios", JsonOpts, ct);
            if (list is null) return;
            var customs = list
                .Where(s => s.IsCustom)
                .Select(s => (IScenario)new RemoteSchemaScenario(s.Id, s.Name, s.Description, s.Parameters))
                .ToList();
            catalog.ReplaceCustomScenarios(customs);
        }
        catch
        {
            // Offline / GitHub Pages without a server: keep built-ins only.
        }
    }

    public async Task<List<ScriptScenarioSummary>> ListScriptsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ScriptScenarioSummary>>("api/scripts", JsonOpts, ct) ?? [];

    public async Task<ScriptCompileResult> CompileAndSaveAsync(string? id, string fileName, string code, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/scripts", new { id, fileName, code }, JsonOpts, ct);
        var result = await resp.Content.ReadFromJsonAsync<ScriptCompileResult>(JsonOpts, ct)
            ?? new ScriptCompileResult(false, null, [], $"HTTP {(int)resp.StatusCode}");
        if (result.Success && result.Script is { } s)
            catalog.RegisterCustom(new RemoteSchemaScenario(s.ScenarioId, s.ScenarioName, s.Description, s.Parameters));
        return result;
    }

    public async Task DeleteScriptAsync(string scriptId, string? scenarioId, CancellationToken ct = default)
    {
        var resp = await http.DeleteAsync($"api/scripts/{Uri.EscapeDataString(scriptId)}", ct);
        resp.EnsureSuccessStatusCode();
        if (!string.IsNullOrEmpty(scenarioId))
            catalog.UnregisterCustom(scenarioId);
    }

    public async Task<ScriptTestRunResult> TestRunAsync(string scriptId, JsonObject? parameters, string? environmentName, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync($"api/scripts/{Uri.EscapeDataString(scriptId)}/test-run",
            new { parameters, environmentName, timeoutSeconds }, JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errText = await resp.Content.ReadAsStringAsync(ct);
            try
            {
                var node = JsonNode.Parse(errText) as JsonObject;
                if (node?["error"]?.GetValue<string>() is { Length: > 0 } msg)
                    throw new InvalidOperationException(msg);
            }
            catch (JsonException) { }
            throw new InvalidOperationException($"Test run failed (HTTP {(int)resp.StatusCode}): {errText}");
        }
        return await resp.Content.ReadFromJsonAsync<ScriptTestRunResult>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Empty response from test-run endpoint.");
    }

    // Preflights and shutdown programs are the same kind of script (brain 0052): the same calls, on api/preflights or api/shutdowns.
    public const string Preflights = "preflights";
    public const string Shutdowns = "shutdowns";

    public async Task<List<PreflightScriptSummary>> ListPreflightsAsync(CancellationToken ct = default, string kind = Preflights)
    {
        try { return await http.GetFromJsonAsync<List<PreflightScriptSummary>>($"api/{kind}", JsonOpts, ct) ?? []; }
        catch { return []; }
    }

    public async Task<PreflightCompileResult> SavePreflightAsync(string? id, string fileName, string code, CancellationToken ct = default, string kind = Preflights)
    {
        var resp = await http.PostAsJsonAsync($"api/{kind}", new { id, fileName, code }, JsonOpts, ct);
        return await resp.Content.ReadFromJsonAsync<PreflightCompileResult>(JsonOpts, ct)
            ?? new PreflightCompileResult(false, null, [], $"HTTP {(int)resp.StatusCode}");
    }

    public async Task DeletePreflightAsync(string scriptId, CancellationToken ct = default, string kind = Preflights)
    {
        var resp = await http.DeleteAsync($"api/{kind}/{Uri.EscapeDataString(scriptId)}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<PreflightRunResult> TestRunPreflightAsync(string scriptId, string? environmentName, int timeoutSeconds = 60, CancellationToken ct = default, string kind = Preflights)
    {
        var resp = await http.PostAsJsonAsync($"api/{kind}/{Uri.EscapeDataString(scriptId)}/test-run",
            new { environmentName, timeoutSeconds }, JsonOpts, ct);
        return await resp.Content.ReadFromJsonAsync<PreflightRunResult>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Empty response from preflight test-run endpoint.");
    }
}
