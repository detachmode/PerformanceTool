using System.Security;

namespace Beesharp.Services;

/// <summary>
/// Auto-drops the bundled <c>Beesharp.Core</c> NuGet package and reference assemblies into <c>&lt;scriptsDir&gt;/.beesharp/</c>
/// plus a minimal <c>&lt;scriptsDir&gt;/NuGet.config</c> (only when none exists) so .NET 11 file-based scenario scripts
/// (<c>#:package Beesharp.Core@0.1.0</c>) get full IDE IntelliSense and <c>dotnet run</c> support locally and in Docker (brain 0057).
/// Silently skips when no SDK files are present or the scripts folder is read-only.
/// </summary>
public static class ScriptSdkDropper
{
    private const string PackagePattern = "Beesharp.Core.*.nupkg";

    private const string DefaultNuGetConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <add key="beesharp-local" value="./.beesharp" />
          </packageSources>
        </configuration>
        """;

    /// <summary>
    /// Copies the bundled <c>Beesharp.Core</c> NuGet package and reference assemblies into <c>&lt;scriptsDirectory&gt;/.beesharp/</c>
    /// and ensures a minimal <c>NuGet.config</c> exists when one is not already present.
    /// </summary>
    /// <param name="scriptsDirectory">The root scripts directory into which <c>.beesharp/</c> and <c>NuGet.config</c> are written.</param>
    /// <param name="sdkSourceDirectory">Optional directory containing <c>Beesharp.Core.*.nupkg</c>; defaults to <c>&lt;AppContext.BaseDirectory&gt;/sdk</c>.</param>
    /// <param name="binDirectory">Optional directory containing <c>Beesharp.Core.dll</c> and <c>Beesharp.Core.xml</c>; defaults to <see cref="AppContext.BaseDirectory"/>.</param>
    public static void Sync(string scriptsDirectory, string? sdkSourceDirectory = null, string? binDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(scriptsDirectory)) return;

        var sdkSource = sdkSourceDirectory ?? Path.Combine(AppContext.BaseDirectory, "sdk");
        var binSource = binDirectory ?? AppContext.BaseDirectory;

        var nupkgs = Directory.Exists(sdkSource)
            ? Directory.EnumerateFiles(sdkSource, PackagePattern, SearchOption.TopDirectoryOnly).ToArray()
            : [];
        var coreDll = Directory.Exists(binSource) ? Path.Combine(binSource, "Beesharp.Core.dll") : null;
        var coreXml = Directory.Exists(binSource) ? Path.Combine(binSource, "Beesharp.Core.xml") : null;
        var hasDll = coreDll is not null && File.Exists(coreDll);
        var hasXml = coreXml is not null && File.Exists(coreXml);

        if (nupkgs.Length == 0 && !hasDll)
            return;

        try
        {
            var targetDir = Path.Combine(scriptsDirectory, ".beesharp");
            Directory.CreateDirectory(targetDir);

            // Remove stale Beesharp.Core.*.nupkg versions before copying the current ones.
            var currentNames = new HashSet<string>(
                nupkgs.Select(Path.GetFileName).Where(n => n is not null)!,
                StringComparer.OrdinalIgnoreCase);

            foreach (var existing in Directory.EnumerateFiles(targetDir, PackagePattern, SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(existing);
                if (!currentNames.Contains(name))
                    File.Delete(existing);
            }

            foreach (var pkg in nupkgs)
                CopyIfChanged(pkg, Path.Combine(targetDir, Path.GetFileName(pkg)));

            if (hasDll)
                CopyIfChanged(coreDll!, Path.Combine(targetDir, "Beesharp.Core.dll"));
            if (hasXml)
                CopyIfChanged(coreXml!, Path.Combine(targetDir, "Beesharp.Core.xml"));

            var gitignorePath = Path.Combine(targetDir, ".gitignore");
            if (!File.Exists(gitignorePath) || File.ReadAllText(gitignorePath) != "*\n")
                File.WriteAllText(gitignorePath, "*\n");

            // Write a minimal NuGet.config only if the user doesn't already have one.
            var hasNuGetConfig = Directory.EnumerateFiles(scriptsDirectory, "*", SearchOption.TopDirectoryOnly)
                .Any(f => string.Equals(Path.GetFileName(f), "NuGet.config", StringComparison.OrdinalIgnoreCase));
            if (!hasNuGetConfig)
                File.WriteAllText(Path.Combine(scriptsDirectory, "NuGet.config"), DefaultNuGetConfig + "\n");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            // Read-only mount (e.g. Docker :ro) or restricted permissions — skip gracefully without failing startup.
        }
    }

    private static void CopyIfChanged(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            var srcInfo = new FileInfo(sourcePath);
            var dstInfo = new FileInfo(destinationPath);
            if (srcInfo.Length == dstInfo.Length && srcInfo.LastWriteTimeUtc == dstInfo.LastWriteTimeUtc)
                return;
        }
        File.Copy(sourcePath, destinationPath, overwrite: true);
    }
}
