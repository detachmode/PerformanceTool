using Beesharp.Ai;
using Beesharp.AiAdapters;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;
using Beesharp.Services.Storage;

namespace Beesharp.Tests;

/// <summary>
/// Integration stories for all host AI adapters (<see cref="EnvironmentAdapter"/>,
/// <see cref="ScriptAdapter"/>, <see cref="PlanWorkspaceAdapter"/>, <see cref="RunAdapter"/>,
/// <see cref="RunHistoryAdapter"/>) wired directly to real engine, scripting and storage services.
/// </summary>
public sealed class HostAiAdaptersTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beesharp-adapters-{Guid.NewGuid():N}");

    public HostAiAdaptersTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private sealed record StubParams(bool Fail = false);

    private sealed class StubScenario : Scenario<StubParams>
    {
        public override string Id => "stub-work";
        public override string Name => "Stub Work";
        public override string Description => "Fast stub for adapter tests.";

        protected override Task<ScenarioOutcome> ExecuteAsync(StubParams p, ScenarioContext ctx, CancellationToken ct)
        {
            ctx.Log(p.Fail ? "stub failed" : "stub succeeded");
            ctx.RecordCall("GET /stub", 15, p.Fail ? 500 : 200, 12, p.Fail ? "HTTP 500" : null);
            return Task.FromResult(p.Fail ? ScenarioOutcome.Failed("Stub error", 500) : new ScenarioOutcome(200, 12));
        }
    }

    [Fact]
    public async Task Environment_and_script_adapters_bridge_assistant_ports_to_real_stores()
    {
        var envStore = new EnvironmentStore(Path.Combine(root, "environments"));
        var envAdapter = new EnvironmentAdapter(envStore);

        // Environment CRUD through the adapter.
        await envAdapter.SaveAsync(new EnvironmentInfo("Staging", [new("BaseUrl", "https://staging.test")]), default);
        Assert.Single(await envAdapter.ListAsync(default));
        var got = await envAdapter.GetAsync("Staging", default);
        Assert.Equal("https://staging.test", got!.Variables[0].Value);
        Assert.Null(await envAdapter.GetAsync("Prod", default));
        Assert.True(await envAdapter.DeleteAsync("Staging", default));
        Assert.False(await envAdapter.DeleteAsync("Staging", default));

        // Scenario script & preflight CRUD + test runs through ScriptAdapter.
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var scriptStore = new ScriptScenarioStore(Path.Combine(root, "scripts"), catalog, envStore);
        var preflightStore = new PreflightStore(Path.Combine(root, "scripts", "preflight"), envStore);
        var scriptAdapter = new ScriptAdapter(scriptStore, preflightStore);

        var uploaded = await scriptAdapter.UploadScriptAsync("ping", "ping.cs",
            """Scenario.Define("Ping", ctx => { ctx.Log("pong"); return Task.CompletedTask; }, id: "script-ping");""", default);
        Assert.True(uploaded.Success, uploaded.Error);
        Assert.Single(scriptAdapter.ListScripts());
        Assert.NotNull(scriptAdapter.GetScript("ping"));
        Assert.Null(scriptAdapter.GetScript("missing"));

        var testOutcome = await scriptAdapter.TestRunScriptAsync("ping", null, null, 15, default);
        Assert.True(testOutcome.Success);
        Assert.Contains(testOutcome.Logs, l => l.Contains("pong"));
        Assert.True(scriptAdapter.DeleteScript("ping"));

        var preUploaded = await scriptAdapter.UploadPreflightAsync("ready", "ready.cs", """Log("all systems go");""", default);
        Assert.True(preUploaded.Success, preUploaded.Error);
        Assert.Single(scriptAdapter.ListPreflights());
        Assert.NotNull(scriptAdapter.GetPreflight("ready"));
        Assert.Null(scriptAdapter.GetPreflight("missing"));

        var preRun = await scriptAdapter.TestRunPreflightAsync("ready", null, 15, default);
        Assert.True(preRun.Success);
        Assert.Contains(preRun.Logs, l => l.Contains("all systems go"));
        Assert.True(scriptAdapter.DeletePreflight("ready"));
    }

    [Fact]
    public async Task Plan_run_and_history_adapters_cover_live_and_stored_runs_validation_and_queries()
    {
        var catalog = new ScenarioCatalog([new StubScenario()]);
        var presetStore = new PresetStore(Path.Combine(root, "presets"));
        var workspace = new PlanWorkspace(catalog, presetStore);
        var envStore = new EnvironmentStore(Path.Combine(root, "environments"));
        await using var storage = new InMemoryRunStorage();
        var runManager = new RunManager(new LoadRunner(catalog), envStore, storage);

        var planAdapter = new PlanWorkspaceAdapter(workspace);
        var runAdapter = new RunAdapter(runManager, workspace, catalog);
        var historyAdapter = new RunHistoryAdapter(runManager, storage);

        // Before any run exists, RunAdapter and RunHistoryAdapter report null cleanly.
        Assert.Null(runAdapter.GetCurrent());
        Assert.Null(await historyAdapter.LatestRunIdAsync(default));

        // Starting an empty/invalid plan fails with a clear ArgumentException.
        await Assert.ThrowsAsync<ArgumentException>(() => runAdapter.StartAsync(null, default));

        // Build a plan via PlanWorkspaceAdapter, save/load/list/delete a preset, then run it.
        var idx0 = planAdapter.AddEntry("stub-work");
        var idx1 = planAdapter.AddEntry("stub-work");
        Assert.Equal(0, idx0);
        Assert.Equal(1, idx1);
        planAdapter.Update(p =>
        {
            p.DurationSeconds = 1;
            p.Scenarios[0].Label = "Healthy";
            p.Scenarios[0].RatePerMinute = 180;
            p.Scenarios[1].Label = "Failing";
            p.Scenarios[1].RatePerMinute = 180;
            p.Scenarios[1].Parameters["fail"] = true;
            return true;
        });

        await planAdapter.SavePresetAsync("AdapterPreset", default);
        Assert.Single(await planAdapter.ListPresetsAsync(default));
        Assert.True(await planAdapter.LoadPresetAsync("AdapterPreset", default));
        Assert.True(await planAdapter.DeletePresetAsync("AdapterPreset", default));

        // Add & remove a temporary entry.
        var tempIdx = planAdapter.AddEntry("stub-work");
        planAdapter.RemoveEntry(tempIdx);

        // Start a real run and inspect live & finished history through the adapters.
        var live = await runAdapter.StartAsync(null, default);
        Assert.False(string.IsNullOrEmpty(live.RunId));
        await runManager.WaitUntilFinishedAsync();
        await storage.FlushAsync();

        var latestId = await historyAdapter.LatestRunIdAsync(default);
        Assert.Equal(live.RunId, latestId);

        var allRuns = await historyAdapter.ListRunsAsync(10, default);
        Assert.Contains(allRuns, r => r.RunId == live.RunId);

        var liveSummary = await historyAdapter.GetRunAsync(live.RunId, default);
        Assert.NotNull(liveSummary);
        Assert.Equal(2, liveSummary!.Entries.Count);

        // Query bees on the live/current run (failed first, then ok), pick specific sequences, get bee detail and logs.
        var allBees = await historyAdapter.ListBeesAsync(live.RunId, entry: null, BeeOutcomeFilter.All, 0, 50, default);
        Assert.True(allBees.Total > 0);
        Assert.Equal("failed", allBees.Items[0].Outcome); // failed-first ordering

        var picked = await historyAdapter.GetBeesAsync(live.RunId, [allBees.Items[0].Sequence], default);
        Assert.Single(picked);

        var detail = await historyAdapter.GetBeeAsync(live.RunId, allBees.Items[0].Sequence, default);
        Assert.NotNull(detail);
        Assert.NotEmpty(detail!.Calls);
        Assert.Null(await historyAdapter.GetBeeAsync(live.RunId, 999_999, default));

        var logs = await historyAdapter.QueryLogsAsync(live.RunId, new LogFilter(Search: "stub"), default);
        Assert.True(logs.Total > 0);

        // Also seed a separate historical (non-current) stored run to verify the stored-run fallback branch!
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        storage.EnqueueRunStarted("historic-1", started, workspace.GetPlan());
        storage.EnqueueBeeCompleted(
            "historic-1",
            new ExecutionResult(0, 0, 0.1, 100, 200, 12, null, [new RecordedCall("GET /stub", 0, 100, 200, 12, null)], []),
            beeOutcome: 0, slowCalls: 0);
        storage.EnqueueBeeCompleted(
            "historic-1",
            new ExecutionResult(1, 1, 0.2, 150, 500, 0, "boom", [new RecordedCall("GET /stub", 0, 150, 500, 0, "HTTP 500")], []),
            beeOutcome: 2, slowCalls: 0);
        storage.EnqueueRunFinished("historic-1", started.AddSeconds(2), "Finished", 2, null);
        await storage.FlushAsync();

        var historic = await historyAdapter.GetRunAsync("historic-1", default);
        Assert.NotNull(historic);
        Assert.Equal(2, historic!.TotalBees);
        Assert.Equal(1, historic.FailedBees);

        var historicFailed = await historyAdapter.ListBeesAsync("historic-1", null, BeeOutcomeFilter.Failed, 0, 10, default);
        Assert.Single(historicFailed.Items);
        Assert.Null(await historyAdapter.GetRunAsync("does-not-exist", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => historyAdapter.ListBeesAsync("does-not-exist", null, BeeOutcomeFilter.All, 0, 10, default));
    }
}
