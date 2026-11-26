using System.Net;
using System.Text;
using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Storage;
using Beesharp.Services.TraceStore;
using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Cli.Tests;

/// <summary>
/// <c>beesharp runs trace</c> and <c>runs hotspots</c> (brain 0065): offline on all three bundle formats, live against a server, the not-found exit code,
/// trace IDs in <c>runs calls --slowest</c> and <c>runs bee</c>, and <c>beesharp run</c> fetching and reporting traces.
/// </summary>
[Collection("RunsTempIsolation")]
public sealed class TraceCommandsTests : IDisposable
{
    private const string RunId = "run-traces";
    private const long Nanos = 1_795_000_000_000_000_000;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly string work = Path.Combine(Path.GetTempPath(), $"beesharp-trace-cli-{Guid.NewGuid():N}");

    public TraceCommandsTests() => Directory.CreateDirectory(work);

    public void Dispose()
    {
        if (Directory.Exists(work)) try { Directory.Delete(work, recursive: true); } catch (IOException) { }
    }

    private static string Trace(int bee) => bee.ToString("x32");
    private static string CallSpan(int bee) => (0x100 + bee).ToString("x16");

    private static TraceSpanDto Span(int bee, string id, string? parent, string service, string name, double startMs, double durationMs, Dictionary<string, string>? attrs = null) =>
        new(Trace(bee), id, parent, service, name, "internal", Nanos + (long)(startMs * 1_000_000), durationMs, false, null, attrs);

