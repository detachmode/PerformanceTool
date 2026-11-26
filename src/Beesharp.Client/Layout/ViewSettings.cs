using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using Beesharp.Client.Chart;

namespace Beesharp.Client.Layout;

/// <summary>
/// The user's view settings (brain 0023, update 3): zoom strength of the dock magnification, the number of bars before the popup chart
/// groups bees into time buckets, and an optional trace URL template override. Kept in the browser's localStorage, applied live.
/// </summary>
public sealed class ViewSettings(IJSRuntime js, HttpClient? http = null)
{
    public const double ZoomMin = 0.5, ZoomMax = 2.5, ZoomDefault = 1;
    public const int MaxBarsMin = 30, MaxBarsMax = 1000, MaxBarsDefault = BeeBars.MaxBars;
    private const string Key = "pt.view-settings";
    private const string TraceUrlKey = "pt.trace-url";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private bool serverConfigLoaded;

    public double Zoom { get; private set; } = ZoomDefault;
    public int MaxBars { get; private set; } = MaxBarsDefault;
    public string? TraceUrlTemplate { get; private set; }
    public string? ServerTraceUrlTemplate { get; private set; }
    public string? EffectiveTraceUrlTemplate => !string.IsNullOrWhiteSpace(TraceUrlTemplate) ? TraceUrlTemplate : ServerTraceUrlTemplate;
    public bool IsOpen { get; private set; }
    public bool IsDefault => Math.Abs(Zoom - ZoomDefault) < 0.001 && MaxBars == MaxBarsDefault && string.IsNullOrWhiteSpace(TraceUrlTemplate);

    public event Action? Changed;

    public void Open() { IsOpen = true; Changed?.Invoke(); }
    public void Close() { IsOpen = false; Changed?.Invoke(); }

    public async Task LoadAsync()
    {
        var changed = false;

        try
        {
            var text = await js.InvokeAsync<string?>("localStorage.getItem", Key);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var parts = text.Split('|');
                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
                {
                    Zoom = Math.Clamp(z, ZoomMin, ZoomMax);
                    MaxBars = Math.Clamp(m, MaxBarsMin, MaxBarsMax);
                    changed = true;
                }
            }
        }
        catch (Exception) { /* storage blocked or malformed: defaults */ }

        try
        {
            var traceText = await js.InvokeAsync<string?>("localStorage.getItem", TraceUrlKey);
            var normalized = string.IsNullOrWhiteSpace(traceText) ? null : traceText.Trim();
            if (!string.Equals(TraceUrlTemplate, normalized, StringComparison.Ordinal))
            {
                TraceUrlTemplate = normalized;
                changed = true;
            }
        }
        catch (Exception) { /* storage blocked: default */ }

        if (http is not null && !serverConfigLoaded)
        {
            serverConfigLoaded = true;
            try
            {
                var cfg = await http.GetFromJsonAsync<TracingUiConfig>("api/tracing/config", JsonOpts);
                var serverTemplate = string.IsNullOrWhiteSpace(cfg?.TraceUrlTemplate) ? null : cfg.TraceUrlTemplate.Trim();
                if (!string.Equals(ServerTraceUrlTemplate, serverTemplate, StringComparison.Ordinal))
                {
                    ServerTraceUrlTemplate = serverTemplate;
                    changed = true;
                }
            }
            catch (Exception) { /* Fake mode, GitHub Pages, or older server: ignore silently */ }
        }

        if (changed) Changed?.Invoke();
    }

    public async Task SetAsync(double? zoom = null, int? maxBars = null)
    {
        if (zoom is { } z) Zoom = Math.Round(Math.Clamp(z, ZoomMin, ZoomMax), 2);
        if (maxBars is { } m) MaxBars = Math.Clamp(m, MaxBarsMin, MaxBarsMax);
        Changed?.Invoke();
        await SaveAsync();
    }

    public async Task SetTraceUrlAsync(string? template)
    {
        TraceUrlTemplate = string.IsNullOrWhiteSpace(template) ? null : template.Trim();
        Changed?.Invoke();
        await SaveTraceUrlAsync();
    }

    public async Task ResetAsync()
    {
        Zoom = ZoomDefault;
        MaxBars = MaxBarsDefault;
        TraceUrlTemplate = null;
        Changed?.Invoke();
        await SaveAsync();
        await SaveTraceUrlAsync();
    }

    /// <summary>Expands a trace URL template with <paramref name="traceId"/> and optional <paramref name="spanId"/>, allowing only <c>http</c> or <c>https</c> URLs.</summary>
    public static string? BuildTraceUrl(string? template, string? traceId, string? spanId = null)
    {
        if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(traceId))
            return null;

        var url = template.Trim()
            .Replace("{traceId}", Uri.EscapeDataString(traceId.Trim()), StringComparison.Ordinal)
            .Replace("{spanId}", Uri.EscapeDataString(spanId?.Trim() ?? ""), StringComparison.Ordinal);

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>Resolves a clickable URL for <paramref name="traceId"/> using <see cref="EffectiveTraceUrlTemplate"/>.</summary>
    public string? ResolveTraceUrl(string? traceId, string? spanId = null) =>
        BuildTraceUrl(EffectiveTraceUrlTemplate, traceId, spanId);

    private async Task SaveAsync()
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", Key, $"{Zoom.ToString(CultureInfo.InvariantCulture)}|{MaxBars}"); }
        catch (Exception) { /* private window etc.: the setting just does not persist */ }
    }

    private async Task SaveTraceUrlAsync()
    {
        try
        {
            if (TraceUrlTemplate is null)
                await js.InvokeVoidAsync("localStorage.removeItem", TraceUrlKey);
            else
                await js.InvokeVoidAsync("localStorage.setItem", TraceUrlKey, TraceUrlTemplate);
        }
        catch (Exception) { /* private window etc.: the setting just does not persist */ }
    }
}
