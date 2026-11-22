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

    public string Presets { get; set; } = "presets";
    public string Environments { get; set; } = "environments";
    /// <summary>The scripts folder; scenario scripts live in it, preflights in <c>preflight/</c>, shutdown programs in <c>shutdown/</c>.</summary>
    public string Scripts { get; set; } = "scripts";

    public string ResolvePresets() => Resolve(Presets);
    public string ResolveEnvironments() => Resolve(Environments);
    public string ResolveScripts() => Resolve(Scripts);

    /// <summary>Makes <paramref name="path"/> absolute (relative paths start at <see cref="Root"/>). An empty path stays empty.</summary>
    public string Resolve(string path) =>
        string.IsNullOrWhiteSpace(path) ? "" : Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path));
}