    /// <summary>Eight bees call GET /products/{id}: seven in 40 ms (the query 30 ms), bee 8 in 400 ms (the query 380 ms). Bee 4's trace was not found.</summary>
    private static async Task SeedAsync(IRunStorage storage)
    {
        storage.EnqueueRunStarted(RunId, DateTimeOffset.UtcNow, new LoadPlan { Scenarios = [new ScenarioRunConfig { ScenarioId = "shop", Label = "Shop" }] });
        var traces = new List<StoredTrace>();
        for (var bee = 0; bee < 8; bee++)
        {
            var slow = bee == 7;
            var duration = slow ? 400 : 40;
            storage.EnqueueBeeCompleted(RunId, new ExecutionResult(bee, 0, bee * 0.5, duration + 5, 200, 10, null,
                Calls: [new RecordedCall("GET /products/{id}", 0, duration, 200, 10, Endpoint: "GET /products/{id}", Method: "GET", Url: $"https://shop.test/products/{bee}", TraceId: Trace(bee), SpanId: CallSpan(bee))],
                TraceId: Trace(bee)), slow ? 1 : 0, slow ? 1 : 0);
            if (bee == 3)
            {
                traces.Add(new StoredTrace(new TraceFetchDto(Trace(bee), bee, TraceFetchStatus.NotFound, TraceFetchReasons.NotArrived, 0, 2, ["typical GET /products/{id}"], DateTimeOffset.UtcNow), []));
                continue;
            }
            var server = (0x200 + bee).ToString("x16");
            traces.Add(new StoredTrace(
                new TraceFetchDto(Trace(bee), bee, TraceFetchStatus.Found, null, 2, 1, [slow ? "slowest GET /products/{id}" : "typical GET /products/{id}"], new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero)),
                [
                    Span(bee, server, CallSpan(bee), "shop-api", "GET /products/{id:int}", 1, duration - 2, new() { ["http.response.status_code"] = "200" }),
                    Span(bee, (0x300 + bee).ToString("x16"), server, "shop-api", "SELECT product", 2, slow ? 380 : 30, new() { ["db.query.text"] = "SELECT product" }),
                ]));
        }
        await storage.SaveTracesAsync(RunId, traces);
        storage.EnqueueRunFinished(RunId, DateTimeOffset.UtcNow, "Finished", 5, null);
        await storage.FlushAsync();
    }

    private async Task<(string Zip, string Duck, string Json, string Db)> BundlesAsync()
    {
        var db = Path.Combine(work, "store.duckdb");
        await using var storage = new DuckDbRunStorage(db);
        await SeedAsync(storage);
        string Save(string format)
        {
            var export = storage.ExportRunAsync(RunId, format).GetAwaiter().GetResult()!;
            var path = Path.Combine(work, "bundle" + Path.GetExtension(export.FileName));
            path = format == "parquet" ? Path.Combine(work, "bundle.zip") : format == "duckdb" ? Path.Combine(work, "bundle.duckdb") : Path.Combine(work, "bundle.json");
            File.WriteAllBytes(path, export.Content);
            return path;
        }
        return (Save("parquet"), Save("duckdb"), Save("json"), db);
    }

    private static async Task<(int Exit, string Out, string Err)> CliAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    // ---- offline, on every bundle format ----

    [Fact]
    public async Task Hotspots_show_where_the_slow_calls_time_went_on_every_bundle_format()
    {
        var (zip, duck, json, _) = await BundlesAsync();
        foreach (var bundle in new[] { zip, duck, json })
        {
            var text = await CliAsync("runs", "hotspots", bundle);
            Assert.Equal(0, text.Exit);
            Assert.Equal("", text.Err);
            Assert.Contains("traces: 7 of 8 found, 1 not found: 1 not arrived", text.Out);
            Assert.Contains("GET /products/{id}: 1 of 8 calls slow", text.Out);
            Assert.Contains("shop-api  SELECT product", text.Out);
            Assert.Contains("explains 1 of 1 slow calls (100%)", text.Out);
            Assert.Contains("typically 380 ms = 95% of the call, 12.7x the baseline of 30 ms", text.Out);
            Assert.Contains("db.query.text=SELECT product", text.Out);
            Assert.Contains(Trace(7), text.Out);

            var jsonResult = await CliAsync("runs", "hotspots", bundle, "--json");
            using var doc = JsonDocument.Parse(jsonResult.Out);
            var endpoint = doc.RootElement.GetProperty("endpoints")[0];
            Assert.Equal("GET /products/{id}", endpoint.GetProperty("endpoint").GetString());
            Assert.Equal(1, endpoint.GetProperty("slowCallsWithTrace").GetInt32());
            var hotspot = endpoint.GetProperty("hotspots")[0];
            Assert.Equal("SELECT product", hotspot.GetProperty("name").GetString());
            Assert.Equal(Trace(7), hotspot.GetProperty("exampleTraceIds")[0].GetString());
            Assert.Equal(7, doc.RootElement.GetProperty("coverage").GetProperty("found").GetInt32());
        }
    }

    [Fact]
    public async Task Hotspot_filters_and_limits_apply_and_bad_options_exit_with_2()
    {
        var (zip, _, _, _) = await BundlesAsync();

        var none = await CliAsync("runs", "hotspots", zip, "--endpoint", "/nothing");
        Assert.Equal(0, none.Exit);
        Assert.Contains("No endpoint containing '/nothing'", none.Out);

        var slow = await CliAsync("runs", "hotspots", zip, "--slow", "--limit", "1");
        Assert.Contains("GET /products/{id}", slow.Out);

        Assert.Equal(2, (await CliAsync("runs", "hotspots", zip, "--limit", "0")).Exit);
        Assert.Equal(2, (await CliAsync("runs", "hotspots")).Exit);
    }

    [Fact]
    public async Task Trace_shows_a_bee_as_a_flat_bounded_list_with_self_time_share_and_key_attributes_on_every_bundle_format()
    {
        var (zip, duck, json, _) = await BundlesAsync();
        foreach (var bundle in new[] { zip, duck, json })
        {
            var text = await CliAsync("runs", "trace", bundle, "B8");
            Assert.Equal(0, text.Exit);
            Assert.Contains($"Trace B8  {Trace(7)}", text.Out);
            Assert.Contains("2 (all shown) in 1 service(s): shop-api", text.Out);
            Assert.Contains("the bee's duration 405 ms", text.Out);
            Assert.Contains("selected for: slowest GET /products/{id}", text.Out);
            var db = text.Out.Split('\n').Single(l => l.Contains("SELECT product") && l.Contains("Status") == false && l.Contains("380 ms"));
            Assert.Contains("94%", db);   // 380 of 405 ms
            Assert.Contains("db.query.text=SELECT product", text.Out);
            Assert.Contains("HTTP 200", text.Out);

            var byId = await CliAsync("runs", "trace", bundle, Trace(7), "--call", "1", "--max-spans", "1");
            Assert.Equal(0, byId.Exit);
            Assert.Contains("call #1", byId.Out);
            Assert.Contains("showing 1", byId.Out);

            var jsonResult = await CliAsync("runs", "trace", bundle, "8", "--json");
            using var doc = JsonDocument.Parse(jsonResult.Out);
            Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
            Assert.Equal(8, doc.RootElement.GetProperty("bee").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("spans").GetArrayLength());
            Assert.Equal(380, doc.RootElement.GetProperty("spans")[1].GetProperty("selfMs").GetDouble(), 3);
        }
    }

    [Fact]
    public async Task A_trace_that_is_not_stored_exits_with_3_and_says_why_while_a_missing_bee_or_a_bad_call_exits_with_2()
    {
        var (zip, _, json, _) = await BundlesAsync();
        foreach (var bundle in new[] { zip, json })
        {
            var notFound = await CliAsync("runs", "trace", bundle, "4");
            Assert.Equal(ExitCodes.TraceNotFound, notFound.Exit);
            Assert.Contains("The trace of bee B4 is not stored with run run-traces.", notFound.Out);
            Assert.Contains("Reason     not arrived yet", notFound.Out);
            Assert.Contains("Selected   typical GET /products/{id}", notFound.Out);

            var asJson = await CliAsync("runs", "trace", bundle, "4", "--json");
            Assert.Equal(3, asJson.Exit);
            using var doc = JsonDocument.Parse(asJson.Out);
            Assert.False(doc.RootElement.GetProperty("found").GetBoolean());
            Assert.Equal("not-arrived", doc.RootElement.GetProperty("reason").GetString());

            Assert.Equal(2, (await CliAsync("runs", "trace", bundle, "99")).Exit);
            var badCall = await CliAsync("runs", "trace", bundle, "1", "--call", "9");
            Assert.Equal(2, badCall.Exit);
            Assert.Contains("there is no call 9", badCall.Err);
            Assert.Equal(2, (await CliAsync("runs", "trace", bundle, "banana")).Exit);
            Assert.Equal(2, (await CliAsync("runs", "trace", bundle, Trace(9999))).Exit);
        }
    }

    [Fact]
    public async Task A_run_without_fetched_traces_says_so_in_both_commands()
    {
        var db = Path.Combine(work, "plain.duckdb");
        string zip;
        await using (var storage = new DuckDbRunStorage(db))
        {
            storage.EnqueueRunStarted("plain", DateTimeOffset.UtcNow, new LoadPlan());
            storage.EnqueueBeeCompleted("plain", new ExecutionResult(0, 0, 0, 40, 200, 1, null, Calls: [new RecordedCall("GET /x", 0, 40, 200, TraceId: Trace(1), SpanId: CallSpan(1))], TraceId: Trace(1)), 0, 0);
            storage.EnqueueRunFinished("plain", DateTimeOffset.UtcNow, "Finished", 1, null);
            await storage.FlushAsync();
            zip = Path.Combine(work, "plain.zip");
            File.WriteAllBytes(zip, (await storage.ExportRunAsync("plain", "parquet"))!.Content);
        }

        var trace = await CliAsync("runs", "trace", zip, "1");
        Assert.Equal(3, trace.Exit);
        Assert.Contains("no trace store was configured for this run", trace.Out);
        var hotspots = await CliAsync("runs", "hotspots", zip);
        Assert.Equal(0, hotspots.Exit);
        Assert.Contains("No traces are stored with this run", hotspots.Out);
    }

    [Fact]
    public async Task Trace_ids_are_printed_by_runs_calls_slowest_and_runs_bee_in_text_and_json_on_every_bundle_format()
    {
        var (zip, duck, json, _) = await BundlesAsync();
        foreach (var bundle in new[] { zip, duck, json })
        {
            var slowest = await CliAsync("runs", "calls", bundle, "--slowest", "3");
            Assert.Contains($"trace: {Trace(7)}", slowest.Out);
            using (var doc = JsonDocument.Parse((await CliAsync("runs", "calls", bundle, "--slowest", "3", "--json")).Out))
            {
                var first = doc.RootElement.GetProperty("calls")[0];
                Assert.Equal(Trace(7), first.GetProperty("traceId").GetString());
                Assert.Equal(CallSpan(7), first.GetProperty("spanId").GetString());
            }

            var bee = await CliAsync("runs", "bee", bundle, "B8");
            Assert.Contains($"Trace       {Trace(7)}", bee.Out);
            Assert.Contains($"span {CallSpan(7)}", bee.Out);
            using (var doc = JsonDocument.Parse((await CliAsync("runs", "bee", bundle, "8", "--json")).Out))
            {
                Assert.Equal(Trace(7), doc.RootElement.GetProperty("traceId").GetString());
                Assert.Equal(CallSpan(7), doc.RootElement.GetProperty("calls")[0].GetProperty("spanId").GetString());
            }
        }
    }

    // ---- live, with --url: a stub server backed by the same storage and the same engine ----

    private sealed class ServerStub(IRunStorage storage) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (uri.AbsolutePath == "/api/runs") return Json(await storage.ListRunsAsync(5, ct));
            if (parts is ["api", "runs", var id, "traces", var key])
            {
                try
                {
                    var view = await storage.GetTraceViewAsync(Uri.UnescapeDataString(id), new TraceViewQuery(Uri.UnescapeDataString(key), int.TryParse(query["call"], out var c) ? c : null, int.Parse(query["maxSpans"] ?? "50")), ct);
                    return view is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(view);
                }
                catch (ArgumentException ex)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(new { error = ex.Message }), Encoding.UTF8, "application/json") };
                }
            }
            if (parts is ["api", "runs", var runId, "hotspots"])
            {
                var result = await storage.GetHotspotsAsync(runId, new HotspotsQuery(query["endpoint"], query["slowOnly"] == "true", int.Parse(query["limit"] ?? "10")), ct);
                return result is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(result);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, WebJson), Encoding.UTF8, "application/json") };
    }

    private static async Task<(int Exit, string Out, string Err)> LiveAsync(HttpMessageHandler handler, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, output, error, CancellationToken.None, configureServices: null, httpHandler: handler, getEnvironmentVariable: _ => null);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Both_commands_work_against_a_server_with_latest_run_aliases_exit_codes_and_server_side_errors()
    {
        await using var storage = new InMemoryRunStorage();
        await SeedAsync(storage);
        var server = new ServerStub(storage);

        var hotspots = await LiveAsync(server, "runs", "hotspots", "latest", "--url", "http://beesharp.test", "--slow");
        Assert.Equal(0, hotspots.Exit);
        Assert.Contains("shop-api  SELECT product", hotspots.Out);
        Assert.Contains(server.Requests, u => u.AbsolutePath == "/api/runs/run-traces/hotspots" && u.Query.Contains("slowOnly=true"));

        var trace = await LiveAsync(server, "runs", "trace", "latest", "B8", "--url", "http://beesharp.test", "--call", "1", "--max-spans", "5");
        Assert.Equal(0, trace.Exit);
        Assert.Contains("call #1 GET /products/{id}", trace.Out);
        Assert.Contains(server.Requests, u => u.AbsolutePath == "/api/runs/run-traces/traces/B8" && u.Query.Contains("call=1") && u.Query.Contains("maxSpans=5"));

        var notStored = await LiveAsync(server, "runs", "trace", "latest", "4", "--url", "http://beesharp.test");
        Assert.Equal(3, notStored.Exit);
        Assert.Contains("not arrived yet", notStored.Out);

        var badCall = await LiveAsync(server, "runs", "trace", "latest", "1", "--call", "9", "--url", "http://beesharp.test");
        Assert.Equal(2, badCall.Exit);
        Assert.Contains("there is no call 9", badCall.Err);

        Assert.Equal(2, (await LiveAsync(server, "runs", "trace", "latest", "99", "--url", "http://beesharp.test")).Exit);
    }

    // ---- beesharp run fetches and reports ----

    private sealed class FoundStore : ITraceStore
    {
        public string Kind => "fake";
        public string Description => "Fake store at http://trace.test";
        public Task<TraceLookup> GetTraceAsync(string traceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            Task.FromResult(new TraceLookup(TraceLookupOutcome.Found, [new TraceSpanDto(traceId, "00000000000000a1", null, "beesharp", "bee", "internal", 1, 5)]));
    }

    private sealed class DownStore : ITraceStore
    {
        public string Kind => "fake";
        public string Description => "Fake store at http://trace.test";
        public Task<TraceLookup> GetTraceAsync(string traceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            Task.FromResult(TraceLookup.Unreachable("Fake store at http://trace.test could not be reached: Connection refused"));
    }

    private sealed class StoreFactory(ITraceStore store) : ITraceStoreFactory
    {
        public ITraceStore Create(TraceStoreSettings settings) => store;
    }

    private string PrepareRoot(string environmentJson)
    {
        var root = Path.Combine(work, "root");
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        Directory.CreateDirectory(Path.Combine(root, "presets"));
        Directory.CreateDirectory(Path.Combine(root, "environments"));
        File.WriteAllText(Path.Combine(root, "scripts", "traced.cs"), """
            public record TracedParams(int DelayMs = 10);
            Scenario.Define<TracedParams>("Traced", async (p, ctx) =>
            {
                await ctx.MeasureCallAsync("GET /items/1", () => Task.Delay(p.DelayMs, ctx.CancellationToken));
            }, id: "traced");
            """);
        File.WriteAllText(Path.Combine(root, "presets", "traced.json"),
            """{ "name": "Traced", "savedAt": "2026-11-26T10:00:00Z", "plan": { "durationSeconds": 1, "maxConcurrency": 20, "timeoutSeconds": 20, "scenarios": [ { "scenarioId": "traced", "label": "Traced", "ratePerMinute": 300, "parameters": { "DelayMs": 10 } } ] } }""");
        File.WriteAllText(Path.Combine(root, "environments", "dev.json"), environmentJson);
        return root;
    }

    private static Action<IServiceCollection> Services(ITraceStore store) => services =>
    {
        services.AddSingleton<ITraceStoreFactory>(new StoreFactory(store));
        services.AddSingleton(new TraceFetchOptions { SettleDelay = TimeSpan.Zero, PollInterval = TimeSpan.FromMilliseconds(20), RetryDelay = TimeSpan.FromMilliseconds(30), RequestInterval = TimeSpan.FromMilliseconds(1), TypicalMinSample = 2 });
    };

    private static async Task<(int Exit, string Out, string Err)> RunAsync(string root, ITraceStore store, params string[] more)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = new List<string> { "run", "--preset", "Traced", "--env", "Dev", "--root", root, "--progress", "0" };
        args.AddRange(more);
        var exit = await CliApp.RunAsync([.. args], output, error, CancellationToken.None, Services(store));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Beesharp_run_fetches_traces_prints_the_summary_line_streams_the_fetchers_logs_and_exports_them()
    {
        var root = PrepareRoot("""{ "name": "Dev", "variables": [ { "key": "TraceStore.Url", "value": "http://trace.test" } ] }""");
        var export = Path.Combine(work, "run.zip");

        var result = await RunAsync(root, new FoundStore(), "--logs", "info", "--export", export);

        Assert.Equal(0, result.Exit);
        Assert.Matches(@"Traces\s+\d+ of \d+ found", result.Out);
        Assert.Contains("[host]", result.Out);                                    // --logs streams the fetcher's lines
        Assert.Contains("[Traces] Trace store: Fake store at http://trace.test", result.Out);
        Assert.Matches(@"\[Traces\] traces: (\d+) of \1 found", result.Out);

        var hostLogs = await CliAsync("runs", "logs", export, "--entry", "host");   // the run keeps them: runs logs --entry host
        Assert.Contains("[Traces] Trace store: Fake store at http://trace.test", hostLogs.Out);
        Assert.Contains("[Traces] fetched ", hostLogs.Out);
        Assert.Matches(@"\[Traces\] traces: (\d+) of \1 found", hostLogs.Out);

        var hotspots = await CliAsync("runs", "hotspots", export);   // the export carries the fetched traces
        Assert.Matches(@"traces: (\d+) of \1 found", hotspots.Out);
    }

    [Fact]
    public async Task Beesharp_run_json_has_the_trace_summary_and_a_store_that_is_down_changes_nothing_but_the_traces()
    {
        var root = PrepareRoot("""{ "name": "Dev", "variables": [ { "key": "TraceStore.Url", "value": "http://trace.test" } ] }""");

        var result = await RunAsync(root, new DownStore(), "--json");

        Assert.Equal(0, result.Exit);   // all bees ok: the store is none of the run's business
        using var doc = JsonDocument.Parse(result.Out);
        var traces = doc.RootElement.GetProperty("traces");
        Assert.True(traces.GetProperty("selected").GetInt32() > 0);
        Assert.Equal(0, traces.GetProperty("found").GetInt32());
        Assert.Contains("store unreachable", traces.GetProperty("line").GetString());
        Assert.Equal(traces.GetProperty("selected").GetInt32(), traces.GetProperty("reasons").GetProperty("store-unreachable").GetInt32());
    }

    [Fact]
    public async Task Beesharp_run_without_a_trace_store_prints_no_trace_line_and_json_has_no_traces()
    {
        var root = PrepareRoot("""{ "name": "Dev", "variables": [ { "key": "BaseUrl", "value": "http://shop" } ] }""");

        var text = await RunAsync(root, new FoundStore());
        Assert.Equal(0, text.Exit);
        Assert.DoesNotContain("Traces ", text.Out);

        var json = await RunAsync(root, new FoundStore(), "--json");
        using var doc = JsonDocument.Parse(json.Out);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("traces").ValueKind);
    }
}
