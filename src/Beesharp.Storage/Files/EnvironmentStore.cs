using System.Text.Json;
using Beesharp.Services.Environments;

namespace Beesharp.Services;

/// <summary>
/// Stores environments as one JSON file per environment in a directory (config key <c>Environments:Directory</c>, see <see cref="BeesharpDirectories"/>, next to the presets), like
/// Bruno: each file can be shared or kept local on its own. See docs/brain/0033-environments.md.
/// </summary>
public sealed class EnvironmentStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly SemaphoreSlim mutex = new(1, 1);
    private readonly ILogger logger;

    /// <summary>Initializes a new <see cref="EnvironmentStore"/> backed by JSON files in <paramref name="directory"/>.</summary>
    /// <param name="directory">The folder of the JSON files (see <see cref="BeesharpDirectories"/>).</param>
    /// <param name="logger">Optional logger for warnings about unreadable environment files.</param>
    public EnvironmentStore(string directory, ILogger<EnvironmentStore>? logger = null)
    {
        this.logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Directory = Path.GetFullPath(directory);
    }

    /// <summary>The full path to the directory where environment JSON files are stored.</summary>
    public string Directory { get; }

    /// <summary>Lists all valid environments found in <see cref="Directory"/>, sorted by name.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of environment definitions ordered case-insensitively by name.</returns>
    public async Task<IReadOnlyList<EnvironmentDefinition>> ListAsync(CancellationToken ct = default)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var all = new List<EnvironmentDefinition>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                if (await JsonSerializer.DeserializeAsync<EnvironmentDefinition>(stream, Json, ct) is { Name.Length: > 0 } e)
                    all.Add(e);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger.LogWarning(ex, "Skipping unreadable environment file {File}", file);
            }
        }
        return all.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Loads a single environment by <paramref name="name"/>, or returns <see langword="null"/> if missing or unreadable.</summary>
    /// <param name="name">The environment name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized environment definition, or <see langword="null"/> if not found or invalid.</returns>
    public async Task<EnvironmentDefinition?> GetAsync(string name, CancellationToken ct = default)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<EnvironmentDefinition>(stream, Json, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Unreadable environment file {File}", path);
            return null;
        }
    }

    /// <summary>Saves the environment; throws <see cref="ArgumentException"/> when it is not valid (see <see cref="EnvironmentDefinition.Validate"/>).</summary>
    /// <param name="environment">The environment definition to trim, validate, and persist.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAsync(EnvironmentDefinition environment, CancellationToken ct = default)
    {
        var clean = environment.Clone();
        clean.Name = clean.Name.Trim();
        clean.Variables = clean.Variables.Where(v => v.Key.Trim().Length > 0)
            .Select(v => new EnvironmentVariable(v.Key.Trim(), v.Value)).ToList();
        if (clean.Validate() is { Count: > 0 } errors) throw new ArgumentException(string.Join(" ", errors), nameof(environment));

        await mutex.WaitAsync(ct);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(clean.Name);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(clean, Json), ct);
            File.Move(temp, path, overwrite: true); // atomic replace, like presets
        }
        finally { mutex.Release(); }
    }

    /// <summary>Deletes the persisted environment file matching <paramref name="name"/> if it exists.</summary>
    /// <param name="name">The environment name.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await mutex.WaitAsync(ct);
        try { File.Delete(PathFor(name)); }
        finally { mutex.Release(); }
    }

    private string PathFor(string name) => Path.Combine(Directory, PresetStore.Slug(name) + ".json");
}
