using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beesharp.Client.Chart;
using Beesharp.Client.Data;
using Beesharp.Client.Logs;
using Beesharp.Client.Runs;
using Beesharp.Services;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

/// <summary>
/// Focused stories for <see cref="ApiRunSource"/>: Live plan sync (brain 0050), live run polling &amp; control,
/// lazy bee detail caching &amp; retry backoff, and history / log / call-endpoint queries.
/// </summary>
public sealed class ApiRunSourceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Live_plan_sync_pushes_editor_edits_manages_presets_and_pulls_server_changes()
    {
        var gate = new Lock();
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var serverPlan = new LoadPlan { DurationSeconds = 30, Scenarios = [ScenarioRunConfig.CreateFor(catalog.Find("http-get")!)] };
        serverPlan.Scenarios[0].Label = "Server Entry";
        long version = 1;
        var presetName = "Default";
        var presets = new List<Preset> { new("Default", DateTimeOffset.UtcNow, serverPlan.Clone()) };
        var presetLoadedOnServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handler = new StubHttpHandler(async (req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var query = req.RequestUri.Query;

            if (req.Method == HttpMethod.Get && path == "/api/plan")
            {
                lock (gate)
                {
                    if (query.Contains($"knownVersion={version}", StringComparison.Ordinal))
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    return StubHttpHandler.JsonResponse(new PlanWorkspaceState(serverPlan.Clone(), presetName, serverPlan.Clone(), false, version));
                }
            }
            if (req.Method == HttpMethod.Put && path == "/api/plan")
            {
                var body = (await JsonSerializer.DeserializeAsync<LoadPlan>(await req.Content!.ReadAsStreamAsync(ct), Json, ct))!;
                lock (gate)
                {
                    serverPlan = body;
                    version++;
                    return StubHttpHandler.JsonResponse(new PlanWorkspaceState(serverPlan.Clone(), presetName, serverPlan.Clone(), false, version));
                }
            }
            if (req.Method == HttpMethod.Get && path == "/api/presets")
            {
                lock (gate) return StubHttpHandler.JsonResponse(presets.ToList());
            }
            if (req.Method == HttpMethod.Put && path.StartsWith("/api/presets/", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path["/api/presets/".Length..]);
                var body = (await JsonSerializer.DeserializeAsync<LoadPlan>(await req.Content!.ReadAsStreamAsync(ct), Json, ct))!;
                lock (gate)
                {
                    presets.RemoveAll(p => p.Name == name);
                    presets.Add(new Preset(name, DateTimeOffset.UtcNow, body.Clone()));
                    presetName = name;
                    serverPlan = body.Clone();
                    version++;
                    return StubHttpHandler.JsonResponse(new PlanWorkspaceState(serverPlan.Clone(), presetName, serverPlan.Clone(), false, version));
                }
            }
            if (req.Method == HttpMethod.Post && path.EndsWith("/load", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path["/api/presets/".Length..^"/load".Length]);
                lock (gate)
                {
                    var found = presets.First(p => p.Name == name);
                    serverPlan = found.Plan.Clone();
                    presetName = found.Name;
                    version++;
                    presetLoadedOnServer.TrySetResult();
                    return StubHttpHandler.JsonResponse(new PlanWorkspaceState(serverPlan.Clone(), presetName, serverPlan.Clone(), false, version));
                }
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };
        var envs = new ApiEnvironmentSource(http);
        using var source = new ApiRunSource(http, catalog, envs);

        // 1. StartSync pulls the initial server plan and preset list.
        source.StartSync();
        await WaitFor(() => source.Plan.Scenarios.Count == 1 && source.PresetNames.Contains("Default"));
        Assert.Equal("Server Entry", source.Plan.Scenarios[0].Label);
        Assert.False(source.HasUnsavedChanges);

        // 2. Local editor edits mark HasUnsavedChanges & IsEntryChanged, then SyncLoop pushes to the server.
        source.AddEntry(catalog.Find("http-get")!);
        Assert.Equal(2, source.Plan.Scenarios.Count);
        Assert.Equal("HTTP GET #2", source.Plan.Scenarios[1].Label);
        Assert.True(source.HasUnsavedChanges);
        Assert.True(source.HasUnpushedEdits);
        Assert.True(source.IsEntryChanged(source.Plan.Scenarios[1]));

        await WaitFor(() => { lock (gate) return serverPlan.Scenarios.Count == 2; });
        await WaitFor(() => !source.HasUnpushedEdits);

        // 3. Save as a new preset, remove an entry locally, then reload the saved preset and wait for the server round-trip.
        source.SavePreset("Peak");
        await WaitFor(() => { lock (gate) return presets.Any(p => p.Name == "Peak"); });
        Assert.False(source.HasUnsavedChanges);

        source.RemoveEntry(1);
        Assert.Single(source.Plan.Scenarios);

        source.LoadPreset("Peak");
        Assert.Equal(2, source.Plan.Scenarios.Count);
        Assert.Equal(2, source.Current.Count);
        Assert.Equal("Peak", source.PresetName);
        Assert.Equal(FakeRunState.Idle, source.Current.State);
        await presetLoadedOnServer.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitFor(() => !source.HasUnpushedEdits);

        // 4. Server-side change (e.g. from the AI assistant) is pulled immediately via SyncPlanNowAsync.
        lock (gate)
        {
            serverPlan.DurationSeconds = 120;
            version++;
        }
        await source.SyncPlanNowAsync();
        Assert.Equal(120, source.Plan.DurationSeconds);

        // 5. Conflict rule (brain 0050): unpushed local edits win over a newer server version on pull.
        source.Plan.DurationSeconds = 95;
        Assert.True(source.HasUnpushedEdits);
        lock (gate)
        {
            serverPlan.DurationSeconds = 300;
            version++;
        }
        await source.SyncPlanNowAsync();
        Assert.Equal(95, source.Plan.DurationSeconds);

        source.StopSync();
    }

    [Fact]
    public async Task Live_run_polling_deduplicates_bees_tracks_runtime_and_handles_pause_resume_and_shutdown()
    {
        var gate = new Lock();
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var runPlan = new LoadPlan
        {
            DurationSeconds = 30,
            Scenarios = [ScenarioRunConfig.CreateFor(catalog.Find("http-get")!)],
        };
        runPlan.Scenarios[0].Label = "Checkout";

        var startedUtc = DateTimeOffset.UtcNow;
        var state = "Running";
        var pollCount = 0;
        var shutdownPollsAfterStop = 0;
        var pollReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var bee0 = new LiveBeeDto(0, 0, 0.5, 140, 200, 128, null, 0, RatioCode: 1, FailedStep: -1, FailedRep: -1);
        var bee1 = new LiveBeeDto(1, 0, 1.2, 620, 500, 0, "HTTP 500", 1, RatioCode: 4, FailedStep: 1, FailedRep: 0);
        var runningBee = new LiveRunningBeeDto(2, 0, 2.0, Step: 0, Rep: 0, StepVisits: [new RecordedStepVisit(0, 0, null)]);
        IReadOnlyList<IReadOnlyList<StepInfo>> steps = [[new StepInfo("Sign in"), new StepInfo("Pay", Times: 2)]];

        LiveRunStatusDto MakeStatus(string s, List<LiveBeeDto> bees, List<LiveRunningBeeDto>? inFlight = null) =>
            new(
                RunId: "run-1",
                State: s,
                ElapsedSeconds: 2.5,
                StartedUtc: startedUtc,
                Plan: runPlan,
                Scenarios: [new LiveScenarioViewDto(bees.Count + (inFlight?.Count ?? 0), bees.Count, inFlight?.Count ?? 0, bees.Count(b => b.Error is not null), bees.Count(b => b.SlowCalls > 0), bees.Sum(b => b.SlowCalls), 0)],
                NewBees: bees,
                LastSequence: bees.Count > 0 ? bees.Max(b => b.Sequence) : -1,
                TotalLogs: 12,
                StorageProvider: "DuckDb",
                Error: null,
                PreflightState: "Succeeded",
                PreflightScriptName: "warmup.cs",
                PreflightLogs: ["preflight ok"],
                ShutdownState: s is "Shutdown" ? "Running" : s == "Finished" ? "Succeeded" : null,
                ShutdownScriptName: s is "Shutdown" or "Finished" ? "cleanup.cs" : null,
                ShutdownLogs: s == "Finished" ? ["cleanup ok"] : null,
                RuntimeVersion: 1,
                NextBeeIndex: bees.Count,
                Steps: steps,
                RunningBees: inFlight);

        using var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            lock (gate)
            {
                if (req.Method == HttpMethod.Post && path == "/api/runs/start")
                    return StubHttpHandler.JsonResponse(MakeStatus("Running", [bee0], [runningBee]));

                if (req.Method == HttpMethod.Get && path == "/api/runs/current")
                {
                    pollCount++;
                    // Deliver bee0 again alongside bee1 to verify overlapping poll deduplication.
                    if (state == "Shutdown" && ++shutdownPollsAfterStop >= 1)
                        state = "Finished";
                    var dto = MakeStatus(state, [bee0, bee1]);
                    pollReceived.TrySetResult();
                    return StubHttpHandler.JsonResponse(dto);
                }

                if (req.Method == HttpMethod.Post && path == "/api/runs/pause")
                {
                    state = "Paused";
                    return StubHttpHandler.JsonResponse(MakeStatus("Paused", [bee0, bee1]));
                }
                if (req.Method == HttpMethod.Post && path == "/api/runs/resume")
                {
                    state = "Running";
                    return StubHttpHandler.JsonResponse(MakeStatus("Running", [bee0, bee1]));
                }
                if (req.Method == HttpMethod.Post && path == "/api/runs/stop")
                {
                    // Return Shutdown first (brain 0052) so ApiRunSource keeps polling until Finished!
                    state = "Shutdown";
                    return StubHttpHandler.JsonResponse(MakeStatus("Shutdown", [bee0, bee1]));
                }
                if (req.Method == HttpMethod.Get && path == "/api/runs/run-1/runtime")
                {
                    var snap = new RuntimeDataSnapshot(
                        new RuntimeBag(-1, [new RuntimeValue("tenant", JsonValue.Create("acme"), new DataWriter(DataWriterKind.Preflight), 0.1)]),
                        [new RuntimeBag(0, [])]);
                    return StubHttpHandler.JsonResponse(snap);
                }
                if (req.Method == HttpMethod.Get && path == "/api/runs/live-logs")
                    return StubHttpHandler.JsonResponse(new List<LiveLogEntryDto> { new(1, 0, "B1", 0, 0.5, startedUtc, "Information", "live log", null) });
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };
        var envs = new ApiEnvironmentSource(http);
        using var source = new ApiRunSource(http, catalog, envs);

        // 1. Start a live run -> receives running bees, steps, preflight status, and runtime data.
        source.AddEntry(catalog.Find("http-get")!);
        source.Start();
        await WaitFor(() => source.CurrentRunId == "run-1" && source.Current.Runtime?.TotalValues == 1);

        Assert.True(source.IsRunning);
        Assert.NotNull(source.ActiveStartedUtc);
        Assert.Equal(startedUtc, source.CurrentStartedUtc);
        Assert.Equal("DuckDb", source.StorageProvider);
        Assert.Equal("Succeeded", source.PreflightState);
        Assert.NotNull(source.Current.Steps);
        Assert.Single(source.Current.BeesInFlight![0]);

        source.DismissPreflight();
        Assert.True(source.PreflightDismissed);

        // 2. Wait for the background poll loop to actually hit GET /api/runs/current and deduplicate bee0!
        await pollReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitFor(() => source.Current.Store.Bees(0).Count == 2);
        Assert.True(Volatile.Read(ref pollCount) >= 1);

        // 3. Pause -> Resume -> Stop (stays active during Shutdown and polls until Finished).
        source.Pause();
        await WaitFor(() => source.Current.State == FakeRunState.Paused);

        source.Resume();
        await WaitFor(() => source.Current.State == FakeRunState.Running);

        source.Stop();
        await WaitFor(() => source.Current.State == FakeRunState.Finished);
        Assert.NotNull(source.Current.Shutdown);
        Assert.Equal("cleanup.cs", source.Current.Shutdown!.Name);

        // 4. Live logs endpoint forwards cursor, sequence and entry query params.
        var liveLogs = await source.FetchLiveLogsAsync(afterId: 0, limit: 10, sequence: 0, entry: 0);
        Assert.Single(liveLogs);
        Assert.True(handler.HasRequest(r => r.PathAndQuery == "/api/runs/live-logs?afterId=0&limit=10&sequence=0&entry=0"));
    }

    [Fact]
    public async Task Lazy_bee_detail_caches_success_backs_off_after_miss_and_converts_calls_and_logs()
    {
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var runPlan = new LoadPlan { DurationSeconds = 10, Scenarios = [ScenarioRunConfig.CreateFor(catalog.Find("http-get")!)] };
        var startedUtc = DateTimeOffset.UtcNow;
        var bee1 = new LiveBeeDto(1, 0, 1.2, 620, 500, 0, "HTTP 500", 1, RatioCode: 4, FailedStep: 1, FailedRep: 0);
        var failDetailFetch = false;

        using var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path == "/api/runs/current")
                return StubHttpHandler.JsonResponse(new LiveRunStatusDto("run-1", "Finished", 2.0, startedUtc, runPlan, [new LiveScenarioViewDto(1, 1, 0, 1, 0, 0, 0)], [bee1], 1, 1, "Memory", null));
            if (req.Method == HttpMethod.Get && path == "/api/runs/run-1/runtime")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (req.Method == HttpMethod.Get && path == "/api/runs/run-1/bees/1")
            {
                return StubHttpHandler.JsonResponse(new StoredBeeDetail(
                    "run-1", 1, 0, 1.2, 620, 500, 0, "HTTP 500",
                    [new RecordedCall("POST /pay", 0, 620, 500, 0, "HTTP 500", 1, 0, "POST /pay", "POST")],
                    [new RecordedLog(1.2, "Error", "payment failed", "Boom", 0, 1, 0)],
                    FailedStep: 1, FailedRep: 0));
            }
            if (req.Method == HttpMethod.Get && path == "/api/runs/run-1/bees/99")
                return failDetailFetch ? throw new HttpRequestException("offline") : new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };
        using var source = new ApiRunSource(http, catalog, new ApiEnvironmentSource(http));

        await source.RefreshAsync();
        Assert.Equal("run-1", source.CurrentRunId);

        // First fetch loads calls AND converts lazy logs via RemoteBeeDetail; second call hits cache.
        await source.EnsureBeeDetailLoadedAsync(1);
        var loadedDetail = source.Current.Store.Detail(1);
        Assert.NotNull(loadedDetail);
        Assert.Equal("POST /pay", loadedDetail!.Calls[0].Name);
        var convertedLogs = loadedDetail.Logs;
        var logEntry = Assert.Single(convertedLogs);
        Assert.Equal("Error", logEntry.Level);
        Assert.Equal("payment failed", logEntry.Message);
        Assert.Equal("Boom", logEntry.Exception);
        Assert.Equal("B2", logEntry.Scope["Bee"]);

        var hitsBefore = handler.CountRequests(r => r.PathAndQuery == "/api/runs/run-1/bees/1");
        await source.EnsureBeeDetailLoadedAsync(1);
        Assert.Equal(hitsBefore, handler.CountRequests(r => r.PathAndQuery == "/api/runs/run-1/bees/1"));

        // A failed detail fetch backs off for 3 seconds instead of hammering the server on every render.
        failDetailFetch = true;
        await source.EnsureBeeDetailLoadedAsync(99);
        var missedHits = handler.CountRequests(r => r.PathAndQuery == "/api/runs/run-1/bees/99");
        await source.EnsureBeeDetailLoadedAsync(99);
        Assert.Equal(missedHits, handler.CountRequests(r => r.PathAndQuery == "/api/runs/run-1/bees/99"));
    }

    [Fact]
    public async Task History_mode_queries_logs_facets_calls_and_tracks_server_reachability()
    {
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var runPlan = new LoadPlan { DurationSeconds = 10, Scenarios = [ScenarioRunConfig.CreateFor(catalog.Find("http-get")!)] };
        var startedUtc = DateTimeOffset.UtcNow;
        var bee0 = new LiveBeeDto(0, 0, 0.5, 140, 200, 128, null, 0);
        var serverOffline = false;

        using var handler = new StubHttpHandler(req =>
        {
            if (serverOffline) throw new HttpRequestException("server restarting");
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path == "/api/runs")
                return StubHttpHandler.JsonResponse(new List<StoredRunSummary> { new("historic-9", startedUtc.AddHours(-1), startedUtc.AddMinutes(-59), "Finished", 60, 10, 1, 20, null) });
            if (req.Method == HttpMethod.Post && path == "/api/runs/historic-9/load")
                return StubHttpHandler.JsonResponse(new LiveRunStatusDto("historic-9", "Finished", 60, startedUtc.AddHours(-1), runPlan, [new LiveScenarioViewDto(1, 1, 0, 0, 0, 0, 0)], [bee0], 0, 1, "DuckDb", null));
            if (req.Method == HttpMethod.Get && path == "/api/runs/historic-9/runtime")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (req.Method == HttpMethod.Get && path == "/api/runs/historic-9/logs")
                return StubHttpHandler.JsonResponse(new LogPageResult(1, 0, 20, [new StoredLogRow(1, 1, "B2", 0, 1.2, "Error", "payment failed", null, 0, 2, true, false, 1)]));
            if (req.Method == HttpMethod.Get && path == "/api/runs/historic-9/logs/facets")
                return StubHttpHandler.JsonResponse(new LogFacets(1, new Dictionary<string, long> { ["Error"] = 1 }, [1], [new LogStepCount(1, 1, 1)]));
            if (req.Method == HttpMethod.Get && path == "/api/runs/historic-9/calls/endpoints")
                return StubHttpHandler.JsonResponse(new CallEndpointsResult([new CallEndpointSummaryDto("POST /pay", "POST", "shop.test", 1, 0, 1, 620, 620, 620, 620, 620, 1000, true, [0])], 1, 0, 1));
            if (req.Method == HttpMethod.Get && path == "/api/runs/historic-9/calls/detail")
                return StubHttpHandler.JsonResponse(new CallEndpointDetailDto(
                    new CallEndpointSummaryDto("POST /pay", "POST", "shop.test", 1, 0, 1, 620, 620, 620, 620, 620, 1000, true, [0]),
                    2.5, [], [], 1, 0, 20,
                    [new CallSampleDto(1, 1, "B2", 0, 1, 0, 1.2, 0, 620, 500, 0, "HTTP 500", "POST /pay", "https://shop.test/pay", "POST /pay", false, 620, 1000)]));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };
        using var source = new ApiRunSource(http, catalog, new ApiEnvironmentSource(http));

        // Before any run exists, queries short-circuit cleanly without hitting the network.
        Assert.Equal(0, (await source.QueryLogsAsync(LogFilter.None, 0, 20))!.TotalCount);
        Assert.Equal(0, (await source.QueryLogFacetsAsync(LogFilter.None))!.Total);
        Assert.Equal(0, (await source.QueryCallEndpointsAsync(null, false, false, null, null))!.TotalCalls);
        Assert.Null(await source.QueryCallEndpointDetailAsync("POST /pay", null, null, null, null, null, null, null, 0, 20, 10));
        Assert.Empty(handler.Requests);

        // Load a historical run (404 /runtime falls back to an empty snapshot).
        Assert.Single(await source.ListRunsAsync());
        await source.LoadStoredRunAsync("historic-9");
        await WaitFor(() => source.CurrentRunId == "historic-9" && source.Current.Runtime is not null);
        Assert.True(source.IsViewingHistory);
        Assert.Equal(0, source.Current.Runtime!.TotalValues);

        // Query logs, facets, call endpoints, and call detail with full filter parameters.
        var logFilter = LogFilter.None.WithEntry(0).WithStep(1).WithBee(1).WithOutcome(BeeShow.Failed).WithLevels(["Error"]).WithSearch("payment");
        Assert.Equal(1, (await source.QueryLogsAsync(logFilter, 0, 20))!.TotalCount);
        Assert.Equal(1, (await source.QueryLogFacetsAsync(logFilter))!.Total);

        // Contradictory filter (bee #1 in filter AND "B2" in search) returns empty without hitting the server.
        var beforeContradict = handler.Requests.Count;
        var contradicting = LogFilter.None.WithBee(0).WithSearch("B2");
        Assert.True(contradicting.Contradicts);
        Assert.Equal(0, (await source.QueryLogsAsync(contradicting, 0, 20))!.TotalCount);
        Assert.Equal(0, (await source.QueryLogFacetsAsync(contradicting))!.Total);
        Assert.Equal(beforeContradict, handler.Requests.Count);

        Assert.Single((await source.QueryCallEndpointsAsync(0, true, true, 100, "pay"))!.Endpoints);
        Assert.Single((await source.QueryCallEndpointDetailAsync("POST /pay", 0, "failed", 100, 2000, 0, 10, "slowest", 0, 20, 50))!.Calls);

        source.ReturnToLive();
        Assert.False(source.IsViewingHistory);

        // When the server goes offline, RefreshAsync flips IsServerReachable to false cleanly without throwing.
        serverOffline = true;
        await source.RefreshAsync();
        Assert.False(source.IsServerReachable);
        Assert.Empty(await source.ListRunsAsync());
        Assert.Empty(await source.FetchLiveLogsAsync());
    }

    [Fact]
    public async Task Loading_preset_after_finished_run_refreshes_overview_snapshot()
    {
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var runPlan = new LoadPlan
        {
            DurationSeconds = 10,
            Scenarios = [new ScenarioRunConfig { ScenarioId = "http-get", Label = "Old Run Entry", RatePerMinute = 10 }],
        };
        var smokePlan = new LoadPlan
        {
            DurationSeconds = 20,
            Scenarios =
            [
                new ScenarioRunConfig { ScenarioId = "http-get", Label = "Smoke A", RatePerMinute = 12 },
                new ScenarioRunConfig { ScenarioId = "http-get", Label = "Smoke B", RatePerMinute = 18 },
            ],
        };
        var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new StubHttpHandler(async (req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path == "/api/plan")
                return StubHttpHandler.JsonResponse(new PlanWorkspaceState(runPlan.Clone(), "Old", runPlan.Clone(), false, 1));
            if (req.Method == HttpMethod.Get && path == "/api/presets")
                return StubHttpHandler.JsonResponse(new List<Preset>
                {
                    new("Old", DateTimeOffset.UtcNow, runPlan.Clone()),
                    new("Smoke", DateTimeOffset.UtcNow, smokePlan.Clone()),
                });
            if (req.Method == HttpMethod.Post && path == "/api/runs/run-42/load")
                return StubHttpHandler.JsonResponse(new LiveRunStatusDto(
                    "run-42", "Finished", 10, DateTimeOffset.UtcNow, runPlan.Clone(),
                    [new LiveScenarioViewDto(5, 0, 0, 0, 0, 5, 0)], [], 4, 0, "DuckDb", null, NextBeeIndex: 5));
            if (req.Method == HttpMethod.Get && path == "/api/runs/run-42/runtime")
                return StubHttpHandler.JsonResponse(RuntimeDataSnapshot.Empty(1));
            if (req.Method == HttpMethod.Post && path == "/api/runs/unload")
            {
                unloaded.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (req.Method == HttpMethod.Post && path == "/api/presets/Smoke/load")
            {
                loaded.TrySetResult();
                return StubHttpHandler.JsonResponse(new PlanWorkspaceState(smokePlan.Clone(), "Smoke", smokePlan.Clone(), false, 2));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };
        using var source = new ApiRunSource(http, catalog, new ApiEnvironmentSource(http));

        source.StartSync();
        await WaitFor(() => source.PresetNames.Contains("Smoke"));

        await source.LoadStoredRunAsync("run-42");
        Assert.True(source.IsViewingHistory);
        Assert.Equal("run-42", source.CurrentRunId);
        Assert.Single(source.Current.Labels);
        Assert.Equal("Old Run Entry", source.Current.Labels[0]);

        source.LoadPreset("Smoke");

        Assert.Equal("Smoke", source.PresetName);
        Assert.False(source.IsViewingHistory);
        Assert.Equal("", source.CurrentRunId);
        Assert.Equal(FakeRunState.Idle, source.Current.State);
        Assert.Equal(["Smoke A", "Smoke B"], source.Current.Labels);

        await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
        source.StopSync();
    }

    [Fact]
    public async Task Default_environment_from_plan_workspace_is_selected_in_ApiEnvironmentSource()
    {
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var plan = new LoadPlan { DurationSeconds = 15, Scenarios = [] };
        var envsList = new List<Beesharp.Services.Environments.EnvironmentDefinition>
        {
            new() { Name = "Local" },
            new() { Name = "Staging" },
        };

        var handler = new StubHttpHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path == "/api/plan")
                return Task.FromResult(StubHttpHandler.JsonResponse(new PlanWorkspaceState(plan.Clone(), "Default", plan.Clone(), false, 1, DefaultEnvironment: "staging")));
            if (req.Method == HttpMethod.Get && path == "/api/presets")
                return Task.FromResult(StubHttpHandler.JsonResponse(new List<Preset>()));
            if (req.Method == HttpMethod.Get && path == "/api/environments")
                return Task.FromResult(StubHttpHandler.JsonResponse(envsList));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };
        var envs = new ApiEnvironmentSource(http);
        using var source = new ApiRunSource(http, catalog, envs);

        // Case 1: /api/plan arrives before /api/environments.
        source.StartSync();
        await WaitFor(() => source.PresetName == "Default");
        Assert.Null(envs.ActiveName);
        await envs.RefreshAsync();
        Assert.Equal("Staging", envs.ActiveName);

        // Explicit user choice ("No environment") is respected on subsequent refreshes.
        envs.SetActive(null);
        Assert.Null(envs.ActiveName);
        await envs.RefreshAsync();
        Assert.Null(envs.ActiveName);

        source.StopSync();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 120 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}
