using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Storage;

namespace Beesharp.Storage.Tests;

/// <summary>
/// Stories for <see cref="ProgramLogHelper"/>, <see cref="PresetStore"/> and <see cref="EnvironmentStore"/>:
/// normalizing preflight/shutdown log lines and recovering gracefully from corrupt or missing JSON files on disk.
/// </summary>
public sealed class FileStoresAndProgramLogsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beesharp-storage-files-{Guid.NewGuid():N}");

    public FileStoresAndProgramLogsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void Program_log_helper_maps_kinds_and_parses_every_log_prefix_and_fallback_format()
    {
        Assert.Equal(("preflight", ProgramLogEntry.Preflight, "Preflight"), ProgramLogHelper.ResolveKind("Preflight"));
        Assert.Equal(("shutdown", ProgramLogEntry.Shutdown, "Shutdown"), ProgramLogHelper.ResolveKind("SHUTDOWN"));
        Assert.Throws<ArgumentException>(() => ProgramLogHelper.ResolveKind("warmup"));

        // Standard prefixed lines with separate or combined timestamp + level tags.
        Assert.Equal(("Trace", "trace detail"), ProgramLogHelper.ParseLine("[0.01s] [TRC] trace detail"));
        Assert.Equal(("Trace", "verbose detail"), ProgramLogHelper.ParseLine("[0.01s VRB] verbose detail"));
        Assert.Equal(("Debug", "debug detail"), ProgramLogHelper.ParseLine("[0.02s] [DBG] debug detail"));
        Assert.Equal(("Information", "stdout line"), ProgramLogHelper.ParseLine("[0.03s] [OUT] stdout line"));
        Assert.Equal(("Information", "untagged"), ProgramLogHelper.ParseLine("[0.04s] untagged"));
        Assert.Equal(("Warning", "slow warmup"), ProgramLogHelper.ParseLine("[19:05:12.345 WRN] slow warmup"));
        Assert.Equal(("Error", "db unreachable"), ProgramLogHelper.ParseLine("[0.05s] [ERR] db unreachable"));
        Assert.Equal(("Error", "fatal crash"), ProgramLogHelper.ParseLine("[0.06s] [FTL] fatal crash"));

        // Unprefixed lines fall back to scanning embedded severity tokens.
        Assert.Equal(("Error", "oops [ERR] failed"), ProgramLogHelper.ParseLine("oops [ERR] failed"));
        Assert.Equal(("Error", "oops [FTL] crashed"), ProgramLogHelper.ParseLine("oops [FTL] crashed"));
        Assert.Equal(("Warning", "careful [WRN]"), ProgramLogHelper.ParseLine("careful [WRN]"));
        Assert.Equal(("Debug", "details [DBG]"), ProgramLogHelper.ParseLine("details [DBG]"));
        Assert.Equal(("Trace", "noise [TRC]"), ProgramLogHelper.ParseLine("noise [TRC]"));
        Assert.Equal(("Information", "plain stdout text"), ProgramLogHelper.ParseLine("plain stdout text"));
    }

    [Fact]
    public async Task Preset_and_environment_stores_tolerate_missing_folders_corrupt_json_and_unusual_names()
    {
        var presetsDir = Path.Combine(root, "presets");
        var envsDir = Path.Combine(root, "environments");
        var presetStore = new PresetStore(presetsDir);
        var envStore = new EnvironmentStore(envsDir);

        // Before folders exist, listing returns empty instead of throwing.
        Assert.Empty(await presetStore.ListAsync());
        Assert.Empty(await envStore.ListAsync());

        // Blank preset name is rejected; punctuation-only names fall back cleanly to "preset.json".
        await Assert.ThrowsAsync<ArgumentException>(() => presetStore.SaveAsync("   ", new LoadPlan()));
        await presetStore.SaveAsync("!!!", new LoadPlan { DurationSeconds = 42 });
        Assert.True(File.Exists(Path.Combine(presetsDir, "preset.json")));

        // Corrupt JSON files alongside valid files are skipped during ListAsync and return null on GetAsync.
        await File.WriteAllTextAsync(Path.Combine(presetsDir, "corrupt.json"), "{ not valid json");
        var loadedPresets = await presetStore.ListAsync();
        Assert.Single(loadedPresets);
        Assert.Equal(42, loadedPresets[0].Plan.DurationSeconds);

        await envStore.SaveAsync(new EnvironmentDefinition { Name = "Staging", Variables = [new("BaseUrl", "https://staging.test")] });
        await File.WriteAllTextAsync(Path.Combine(envsDir, "broken.json"), "{ invalid");

        var loadedEnvs = await envStore.ListAsync();
        Assert.Single(loadedEnvs);
        Assert.Equal("Staging", loadedEnvs[0].Name);
        Assert.Null(await envStore.GetAsync("broken"));

        await presetStore.DeleteAsync("!!!");
        await envStore.DeleteAsync("Staging");
        Assert.Empty(await presetStore.ListAsync());
        Assert.Empty(await envStore.ListAsync());
    }
}
