using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Beesharp.Client.Data;
using Beesharp.Client.Runs;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

/// <summary>
/// Verifies clean separation of workspace view modes (<see cref="RunViewMode"/>) and titles (<see cref="RunSnapshot.DisplayTitle"/>)
/// across idle plan editing, live runs, finished runs, and historical run inspection.
/// </summary>
public sealed class RunViewModeTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static ScenarioCatalog MakeCatalog() => new([new HttpGetScenario()]);

    private static LoadPlan MakePlan(string label, double rate = 30)
    {
        var sc = new HttpGetScenario();
        var cfg = ScenarioRunConfig.CreateFor(sc);
        cfg.Label = label;
        cfg.RatePerMinute = rate;
        return new LoadPlan { DurationSeconds = 60, MaxConcurrency = 0, Scenarios = [cfg] };
    }

    [Fact]
    public void FakeRunSource_TransitionsCleanlyBetweenFinished_EditToNewPlan_LoadPreset_AndLiveRun()
    {
        var catalog = MakeCatalog();
        using var sim = new FakeRunSource(catalog);

        // 1. Seeded finished run
        Assert.Equal(RunViewMode.FinishedRun, sim.Current.ViewMode);
        Assert.Equal("Black Friday mix", sim.Current.DisplayTitle);
        Assert.True(sim.Current.CanEditPlan);
        Assert.True(sim.Current.Scenarios.Sum(s => s.Finished) > 0);

        // 2. Editing a preset turns it into "New Plan" and clears previous run results
        sim.MarkPlanEdited();
        Assert.Equal(RunViewMode.IdlePlan, sim.Current.ViewMode);
        Assert.Equal("New Plan", sim.Current.DisplayTitle);
        Assert.Equal("New Plan", sim.PresetName);
        Assert.Equal(0, sim.Current.Scenarios.Sum(s => s.Finished));

        // 3. Loading a preset restores its title and stays in IdlePlan
        var preset = sim.PresetNames.First(n => !string.Equals(n, "New Plan", StringComparison.Ordinal));
        sim.LoadPreset(preset);
        Assert.Equal(RunViewMode.IdlePlan, sim.Current.ViewMode);
        Assert.Equal(preset, sim.Current.DisplayTitle);

        // 4. Starting a run transitions to LiveRun (plan becomes read-only)
        sim.Start();
        Assert.Equal(RunViewMode.LiveRun, sim.Current.ViewMode);
        Assert.True(sim.Current.IsLiveRun);
        Assert.True(sim.Current.IsPlanReadOnly);
        Assert.False(sim.Current.CanEditPlan);
        Assert.Equal(preset, sim.Current.DisplayTitle);

        // 5. Stopping transitions to FinishedRun (editable again); EditPlan returns to IdlePlan keeping the preset title
        sim.Stop();
        Assert.Equal(RunViewMode.FinishedRun, sim.Current.ViewMode);
        Assert.True(sim.Current.CanEditPlan);
        sim.EditPlan();
        Assert.Equal(RunViewMode.IdlePlan, sim.Current.ViewMode);
        Assert.Equal(preset, sim.Current.DisplayTitle);

        // 6. New plan clears scenarios, sets title to "New Plan", and clears run results
        sim.ClearEntries();
        Assert.Equal(RunViewMode.IdlePlan, sim.Current.ViewMode);
        Assert.Equal("New Plan", sim.Current.DisplayTitle);
        Assert.Empty(sim.Plan.Scenarios);
        Assert.Empty(sim.Current.Scenarios);
    }

    [Fact]
    public async Task ApiRunSource_HistoryView_UpdatesTitle_IsReadOnly_AndPreservesDraftPlanOnReturnToLive()
    {
        var shopPlan = MakePlan("Shop browse", 90);
        var customPlan = MakePlan("Custom adhoc", 15);
        var startedShop = new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
        var startedCustom = new DateTimeOffset(2026, 11, 28, 11, 15, 30, TimeSpan.Zero);

        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/plan" && req.Method == HttpMethod.Get)
                return JsonResp(new PlanWorkspaceState(shopPlan, "Shop full mix", shopPlan, false, 1));
            if (path == "/api/plan" && req.Method == HttpMethod.Put)
                return JsonResp(new PlanWorkspaceState(new LoadPlan(), "New Plan", new LoadPlan(), false, 2));
            if (path == "/api/presets" && req.Method == HttpMethod.Get)
                return JsonResp(new[] { new Preset("Shop full mix", startedShop, shopPlan) });
            if (path == "/api/runs/run-shop/load" && req.Method == HttpMethod.Post)
                return JsonResp(MakeStatus("run-shop", "Finished", shopPlan, startedShop, finished: 12));
            if (path == "/api/runs/run-custom/load" && req.Method == HttpMethod.Post)
                return JsonResp(MakeStatus("run-custom", "Finished", customPlan, startedCustom, finished: 5));
            if (path == "/api/runs/current")
                return JsonResp(MakeStatus("run-shop", "Finished", shopPlan, startedShop, finished: 12));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var catalog = MakeCatalog();
        var envs = new ApiEnvironmentSource(http);
        using var src = new ApiRunSource(http, catalog, envs);
        src.StartSync();

        await WaitFor(() => src.HasLoadedPlan && src.PresetNames.Contains("Shop full mix"));
        Assert.Equal(RunViewMode.IdlePlan, src.Current.ViewMode);
        Assert.Equal("Shop full mix", src.Current.DisplayTitle);

        // User edits the plan -> becomes "New Plan" and clears previous run view
        src.ClearEntries();
        Assert.Equal(RunViewMode.IdlePlan, src.Current.ViewMode);
        Assert.Equal("New Plan", src.Current.DisplayTitle);
        Assert.Empty(src.Plan.Scenarios);

        // User loads a historical run matching "Shop full mix" -> title MUST update to "Shop full mix" (not stay "New Plan")
        await src.LoadStoredRunAsync("run-shop");
        Assert.Equal(RunViewMode.HistoricalRun, src.Current.ViewMode);
        Assert.True(src.Current.IsHistorical);
        Assert.True(src.Current.IsPlanReadOnly);
        Assert.False(src.Current.CanEditPlan);
        Assert.Equal("Shop full mix", src.Current.DisplayTitle);
        // Draft plan must remain untouched!
        Assert.Equal("New Plan", src.PresetName);
        Assert.Empty(src.Plan.Scenarios);

        // User loads a historical run with a custom/unmatched plan -> title updates to timestamped run label
        await src.LoadStoredRunAsync("run-custom");
        Assert.Equal(RunViewMode.HistoricalRun, src.Current.ViewMode);
        Assert.StartsWith("Run · ", src.Current.DisplayTitle);
        Assert.NotEqual("New Plan", src.Current.DisplayTitle);

        // Returning to Live restores IdlePlan with the untouched "New Plan" draft
        src.ReturnToLive();
        Assert.Equal(RunViewMode.IdlePlan, src.Current.ViewMode);
        Assert.False(src.Current.IsHistorical);
        Assert.True(src.Current.CanEditPlan);
        Assert.Equal("New Plan", src.Current.DisplayTitle);
    }

    [Fact]
    public async Task ApiRunSource_EditAfterFinishedRun_ClearsRunAndDoesNotRepullDismissedRun()
    {
        var shopPlan = MakePlan("Shop browse", 90);
        var started = new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
        var currentStatus = MakeStatus("run-1", "Finished", shopPlan, started, finished: 20);

        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/plan" && req.Method == HttpMethod.Get)
                return JsonResp(new PlanWorkspaceState(shopPlan, "Shop full mix", shopPlan, false, 1));
            if (path == "/api/plan" && req.Method == HttpMethod.Put)
                return JsonResp(new PlanWorkspaceState(shopPlan, "New Plan", shopPlan, true, 2));
            if (path == "/api/presets")
                return JsonResp(Array.Empty<Preset>());
            if (path == "/api/runs/current")
                return JsonResp(currentStatus);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var catalog = MakeCatalog();
        var envs = new ApiEnvironmentSource(http);
        using var src = new ApiRunSource(http, catalog, envs);
        src.StartSync();
        await WaitFor(() => src.HasLoadedPlan);
        await src.RefreshAsync();

        // Initially showing finished run-1
        Assert.Equal(RunViewMode.FinishedRun, src.Current.ViewMode);
        Assert.Equal(20, src.Current.Scenarios[0].Finished);

        // User edits plan -> becomes "New Plan" and clears run-1
        src.MarkPlanEdited();
        Assert.Equal(RunViewMode.IdlePlan, src.Current.ViewMode);
        Assert.Equal("New Plan", src.Current.DisplayTitle);
        Assert.Equal("", src.CurrentRunId);
        Assert.Equal(0, src.Current.Scenarios.Sum(s => s.Finished));

        // Even when a remote run-2 starts, client picks it up as LiveRun
        currentStatus = MakeStatus("run-2", "Running", shopPlan, started.AddMinutes(1), finished: 3);
        await WaitFor(() => src.Current.ViewMode == RunViewMode.LiveRun, timeoutMs: 4000);
        Assert.Equal("run-2", src.CurrentRunId);
        Assert.True(src.Current.IsPlanReadOnly);
    }

    [Fact]
    public async Task ApiRunSource_InitialPlanSync_DoesNotResetLoadedRunToIdle()
    {
        var shopPlan = MakePlan("Shop browse", 90);
        var started = new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
        var finishedStatus = MakeStatus("run-hist-1", "Finished", shopPlan, started, finished: 15);
        var unloadCalled = false;

        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/runs/current")
                return JsonResp(finishedStatus);
            if (path == "/api/runs/unload")
            {
                unloadCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (path == "/api/plan" && req.Method == HttpMethod.Get)
            {
                if (req.RequestUri.Query.Contains("knownVersion=1", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                return JsonResp(new PlanWorkspaceState(shopPlan, "Shop full mix", shopPlan, false, 1));
            }
            if (path == "/api/presets")
                return JsonResp(new List<Preset> { new("Shop full mix", started, shopPlan) });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var catalog = MakeCatalog();
        var envs = new ApiEnvironmentSource(http);
        using var src = new ApiRunSource(http, catalog, envs);

        // Simulate reload where RefreshAsync finishes before StartSync's initial GET /api/plan
        await src.RefreshAsync();
        Assert.Equal(RunViewMode.FinishedRun, src.Current.ViewMode);
        Assert.Equal("run-hist-1", src.CurrentRunId);

        src.StartSync();
        await WaitFor(() => src.HasLoadedPlan, timeoutMs: 3000);

        // Initial plan adoption must NOT wipe the loaded run back to IdlePlan
        Assert.Equal(RunViewMode.FinishedRun, src.Current.ViewMode);
        Assert.Equal("run-hist-1", src.CurrentRunId);
        Assert.False(unloadCalled);
    }

    private static LiveRunStatusDto MakeStatus(string runId, string state, LoadPlan plan, DateTimeOffset startedUtc, int finished) =>
        new(
            RunId: runId,
            State: state,
            ElapsedSeconds: 5,
            StartedUtc: startedUtc,
            Plan: plan,
            Scenarios: [new LiveScenarioViewDto(finished, finished, 0, 0, 0, 0, 0)],
            NewBees: Enumerable.Range(0, finished).Select(i => new LiveBeeDto(i, 0, i * 0.1, 100, 200, 128, null, 0)).ToList(),
            LastSequence: finished - 1,
            TotalLogs: 0,
            StorageProvider: "Memory",
            Error: null);

    private static HttpResponseMessage JsonResp<T>(T value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: JsonOpts) };

    private static async Task WaitFor(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!predicate() && Environment.TickCount64 < deadline)
            await Task.Delay(25);
        Assert.True(predicate(), "Condition was not satisfied before timeout.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
