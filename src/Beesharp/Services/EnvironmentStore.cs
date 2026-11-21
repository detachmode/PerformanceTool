using System.Text.Json;
using PerformanceTool.Services.Environments;

namespace PerformanceTool.Services;

/// <summary>
/// Stores environments as one JSON file per environment in a directory (config key <c>Environments:Directory</c>, next to the presets), like
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
    private readonly ILogger<EnvironmentStore> logger;

    public EnvironmentStore(IConfiguration config, IWebHostEnvironment env, ILogger<EnvironmentStore> logger)
    {
        this.logger = logger;
        Directory = Path.GetFullPath(config["Environments:Directory"] ?? "environments", env.ContentRootPath);
    }

    public string Directory { get; }

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

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await mutex.WaitAsync(ct);
        try { File.Delete(PathFor(name)); }
        finally { mutex.Release(); }
    }

    private string PathFor(string name) => Path.Combine(Directory, PresetStore.Slug(name) + ".json");
}
