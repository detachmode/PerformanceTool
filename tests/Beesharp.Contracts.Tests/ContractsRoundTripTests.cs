using System.Text.Json;
using System.Text.Json.Nodes;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Contracts.Tests;

/// <summary>The REST wire types in Beesharp.Contracts survive System.Text.Json with the web defaults both sides use (brain 0054).</summary>
public class ContractsRoundTripTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Web);
        var back = JsonSerializer.Deserialize<T>(json, Web)!;
        Assert.Equal(json, JsonSerializer.Serialize(back, Web));
        return back;
    }

    [Fact]
    public void Run_status_keeps_its_property_names_and_values()
    {
        var dto = new LiveRunStatusDto(
            "r1", "Running", 12.5, DateTimeOffset.UtcNow, new LoadPlan(),
            [new LiveScenarioViewDto(10, 8, 2, 1, 0, 0, 0, [0, 1], [1, 1])],
            [new LiveBeeDto(0, 0, 1.5, 120, 200, 42, "boom", 0, FailedStep: 1, FailedRep: 2)],
            7, 3, "DuckDb", null, PreflightState: "Done",
            RunningBees: [new LiveRunningBeeDto(8, 0, 11.9, Step: 1, Rep: 2)],
            RuntimeVersion: 4,
            Steps: [[new StepInfo("Log in"), new StepInfo("Pay")]],
            NextBeeIndex: 12);
        var json = JsonSerializer.Serialize(dto, Web);
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("r1", (string?)node["runId"]);
        Assert.Equal("Running", (string?)node["state"]);
        Assert.Equal(7, (int?)node["lastSequence"]);
        Assert.NotNull(node["newBees"]);
        Assert.Equal(4, (long?)node["runtimeVersion"]);
        Assert.Equal(12, (int?)node["nextBeeIndex"]);   // the cursor of the live bee poll (afterIndex)
        var back = RoundTrip(dto);
        Assert.Equal(dto.NewBees[0], back.NewBees[0]);
        Assert.Equal(dto.RunningBees![0], back.RunningBees![0]);
        Assert.Equal(2, back.Steps![0].Count);
        Assert.Equal(1, back.NewBees[0].FailedStep);
        Assert.Equal(2, back.NewBees[0].FailedRep);
        Assert.Equal(1, back.RunningBees[0].Step);
        Assert.Equal(2, back.RunningBees[0].Rep);
    }

    [Fact]
    public void Stored_run_bee_detail_and_log_page_round_trip()
    {
        RoundTrip(new StoredRunSummary("r1", DateTimeOffset.UtcNow, null, "Finished", 3, 10, 1, 99, null));
        var detail = RoundTrip(new StoredBeeDetail("r1", 2, 0, 1, 50, 500, 10, "boom",
            [new RecordedCall("GET /x", 0, 50, 500, 10, "boom")], [new RecordedLog(0.5, "Error", "bad", "stack", null)]));
        Assert.Single(detail.Calls);
        var page = RoundTrip(new LogPageResult(1, 0, 200,
            [new StoredLogRow(1, 2, "B3", 0, 0.5, "Error", "bad", null, 0, 2, true, false)]));
        Assert.Equal("B3", page.Items[0].BeeName);
        RoundTrip(new LiveLogEntryDto(1, 2, "B3", 0, 0.5, DateTimeOffset.UtcNow, "Info", "hi", null));
    }

    [Fact]
    public void Log_rows_carry_their_step_and_the_facets_round_trip()
    {
        var row = RoundTrip(new StoredLogRow(1, 2, "B3", 0, 0.5, "Error", "bad", null, 0, 2, true, false, Step: 3));
        Assert.Equal(3, row.Step);
        var facets = RoundTrip(new LogFacets(7, new Dictionary<string, long> { ["Error"] = 4, ["Information"] = 3 }, [5, 2], [new LogStepCount(0, 3, 0), new LogStepCount(1, 4, 4)], Preflight: 2, Shutdown: 1));
        Assert.Equal(4, facets.Levels["Error"]);
        Assert.Equal([5L, 2L], facets.Entries);
        Assert.Equal(new LogStepCount(1, 4, 4), facets.Steps[1]);
        Assert.Equal(2, facets.Preflight);
        Assert.Equal(1, facets.Shutdown);

        var prog = RoundTrip(new StoredProgramRunDto("Succeeded", "WarmupPreflight.cs", null, ["[12:00:00] [INF] Ready"]));
        Assert.Equal("WarmupPreflight.cs", prog.Name);
        Assert.Single(prog.Logs);
    }

    [Fact]
    public void Script_plan_and_request_types_round_trip()
    {
        var summary = new ScriptScenarioSummary("s", "s.cs", "script:s", "S", "d", "code", DateTimeOffset.UtcNow, new JsonObject { ["a"] = 1 });
        var compile = RoundTrip(new ScriptCompileResult(true, summary, [new ScriptDiagnosticDto("CS1", "Error", 1, 2, "m")]));
        Assert.Equal(1, (int?)compile.Script!.Parameters["a"]);
        RoundTrip(new ScriptTestRunResult(true, "id", "n", 200, 1, 2, 3, 4, null, [new ScriptRecordedCall("GET", "u", 200, 1, 2)], ["log"]));
        RoundTrip(new PreflightCompileResult(true, new PreflightScriptSummary("p", "p.cs", "P", "c", DateTimeOffset.UtcNow), []));
        RoundTrip(new PreflightRunResult(true, 5, null, ["x"]));
        RoundTrip(new ScenarioInfoDto("id", "n", "d", true, "s", new JsonObject { ["type"] = "object" }));
        RoundTrip(new PlanWorkspaceState(new LoadPlan(), "p", new LoadPlan(), false, 3));
        RoundTrip(new Preset("p", DateTimeOffset.UtcNow, new LoadPlan()));
        RoundTrip(new StartRunRequest(new LoadPlan(), "Staging"));
        RoundTrip(new UploadScriptRequest("i", "f.cs", "code"));
        RoundTrip(new TestRunScriptRequest(new JsonObject { ["k"] = "v" }, "env", 5));
        RoundTrip(new TestRunPreflightRequest("env", 9));
        RoundTrip(new AddEntryRequest("scn"));
    }
}
