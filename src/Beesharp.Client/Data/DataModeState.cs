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
