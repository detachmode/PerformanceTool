namespace Beesharp.Services;

/// <summary>
/// Where the tool keeps its files (brain 0054): presets, environments and scripts. Plain options, so the web host and the command-line tool
/// set them the same way (the host from <c>Presets:Directory</c>, <c>Environments:Directory</c>, <c>Scripts:Directory</c> and its content root).
/// Relative folders are resolved against <see cref="Root"/>.
/// </summary>
public sealed class BeesharpDirectories
{
    /// <summary>The folder relative paths start from. Default: the current directory.</summary>
    public string Root { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>Relative or absolute path to the presets folder. Default: <c>presets</c>.</summary>
    public string Presets { get; set; } = "presets";

    /// <summary>Relative or absolute path to the environments folder. Default: <c>environments</c>.</summary>
    public string Environments { get; set; } = "environments";

    /// <summary>The scripts folder; scenario scripts live in it, preflights in <c>preflight/</c>, shutdown programs in <c>shutdown/</c>.</summary>
    public string Scripts { get; set; } = "scripts";

    /// <summary>Resolves <see cref="Presets"/> to an absolute path relative to <see cref="Root"/>.</summary>
    /// <returns>The resolved absolute presets directory path, or an empty string if <see cref="Presets"/> is blank.</returns>
    public string ResolvePresets() => Resolve(Presets);

    /// <summary>Resolves <see cref="Environments"/> to an absolute path relative to <see cref="Root"/>.</summary>
    /// <returns>The resolved absolute environments directory path, or an empty string if <see cref="Environments"/> is blank.</returns>
    public string ResolveEnvironments() => Resolve(Environments);

    /// <summary>Resolves <see cref="Scripts"/> to an absolute path relative to <see cref="Root"/>.</summary>
    /// <returns>The resolved absolute scripts directory path, or an empty string if <see cref="Scripts"/> is blank.</returns>
    public string ResolveScripts() => Resolve(Scripts);

    /// <summary>Makes <paramref name="path"/> absolute (relative paths start at <see cref="Root"/>). An empty path stays empty.</summary>
    /// <param name="path">The relative or absolute path to resolve.</param>
    /// <returns>The resolved absolute path, or an empty string when <paramref name="path"/> is null or whitespace.</returns>
    public string Resolve(string path) =>
        string.IsNullOrWhiteSpace(path) ? "" : Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path));
}
