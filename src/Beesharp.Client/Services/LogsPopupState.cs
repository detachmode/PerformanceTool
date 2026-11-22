using Beesharp.Client.Components;

namespace Beesharp.Client.Services;

/// <summary>
/// Whether the Logs popup is open and what it opens pre-filtered to (brain 0017). The popup itself lives in the layout, so it can
/// open from the top bar and from the scenario popup alike.
/// </summary>
public sealed class LogsPopupState
{
    public bool IsOpen { get; private set; }
    public int? Entry { get; private set; }
    public int? Bee { get; private set; }
    /// <summary>Optional pre-set outcome filter (all / failed / slow), e.g. from the player bar's Errors and Slow readouts.</summary>
    public BeeShow Outcome { get; private set; }
    /// <summary>Increases on every <see cref="Show"/>, so the popup can start fresh each time.</summary>
    public int Version { get; private set; }
    /// <summary>Raised on every open and close.</summary>
    public event Action? Changed;

    public void Show(int? entry = null, int? bee = null, BeeShow outcome = BeeShow.All)
    {
        (IsOpen, Entry, Bee, Outcome) = (true, entry, bee, outcome);
        Version++;
        Changed?.Invoke();
    }

    public void Close()
    {
        IsOpen = false;
        Changed?.Invoke();
    }
}
