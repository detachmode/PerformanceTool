using DuckDB.NET.Data;
using Beesharp.Services;
using Beesharp.Services.Storage;

namespace Beesharp.Storage.Tests;

/// <summary>The trace ID and span IDs a run recorded (brain 0062) survive storage, the call queries and the export bundles.</summary>
public class TraceIdStorageTests
{
    private const string TraceA = "4d7ce6ac47fbe9b3822d67a473ce9774";
    private const string TraceB = "0af7651916cd43dd8448eb211c80319c";

    private static ExecutionResult Bee(int sequence, string traceId) => new(
        sequence, 0, sequence * 0.5, 120, 200, 64, null,
        Calls:
        [
            new RecordedCall("GET /login", 0, 40, 200, 32, Endpoint: "GET /login", Method: "GET", Url: "https://shop.test/login", TraceId: traceId, SpanId: $"00f067aa0ba902b{sequence}"),
            new RecordedCall("POST /pay", 50, 70, 200, 32, Endpoint: "POST /pay", Method: "POST", Url: "https://shop.test/pay", TraceId: traceId, SpanId: $"b7ad6b716920333{sequence}"),
        ],
        TraceId: traceId);

    private static async Task SeedAsync(IRunStorage storage, string runId = "run-trace")
    {
        storage.EnqueueRunStarted(runId, DateTimeOffset.UtcNow, new LoadPlan { Scenarios = [new ScenarioRunConfig { ScenarioId = "http-get", Label = "Shop" }] });
        storage.EnqueueBeeCompleted(runId, Bee(0, TraceA), 0, 0);
        storage.EnqueueBeeCompleted(runId, Bee(1, TraceB), 0, 0);
        storage.EnqueueRunFinished(runId, DateTimeOffset.UtcNow, "Finished", 2, null);
        await storage.FlushAsync();
    }

    private static async Task AssertStoredAsync(IRunStorage storage, string runId = "run-trace")
    {
        var bee = (await storage.GetBeeDetailAsync(runId, 1))!;
        Assert.Equal(TraceB, bee.TraceId);
        Assert.Equal([TraceB, TraceB], bee.Calls.Select(c => c.TraceId!).ToArray());
        Assert.Equal(["00f067aa0ba902b1", "b7ad6b7169203331"], bee.Calls.Select(c => c.SpanId!).ToArray());

        // The /calls page's call list and the slowest list carry them too.
        var detail = (await storage.GetCallEndpointDetailAsync(runId, new CallDetailQuery("GET /login", Sort: "time")))!;
        Assert.Equal([TraceA, TraceB], detail.Calls.Select(c => c.TraceId!).ToArray());
        Assert.Equal(["00f067aa0ba902b0", "00f067aa0ba902b1"], detail.Calls.Select(c => c.SpanId!).ToArray());
        var slowest = await storage.GetSlowestCallsAsync(runId, new SlowestCallsQuery());
        Assert.All(slowest.Calls, c => Assert.Matches("^[0-9a-f]{32}$", c.TraceId));
    }

    [Fact]
    public async Task In_memory_storage_keeps_trace_and_span_ids()
    {
        var storage = new InMemoryRunStorage();
        await SeedAsync(storage);
        await AssertStoredAsync(storage);
    }

