using Microsoft.Extensions.Logging.Abstractions;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Engine.Tests;

public class PlanWorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "beesharp-plan-" + Guid.NewGuid().ToString("N"));

    private PlanWorkspace Create()
    {
        Directory.CreateDirectory(root);
        var presets = new PresetStore(Path.Combine(root, "presets"), NullLogger<PresetStore>.Instance);
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

    [Fact]
    public async Task Code_presets_list_load_and_are_overridden_by_disk_saves()
    {
        var ws = Create();
        var codePlan = new LoadPlan
        {
            DurationSeconds = 90,
            Scenarios = [new ScenarioRunConfig { ScenarioId = "http-get", Label = "Code GET", RatePerMinute = 30 }],
        };
        ws.RegisterCodePreset(new Preset("Smoke", DateTimeOffset.UtcNow, codePlan));

        var list = await ws.ListPresetsAsync();
        Assert.Equal(["Smoke"], list.Select(p => p.Name));
        Assert.Equal(90, list[0].Plan.DurationSeconds);

        var loaded = await ws.LoadPresetAsync("smoke");
        Assert.NotNull(loaded);
        Assert.Equal("Smoke", loaded!.PresetName);
        Assert.Equal(90, loaded.Plan.DurationSeconds);
        Assert.False(loaded.HasUnsavedChanges);

        // Saving edits to disk overrides the code preset by name.
        var edited = ws.GetPlan();
        edited.DurationSeconds = 180;
        await ws.SavePresetAsync("Smoke", edited);

        var overridden = await ws.ListPresetsAsync();
        Assert.Single(overridden);
        Assert.Equal(180, overridden[0].Plan.DurationSeconds);

        // Deleting the disk override falls back to the code preset.
        Assert.True(await ws.DeletePresetAsync("Smoke"));
        var reverted = await ws.ListPresetsAsync();
        Assert.Single(reverted);
        Assert.Equal(90, reverted[0].Plan.DurationSeconds);
    }

    [Fact]
    public async Task ApplyDefaultPlan_loads_default_preset_and_sets_default_environment()
    {
        var ws = Create();
        var smoke = new PlanBuilder()
            .Duration(TimeSpan.FromMinutes(2))
            .MaxConcurrency(20)
            .Timeout(TimeSpan.FromSeconds(15))
            .Add<HttpGetScenario>(new HttpGetParameters { Url = "https://example.test/health" }, ratePerMinute: 30)
            .AddBurst<HttpGetScenario>(beesPerBurst: 5, burstsPerMinute: 2, label: "Burst GET")
            .Build(new ScenarioCatalog([new HttpGetScenario()]));
        ws.RegisterCodePreset(new Preset("Smoke", DateTimeOffset.UtcNow, smoke));

        var applied = await ws.ApplyDefaultPlanAsync(new DefaultPlanOptions
        {
            DefaultPreset = "Smoke",
            DefaultEnvironment = "staging",
        });

        Assert.True(applied);
        Assert.Equal("staging", ws.DefaultEnvironment);
        var state = ws.GetState();
        Assert.Equal("Smoke", state.PresetName);
        Assert.False(state.HasUnsavedChanges);
        Assert.Equal(120, state.Plan.DurationSeconds);
        Assert.Equal(20, state.Plan.MaxConcurrency);
        Assert.Equal(15, state.Plan.TimeoutSeconds);
        Assert.Equal(2, state.Plan.Scenarios.Count);
        Assert.Equal("https://example.test/health", state.Plan.Scenarios[0].Parameters["url"]?.GetValue<string>());
        Assert.Equal(30, state.Plan.Scenarios[0].RatePerMinute);
        Assert.Equal(SpawningPattern.Bursts, state.Plan.Scenarios[1].Pattern);
        Assert.Equal(5, state.Plan.Scenarios[1].BeesPerBurst);
        Assert.Equal(2, state.Plan.Scenarios[1].BurstsPerMinute);
    }

    [Fact]
    public async Task ApplyDefaultPlan_seeds_from_inline_config_and_normalizes_parameters()
    {
        var ws = Create();
        var applied = await ws.ApplyDefaultPlanAsync(new DefaultPlanOptions
        {
            DurationSeconds = 45,
            MaxConcurrency = 12,
            TimeoutSeconds = 10,
            Scenarios =
            [
                new DefaultScenarioEntryOptions
                {
                    ScenarioId = "http-get",
                    Label = "Health",
                    RatePerMinute = 24,
                    Parameters = new System.Text.Json.Nodes.JsonObject { ["url"] = "https://example.test/ping" },
                },
            ],
        });

        Assert.True(applied);
        var state = ws.GetState();
        Assert.Equal(PlanWorkspace.DefaultPresetName, state.PresetName);
        Assert.False(state.HasUnsavedChanges);
        Assert.Equal(45, state.Plan.DurationSeconds);
        Assert.Equal(12, state.Plan.MaxConcurrency);
        Assert.Equal(10, state.Plan.TimeoutSeconds);
        Assert.Single(state.Plan.Scenarios);
        Assert.Equal("Health", state.Plan.Scenarios[0].Label);
        Assert.Equal(24, state.Plan.Scenarios[0].RatePerMinute);
        Assert.Equal("https://example.test/ping", state.Plan.Scenarios[0].Parameters["url"]?.GetValue<string>());
    }

    [Fact]
    public async Task ApplyDefaultPlan_respects_restored_run_unless_ForceDefault_is_true()
    {
        var ws = Create();
        var restored = new LoadPlan
        {
            DurationSeconds = 60,
            Scenarios = [new ScenarioRunConfig { ScenarioId = "http-get", Label = "Restored" }],
        };
        ws.SeedIfEmpty(restored);

        ws.RegisterCodePreset(new Preset("Default", DateTimeOffset.UtcNow, new LoadPlan
        {
            DurationSeconds = 120,
            Scenarios = [new ScenarioRunConfig { ScenarioId = "http-get", Label = "FromDefault" }],
        }));

        // Restored run wins when ForceDefault is false.
        var skipped = await ws.ApplyDefaultPlanAsync(new DefaultPlanOptions { DefaultPreset = "Default" });
        Assert.False(skipped);
        Assert.Equal("Restored", ws.GetPlan().Scenarios[0].Label);

        // ForceDefault overrides the restored run.
        var forced = await ws.ApplyDefaultPlanAsync(new DefaultPlanOptions { DefaultPreset = "Default", ForceDefault = true });
        Assert.True(forced);
        Assert.Equal("Default", ws.GetState().PresetName);
        Assert.Equal("FromDefault", ws.GetPlan().Scenarios[0].Label);
        Assert.Equal(120, ws.GetPlan().DurationSeconds);
    }
}
