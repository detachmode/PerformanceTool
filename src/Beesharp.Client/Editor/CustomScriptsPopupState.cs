using Beesharp.Client.Pages;

namespace Beesharp.Client.Editor;

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
