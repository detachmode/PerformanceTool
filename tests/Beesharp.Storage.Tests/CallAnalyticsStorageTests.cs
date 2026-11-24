using Beesharp.Services;
using Beesharp.Services.Storage;

namespace Beesharp.Storage.Tests;

public class CallAnalyticsStorageTests
{
    [Fact]
    public async Task DuckDb_and_InMemory_persist_call_metadata_and_serve_endpoints_and_detail()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"beesharp-calls-{Guid.NewGuid():N}.duckdb");
        try
        {
            await using var duck = new DuckDbRunStorage(dbPath);
            var mem = new InMemoryRunStorage();

            await VerifyProviderAsync(mem);
            await VerifyProviderAsync(duck);
        }
        finally
        {
            foreach (var p in new[] { dbPath, dbPath + ".wal" })
                if (File.Exists(p)) File.Delete(p);
        }
    }

    private static async Task VerifyProviderAsync(IRunStorage storage)
    {
        var plan = new LoadPlan
        {
            DurationSeconds = 10,
            Scenarios = [new ScenarioRunConfig { ScenarioId = "http-get", Label = "Shop", RatePerMinute = 60 }],
        };
        storage.EnqueueRunStarted("run-calls-1", DateTimeOffset.UtcNow, plan);

        for (var i = 0; i < 6; i++)
        {
            var dur = i == 5 ? 450.0 : 90.0 + i * 5;
            storage.EnqueueBeeCompleted("run-calls-1", new ExecutionResult(
                i, 0, i * 0.5, dur, 200, 128, null,
                Calls:
                [
                    new RecordedCall($"GET /products/{100 + i}?ref=nav", 0, dur, 200, 128, null, 0, 0,
                        Endpoint: "GET /products/{id}", Method: "GET", Host: "api.shop.test", SlowThresholdMs: null, Url: $"https://api.shop.test/products/{100 + i}?ref=nav"),
                    new RecordedCall("POST /pay", dur, 600, i == 0 ? 500 : 200, 0, i == 0 ? "HTTP 500" : null, 0, 0,
                        Endpoint: "POST /pay", Method: "POST", Host: "api.shop.test", SlowThresholdMs: 400, Url: "https://api.shop.test/pay"),
                ]), beeOutcome: i == 0 ? 2 : 1, slowCalls: 1);
        }

        storage.EnqueueRunFinished("run-calls-1", DateTimeOffset.UtcNow, "Finished", 5.0, null);
        await storage.FlushAsync();

        var bee = await storage.GetBeeDetailAsync("run-calls-1", 5);
        Assert.NotNull(bee);
        Assert.Equal("GET /products/{id}", bee!.Calls[0].Endpoint);
        Assert.Equal("https://api.shop.test/products/105?ref=nav", bee.Calls[0].Url);
        Assert.Equal(400, bee.Calls[1].SlowThresholdMs);

        var endpoints = await storage.GetCallEndpointsAsync("run-calls-1", new CallEndpointsQuery());
        Assert.Equal(2, endpoints.Endpoints.Count);
        Assert.Equal(12, endpoints.TotalCalls);

        var payDetail = await storage.GetCallEndpointDetailAsync("run-calls-1", new CallDetailQuery("POST /pay"));
        Assert.NotNull(payDetail);
        Assert.True(payDetail!.Summary.HasCustomThreshold);
        Assert.Equal(400, payDetail.Summary.SlowThresholdMs);
        Assert.Equal(5, payDetail.Summary.SlowCount);
        Assert.Equal(1, payDetail.Summary.FailedCount);
        Assert.Equal(6, payDetail.MatchingCalls);
    }
}
