using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Tests;

public class PlanWorkspaceTests : IDisposable
{
    private sealed class FakeHost(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "t";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "beesharp-plan-" + Guid.NewGuid().ToString("N"));

    private PlanWorkspace Create()
    {
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().Build();
        var presets = new PresetStore(config, new FakeHost(root), NullLogger<PresetStore>.Instance);
        return new PlanWorkspace(new ScenarioCatalog([new HttpGetScenario()]), presets);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public void Starts_empty_at_version_one_without_unsaved_changes()
    {
        var state = Create().GetState();
        Assert.Empty(state.Plan.Scenarios);
        Assert.Equal(PlanWorkspace.DefaultPresetName, state.PresetName);
        Assert.False(state.HasUnsavedChanges);
        Assert.Equal(1, state.Version);
    }

    [Fact]
    public void Adding_and_removing_entries_changes_the_version_and_marks_unsaved()
    {
        var ws = Create();
        var raised = 0;
        ws.Changed += _ => raised++;

        var index = ws.AddEntry("http-get");
        Assert.Equal(0, index);
        var state = ws.GetState();
        Assert.Single(state.Plan.Scenarios);
        Assert.Equal("HTTP GET", state.Plan.Scenarios[0].Label);
        Assert.True(state.HasUnsavedChanges);
        Assert.Equal(2, state.Version);

        ws.AddEntry("http-get");
        Assert.Equal("HTTP GET #2", ws.GetState().Plan.Scenarios[1].Label);

        ws.RemoveEntry(0);
        Assert.Single(ws.GetState().Plan.Scenarios);
        Assert.Equal(3, raised);
    }

    [Fact]
    public void Unknown_scenario_and_bad_index_are_rejected_without_a_version_bump()
    {
        var ws = Create();
        Assert.Throws<ArgumentException>(() => ws.AddEntry("nope"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ws.RemoveEntry(0));
        Assert.Equal(1, ws.Version);
    }

    [Fact]
    public void Setting_an_equal_plan_keeps_the_version()
    {
        var ws = Create();
        ws.AddEntry("http-get");
        var v = ws.Version;
        ws.SetPlan(ws.GetPlan());
        Assert.Equal(v, ws.Version);

        var edited = ws.GetPlan();
        edited.Scenarios[0].RatePerMinute = 99;
        ws.SetPlan(edited);
        Assert.Equal(v + 1, ws.Version);
        Assert.Equal(99, ws.GetPlan().Scenarios[0].RatePerMinute);
    }

    [Fact]
    public void Update_edits_a_copy_and_ignores_a_no_op()
    {
        var ws = Create();
        ws.AddEntry("http-get");
        var v = ws.Version;
        ws.Update(_ => false);
        Assert.Equal(v, ws.Version);
        ws.Update(p => { p.DurationSeconds = 120; return true; });
        Assert.Equal(120, ws.GetPlan().DurationSeconds);
        Assert.Equal(v + 1, ws.Version);
    }

    [Fact]
    public async Task Saving_a_preset_makes_it_current_and_clears_unsaved()
    {
        var ws = Create();
        ws.AddEntry("http-get");
        var state = await ws.SavePresetAsync("Smoke test");
        Assert.Equal("Smoke test", state.PresetName);
        Assert.False(state.HasUnsavedChanges);

        var edited = ws.GetPlan();
        edited.Scenarios[0].RatePerMinute = 5;
        Assert.True(ws.SetPlan(edited).HasUnsavedChanges);

        var presets = await ws.ListPresetsAsync();
        Assert.Equal(["Smoke test"], presets.Select(p => p.Name));
    }

    [Fact]
    public async Task Loading_a_preset_replaces_the_plan_and_unknown_names_return_null()
    {
        var ws = Create();
        ws.AddEntry("http-get");
        await ws.SavePresetAsync("One");
        ws.AddEntry("http-get");
        await ws.SavePresetAsync("Two");

        var loaded = await ws.LoadPresetAsync("one");
        Assert.NotNull(loaded);
        Assert.Equal("One", loaded!.PresetName);
        Assert.Single(loaded.Plan.Scenarios);
        Assert.Null(await ws.LoadPresetAsync("missing"));
    }

    [Fact]
    public async Task Deleting_a_preset_removes_it_and_bumps_the_version()
    {
        var ws = Create();
        ws.AddEntry("http-get");
        await ws.SavePresetAsync("Gone");
        var v = ws.Version;
        Assert.True(await ws.DeletePresetAsync("Gone"));
        Assert.False(await ws.DeletePresetAsync("Gone"));
        Assert.Empty(await ws.ListPresetsAsync());
        Assert.Equal(v + 1, ws.Version);
    }

    [Fact]
    public void Seed_fills_only_an_empty_workspace()
    {
        var ws = Create();
        var seed = new LoadPlan { DurationSeconds = 77, Scenarios = [new ScenarioRunConfig { ScenarioId = "http-get", Label = "X" }] };
        ws.SeedIfEmpty(seed);
        Assert.Equal(77, ws.GetPlan().DurationSeconds);
        Assert.False(ws.GetState().HasUnsavedChanges);
        ws.SeedIfEmpty(new LoadPlan { DurationSeconds = 5, Scenarios = [new ScenarioRunConfig { ScenarioId = "http-get" }] });
        Assert.Equal(77, ws.GetPlan().DurationSeconds);
    }
}
