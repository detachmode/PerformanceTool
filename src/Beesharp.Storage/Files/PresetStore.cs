using System.Text;
using System.Text.Json;

namespace Beesharp.Services;

/// <summary>
/// Stores presets as one JSON file per preset in a directory (config key <c>Presets:Directory</c>, see <see cref="BeesharpDirectories"/>).
/// Plain files so presets can be diffed, shared and committed. See docs/brain/0005-presets-as-json-files.md.
/// </summary>
public sealed class PresetStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Keep quotes etc. readable: preset files are meant to be diffed and committed.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly SemaphoreSlim mutex = new(1, 1);
    private readonly ILogger logger;

    /// <summary>Initializes a new <see cref="PresetStore"/> backed by JSON files in <paramref name="directory"/>.</summary>
    /// <param name="directory">The folder of the JSON files (see <see cref="BeesharpDirectories"/>).</param>
    /// <param name="logger">Optional logger for warnings about unreadable preset files.</param>
    public PresetStore(string directory, ILogger<PresetStore>? logger = null)
    {
        this.logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Directory = Path.GetFullPath(directory);
    }

    /// <summary>The full path to the directory where preset JSON files are stored.</summary>
    public string Directory { get; }

    /// <summary>Lists all valid presets found in <see cref="Directory"/>, sorted by name.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of presets ordered case-insensitively by name.</returns>
    public async Task<IReadOnlyList<Preset>> ListAsync(CancellationToken ct = default)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var presets = new List<Preset>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                if (await JsonSerializer.DeserializeAsync<Preset>(stream, Json, ct) is { } p)
                    presets.Add(p);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger.LogWarning(ex, "Skipping unreadable preset file {File}", file);
            }
        }
        return presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Saves a load plan as a named preset using an atomic file replace.</summary>
    /// <param name="name">The preset name; must not be blank.</param>
    /// <param name="plan">The load plan to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAsync(string name, LoadPlan plan, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Preset name is required.", nameof(name));

        await mutex.WaitAsync(ct);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(name);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Preset(name, DateTimeOffset.UtcNow, plan.Clone()), Json), ct);
            File.Move(temp, path, overwrite: true); // atomic replace: a crash never leaves a half-written preset
        }
        finally { mutex.Release(); }
    }

    /// <summary>Deletes the persisted preset file matching <paramref name="name"/> if it exists.</summary>
    /// <param name="name">The preset name.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await mutex.WaitAsync(ct);
        try { File.Delete(PathFor(name)); }
        finally { mutex.Release(); }
    }

    private string PathFor(string name) => Path.Combine(Directory, Slug(name) + ".json");

    internal static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "preset" : slug;
    }
}
