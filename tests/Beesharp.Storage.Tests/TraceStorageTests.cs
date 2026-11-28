using System.Text;
using DuckDB.NET.Data;
using Beesharp.Services;
using Beesharp.Services.Storage;

namespace Beesharp.Storage.Tests;

/// <summary>
/// Fetched traces live with the run (brain 0065): spans and fetch status in every storage, in the export bundles (parquet, duckdb, json) and
/// their import, in databases from before; and the trace queries answer the same on every storage.
/// </summary>
public class TraceStorageTests
{
    private const string RunId = "run-traces";
    private const long Nanos = 1_795_000_000_000_000_000;

    private static string Trace(int bee) => bee.ToString("x32");
    private static string CallSpan(int bee) => (0x100 + bee).ToString("x16");

    private static TraceSpanDto Span(int bee, string id, string? parent, string service, string name, double startMs, double durationMs,
        bool error = false, Dictionary<string, string>? attrs = null) =>
        new(Trace(bee), id, parent, service, name, "internal", Nanos + (long)(startMs * 1_000_000), durationMs, error, error ? "boom" : null, attrs);

    /// <summary>Eight bees call GET /products/{id}: seven take 40 ms (the query 30 ms), bee 7 takes 400 ms (the query 380 ms). Bee 3's trace was not found.</summary>
    private static async Task SeedAsync(IRunStorage storage, string runId = RunId)
    {
        storage.EnqueueRunStarted(runId, DateTimeOffset.UtcNow, new LoadPlan { Scenarios = [new ScenarioRunConfig { ScenarioId = "shop", Label = "Shop" }] });
        var traces = new List<StoredTrace>();
        for (var bee = 0; bee < 8; bee++)
        {
            var slow = bee == 7;
            var duration = slow ? 400 : 40;
            storage.EnqueueBeeCompleted(runId, new ExecutionResult(bee, 0, bee * 0.5, duration + 5, 200, 10, null,
                Calls: [new RecordedCall("GET /products/{id}", 0, duration, 200, 10, Endpoint: "GET /products/{id}", Method: "GET", Url: $"https://shop.test/products/{bee}", TraceId: Trace(bee), SpanId: CallSpan(bee))],
                TraceId: Trace(bee)), slow ? 1 : 0, slow ? 1 : 0);

            if (bee == 3)
            {
                traces.Add(new StoredTrace(new TraceFetchDto(Trace(bee), bee, TraceFetchStatus.NotFound, TraceFetchReasons.NotArrived, 0, 2, ["typical GET /products/{id}"], DateTimeOffset.UtcNow), []));
                continue;
            }
            var server = (0x200 + bee).ToString("x16");
            var db = (0x300 + bee).ToString("x16");
            traces.Add(new StoredTrace(
                new TraceFetchDto(Trace(bee), bee, TraceFetchStatus.Found, null, 2, 1, [slow ? "slowest GET /products/{id}" : "typical GET /products/{id}"], new DateTimeOffset(2026, 11, 26, 12, 0, bee, TimeSpan.Zero)),
                [
                    Span(bee, server, CallSpan(bee), "shop-api", "GET /products/{id:int}", 1, duration - 2, attrs: new() { ["http.response.status_code"] = "200" }),
                    Span(bee, db, server, "shop-api", "SELECT product", 2, slow ? 380 : 30, error: slow, attrs: new() { ["db.query.text"] = "SELECT product", ["db.system.name"] = "postgresql" }),
                ]));
        }
        await storage.SaveTracesAsync(runId, traces);
        storage.EnqueueRunFinished(runId, DateTimeOffset.UtcNow, "Finished", 5, null);
        await storage.FlushAsync();
    }