    [Fact]
    public async Task DuckDb_storage_keeps_trace_and_span_ids_and_old_databases_get_the_new_columns()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"beesharp-trace-{Guid.NewGuid():N}.duckdb");
        try
        {
            await using (var storage = new DuckDbRunStorage(dbPath))
            {
                await SeedAsync(storage);
                await AssertStoredAsync(storage);
            }

            // Reopening: the schema is migrated in place (ADD COLUMN IF NOT EXISTS), the stored IDs are still there.
            await using (var reopened = new DuckDbRunStorage(dbPath))
                await AssertStoredAsync(reopened);
            Assert.True(DuckDbRunStorage.CurrentSchemaVersion >= 5);
        }
        finally { Delete(dbPath); }
    }

    [Fact]
    public async Task A_database_from_before_tracing_is_upgraded_and_its_calls_simply_have_no_trace()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"beesharp-trace-old-{Guid.NewGuid():N}.duckdb");
        try
        {
            using (var conn = new DuckDBConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE schema_meta (key VARCHAR PRIMARY KEY, value VARCHAR NOT NULL);
                    INSERT INTO schema_meta VALUES ('format', 'performancetool-run-db'), ('schema_version', '4');
                    CREATE TABLE runs (run_id VARCHAR PRIMARY KEY, started_utc VARCHAR NOT NULL, finished_utc VARCHAR, state VARCHAR NOT NULL, elapsed_seconds DOUBLE NOT NULL, plan_json VARCHAR NOT NULL, error VARCHAR);
                    CREATE TABLE bees (run_id VARCHAR NOT NULL, sequence INTEGER NOT NULL, bee_name VARCHAR NOT NULL, entry INTEGER NOT NULL, started_at DOUBLE NOT NULL, duration_ms DOUBLE NOT NULL, status_code INTEGER, bytes BIGINT NOT NULL, error VARCHAR, outcome INTEGER NOT NULL, is_failed BOOLEAN NOT NULL, is_slow BOOLEAN NOT NULL, slow_calls INTEGER NOT NULL, call_count INTEGER NOT NULL, log_count INTEGER NOT NULL, failed_step INTEGER DEFAULT -1, failed_rep INTEGER DEFAULT -1, step_visits_json VARCHAR);
                    CREATE TABLE calls (id BIGINT NOT NULL, run_id VARCHAR NOT NULL, sequence INTEGER NOT NULL, call_index INTEGER NOT NULL, name VARCHAR NOT NULL, offset_ms DOUBLE NOT NULL, duration_ms DOUBLE NOT NULL, status_code INTEGER, bytes BIGINT NOT NULL, error VARCHAR, step INTEGER DEFAULT 0, rep INTEGER DEFAULT 0, endpoint VARCHAR, method VARCHAR, host VARCHAR, url VARCHAR, slow_threshold_ms DOUBLE, ttfb_ms DOUBLE, response_read_ms DOUBLE, headers_json VARCHAR);
                    INSERT INTO runs VALUES ('old-run', '2026-11-24T10:00:00Z', '2026-11-24T10:00:05Z', 'Finished', 5, '{}', NULL);
                    INSERT INTO bees VALUES ('old-run', 0, 'B1', 0, 0.1, 50, 200, 10, NULL, 0, false, false, 0, 1, 0, -1, -1, NULL);
                    INSERT INTO calls VALUES (1, 'old-run', 0, 0, 'GET /old', 0, 50, 200, 10, NULL, 0, 0, 'GET /old', 'GET', 'x', 'http://x/old', NULL, NULL, NULL, NULL);
                    """;
                cmd.ExecuteNonQuery();
            }

            await using var storage = new DuckDbRunStorage(dbPath);
            var old = (await storage.GetBeeDetailAsync("old-run", 0))!;
            Assert.Null(old.TraceId);
            Assert.Null(old.Calls[0].TraceId);

            // New runs can be added to the same file.
            await SeedAsync(storage);
            await AssertStoredAsync(storage);
        }
        finally { Delete(dbPath); }
    }

    [Fact]
    public async Task Run_exports_carry_the_ids_and_importing_them_restores_them()
    {
        var srcDb = Path.Combine(Path.GetTempPath(), $"beesharp-trace-src-{Guid.NewGuid():N}.duckdb");
        var dstDb = Path.Combine(Path.GetTempPath(), $"beesharp-trace-dst-{Guid.NewGuid():N}.duckdb");
        try
        {
            RunExportResult pqZip, duckFile, jsonFile;
            await using (var src = new DuckDbRunStorage(srcDb))
            {
                await SeedAsync(src);
                pqZip = (await src.ExportRunAsync("run-trace", "parquet"))!;
                duckFile = (await src.ExportRunAsync("run-trace", "duckdb"))!;
                jsonFile = (await src.ExportRunAsync("run-trace", "json"))!;
            }

            // The JSON bundle names them, so people and agents reading it find the trace of a bee and the span of a call.
            var json = System.Text.Encoding.UTF8.GetString(jsonFile.Content);
            Assert.Contains($"\"traceId\": \"{TraceA}\"", json);
            Assert.Contains("\"spanId\": \"00f067aa0ba902b0\"", json);

            await using var dst = new DuckDbRunStorage(dstDb);
            foreach (var export in new[] { pqZip, duckFile, jsonFile })
            {
                await using var ms = new MemoryStream(export.Content);
                var summary = await dst.ImportRunAsync(export.FileName, ms);
                await AssertStoredAsync(dst, summary.RunId);
            }
        }
        finally
        {
            Delete(srcDb);
            Delete(dstDb);
        }
    }

    private static void Delete(string dbPath)
    {
        foreach (var p in new[] { dbPath, dbPath + ".wal" })
            if (File.Exists(p)) File.Delete(p);
    }
}
