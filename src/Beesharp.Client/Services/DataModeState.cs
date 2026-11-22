using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.JSInterop;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Client.Services;

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

public sealed record ClientScriptDiagnostic(string Id, string Severity, int Line, int Column, string Message);

public sealed record ClientScriptSummary(
    string ScriptId,
    string FileName,
    string ScenarioId,
    string ScenarioName,
    string Description,
    string Code,
    DateTimeOffset UpdatedUtc,
    JsonObject Parameters);

public sealed record ClientScriptCompileResult(
    bool Success,
    ClientScriptSummary? Script,
    IReadOnlyList<ClientScriptDiagnostic> Diagnostics,
    string? Error);

public sealed record ClientRecordedCall(string Method, string Url, int? StatusCode, double DurationMs, long Bytes, string? Error);

public sealed record ClientTestRunResult(
    bool Success,
    string ScenarioId,
    string ScenarioName,
    int? StatusCode,
    long Bytes,
    double DurationMs,
    double StartupDurationMs,
    double TeardownDurationMs,
    string? Error,
    IReadOnlyList<ClientRecordedCall> Calls,
    IReadOnlyList<string> Logs);

public sealed record RemoteScenarioDto(
    string Id,
    string Name,
    string Description,
    bool IsCustom,
    string? ScriptId,
    JsonObject Parameters);

public sealed record ClientPreflightSummary(
    string ScriptId,
    string FileName,
    string Name,
    string Code,
    DateTimeOffset UpdatedUtc);

public sealed record ClientPreflightCompileResult(
    bool Success,
    ClientPreflightSummary? Script,
    IReadOnlyList<ClientScriptDiagnostic> Diagnostics,
    string? Error = null);

public sealed record ClientPreflightRunResult(
    bool Success,
    double DurationMs,
    string? Error,
    IReadOnlyList<string> Logs);

/// <summary>HTTP client for <c>/api/scenarios</c> and <c>/api/scripts</c>, keeping the client <see cref="ScenarioCatalog"/> synced.</summary>
public sealed class ScriptApiClient(HttpClient http, ScenarioCatalog catalog)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task SyncCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await http.GetFromJsonAsync<List<RemoteScenarioDto>>("api/scenarios", JsonOpts, ct);
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

    public async Task<List<ClientScriptSummary>> ListScriptsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ClientScriptSummary>>("api/scripts", JsonOpts, ct) ?? [];

    public async Task<ClientScriptCompileResult> CompileAndSaveAsync(string? id, string fileName, string code, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/scripts", new { id, fileName, code }, JsonOpts, ct);
        var result = await resp.Content.ReadFromJsonAsync<ClientScriptCompileResult>(JsonOpts, ct)
            ?? new ClientScriptCompileResult(false, null, [], $"HTTP {(int)resp.StatusCode}");
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

    public async Task<ClientTestRunResult> TestRunAsync(string scriptId, JsonObject? parameters, string? environmentName, int timeoutSeconds = 30, CancellationToken ct = default)
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
        return await resp.Content.ReadFromJsonAsync<ClientTestRunResult>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Empty response from test-run endpoint.");
    }

    // Preflights and shutdown programs are the same kind of script (brain 0052): the same calls, on api/preflights or api/shutdowns.
    public const string Preflights = "preflights";
    public const string Shutdowns = "shutdowns";

    public async Task<List<ClientPreflightSummary>> ListPreflightsAsync(CancellationToken ct = default, string kind = Preflights)
    {
        try { return await http.GetFromJsonAsync<List<ClientPreflightSummary>>($"api/{kind}", JsonOpts, ct) ?? []; }
        catch { return []; }
    }

    public async Task<ClientPreflightCompileResult> SavePreflightAsync(string? id, string fileName, string code, CancellationToken ct = default, string kind = Preflights)
    {
        var resp = await http.PostAsJsonAsync($"api/{kind}", new { id, fileName, code }, JsonOpts, ct);
        return await resp.Content.ReadFromJsonAsync<ClientPreflightCompileResult>(JsonOpts, ct)
            ?? new ClientPreflightCompileResult(false, null, [], $"HTTP {(int)resp.StatusCode}");
    }

    public async Task DeletePreflightAsync(string scriptId, CancellationToken ct = default, string kind = Preflights)
    {
        var resp = await http.DeleteAsync($"api/{kind}/{Uri.EscapeDataString(scriptId)}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ClientPreflightRunResult> TestRunPreflightAsync(string scriptId, string? environmentName, int timeoutSeconds = 60, CancellationToken ct = default, string kind = Preflights)
    {
        var resp = await http.PostAsJsonAsync($"api/{kind}/{Uri.EscapeDataString(scriptId)}/test-run",
            new { environmentName, timeoutSeconds }, JsonOpts, ct);
        return await resp.Content.ReadFromJsonAsync<ClientPreflightRunResult>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Empty response from preflight test-run endpoint.");
    }
}
