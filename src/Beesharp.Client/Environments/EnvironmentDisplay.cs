namespace Beesharp.Client.Environments;

/// <summary>Small display rules shared by the editor hints and the popup.</summary>
public static class EnvironmentDisplay
{
    private static readonly string[] SecretWords = ["token", "secret", "password", "passwd", "pwd", "apikey", "api_key", "api-key", "credential", "auth", "bearer"];

    /// <summary>Keys that look like they hold a credential are masked by default in hints and in the popup (a convenience, not a secret flag, brain 0033).</summary>
    public static bool LooksLikeCredential(string key) =>
        SecretWords.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase));

    public const string Mask = "••••••••";
}
