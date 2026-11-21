using System.Globalization;
using Microsoft.JSInterop;
using PerformanceTool.Client.Components;

namespace PerformanceTool.Client.Services;

/// <summary>
/// The user's view settings (brain 0023, update 3): zoom strength of the dock magnification and the number of bars before the popup chart
/// groups bees into time buckets. Kept in the browser's localStorage (wwwroot/prefs.js, which never throws), applied live.
/// </summary>
public sealed class ViewSettings(IJSRuntime js)
{
    public const double ZoomMin = 0.5, ZoomMax = 2.5, ZoomDefault = 1;
    public const int MaxBarsMin = 30, MaxBarsMax = 1000, MaxBarsDefault = BeeBars.MaxBars;
    private const string Key = "pt.view-settings";

    public double Zoom { get; private set; } = ZoomDefault;
    public int MaxBars { get; private set; } = MaxBarsDefault;
    public bool IsOpen { get; private set; }
    public bool IsDefault => Math.Abs(Zoom - ZoomDefault) < 0.001 && MaxBars == MaxBarsDefault;

    public event Action? Changed;

    public void Open() { IsOpen = true; Changed?.Invoke(); }
    public void Close() { IsOpen = false; Changed?.Invoke(); }

    public async Task LoadAsync()
    {
        try
        {
            var text = await js.InvokeAsync<string?>("localStorage.getItem", Key);
            if (string.IsNullOrWhiteSpace(text)) return;
            var parts = text.Split('|');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
            { Zoom = Math.Clamp(z, ZoomMin, ZoomMax); MaxBars = Math.Clamp(m, MaxBarsMin, MaxBarsMax); Changed?.Invoke(); }
        }
        catch (Exception) { /* storage blocked or malformed: defaults */ }
    }

    public async Task SetAsync(double? zoom = null, int? maxBars = null)
    {
        if (zoom is { } z) Zoom = Math.Round(Math.Clamp(z, ZoomMin, ZoomMax), 2);
        if (maxBars is { } m) MaxBars = Math.Clamp(m, MaxBarsMin, MaxBarsMax);
        Changed?.Invoke();
        await SaveAsync();
    }

    public Task ResetAsync() => SetAsync(ZoomDefault, MaxBarsDefault);

    private async Task SaveAsync()
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", Key, $"{Zoom.ToString(CultureInfo.InvariantCulture)}|{MaxBars}"); }
        catch (Exception) { /* private window etc.: the setting just does not persist */ }
    }
}
