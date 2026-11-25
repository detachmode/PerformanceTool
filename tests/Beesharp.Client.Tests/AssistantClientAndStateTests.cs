using System.Net;
using System.Text;
using Beesharp.Ai.Contracts;
using Beesharp.Client.Assistant;
using Beesharp.Client.Calls;
using Beesharp.Client.Data;
using Beesharp.Client.Environments;
using Beesharp.Client.Logs;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

/// <summary>
/// Focused stories for <see cref="AssistantClient"/> and <see cref="AssistantState"/> (brain 0047, 0049):
/// status checks, chat error mapping, changed-area refreshes, link routing, and live-run log overlay fallback.
/// </summary>
public sealed class AssistantClientAndStateTests
{
    [Fact]
    public async Task Assistant_client_handles_status_chat_errors_and_card_data_queries()
    {
        var chatBehavior = 0; // 0 = ok, 1 = json error, 2 = plain HTTP 503, 3 = network error
        using var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var query = req.RequestUri.Query;
            if (path == "/api/assistant/status")
                return StubHttpHandler.JsonResponse(new AssistantStatusDto(true, "gpt-4.1"));
            if (path == "/api/assistant/chat")
            {
                return chatBehavior switch
                {
                    1 => StubHttpHandler.JsonResponse(new { error = "Assistant is busy" }, HttpStatusCode.Conflict),
                    2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down", Encoding.UTF8, "text/plain") },
                    3 => throw new HttpRequestException("connection refused"),
                    _ => StubHttpHandler.JsonResponse(new AssistantTurnDto("s-1", [new AssistantItemDto(AssistantItemTypes.Text, Text: "Ready.")], [])),
                };
            }
            if (req.Method == HttpMethod.Delete && path == "/api/assistant/sessions/s-1")
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (path == "/api/runs/r-1/summary")
                return StubHttpHandler.JsonResponse(new AssistantRunDto("r-1", "Finished", 10, DateTimeOffset.UtcNow, 3, 1, 1, 5, null, []));
            if (path == "/api/runs/r-1/bees")
                return StubHttpHandler.JsonResponse(new AssistantBeePageDto(2, [new AssistantBeeDto(0, 0, 1, 120, 200, 10, null, 0), new AssistantBeeDto(1, 0, 2, 400, 500, 0, "err", 0)]));
            if (path == "/api/runs/r-1/bees/1")
                return StubHttpHandler.JsonResponse(new StoredBeeDetail("r-1", 1, 0, 2, 400, 500, 0, "err", [], []));
            if (path == "/api/runs/r-1/logs")
            {
                Assert.Contains("entry=0", query);
                Assert.Contains("bee=1", query);
                Assert.Contains("failed=true", query);
                Assert.Contains("levels=Error", query);
                Assert.Contains("search=boom", query);
                return StubHttpHandler.JsonResponse(new LogPageResult(1, 0, 20, []));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };
        var client = new AssistantClient(http);

        var status = await client.GetStatusAsync();
        Assert.True(status!.Configured);
        Assert.Equal("gpt-4.1", status.Model);

        // Chat success & all three failure surfaces map cleanly to AssistantException.
        var okTurn = await client.ChatAsync(null, "hello");
        Assert.Equal("s-1", okTurn.SessionId);

        chatBehavior = 1;
        Assert.Equal("Assistant is busy", (await Assert.ThrowsAsync<AssistantException>(() => client.ChatAsync("s-1", "hi"))).Message);
        chatBehavior = 2;
        Assert.Contains("HTTP 503", (await Assert.ThrowsAsync<AssistantException>(() => client.ChatAsync("s-1", "hi"))).Message);
        chatBehavior = 3;
        Assert.Contains("could not be reached", (await Assert.ThrowsAsync<AssistantException>(() => client.ChatAsync("s-1", "hi"))).Message);

        await client.DeleteSessionAsync("s-1");
        Assert.True(handler.HasRequest(r => r.Method == HttpMethod.Delete && r.PathAndQuery == "/api/assistant/sessions/s-1"));

        // Card re-query methods translate 1-based ViewQueryDto fields into 0-based REST query params.
        Assert.Equal("r-1", (await client.GetRunAsync("r-1"))!.RunId);
        var paged = await client.GetBeesAsync("r-1", entry: 0, outcome: "failed", offset: 0, limit: 10);
        Assert.Equal("ok", paged!.Items[0].Outcome);
        Assert.Equal("failed", paged.Items[1].Outcome);
        Assert.Equal(2, (await client.GetBeesAsync("r-1", [0, 1]))!.Total);
        Assert.Equal(500, (await client.GetBeeDetailAsync("r-1", 1))!.StatusCode);

        var logs = await client.GetLogsAsync("r-1", new ViewQueryDto("r-1", Entry: 1, Bee: 2, Levels: ["Error"], Search: "boom", FailedBees: true), 0, 20);
        Assert.Equal(1, logs!.TotalCount);
    }

