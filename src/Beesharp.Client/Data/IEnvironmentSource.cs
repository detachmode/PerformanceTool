using Beesharp.Services.Environments;
using Beesharp.Client.Logs;

namespace Beesharp.Client.Data;

/// <summary>
/// Where the pages get the environments (brain 0033): the list, the active one and edits. <see cref="FakeEnvironmentSource"/> keeps them in
/// the browser; <see cref="ApiEnvironmentSource"/> will use the server's <c>/api/environments</c>. Pick with <c>DataSource</c>, like
/// <see cref="IRunSource"/>.
/// </summary>
public interface IEnvironmentSource
{
    /// <summary>All environments, in the order the user sees them.</summary>
    IReadOnlyList<EnvironmentDefinition> Environments { get; }
    /// <summary>Name of the active environment, or null for "No environment".</summary>
    string? ActiveName { get; }
    EnvironmentDefinition? Active { get; }
    event Action? Changed;

    void SetActive(string? name);
    /// <summary>Saves <paramref name="environment"/>. <paramref name="originalName"/> is the name it had before (null for a new one), so a rename replaces instead of adding.</summary>
    void Save(string? originalName, EnvironmentDefinition environment);
    void Delete(string name);
}

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

/// <summary>Small display rules shared by the editor hints and the popup.</summary>
public static class EnvironmentDisplay
{
    private static readonly string[] SecretWords = ["token", "secret", "password", "passwd", "pwd", "apikey", "api_key", "api-key", "credential", "auth", "bearer"];

    /// <summary>Keys that look like they hold a credential are masked by default in hints and in the popup (a convenience, not a secret flag, brain 0033).</summary>
    public static bool LooksLikeCredential(string key) =>
        SecretWords.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase));

    public const string Mask = "••••••••";
}
