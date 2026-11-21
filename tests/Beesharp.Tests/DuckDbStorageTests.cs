using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PerformanceTool.Services;
using PerformanceTool.Services.Storage;

namespace PerformanceTool.Tests;

public sealed class DuckDbStorageTests
{
    [Fact]
    public async Task DuckDbRunStorage_PersistsFiltersAndExportsAllFormats()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"perftool-duck-{Guid.NewGuid():N}.duckdb");
        var exportedDuckPath = Path.Combine(Path.GetTempPath(), $"perftool-exp-verify-{Guid.NewGuid():N}.duckdb");
        try
        {
            await using (var storage = new DuckDbRunStorage(dbPath))
            {
                var plan = new LoadPlan
                {
                    DurationSeconds = 10,
                    MaxConcurrency = 10,
                    TimeoutSeconds = 5,
                    Scenarios = [new ScenarioRunConfig { ScenarioId = "http-get", Label = "Orders", RatePerMinute = 60 }],
                };
                var started = DateTimeOffset.UtcNow;
                storage.EnqueueRunStarted("run-duck-1", started, plan);

                storage.EnqueueBeeCompleted("run-duck-1", new ExecutionResult(
                    Sequence: 0, Entry: 0, StartedAtSeconds: 0.1, ElapsedMs: 120, StatusCode: 200, Bytes: 512, Error: null,
                    Calls: [new RecordedCall("Search", 0, 50, 200, 256), new RecordedCall("Checkout", 50, 70, 200, 256)],
                    Logs: [new RecordedLog(0.15, "Information", "Order succeeded", null, 0)]),
                    beeOutcome: 0, slowCalls: 0);

                storage.EnqueueBeeCompleted("run-duck-1", new ExecutionResult(
                    Sequence: 1, Entry: 0, StartedAtSeconds: 0.5, ElapsedMs: 2100, StatusCode: 500, Bytes: 0, Error: "Payment declined",
                    Calls: [new RecordedCall("Payment", 0, 2100, 500, 0, "Payment declined")],
                    Logs:
                    [
                        new RecordedLog(0.6, "Warning", "Retrying payment gateway", null, 0),
                        new RecordedLog(2.6, "Error", "Payment failed for cart", "Payment declined", 0),
                    ]),
                    beeOutcome: 2, slowCalls: 1);

                storage.EnqueueRunFinished("run-duck-1", started.AddSeconds(3), "Finished", 3.0, null);
                await storage.FlushAsync();

                var runs = await storage.ListRunsAsync();
                Assert.Single(runs);
                Assert.Equal("run-duck-1", runs[0].RunId);
                Assert.Equal(2, runs[0].TotalBees);
                Assert.Equal(1, runs[0].FailedBees);
                Assert.Equal(3, runs[0].TotalLogs);

                var detail = await storage.GetBeeDetailAsync("run-duck-1", 1);
                Assert.NotNull(detail);
                Assert.Single(detail!.Calls);
                Assert.Equal(2, detail.Logs.Count);

                // Indexed filters: failed, slow, bee_name, level, search
                Assert.Equal(2, (await storage.QueryLogsAsync("run-duck-1", new LogQuery(Failed: true))).TotalCount);
                Assert.Equal(2, (await storage.QueryLogsAsync("run-duck-1", new LogQuery(Slow: true))).TotalCount);
                Assert.Equal(1, (await storage.QueryLogsAsync("run-duck-1", new LogQuery(BeeName: "B1"))).TotalCount);
                Assert.Equal(1, (await storage.QueryLogsAsync("run-duck-1", new LogQuery(Levels: ["Warning"], Search: "gateway"))).TotalCount);

                // Export: JSON
                var jsonExp = await storage.ExportRunAsync("run-duck-1", "json");
                Assert.NotNull(jsonExp);
                using (var doc = JsonDocument.Parse(jsonExp!.Content))
                {
                    Assert.Equal(DuckDbRunStorage.CurrentSchemaVersion, doc.RootElement.GetProperty("schemaVersion").GetInt32());
                    Assert.Equal("run-duck-1", doc.RootElement.GetProperty("runId").GetString());
                    Assert.Equal(2, doc.RootElement.GetProperty("bees").GetArrayLength());
                    Assert.Equal(3, doc.RootElement.GetProperty("logs").GetArrayLength());
                }

                // Export: CSV
                var csvExp = await storage.ExportRunAsync("run-duck-1", "csv");
                Assert.NotNull(csvExp);
                var csvText = Encoding.UTF8.GetString(csvExp!.Content);
                Assert.Contains("Order succeeded", csvText);
                Assert.Contains("Payment failed for cart", csvText);

                // Export: Parquet bundle (.zip containing manifest.json + runs, bees, calls, logs as ZSTD Parquet)
                var parquetExp = await storage.ExportRunAsync("run-duck-1", "parquet");
                Assert.NotNull(parquetExp);
                Assert.EndsWith("-parquet.zip", parquetExp!.FileName);
                using (var zip = new ZipArchive(new MemoryStream(parquetExp.Content), ZipArchiveMode.Read))
                {
                    var names = zip.Entries.Select(e => e.Name).OrderBy(n => n).ToArray();
                    Assert.Equal(["bees.parquet", "calls.parquet", "logs.parquet", "manifest.json", "runs.parquet"], names);
                    using (var manifestDoc = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
                    {
                        Assert.Equal(DuckDbRunStorage.CurrentSchemaVersion, manifestDoc.RootElement.GetProperty("schemaVersion").GetInt32());
                        Assert.Equal("run-duck-1", manifestDoc.RootElement.GetProperty("runId").GetString());
                    }
                    using var logsStream = zip.GetEntry("logs.parquet")!.Open();
                    var header = new byte[4];
                    Assert.Equal(4, logsStream.Read(header, 0, 4));
                    Assert.Equal("PAR1", Encoding.ASCII.GetString(header));
                }

                // Export: standalone DuckDB file
                var duckExp = await storage.ExportRunAsync("run-duck-1", "duckdb");
                Assert.NotNull(duckExp);
                await File.WriteAllBytesAsync(exportedDuckPath, duckExp!.Content);
            }

            // Reopen the exported standalone .duckdb file and verify it contains the run and restores cleanly!
            await using var exportedDb = new DuckDbRunStorage(exportedDuckPath);
            var exportedRuns = await exportedDb.ListRunsAsync();
            Assert.Single(exportedRuns);
            Assert.Equal("run-duck-1", exportedRuns[0].RunId);
            Assert.Equal(3, exportedRuns[0].TotalLogs);

            var restored = await exportedDb.GetRunAsync("run-duck-1");
            Assert.NotNull(restored);
            Assert.Single(restored!.Plan.Scenarios);
            Assert.Equal(2, restored.Bees.Count);
            Assert.Equal(3, restored.TotalLogs);
        }
        finally
        {
            foreach (var p in new[] { dbPath, dbPath + ".wal", exportedDuckPath, exportedDuckPath + ".wal" })
                if (File.Exists(p)) File.Delete(p);
        }
    }

    [Fact]
    public async Task DuckDbRunStorage_HandlesHighThroughputAppenderIngestionAndPaging()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"perftool-duck-bench-{Guid.NewGuid():N}.duckdb");
        try
        {
            await using var storage = new DuckDbRunStorage(dbPath);
            storage.EnqueueRunStarted("run-duck-scale", DateTimeOffset.UtcNow, new LoadPlan());

            const int beeCount = 2000;
            const int logsPerBee = 5; // 10,000 log lines
            var sw = Stopwatch.StartNew();

            for (var i = 0; i < beeCount; i++)
            {
                var failed = i % 20 == 0;
                var logs = new RecordedLog[logsPerBee];
                for (var j = 0; j < logsPerBee; j++)
                    logs[j] = new RecordedLog(i * 0.001, failed && j == 4 ? "Error" : "Information", $"Bee {i} step {j} completed", failed && j == 4 ? "Boom" : null, j);

                storage.EnqueueBeeCompleted("run-duck-scale", new ExecutionResult(
                    i, i % 3, i * 0.001, failed ? 1800 : 120, failed ? 500 : 200, 100, failed ? "Boom" : null,
                    Calls: [new RecordedCall("Step", 0, 120, failed ? 500 : 200)],
                    Logs: logs),
                    beeOutcome: failed ? 2 : 0,
                    slowCalls: failed ? 1 : 0);
            }

            await storage.FlushAsync();
            sw.Stop();

            var logsPerSec = (beeCount * logsPerBee) / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            Assert.True(logsPerSec > 2000, $"Expected > 2000 logs/sec with DuckDBAppender, got {logsPerSec:0}");

            var page1 = await storage.QueryLogsAsync("run-duck-scale", new LogQuery(Failed: true, Offset: 0, Limit: 50));
            Assert.Equal(500, page1.TotalCount);
            Assert.Equal(50, page1.Items.Count);
        }
        finally
        {
            foreach (var p in new[] { dbPath, dbPath + ".wal" })
                if (File.Exists(p)) File.Delete(p);
        }
    }
}
