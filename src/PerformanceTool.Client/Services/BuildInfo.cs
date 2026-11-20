using System.Globalization;
using System.Reflection;

namespace PerformanceTool.Client.Services;

/// <summary>
/// Which build is running (brain 0025). Values are baked into the assembly at publish time
/// (<c>-p:BuildSha=... -p:BuildTime=...</c>); a local build has no sha. <c>build-info.json</c> next to index.html is
/// written by the Pages workflow and fetched fresh to notice a newer deploy.
/// </summary>
public sealed record BuildInfo(string? Sha, DateTime? TimeUtc)
{
    public const string Repo = "https://github.com/detachmode/PerformanceTool";

    public static BuildInfo Current { get; } = Read();

    public string ShortSha => string.IsNullOrEmpty(Sha) ? "local" : Sha[..Math.Min(7, Sha.Length)];
    public string? CommitUrl => string.IsNullOrEmpty(Sha) ? null : $"{Repo}/commit/{Sha}";
    public string TimeText => TimeUtc is { } t ? t.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC" : "";

    private static BuildInfo Read()
    {
        string? Meta(string key) => typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
        var sha = Meta("BuildSha");
        DateTime? time = DateTime.TryParse(Meta("BuildTime"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        return new BuildInfo(string.IsNullOrWhiteSpace(sha) ? null : sha, time);
    }
}
