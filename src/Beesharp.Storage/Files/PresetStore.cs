using System.Text;
using System.Text.Json;

namespace Beesharp.Services;

/// <summary>
/// Stores presets as one JSON file per preset in a directory (config key <c>Presets:Directory</c>).
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
    private readonly ILogger<PresetStore> logger;

    public PresetStore(IConfiguration config, IWebHostEnvironment env, ILogger<PresetStore> logger)
    {
        this.logger = logger;
        Directory = Path.GetFullPath(config["Presets:Directory"] ?? "presets", env.ContentRootPath);
    }

    public string Directory { get; }

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