    [Fact]
    public async Task Assistant_state_unlocks_in_live_mode_sends_turns_refreshes_changed_areas_and_resets_session()
    {
        var runPlan = new LoadPlan { DurationSeconds = 10, Scenarios = [ScenarioRunConfig.CreateFor(new HttpGetScenario())] };
        var failNextChat = false;

        using var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/assistant/status")
                return StubHttpHandler.JsonResponse(new AssistantStatusDto(true, "gpt-4.1-mini"));
            if (path == "/api/assistant/chat")
            {
                if (failNextChat)
                {
                    failNextChat = false;
                    return StubHttpHandler.JsonResponse(new { error = "rate limited" }, HttpStatusCode.TooManyRequests);
                }
                return StubHttpHandler.JsonResponse(new AssistantTurnDto(
                    "sess-77",
                    [new AssistantItemDto(AssistantItemTypes.Text, Text: "Updated plan and started run.")],
                    [ChangedAreas.Plan, ChangedAreas.Environments, ChangedAreas.Scripts, ChangedAreas.Run]));
            }
            if (req.Method == HttpMethod.Delete && path.StartsWith("/api/assistant/sessions/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (path == "/api/plan")
                return StubHttpHandler.JsonResponse(new PlanWorkspaceState(runPlan, "Live", runPlan, false, 2));
            if (path == "/api/presets")
                return StubHttpHandler.JsonResponse(new List<Preset>());
            if (path == "/api/environments")
                return StubHttpHandler.JsonResponse(new List<Beesharp.Services.Environments.EnvironmentDefinition>());
            if (path == "/api/scenarios")
                return StubHttpHandler.JsonResponse(new List<ScenarioInfoDto>());
            if (path == "/api/runs/current")
                return StubHttpHandler.JsonResponse(new LiveRunStatusDto("run-9", "Finished", 4, DateTimeOffset.UtcNow, runPlan, [new LiveScenarioViewDto(1, 1, 0, 0, 0, 0, 0)], [], 0, 0, "Memory", null));
            if (path == "/api/runs/run-9/runtime")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var ctx = CreateHarness(handler, isMobile: false);

        // 1. In Fake mode the assistant stays hidden and Toggle is ignored.
        Assert.False(ctx.State.Available);
        ctx.State.Toggle();
        Assert.False(ctx.State.IsOpen);

        // 2. Switching to Live mode checks /api/assistant/status and unlocks the panel.
        ctx.Mode.SetFakeMode(false);
        await WaitFor(() => ctx.State.Available);
        Assert.Equal("gpt-4.1-mini", ctx.State.Model);

        ctx.State.Toggle();
        Assert.True(ctx.State.IsOpen);

        // 3. Sending a chat turn records user + assistant entries and refreshes Plan, Environments, Scripts and Run.
        await ctx.State.SendAsync("  tune plan and run  ");
        Assert.Equal("sess-77", ctx.State.SessionId);
        Assert.Equal(2, ctx.State.Entries.Count);
        Assert.True(ctx.State.IsCurrentRun("run-9"));
        Assert.True(handler.HasRequest(r => r.PathAndQuery.StartsWith("/api/plan", StringComparison.Ordinal)));
        Assert.True(handler.HasRequest(r => r.PathAndQuery == "/api/environments"));
        Assert.True(handler.HasRequest(r => r.PathAndQuery == "/api/scenarios"));
        Assert.True(handler.HasRequest(r => r.PathAndQuery.StartsWith("/api/runs/current", StringComparison.Ordinal)));

        // 4. Failed turn appends a clear error entry rather than throwing.
        failNextChat = true;
        await ctx.State.SendAsync("try again");
        Assert.Equal("rate limited", ctx.State.Entries.Last().Error);

        // 5. NewChat clears transcript & deletes server session; returning to Fake mode closes panel.
        ctx.State.NewChat();
        Assert.Null(ctx.State.SessionId);
        Assert.Empty(ctx.State.Entries);
        await WaitFor(() => handler.HasRequest(r => r.Method == HttpMethod.Delete && r.PathAndQuery == "/api/assistant/sessions/sess-77"));

        ctx.Mode.SetFakeMode(true);
        Assert.False(ctx.State.IsOpen);
    }

    [Fact]
    public async Task Assistant_state_routes_every_chip_link_loads_stored_runs_and_falls_back_to_full_logs_while_running()
    {
        var runPlan = new LoadPlan { DurationSeconds = 10, Scenarios = [ScenarioRunConfig.CreateFor(new HttpGetScenario())] };
        var serverRunState = "Finished";

        using var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/assistant/status")
                return StubHttpHandler.JsonResponse(new AssistantStatusDto(true, "gpt-4.1-mini"));
            if (path == "/api/runs/current")
                return StubHttpHandler.JsonResponse(new LiveRunStatusDto("run-9", serverRunState, 4, DateTimeOffset.UtcNow, runPlan, [new LiveScenarioViewDto(1, 1, 0, 0, 0, 0, 0)], [], 0, 0, "Memory", null));
            if (path is "/api/runs/run-9/runtime" or "/api/runs/run-old-1/runtime")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (req.Method == HttpMethod.Post && path == "/api/runs/run-old-1/load")
                return StubHttpHandler.JsonResponse(new LiveRunStatusDto("run-old-1", "Finished", 8, DateTimeOffset.UtcNow, runPlan, [new LiveScenarioViewDto(1, 1, 0, 0, 0, 0, 0)], [], 0, 0, "Memory", null));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var ctx = CreateHarness(handler, isMobile: true);
        ctx.Mode.SetFakeMode(false);
        await WaitFor(() => ctx.State.Available);
        await ctx.Api.RefreshAsync();
        Assert.Equal("run-9", ctx.Api.CurrentRunId);

        ctx.State.Toggle();
        Assert.True(ctx.State.IsOpen);

        // 1. Editor, Scripts, Environments, Calls, Logs and Bee-logs links route cleanly (closing mobile drawer).
        await ctx.State.OpenLinkAsync(new ChipLinkDto("editor"));
        Assert.False(ctx.State.IsOpen);
        Assert.StartsWith("?edit=", ctx.Nav.NavigatedTo[^1]);

        await ctx.State.OpenLinkAsync(new ChipLinkDto("scripts"));
        Assert.Equal("editor", ctx.Nav.NavigatedTo[^1]);

        await ctx.State.OpenLinkAsync(new ChipLinkDto("environments"));
        Assert.True(ctx.EnvPopup.IsOpen);

        await ctx.State.OpenLinkAsync(new ChipLinkDto("calls", new ViewQueryDto("run-9", Entry: 1, Outcome: "slow", Endpoint: "POST /pay", MinMs: 250)));
        Assert.StartsWith("calls?", ctx.Nav.NavigatedTo[^1]);
        Assert.Contains("endpoint=POST", ctx.Nav.NavigatedTo[^1]);

        await ctx.State.OpenLinkAsync(new ChipLinkDto("logs", new ViewQueryDto("run-9", Entry: 1, Bee: 1, FailedBees: true, Levels: ["Error"], Search: "500")));
        Assert.StartsWith("logs?", ctx.Nav.NavigatedTo[^1]);
        Assert.Contains("scenario=1", ctx.Nav.NavigatedTo[^1]);

        await ctx.State.OpenBeeLogsAsync("run-9", sequence: 0);
        Assert.Equal("logs?bee=1", ctx.Nav.NavigatedTo[^1]);

        // 2. Run link with a specific bee opens the BeeSet dialog; run link for an older run loads that stored run when idle.
        await ctx.State.OpenLinkAsync(new ChipLinkDto("run", new ViewQueryDto("run-9", Bee: 2)));
        Assert.NotNull(ctx.State.BeeSet);
        Assert.Equal("Bee B2", ctx.State.BeeSet!.Title);
        ctx.State.CloseBees();
        Assert.Null(ctx.State.BeeSet);

        await ctx.State.OpenLinkAsync(new ChipLinkDto("run", new ViewQueryDto("run-old-1")));
        Assert.Equal("run-old-1", ctx.Api.CurrentRunId);
        Assert.Equal("", ctx.Nav.NavigatedTo[^1]);

        // 3. While a live run is active, opening logs for ANOTHER run uses the FullLogs overlay instead of unloading the live run.
        serverRunState = "Running";
        await ctx.Api.RefreshAsync();
        Assert.True(ctx.Api.IsRunning);

        await ctx.State.OpenLogsAsync("Old run logs", new ViewQueryDto("run-old-1", Outcome: "failed", Search: "timeout"));
        Assert.NotNull(ctx.State.FullLogs);
        Assert.Equal("run-old-1", ctx.State.FullLogs!.RunId);
        Assert.Equal("Old run logs", ctx.State.FullLogs.Title);
        ctx.State.CloseFullLogs();
        Assert.Null(ctx.State.FullLogs);
    }

    private sealed class Harness(
        HttpClient http,
        ApiRunSource api,
        DataModeState mode,
        StubNavigationManager nav,
        EnvironmentsPopupState envPopup,
        AssistantState state) : IDisposable
    {
        public ApiRunSource Api => api;
        public DataModeState Mode => mode;
        public StubNavigationManager Nav => nav;
        public EnvironmentsPopupState EnvPopup => envPopup;
        public AssistantState State => state;

        public void Dispose()
        {
            state.Dispose();
            api.Dispose();
            http.Dispose();
        }
    }

    private static Harness CreateHarness(StubHttpHandler handler, bool isMobile)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var js = new FakeJsRuntime { IsMobileAssistantResult = isMobile };
        var mode = new DataModeState(js, initialFakeMode: true);
        var envs = new ApiEnvironmentSource(http);
        var api = new ApiRunSource(http, catalog, envs);
        var scripts = new ScriptApiClient(http, catalog);
        var nav = new StubNavigationManager();
        var logsLinks = new LogsLinks(nav);
        var callsLinks = new CallsLinks(nav);
        var envPopup = new EnvironmentsPopupState();
        var client = new AssistantClient(http);
        var state = new AssistantState(client, mode, api, envs, scripts, logsLinks, callsLinks, envPopup, nav, js);
        return new Harness(http, api, mode, nav, envPopup, state);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 80 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}
