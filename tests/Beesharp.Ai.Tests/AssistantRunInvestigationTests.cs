using System.Text.Json;
using Beesharp.Ai;
using Beesharp.Ai.Contracts;

namespace Beesharp.Ai.Tests;

/// <summary>
/// Focused stories of an operator controlling runs and investigating bees, logs and slow endpoints
/// via <c>AssistantToolbox.Runs.cs</c>, plus <see cref="OpenAiChatClientFactory"/> and full
/// <c>Beesharp.Ai.Contracts</c> wire round-trips.
/// </summary>
public sealed class AssistantRunInvestigationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement ToJson(object result) => JsonSerializer.SerializeToElement(result, Json);

    private static (AssistantToolbox Toolbox, InMemoryRunPort Runs, InMemoryHistoryPort History) CreateSeededToolbox()
    {
        var catalog = TestCatalogFactory.CreateWithSteps();
        var runPort = new InMemoryRunPort();
        var history = new InMemoryHistoryPort();

        history.Runs.Add(new RunInfo(
            "run-42", "Finished", 45.2, DateTimeOffset.UtcNow,
            TotalBees: 3, FailedBees: 1, SlowBees: 1, TotalLogs: 2, Error: null,
            Entries: [new EntryFigures(0, "Checkout", 3, 3, 0, 1, 1, 1, 0, 60)],
            PreflightState: "Succeeded"));

        history.Bees.AddRange([
            new BeeInfo(Sequence: 0, Entry: 0, StartedAt: 1.0, DurationMs: 90, StatusCode: 200, Bytes: 40, Error: null, SlowCalls: 0),
            new BeeInfo(Sequence: 1, Entry: 0, StartedAt: 2.0, DurationMs: 2100, StatusCode: 200, Bytes: 40, Error: null, SlowCalls: 1),
            new BeeInfo(Sequence: 2, Entry: 0, StartedAt: 3.0, DurationMs: 410, StatusCode: 502, Bytes: 0, Error: "Bad gateway", SlowCalls: 0, FailedStep: 1, FailedRep: 2),
        ]);

        history.Details[2] = new BeeDetailInfo(
            history.Bees[2],
            Calls: [new BeeCallInfo("POST /pay", 10, 400, 502, "HTTP 502", Step: 1, Rep: 2, Endpoint: "POST /pay", IsSlow: false)],
            Logs: [new BeeLogInfo(3.1, "Error", "Payment gateway returned 502", "HttpRequestException", CallIndex: 0)]);

        history.Logs.AddRange([
            new LogLineInfo(1, Sequence: 0, Entry: 0, AtSeconds: 1.05, Level: "Information", Message: "Checkout started", Exception: null),
            new LogLineInfo(2, Sequence: 2, Entry: 0, AtSeconds: 3.10, Level: "Error", Message: "Payment gateway returned 502", Exception: "HttpRequestException"),
        ]);

        var paySummary = new CallEndpointInfo("POST /pay", "POST", 15, 4, 2, 180, 1950, 2400, 1000, HasCustomThreshold: true);
        history.Endpoints.Add(paySummary);
        history.EndpointDetails["POST /pay"] = new CallEndpointDetailInfo(
            paySummary,
            MatchingCalls: 2,
            Offset: 0,
            Calls:
            [
                new CallSampleInfo(Sequence: 1, Entry: 0, AtSeconds: 2.0, DurationMs: 2100, StatusCode: 200, Error: null, IsSlow: true, TypicalMs: 180, SlowThresholdMs: 1000, Url: "https://shop.test/pay"),
                new CallSampleInfo(Sequence: 2, Entry: 0, AtSeconds: 3.0, DurationMs: 400, StatusCode: 502, Error: "HTTP 502", IsSlow: false, TypicalMs: 180, SlowThresholdMs: 1000, Url: "https://shop.test/pay"),
            ]);

        var toolbox = new AssistantToolbox(
            new InMemoryPlanPort(catalog),
            runPort,
            history,
            new InMemoryEnvironmentPort(),
            new InMemoryScriptPort(),
            catalog,
            new TurnRecorder());

        return (toolbox, runPort, history);
    }

    [Fact]
    public async Task Operator_starts_monitors_and_stops_a_run_through_the_assistant()
    {
        var (toolbox, runPort, _) = CreateSeededToolbox();

        // Before any run has started, get_run_status reports None clearly.
        Assert.Equal("None", ToJson(toolbox.GetRunStatus()).GetProperty("state").GetString());

        var started = ToJson(await toolbox.StartRun("Staging"));
        Assert.Equal("Running", started.GetProperty("state").GetString());
        Assert.Equal("Staging", runPort.LastStartEnvironment);

        Assert.Equal("run-live-1", ToJson(toolbox.GetRunStatus()).GetProperty("runId").GetString());

        var stopped = ToJson(await toolbox.StopRun(default));
        Assert.Equal("Finished", stopped.GetProperty("state").GetString());
        Assert.Equal(12, stopped.GetProperty("beesTotal").GetInt32());
    }

    [Fact]
    public async Task Operator_investigates_past_runs_filters_bees_by_step_and_queries_logs()
    {
        var (toolbox, _, history) = CreateSeededToolbox();

        // 1. List runs and inspect run summary.
        Assert.Single(ToJson(await toolbox.ListRuns(limit: 10)).EnumerateArray());
        Assert.Equal("run-42", ToJson(await toolbox.GetRun()).GetProperty("runId").GetString());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => toolbox.GetRun("missing-run"));

        // 2. Filter bees by outcome and 1-based failedStep -> 0-based port translation.
        var failedBees = ToJson(await toolbox.ListBees(entry: 1, outcome: "failed", failedStep: 2));
        Assert.Equal(1, failedBees.GetProperty("total").GetInt32());
        var failedRow = failedBees.GetProperty("bees")[0];
        Assert.Equal(3, failedRow.GetProperty("bee").GetInt32()); // sequence 2 -> bee 3
        Assert.Equal(2, failedRow.GetProperty("failedStep").GetInt32());
        Assert.Equal(3, failedRow.GetProperty("failedRep").GetInt32());
        Assert.Equal((0, BeeOutcomeFilter.Failed, 1, 0, 20), history.LastBeeQuery);

        Assert.Equal(1, ToJson(await toolbox.ListBees(outcome: "ok")).GetProperty("total").GetInt32());
        await Assert.ThrowsAsync<ArgumentException>(() => toolbox.ListBees(outcome: "bogus"));

        // 3. Inspect one bee's calls and logs; missing bee throws KeyNotFoundException.
        var bee3 = ToJson(await toolbox.GetBee(bee: 3));
        Assert.Equal(1, bee3.GetProperty("callsTotal").GetInt32());
        Assert.Equal(2, bee3.GetProperty("calls")[0].GetProperty("step").GetInt32());
        Assert.Equal(3, bee3.GetProperty("calls")[0].GetProperty("rep").GetInt32());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => toolbox.GetBee(bee: 99));

        // 4. Query logs with combined filters and level prefix matching ("err" -> "Error").
        var logs = ToJson(await toolbox.QueryLogs(entry: 1, bee: 3, failedBees: true, levels: ["err"], search: "502"));
        Assert.Equal(1, logs.GetProperty("total").GetInt32());
        Assert.Equal(["Error"], history.LastLogFilter!.Levels);
        await Assert.ThrowsAsync<ArgumentException>(() => toolbox.QueryLogs(levels: ["nonsense"]));

        // 5. When no runs exist at all, tools requiring a run id fail cleanly.
        history.LatestId = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => toolbox.GetRun());
    }

    [Fact]
    public async Task Operator_drills_into_slow_and_failing_call_endpoints_with_validation()
    {
        var (toolbox, _, _) = CreateSeededToolbox();

        var endpoints = ToJson(await toolbox.ListCallEndpoints(slowOnly: true, minMs: 1000, search: "pay"));
        Assert.Equal(1, endpoints.GetProperty("totalEndpoints").GetInt32());

        var epDetail = ToJson(await toolbox.GetCallEndpoint("POST /pay", entry: 1, show: "failed"));
        Assert.Equal(1, epDetail.GetProperty("matching").GetInt32());
        Assert.Equal(3, epDetail.GetProperty("calls")[0].GetProperty("bee").GetInt32());

        await Assert.ThrowsAsync<ArgumentException>(() => toolbox.GetCallEndpoint("   "));
        await Assert.ThrowsAsync<ArgumentException>(() => toolbox.GetCallEndpoint("POST /pay", show: "weird"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => toolbox.GetCallEndpoint("GET /unknown"));
    }

    [Fact]
    public void OpenAi_client_factory_and_all_assistant_contract_dtos_round_trip_cleanly()
    {
        var factory = new OpenAiChatClientFactory();

        using var cloudClient = factory.Create(new AssistantOptions { Model = "gpt-4.1-mini", ApiKey = "sk-test-key" });
        Assert.NotNull(cloudClient);

        using var localClient = factory.Create(new AssistantOptions { Model = "llama3", Endpoint = "http://localhost:11434/v1" });
        Assert.NotNull(localClient);

        // Round-trip every AssistantContracts wire DTO (status, chat request, text, tool chip, and all view cards).
        var statusBack = RoundTrip(new AssistantStatusDto(true, "gpt-4.1-mini", Reason: null));
        Assert.True(statusBack.Configured);
        Assert.Equal("gpt-4.1-mini", statusBack.Model);

        var chatReqBack = RoundTrip(new AssistantChatRequest("sess-1", "Why did checkout fail?"));
        Assert.Equal("sess-1", chatReqBack.SessionId);

        var sampleBee = new ViewBeeDto(Bee: 3, Entry: 1, EntryLabel: "Checkout", StartedAt: 3.0, DurationMs: 410, StatusCode: 502, Outcome: "failed", Error: "Bad gateway", Note: "slowest fail");
        var turn = new AssistantTurnDto(
            SessionId: "sess-1",
            Items:
            [
                new AssistantItemDto(AssistantItemTypes.Text, Text: "Found slow payment calls."),
                new AssistantItemDto(AssistantItemTypes.Tool, Tool: new ToolActivityDto("c1", "list_call_endpoints", "Looked at 1 endpoint", true, false, null, "{}", "[]", new ChipLinkDto("calls", new ViewQueryDto(RunId: "r1", Endpoint: "POST /pay", MinMs: 500)))),
                new AssistantItemDto(AssistantItemTypes.View, View: new ViewItemDto(
                    ViewKinds.Logs,
                    "Error logs",
                    new ViewQueryDto(RunId: "r1", Levels: ["Error"], Search: "502"),
                    Total: 1,
                    Logs: [new ViewLogLineDto(3, "B3", 1, 3.1, "Error", "Gateway 502", "HttpRequestException")])),
                new AssistantItemDto(AssistantItemTypes.View, View: new ViewItemDto(
                    ViewKinds.Bee,
                    "Bee B3",
                    new ViewQueryDto(RunId: "r1", Bee: 3),
                    Total: 1,
                    Bees: [sampleBee],
                    Bee: new ViewBeeDetailDto(sampleBee, [new ViewCallDto("POST /pay", 10, 400, 502, "HTTP 502", "POST /pay", IsSlow: false)]))),
                new AssistantItemDto(AssistantItemTypes.View, View: new ViewItemDto(
                    ViewKinds.Run,
                    "Run r1",
                    new ViewQueryDto(RunId: "r1"),
                    Total: 3,
                    Run: new ViewRunDto("r1", "Finished", 45.2, 3, 1, 1, 12, [new ViewEntryDto(1, "Checkout", 3, 3, 0, 1, 1, 0, 60)]),
                    EntryLabels: ["Checkout"])),
                new AssistantItemDto(AssistantItemTypes.View, View: new ViewItemDto(
                    ViewKinds.PlanChanges,
                    "Plan changes",
                    new ViewQueryDto(),
                    Total: 1,
                    Changes: [new ViewPlanChangeDto("changed", 1, "Checkout", ["rate 30 → 60 bees/min"])])),
                new AssistantItemDto(AssistantItemTypes.View, View: new ViewItemDto(
                    ViewKinds.Calls,
                    "Slow payment calls",
                    new ViewQueryDto(RunId: "r1", Entry: 1, Endpoint: "POST /pay", Outcome: "slow"),
                    Total: 4,
                    CallEndpoints: [new ViewCallEndpointDto("POST /pay", "POST", 15, 4, 2, 180, 1950, 2400, 1000, true)],
                    CallSamples: [new ViewCallSampleDto(2, 1, "Checkout", 2.0, 2100, 200, null, true, 11.7, "https://shop.test/pay")])),
            ],
            Changed: [ChangedAreas.Plan, ChangedAreas.Run]);

        var roundTripped = RoundTrip(turn);
        Assert.Equal("sess-1", roundTripped.SessionId);
        Assert.Equal(7, roundTripped.Items.Count);
        Assert.Equal("B3", roundTripped.Items[2].View!.Logs![0].BeeName);
        Assert.Equal("POST /pay", roundTripped.Items[3].View!.Bee!.Calls[0].Endpoint);
        Assert.Equal(60, roundTripped.Items[4].View!.Run!.Entries[0].BeesPerMinute);
        Assert.Equal("changed", roundTripped.Items[5].View!.Changes![0].What);
        Assert.Equal("POST /pay", roundTripped.Items[6].View!.CallEndpoints![0].Endpoint);
        Assert.Equal(11.7, roundTripped.Items[6].View!.CallSamples![0].Ratio);
    }

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}
