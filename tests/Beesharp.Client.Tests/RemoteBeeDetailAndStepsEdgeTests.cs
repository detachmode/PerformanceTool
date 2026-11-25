using Beesharp.Client.Data;
using Beesharp.Client.Runs;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Client.Tests;

/// <summary>
/// Stories for <see cref="RemoteBeeDetail"/> conversion (including call-less bee fallback and lazy logs)
/// and <see cref="StepLogic"/> / <see cref="StepView"/> edge cases (step data formatting, chronological visits,
/// unreached pending steps, and clean non-failure defaults).
/// </summary>
public sealed class RemoteBeeDetailAndStepsEdgeTests
{
    [Fact]
    public void Remote_bee_detail_normalizes_calls_flags_slow_thresholds_converts_logs_and_handles_empty_calls()
    {
        var store = new RunStore(1);
        var runStarted = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

        // 1. Bee with recorded calls: blank Endpoint falls back to EndpointNormalizer, custom SlowThresholdMs flags slowness,
        //    and lazy Logs() preserves Bee scope, CallIndex, Step, Rep and interned Exception.
        var storedWithCalls = new StoredBeeDetail(
            RunId: "run-1",
            Sequence: 4,
            Entry: 0,
            StartedAt: 3.0,
            DurationMs: 650,
            StatusCode: 200,
            Bytes: 128,
            Error: null,
            Calls:
            [
                new RecordedCall("GET /orders/98765", OffsetMs: 5, DurationMs: 420, StatusCode: 200, Bytes: 128, Error: null, Step: 1, Rep: 2, Endpoint: null, Method: "GET", SlowThresholdMs: 200, Url: "https://shop.test/orders/98765"),
            ],
            Logs:
            [
                new RecordedLog(3.2, "Warning", "slow order lookup", "TimeoutSoon", CallIndex: 0, Step: 1, Rep: 2),
            ]);

        var built = RemoteBeeDetail.Build(storedWithCalls, store, runStarted, entry: 0);
        var call = Assert.Single(built.Calls);
        Assert.Equal("GET /orders/{id}", call.Endpoint);
        Assert.True(call.Slow);
        Assert.Equal(200, call.SlowThresholdMs);
        Assert.Equal("https://shop.test/orders/98765", call.RawUrl);

        var log = Assert.Single(built.Logs);
        Assert.Equal("B5", log.Scope["Bee"]);
        Assert.Equal(runStarted.AddSeconds(3.2), log.Timestamp);
        Assert.Equal("TimeoutSoon", log.Exception);
        Assert.Equal(0, log.CallIndex);
        Assert.Equal(1, log.Step);
        Assert.Equal(2, log.Rep);

        // 2. Bee that failed before making any HTTP call gets a synthetic "Run" fallback call preserving FailedStep/FailedRep.
        var callLess = new StoredBeeDetail(
            RunId: "run-1",
            Sequence: 5,
            Entry: 0,
            StartedAt: 4.0,
            DurationMs: 12,
            StatusCode: null,
            Bytes: 0,
            Error: "NullReferenceException",
            Calls: [],
            Logs: [],
            FailedStep: 2,
            FailedRep: 1);

        var fallbackDetail = RemoteBeeDetail.Build(callLess, store, runStarted, entry: 0);
        var synthetic = Assert.Single(fallbackDetail.Calls);
        Assert.Equal("Run", synthetic.Name);
        Assert.Equal("NullReferenceException", synthetic.Error);
        Assert.Equal(2, synthetic.Step);
        Assert.Equal(1, synthetic.Rep);
        Assert.Empty(fallbackDetail.Logs);
    }

    [Fact]
    public void Step_logic_formats_captured_step_data_handles_chronological_visits_and_exposes_expandable_flags()
    {
        var errors = new ErrorDictionary();
        var steps = new ScenarioSteps(["Sign in", "Browse", "Pay"]);

        // FormatStepData covers strings, numbers, booleans, null, arrays, non-object JSON, blank input and invalid JSON.
        Assert.Equal("", StepLogic.FormatStepData("   "));
        Assert.Equal("user: alice · retries: 2 · coupon: null · tags: [1,2]",
            StepLogic.FormatStepData("""{"user":"alice","retries":2,"coupon":null,"tags":[1,2]}"""));
        Assert.Equal("[1,2,3]", StepLogic.FormatStepData("[1,2,3]"));
        Assert.Equal("{not json", StepLogic.FormatStepData("{not json"));

        // FailedRep returns -1 when all calls succeeded.
        var okCalls = new List<FakeCall>
        {
            new(0, "POST /login", 0, 50, 200, ErrorDictionary.None, false, 0, Errors: errors, Step: 0, Rep: 0),
            new(1, "GET /items", 50, 80, 200, ErrorDictionary.None, false, 0, Errors: errors, Step: 1, Rep: 0),
        };
        Assert.Equal(-1, StepLogic.FailedRep(okCalls));

        // Chronological step visits show visited steps in order (with data), then unreached steps as Pending.
        var visits = new List<RecordedStepVisit>
        {
            new(Step: 0, Rep: 0, Data: """{"user":"alice"}"""),
            new(Step: 1, Rep: 0, Data: null),
        };

        var views = StepLogic.ForBee(steps, okCalls, stepVisits: visits);
        Assert.Equal(3, views.Count);

        Assert.Equal(StepState.Done, views[0].State);
        Assert.True(views[0].HasCalls);
        Assert.True(views[0].HasData);
        Assert.True(views[0].Expandable);

        Assert.Equal(StepState.Done, views[1].State);
        Assert.True(views[1].HasCalls);
        Assert.False(views[1].HasData);
        Assert.True(views[1].Expandable);

        Assert.Equal(StepState.Pending, views[2].State);
        Assert.False(views[2].HasCalls);
        Assert.False(views[2].HasData);
        Assert.False(views[2].Expandable);
    }
}
