using Beesharp.Client.Logs;

namespace Beesharp.Client.Environments;

/// <summary>Whether the Configure popup is open, and what it opens on (like <see cref="LogsPopupState"/>): it opens from the top bar and from the editor.</summary>
public sealed class EnvironmentsPopupState
{
    public bool IsOpen { get; private set; }
    /// <summary>Environment to select when opening; null = the active one.</summary>
    public string? Select { get; private set; }
    /// <summary>A key to add a row for (a placeholder the editor found that the environment lacks).</summary>
    public string? AddKey { get; private set; }
    /// <summary>Increases on every <see cref="Show"/>, so the popup starts fresh each time.</summary>
    public int Version { get; private set; }
    public event Action? Changed;

    public void Show(string? select = null, string? addKey = null)
    {
        (IsOpen, Select, AddKey) = (true, select, addKey);
        Version++;
        Changed?.Invoke();
    }

    public void Close() { IsOpen = false; Changed?.Invoke(); }
}
