using System.Diagnostics;
using PerformanceTool.Services;
using PerformanceTool.Services.Storage;

namespace PerformanceTool.Tests;

public sealed class SqliteStorageTests
{
    [Fact]
    public async Task SqliteRunStorage_PersistsRunsActorsCallsAndIndexedFilteredLogs()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"perftool-test-{Guid.NewGuid():N}.db");
        try
        {
            await using (var storage = new SqliteRunStorage(dbPath))
            {
                var plan = new LoadPlan
                {
                    DurationSeconds = 10,
                    MaxConcurrency = 10,
                    TimeoutSeconds = 5,
                    Scenarios = [new ScenarioRunConfig { ScenarioId = "http-get", Label = "Orders", RatePerMinute = 60 }],
                };
                var started = DateTimeOffset.UtcNow;
                storage.EnqueueRunStarted("run-1", started, plan);

                // Actor #1: OK
                storage.EnqueueActorCompleted("run-1", new ExecutionResult(
                    Sequence: 0, Entry: 0, StartedAtSeconds: 0.1, ElapsedMs: 120, StatusCode: 200, Bytes: 512, Error: null,
                    Calls: [new RecordedCall("Search", 0, 50, 200, 256), new RecordedCall("Checkout", 50, 70, 200, 256)],
                    Logs: [new RecordedLog(0.15, "Information", "Order succeeded", null, 0)]),
                    actorOutcome: 0, slowCalls: 0);

                // Actor #2: Failed + Slow
                storage.EnqueueActorCompleted("run-1", new ExecutionResult(
                    Sequence: 1, Entry: 0, StartedAtSeconds: 0.5, ElapsedMs: 2100, StatusCode: 500, Bytes: 0, Error: "Payment declined",
                    Calls: [new RecordedCall("Payment", 0, 2100, 500, 0, "Payment declined")],
                    Logs:
                    [
                        new RecordedLog(0.6, "Warning", "Retrying payment gateway", null, 0),
                        new RecordedLog(2.6, "Error", "Payment failed for cart", "Payment declined", 0),
                    ]),
                    actorOutcome: 2, slowCalls: 1);

                storage.EnqueueRunFinished("run-1", started.AddSeconds(3), "Finished", 3.0, null);
                await storage.FlushAsync();

                var runs = await storage.ListRunsAsync();
                Assert.Single(runs);
                Assert.Equal("run-1", runs[0].RunId);
                Assert.Equal(2, runs[0].TotalActors);
                Assert.Equal(1, runs[0].FailedActors);
                Assert.Equal(3, runs[0].TotalLogs);

                // Lazy-load actor #2 detail
                var detail = await storage.GetActorDetailAsync("run-1", 1);
                Assert.NotNull(detail);
                Assert.Single(detail!.Calls);
                Assert.Equal(2, detail.Logs.Count);
                Assert.Equal("Payment declined", detail.Error);

                // Filter by indexed is_failed flag
                var failedLogs = await storage.QueryLogsAsync("run-1", new LogQuery(Failed: true));
                Assert.Equal(2, failedLogs.TotalCount);
                Assert.All(failedLogs.Items, r => Assert.True(r.IsFailed));

                // Filter by indexed is_slow flag
                var slowLogs = await storage.QueryLogsAsync("run-1", new LogQuery(Slow: true));
                Assert.Equal(2, slowLogs.TotalCount);
                Assert.All(slowLogs.Items, r => Assert.True(r.IsSlow));

                // Filter by indexed actor_name
                var actor1Logs = await storage.QueryLogsAsync("run-1", new LogQuery(ActorName: "Actor #1"));
                Assert.Equal(1, actor1Logs.TotalCount);
                Assert.Equal("Order succeeded", actor1Logs.Items[0].Message);

                // Filter by level + search
                var warnLogs = await storage.QueryLogsAsync("run-1", new LogQuery(Levels: ["Warning"], Search: "gateway"));
                Assert.Equal(1, warnLogs.TotalCount);
                Assert.Equal("Warning", warnLogs.Items[0].Level);
            }

            // Reopen database from disk to verify persistence
            await using var reopened = new SqliteRunStorage(dbPath);
            var persistedRuns = await reopened.ListRunsAsync();
            Assert.Single(persistedRuns);
            Assert.Equal(3, persistedRuns[0].TotalLogs);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    }

    [Fact]
    public async Task SqliteRunStorage_HandlesHighThroughputBatchIngestionAndPaging()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"perftool-bench-{Guid.NewGuid():N}.db");
        try
        {
            await using var storage = new SqliteRunStorage(dbPath);
            storage.EnqueueRunStarted("run-scale", DateTimeOffset.UtcNow, new LoadPlan());

            const int actorCount = 2000;
            const int logsPerActor = 5; // 10,000 log lines
            var sw = Stopwatch.StartNew();

            for (var i = 0; i < actorCount; i++)
            {
                var failed = i % 20 == 0;
                var logs = new RecordedLog[logsPerActor];
                for (var j = 0; j < logsPerActor; j++)
                    logs[j] = new RecordedLog(i * 0.001, failed && j == 4 ? "Error" : "Information", $"Actor {i} step {j} completed", failed && j == 4 ? "Boom" : null, j);

                storage.EnqueueActorCompleted("run-scale", new ExecutionResult(
                    i, i % 3, i * 0.001, failed ? 1800 : 120, failed ? 500 : 200, 100, failed ? "Boom" : null,
                    Calls: [new RecordedCall("Step", 0, 120, failed ? 500 : 200)],
                    Logs: logs),
                    actorOutcome: failed ? 2 : 0,
                    slowCalls: failed ? 1 : 0);
            }

            await storage.FlushAsync();
            sw.Stop();

            // Verify all 10,000 logs persisted quickly (well above 1,000 logs/sec)
            var totalLogs = actorCount * logsPerActor;
            var logsPerSec = totalLogs / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            Assert.True(logsPerSec > 1000, $"Expected > 1000 logs/sec, got {logsPerSec:0}");

            // Paged query over failed actors
            var page1 = await storage.QueryLogsAsync("run-scale", new LogQuery(Failed: true, Offset: 0, Limit: 50));
            Assert.Equal(500, page1.TotalCount); // 100 failed actors * 5 logs
            Assert.Equal(50, page1.Items.Count);

            var page2 = await storage.QueryLogsAsync("run-scale", new LogQuery(Failed: true, Offset: 50, Limit: 50));
            Assert.Equal(50, page2.Items.Count);
            Assert.NotEqual(page1.Items[0].Id, page2.Items[0].Id);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    }
}
