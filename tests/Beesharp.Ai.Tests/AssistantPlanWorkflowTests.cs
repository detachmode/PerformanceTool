using System.Text.Json;
using System.Text.Json.Nodes;
using Beesharp.Ai;
using Beesharp.Ai.Contracts;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Ai.Tests;

/// <summary>
/// Stories of an operator using the assistant's plan &amp; preset tools
/// (<c>AssistantToolbox.Plan.cs</c> and <c>ToolSupport.cs</c>) to inspect scenarios, build and edit
/// a mixed linear + burst load plan, verify positional plan diffs with secret stripping, and manage presets.
/// </summary>
public sealed class AssistantPlanWorkflowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static JsonElement ToJson(object result) =>
        JsonSerializer.SerializeToElement(result, Json);

    [Fact]
    public async Task Operator_discovers_scenarios_builds_a_mixed_plan_saves_a_preset_and_inspects_changes()
    {
        var catalog = TestCatalogFactory.CreateWithSteps();
        var planPort = new InMemoryPlanPort(catalog);
        var scripts = new InMemoryScriptPort();
        scripts.Preflights["check-ready"] = new ScriptInfo("check-ready", "check-ready.cs", "Check ready", null, "", DateTimeOffset.UtcNow, "Log(\"ok\");");

        var recorder = new TurnRecorder();
        var toolbox = new AssistantToolbox(planPort, new InMemoryRunPort(), new InMemoryHistoryPort(), new InMemoryEnvironmentPort(), scripts, catalog, recorder);

        // 1. Discover built-in and custom stepped scenarios, and inspect parameter schema for a stepped scenario.
        var allScenarios = ToJson(toolbox.ListScenarios());
        Assert.Equal(3, allScenarios.GetArrayLength());

        var stepped = ToJson(toolbox.GetScenario("script-checkout"));
        Assert.True(stepped.GetProperty("isCustom").GetBoolean());
        Assert.Equal(2, stepped.GetProperty("steps").GetArrayLength());
        Assert.Throws<KeyNotFoundException>(() => toolbox.GetScenario("does-not-exist"));

        // 2. Add two entries: a linear health check (warning on stray param) and an authenticated custom request.
        var added1 = ToJson(toolbox.AddScenarioEntry(
            "http-get",
            label: "API Health",
            beesPerMinute: 30,
            parameters: new JsonObject { ["url"] = "https://shop.test/health", ["unknownKey"] = 123 }));
        Assert.Contains("unknownKey", added1.GetProperty("warnings")[0].GetString());

        toolbox.AddScenarioEntry(
            "http-custom",
            label: "Orders API",
            beesPerMinute: 20,
            parameters: new JsonObject
            {
                ["url"] = "https://shop.test/orders",
                ["auth"] = new JsonObject { ["scheme"] = "Bearer", ["token"] = "top-secret-token" },
            });

        // 3. Save baseline preset, then start a fresh turn to edit it and verify PlanFormat.Diff + ShowPlanChanges.
        await toolbox.SavePreset("Baseline", default);
        var presetList = ToJson(await toolbox.ListPresets(default));
        Assert.Equal("Baseline", presetList.GetProperty("loaded").GetString());
        Assert.Single(presetList.GetProperty("presets").EnumerateArray());

        var editRecorder = new TurnRecorder();
        var editToolbox = new AssistantToolbox(planPort, new InMemoryRunPort(), new InMemoryHistoryPort(), new InMemoryEnvironmentPort(), scripts, catalog, editRecorder);

        var settingsView = ToJson(editToolbox.SetPlanSettings(durationSeconds: 180, maxInFlight: 80, timeoutSeconds: 25, preflightScriptId: "check-ready"));
        Assert.Equal(180, settingsView.GetProperty("durationSeconds").GetInt32());
        Assert.Equal("check-ready", settingsView.GetProperty("preflightScriptId").GetString());

        // Calling SetPlanSettings with identical values leaves plan version unchanged; unknown preflight throws.
        var versionBeforeNoOp = planPort.Get().Version;
        var noOpSettings = ToJson(editToolbox.SetPlanSettings(durationSeconds: 180));
        Assert.Equal(180, noOpSettings.GetProperty("durationSeconds").GetInt32());
        Assert.Equal(versionBeforeNoOp, planPort.Get().Version);
        Assert.Throws<KeyNotFoundException>(() => editToolbox.SetPlanSettings(preflightScriptId: "missing-pre"));

        // Switch entry 1 to bursts, rename it, and update its URL.
        var edited = ToJson(editToolbox.UpdateScenarioEntry(
            entry: 1,
            label: "Health Bursts",
            pattern: "bursts",
            beesPerBurst: 12,
            burstsPerMinute: 5,
            parameters: new JsonObject { ["url"] = "https://shop.test/ready" }));
        Assert.Equal("bursts", edited.GetProperty("entry").GetProperty("pattern").GetString());
        Assert.Equal(60, edited.GetProperty("entry").GetProperty("effectiveBeesPerMinute").GetDouble());

        // Calling UpdateScenarioEntry with no changes keeps all fields intact and produces no warnings/problems.
        var noOpEntry = ToJson(editToolbox.UpdateScenarioEntry(entry: 1));
        Assert.Equal("Health Bursts", noOpEntry.GetProperty("entry").GetProperty("label").GetString());
        Assert.Empty(noOpEntry.GetProperty("warnings").EnumerateArray());
        Assert.Empty(noOpEntry.GetProperty("problems").EnumerateArray());

        // Remove entry 2, verify secrets never leak in GetPlan, and inspect the positional diff card.
        var removed = ToJson(editToolbox.RemoveScenarioEntry(entry: 2));
        Assert.Equal("Orders API", removed.GetProperty("removed").GetString());
        Assert.Equal(1, removed.GetProperty("entriesLeft").GetInt32());

        var planView = ToJson(editToolbox.GetPlan());
        Assert.DoesNotContain("top-secret-token", planView.GetRawText());

        var diffView = ToJson(editToolbox.ShowPlanChanges());
        var changes = diffView.GetProperty("changes").EnumerateArray().ToList();
        Assert.Contains(changes, c => c.GetProperty("label").GetString() == "Plan settings"
            && c.GetProperty("details").EnumerateArray().Any(d => d.GetString()!.Contains("180")));
        Assert.Contains(changes, c => c.GetProperty("what").GetString() == "changed" && c.GetProperty("label").GetString() == "Health Bursts");
        Assert.Contains(changes, c => c.GetProperty("what").GetString() == "removed" && c.GetProperty("label").GetString() == "Orders API");

        // 4. Reload the saved Baseline preset and then delete it cleanly.
        var reloaded = ToJson(await editToolbox.LoadPreset("Baseline", default));
        Assert.Equal(2, reloaded.GetProperty("entries").GetArrayLength());

        var deleted = ToJson(await editToolbox.DeletePreset("Baseline", default));
        Assert.Equal("Baseline", deleted.GetProperty("deleted").GetString());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => editToolbox.LoadPreset("Baseline", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => editToolbox.DeletePreset("Baseline", default));
    }

    [Fact]
    public void Plan_tools_validate_entry_numbers_patterns_and_burst_diff_details()
    {
        var catalog = TestCatalogFactory.CreateWithSteps();
        var planPort = new InMemoryPlanPort(catalog);
        var toolbox = new AssistantToolbox(planPort, new InMemoryRunPort(), new InMemoryHistoryPort(), new InMemoryEnvironmentPort(), new InMemoryScriptPort(), catalog, new TurnRecorder());

        toolbox.AddScenarioEntry("http-get", label: "Burst Check");

        // Invalid entry indices and invalid spawning pattern produce crisp ArgumentExceptions.
        Assert.Throws<ArgumentException>(() => toolbox.UpdateScenarioEntry(entry: 0, beesPerMinute: 10));
        Assert.Throws<ArgumentException>(() => toolbox.UpdateScenarioEntry(entry: 5, beesPerMinute: 10));
        Assert.Throws<ArgumentException>(() => toolbox.RemoveScenarioEntry(entry: 5));
        Assert.Throws<ArgumentException>(() => toolbox.UpdateScenarioEntry(entry: 1, pattern: "exponential"));

        // Verify burst-to-burst resizing diff & clearing preflight with empty string through ShowPlanChanges.
        planPort.Update(p =>
        {
            p.Scenarios[0].Pattern = SpawningPattern.Bursts;
            p.Scenarios[0].BeesPerBurst = 5;
            p.Scenarios[0].BurstsPerMinute = 4;
            p.PreflightScriptId = "warm";
            return true;
        });

        var diffRec = new TurnRecorder();
        var diffToolbox = new AssistantToolbox(planPort, new InMemoryRunPort(), new InMemoryHistoryPort(), new InMemoryEnvironmentPort(), new InMemoryScriptPort(), catalog, diffRec);
        diffToolbox.UpdateScenarioEntry(entry: 1, beesPerBurst: 10, burstsPerMinute: 2);
        var cleared = ToJson(diffToolbox.SetPlanSettings(preflightScriptId: ""));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("preflightScriptId").ValueKind);

        var burstDiff = ToJson(diffToolbox.ShowPlanChanges()).GetProperty("changes").EnumerateArray().ToList();
        Assert.Contains(burstDiff, d => d.GetProperty("label").GetString() == "Plan settings"
            && d.GetProperty("details").EnumerateArray().Any(s => s.GetString() == "preflight warm → none"));
        Assert.Contains(burstDiff, d => d.GetProperty("what").GetString() == "changed"
            && d.GetProperty("details").EnumerateArray().Any(s => s.GetString()!.Contains("burst 5 × 4/min → 10 × 2/min")));
    }
}