    private static async Task AssertStoredAsync(IRunStorage storage, string runId = RunId)
    {
        var fetches = await storage.GetTraceFetchesAsync(runId);
        Assert.Equal(8, fetches.Count);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], fetches.Select(f => f.Sequence).ToArray());
        var found = fetches[5];
        Assert.Equal((Trace(5), TraceFetchStatus.Found, null, 2, 1), (found.TraceId, found.Status, found.Reason, found.SpanCount, found.Attempts));
        Assert.Equal(["typical GET /products/{id}"], found.SelectedFor);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 12, 0, 5, TimeSpan.Zero), found.FetchedUtc);
        var missing = fetches[3];
        Assert.Equal((TraceFetchStatus.NotFound, TraceFetchReasons.NotArrived, 0, 2), (missing.Status, missing.Reason, missing.SpanCount, missing.Attempts));

        var spans = await storage.GetTraceSpansAsync(runId);
        Assert.Equal(14, spans.Count);   // seven found traces with two spans
        var one = await storage.GetTraceSpansAsync(runId, [Trace(7)]);
        Assert.Equal(["GET /products/{id:int}", "SELECT product"], one.Select(s => s.Name).ToArray());
        var db = one[1];
        Assert.Equal(("shop-api", "internal", true, "boom", 380.0), (db.Service, db.Kind, db.IsError, db.StatusMessage, db.DurationMs));
        Assert.Equal(Nanos + 2_000_000, db.StartUnixNanos);
        Assert.Equal((0x200 + 7).ToString("x16"), db.ParentSpanId);
        Assert.Equal("SELECT product", db.Attributes!["db.query.text"]);
        Assert.Empty(await storage.GetTraceSpansAsync(runId, [Trace(3)]));
        Assert.Empty(await storage.GetTraceSpansAsync(runId, []));

        var facts = await storage.GetCallFactsAsync(runId);
        Assert.Equal(8, facts.Count);
        Assert.Equal(Trace(2), facts.Single(f => f.Sequence == 2).TraceId);
        Assert.Equal(CallSpan(2), facts.Single(f => f.Sequence == 2).SpanId);

        // The trace of a bee as a flat list, found and not found.
        var view = (await storage.GetTraceViewAsync(runId, new TraceViewQuery("B8")))!;
        Assert.True(view.Found);
        Assert.Equal(("B8", Trace(7), 2, 1), (view.BeeName, view.TraceId, view.TotalSpans, view.ErrorSpans));
        Assert.Equal(["slowest GET /products/{id}"], view.SelectedFor);
        Assert.Equal(405, view.ReferenceMs, 3);   // the bee duration
        Assert.Equal(380 / 405.0, view.Spans[1].Share, 3);
        var byTraceId = (await storage.GetTraceViewAsync(runId, new TraceViewQuery(Trace(7).ToUpperInvariant())))!;
        Assert.Equal("B8", byTraceId.BeeName);
        var call = (await storage.GetTraceViewAsync(runId, new TraceViewQuery("8", Call: 1)))!;
        Assert.Equal(("call", "GET /products/{id}", 400.0), (call.ReferenceKind, call.Endpoint, call.ReferenceMs));
        var notFound = (await storage.GetTraceViewAsync(runId, new TraceViewQuery("4")))!;
        Assert.False(notFound.Found);
        Assert.Equal(TraceFetchReasons.NotArrived, notFound.Reason);
        Assert.Null(await storage.GetTraceViewAsync(runId, new TraceViewQuery("99")));
        Assert.Null(await storage.GetTraceViewAsync("no-such-run", new TraceViewQuery("1")));
        Assert.Throws<ArgumentException>(() => storage.GetTraceViewAsync(runId, new TraceViewQuery("banana")).GetAwaiter().GetResult());
        await Assert.ThrowsAsync<ArgumentException>(() => storage.GetTraceViewAsync(runId, new TraceViewQuery("1", Call: 5)));

        // The hotspots: where the one slow call's time went, against the typical calls.
        var hotspots = (await storage.GetHotspotsAsync(runId, new HotspotsQuery()))!;
        Assert.Equal("traces: 7 of 8 found, 1 not found: 1 not arrived", hotspots.Coverage.Line);
        var endpoint = Assert.Single(hotspots.Endpoints);
        Assert.Equal((8, 1, 1), (endpoint.TotalCalls, endpoint.SlowCalls, endpoint.SlowCallsWithTrace));
        var top = endpoint.Hotspots[0];
        Assert.Equal(("shop-api", "SELECT product", 1), (top.Service, top.Name, top.Explains));
        Assert.Equal(380, top.MedianSelfMs, 3);
        Assert.Equal(30, top.BaselineSelfMs!.Value, 3);
        Assert.Equal([Trace(7)], top.ExampleTraceIds);
        Assert.Null(await storage.GetHotspotsAsync("no-such-run", new HotspotsQuery()));
    }

    [Fact]
    public async Task In_memory_storage_keeps_traces_and_answers_the_trace_queries()
    {
        var storage = new InMemoryRunStorage();
        await SeedAsync(storage);
        await AssertStoredAsync(storage);
    }

    [Fact]
    public async Task Saving_a_trace_again_replaces_it_so_a_retry_can_turn_not_found_into_found()
    {
        foreach (var storage in new IRunStorage[] { new InMemoryRunStorage(), new DuckDbRunStorage(TempDb(out var path)) })
        {
            await using (storage)
            {
                await SeedAsync(storage);
                var retried = new StoredTrace(
                    new TraceFetchDto(Trace(3), 3, TraceFetchStatus.Found, null, 1, 2, ["typical GET /products/{id}"], DateTimeOffset.UtcNow),
                    [Span(3, "aaaaaaaaaaaaaaaa", CallSpan(3), "shop-api", "GET /products/{id:int}", 1, 38)]);

                await storage.SaveTracesAsync(RunId, [retried]);

                var fetches = await storage.GetTraceFetchesAsync(RunId);
                Assert.Equal(8, fetches.Count);   // replaced, not added
                Assert.Equal((TraceFetchStatus.Found, 2), (fetches[3].Status, fetches[3].Attempts));
                Assert.Single(await storage.GetTraceSpansAsync(RunId, [Trace(3)]));
                Assert.Equal(15, (await storage.GetTraceSpansAsync(RunId)).Count);
            }
            Delete(storage is DuckDbRunStorage ? path : null);
        }
    }

    [Fact]
    public async Task DuckDb_storage_keeps_traces_across_reopening_and_answers_the_trace_queries_like_memory()
    {
        var path = TempDb(out _);
        try
        {
            await using (var storage = new DuckDbRunStorage(path))
            {
                await SeedAsync(storage);
                await AssertStoredAsync(storage);
            }
            await using var reopened = new DuckDbRunStorage(path);
            await AssertStoredAsync(reopened);
            Assert.True(DuckDbRunStorage.CurrentSchemaVersion >= 6);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task A_database_of_the_previous_version_is_upgraded_in_place_and_gets_the_trace_tables()
    {
        var path = TempDb(out _);
        try
        {
            using (var conn = new DuckDBConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                // Schema version 5 (brain 0063): trace IDs on bees and calls, no trace tables yet.
                cmd.CommandText = """
                    CREATE TABLE schema_meta (key VARCHAR PRIMARY KEY, value VARCHAR NOT NULL);
                    INSERT INTO schema_meta VALUES ('format', 'performancetool-run-db'), ('schema_version', '5');
                    CREATE TABLE runs (run_id VARCHAR PRIMARY KEY, started_utc VARCHAR NOT NULL, finished_utc VARCHAR, state VARCHAR NOT NULL, elapsed_seconds DOUBLE NOT NULL, plan_json VARCHAR NOT NULL, error VARCHAR);
                    CREATE TABLE bees (run_id VARCHAR NOT NULL, sequence INTEGER NOT NULL, bee_name VARCHAR NOT NULL, entry INTEGER NOT NULL, started_at DOUBLE NOT NULL, duration_ms DOUBLE NOT NULL, status_code INTEGER, bytes BIGINT NOT NULL, error VARCHAR, outcome INTEGER NOT NULL, is_failed BOOLEAN NOT NULL, is_slow BOOLEAN NOT NULL, slow_calls INTEGER NOT NULL, call_count INTEGER NOT NULL, log_count INTEGER NOT NULL, failed_step INTEGER DEFAULT -1, failed_rep INTEGER DEFAULT -1, step_visits_json VARCHAR, trace_id VARCHAR);
                    CREATE TABLE calls (id BIGINT NOT NULL, run_id VARCHAR NOT NULL, sequence INTEGER NOT NULL, call_index INTEGER NOT NULL, name VARCHAR NOT NULL, offset_ms DOUBLE NOT NULL, duration_ms DOUBLE NOT NULL, status_code INTEGER, bytes BIGINT NOT NULL, error VARCHAR, step INTEGER DEFAULT 0, rep INTEGER DEFAULT 0, endpoint VARCHAR, method VARCHAR, host VARCHAR, url VARCHAR, slow_threshold_ms DOUBLE, ttfb_ms DOUBLE, response_read_ms DOUBLE, headers_json VARCHAR, trace_id VARCHAR, span_id VARCHAR);
                    INSERT INTO runs VALUES ('old-run', '2026-11-25T10:00:00Z', '2026-11-25T10:00:05Z', 'Finished', 5, '{}', NULL);
                    INSERT INTO bees VALUES ('old-run', 0, 'B1', 0, 0.1, 50, 200, 10, NULL, 0, false, false, 0, 1, 0, -1, -1, NULL, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
                    INSERT INTO calls VALUES (1, 'old-run', 0, 0, 'GET /old', 0, 50, 200, 10, NULL, 0, 0, 'GET /old', 'GET', 'x', 'http://x/old', NULL, NULL, NULL, NULL, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'bbbbbbbbbbbbbbbb');
                    """;
                cmd.ExecuteNonQuery();
            }

            await using (var storage = new DuckDbRunStorage(path))
            {
                // The old run simply has no traces; asking says so instead of failing.
                Assert.Empty(await storage.GetTraceFetchesAsync("old-run"));
                Assert.Empty(await storage.GetTraceSpansAsync("old-run"));
                var view = (await storage.GetTraceViewAsync("old-run", new TraceViewQuery("1")))!;
                Assert.False(view.Found);
                Assert.Equal(TraceFetchReasons.NotConfigured, view.Reason);
                var hotspots = (await storage.GetHotspotsAsync("old-run", new HotspotsQuery()))!;
                Assert.Empty(hotspots.Endpoints);
                Assert.Contains("No traces are stored", hotspots.Note);

                // New runs go into the same file, with traces.
                await SeedAsync(storage);
                await AssertStoredAsync(storage);
            }

            using var check = new DuckDBConnection($"Data Source={path}");
            check.Open();
            using var version = check.CreateCommand();
            version.CommandText = "SELECT value FROM schema_meta WHERE key = 'schema_version';";
            Assert.Equal(DuckDbRunStorage.CurrentSchemaVersion.ToString(), version.ExecuteScalar());
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Run_exports_carry_the_traces_and_importing_them_restores_spans_and_fetch_status()
    {
        var src = TempDb(out _);
        var dst = TempDb(out _);
        try
        {
            RunExportResult parquet, duck, json;
            await using (var storage = new DuckDbRunStorage(src))
            {
                await SeedAsync(storage);
                parquet = (await storage.ExportRunAsync(RunId, "parquet"))!;
                duck = (await storage.ExportRunAsync(RunId, "duckdb"))!;
                json = (await storage.ExportRunAsync(RunId, "json"))!;
            }

            using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(parquet.Content)))
            {
                Assert.Contains(zip.Entries, e => e.Name == "trace_spans.parquet");
                Assert.Contains(zip.Entries, e => e.Name == "trace_fetches.parquet");
            }
            var jsonText = Encoding.UTF8.GetString(json.Content);
            Assert.Contains("\"traceFetches\"", jsonText);
            Assert.Contains("\"db.query.text\": \"SELECT product\"", jsonText);

            await using var target = new DuckDbRunStorage(dst);
            foreach (var export in new[] { parquet, duck, json })
            {
                await using var ms = new MemoryStream(export.Content);
                var summary = await target.ImportRunAsync(export.FileName, ms);
                await AssertStoredAsync(target, summary.RunId);
            }
        }
        finally
        {
            Delete(src);
            Delete(dst);
        }
    }

    [Fact]
    public async Task A_bundle_without_trace_tables_imports_as_a_run_without_traces()
    {
        var src = TempDb(out _);
        var dst = TempDb(out _);
        try
        {
            RunExportResult json;
            await using (var storage = new DuckDbRunStorage(src))
            {
                storage.EnqueueRunStarted("plain", DateTimeOffset.UtcNow, new LoadPlan());
                storage.EnqueueRunFinished("plain", DateTimeOffset.UtcNow, "Finished", 1, null);
                await storage.FlushAsync();
                json = (await storage.ExportRunAsync("plain", "json"))!;
            }
            // An export from before traces: no trace arrays at all.
            var text = Encoding.UTF8.GetString(json.Content);
            var withoutTraces = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
            withoutTraces.Remove("traceFetches");
            withoutTraces.Remove("traceSpans");

            await using var target = new DuckDbRunStorage(dst);
            await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(withoutTraces.ToJsonString()));
            var summary = await target.ImportRunAsync("plain.json", ms);

            Assert.Empty(await target.GetTraceFetchesAsync(summary.RunId));
        }
        finally
        {
            Delete(src);
            Delete(dst);
        }
    }

    private static string TempDb(out string path) => path = Path.Combine(Path.GetTempPath(), $"beesharp-traces-{Guid.NewGuid():N}.duckdb");

    private static void Delete(string? path)
    {
        if (path is null) return;
        foreach (var p in new[] { path, path + ".wal" })
            if (File.Exists(p)) File.Delete(p);
    }
}
