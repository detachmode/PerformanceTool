using System.Text.Json;
using Beesharp.Contracts;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Contracts.Tests;

/// <summary>
/// Edge-case stories for <see cref="LiveTestRunStatusDto"/> serialization and <see cref="CallAnalytics"/>
/// filtering, sorting, degenerate histograms and empty inputs.
/// </summary>
public sealed class LiveTestRunAndAnalyticsEdgeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Live_test_run_status_round_trips_steps_calls_logs_and_step_visits()
    {
        var dto = new LiveTestRunStatusDto(
            TestRunId: "test-1234",
            Running: false,
            Success: false,
            ScenarioId: "script-checkout",
            ScenarioName: "Checkout",
            StatusCode: 500,
            Bytes: 256,
            DurationMs: 420.5,
            StartupDurationMs: 12.0,
            TeardownDurationMs: 4.5,
            Error: "Card declined",
            Calls: [new ScriptRecordedCall("POST", "https://shop.test/pay", 500, 410, 0, "HTTP 500", Step: 1, Rep: 0, OffsetMs: 10)],
            Logs: [new LiveLogEntryDto(1, 0, "Test", 0, 0.4, DateTimeOffset.UtcNow, "Error", "Card declined", "InvalidOperationException", Step: 1, Rep: 0)],
            Steps: [new StepInfo("Sign in"), new StepInfo("Pay")],
            CurrentStep: 1,
            CurrentRep: 0,
            FailedStep: 1,
            FailedRep: 0,
            StepVisits: [new RecordedStepVisit(0, 0, null), new RecordedStepVisit(1, 0, "{\"amount\":99}")]);

        var json = JsonSerializer.Serialize(dto, Web);
        var back = JsonSerializer.Deserialize<LiveTestRunStatusDto>(json, Web)!;

        Assert.Equal("test-1234", back.TestRunId);
        Assert.Equal(1, back.FailedStep);
        Assert.Equal(2, back.Steps!.Count);
        Assert.Equal(2, back.StepVisits!.Count);
        Assert.Single(back.Calls);
        Assert.Single(back.Logs);
    }

    [Fact]
    public void Call_analytics_handles_empty_runs_identical_durations_and_all_detail_filters_and_sorts()
    {
        // Empty input produces clean empty aggregates and null detail.
        var emptyEndpoints = CallAnalytics.BuildEndpoints([]);
        Assert.Empty(emptyEndpoints.Endpoints);
        Assert.Equal(0, emptyEndpoints.TotalCalls);
        Assert.Null(CallAnalytics.BuildEndpointDetail([], "GET /missing", 10));

        // Identical durations (zero spread) still produce a valid single-bucket histogram without dividing by zero.
        var flat = new List<CallFact>
        {
            new(1, 0, 0, 0, 0, 1.0, 0, 100, 200, 32, null, "GET /ping", "https://shop.test/ping", "GET /ping", "GET", "shop.test", null),
            new(2, 1, 0, 0, 0, 5.0, 0, 100, 200, 32, null, "GET /ping", "https://shop.test/ping", "GET /ping", "GET", "shop.test", null),
            new(3, 2, 1, 0, 0, 9.0, 0, 100, 500, 0, "HTTP 500", "GET /ping", "https://shop.test/ping", "GET /ping", "GET", "shop.test", null),
        };

        // Endpoint list filtering: entry, failedOnly, slowOnly, minMs, search.
        Assert.Single(CallAnalytics.BuildEndpoints(flat, entry: 1, failedOnly: true, minMs: 50, search: "ping").Endpoints);
        Assert.Empty(CallAnalytics.BuildEndpoints(flat, slowOnly: true).Endpoints);
        Assert.Empty(CallAnalytics.BuildEndpoints(flat, search: "checkout").Endpoints);

        // Detail filtering by time window [2s, 10s], maxMs, show=failed/slow, and sorting by time & failed.
        var byTime = CallAnalytics.BuildEndpointDetail(flat, "GET /ping", 10, fromSec: 2, toSec: 10, maxMs: 150, sort: "time");
        Assert.NotNull(byTime);
        Assert.Equal(2, byTime!.MatchingCalls);
        Assert.Equal([5.0, 9.0], byTime.Calls.Select(c => c.AtSeconds).ToArray());
        Assert.NotEmpty(byTime.Histogram);

        var failedFirst = CallAnalytics.BuildEndpointDetail(flat, "GET /ping", 10, sort: "failed");
        Assert.Equal("HTTP 500", failedFirst!.Calls[0].Error);

        var onlyFailed = CallAnalytics.BuildEndpointDetail(flat, "GET /ping", 10, show: "failed");
        Assert.Equal(1, onlyFailed!.MatchingCalls);

        var onlySlow = CallAnalytics.BuildEndpointDetail(flat, "GET /ping", 10, show: "slow");
        Assert.Equal(0, onlySlow!.MatchingCalls);
        Assert.Empty(onlySlow.Timeline);
    }
}
