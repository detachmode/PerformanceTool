using System.Text.Json;
using System.Text.Json.Nodes;
using Beesharp.Services;

namespace Beesharp.Tests;

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
            [new LiveScenarioViewDto(10, 8, 2, 1, 0, 0, 0)],
            [new LiveBeeDto(0, 0, 1.5, 120, 200, 42, null, 0)],
            7, 3, "DuckDb", null, PreflightState: "Done", RunningBees: [new LiveRunningBeeDto(8, 0, 11.9)], RuntimeVersion: 4);
        var json = JsonSerializer.Serialize(dto, Web);
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("r1", (string?)node["runId"]);
        Assert.Equal("Running", (string?)node["state"]);
        Assert.Equal(7, (int?)node["lastSequence"]);
        Assert.NotNull(node["newBees"]);
        Assert.Equal(4, (long?)node["runtimeVersion"]);
        var back = RoundTrip(dto);
        Assert.Equal(dto.NewBees[0], back.NewBees[0]);
        Assert.Equal(dto.RunningBees![0], back.RunningBees![0]);
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
