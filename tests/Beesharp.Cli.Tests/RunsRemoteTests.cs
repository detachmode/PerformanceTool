using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;

namespace Beesharp.Cli.Tests;

/// <summary>
/// Remote HTTP inspection tests for <c>beesharp runs ... --url &lt;url&gt;</c> and <c>BEESHARP_URL</c>:
/// verifies <c>runs list</c>, <c>latest</c>/<c>prev</c> run resolution, Parquet export download + inspection,
/// direct endpoint queries (<c>calls</c>, <c>logs</c>, <c>bee</c>), local-vs-env fallback, and clean exit-code-2 errors.
/// </summary>
[Collection("RunsTempIsolation")]
public sealed class RunsRemoteTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly string workDir = Path.Combine(Path.GetTempPath(), $"beesharp-remote-test-{Guid.NewGuid():N}");

    public RunsRemoteTests() => Directory.CreateDirectory(workDir);

    public void Dispose()
    {
        if (Directory.Exists(workDir))
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage JsonResponse<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, WebJson), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage BytesResponse(byte[] bytes, string contentType = "application/zip")
    {
        var msg = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return msg;
    }

    private static async Task<(int Exit, string Out, string Err)> CliAsync(
        HttpMessageHandler? handler,
        Func<string, string?>? env,
        params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, output, error, CancellationToken.None, configureServices: null, httpHandler: handler, getEnvironmentVariable: env);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<(byte[] BaselineZip, byte[] CandidateZip)> CreateTwoParquetExportsAsync()
    {
        var dbPath = Path.Combine(workDir, "server-store.duckdb");
        await using var storage = new DuckDbRunStorage(dbPath);
        var steps = new IReadOnlyList<StepInfo>[] { [new StepInfo("Auth"), new StepInfo("Checkout")] };
        var plan = new LoadPlan
        {
            DurationSeconds = 10,
            Scenarios = [new ScenarioRunConfig { ScenarioId = "shop", Label = "Shop", RatePerMinute = 60 }],
        };

        var t0 = new DateTimeOffset(2026, 11, 25, 11, 0, 0, TimeSpan.Zero);
        storage.EnqueueRunStarted("run-old", t0, plan, steps);
        storage.EnqueueBeeCompleted("run-old", new ExecutionResult(0, 0, 0.1, 50, 200, 100, null), beeOutcome: 0, slowCalls: 0);
        storage.EnqueueBeeCompleted("run-old", new ExecutionResult(1, 0, 0.2, 60, 200, 100, null), beeOutcome: 0, slowCalls: 0);
        storage.EnqueueRunFinished("run-old", t0.AddSeconds(2), "Finished", 2.0, null);

        var t1 = t0.AddMinutes(5);
        storage.EnqueueRunStarted("run-new", t1, plan, steps);
        storage.EnqueueBeeCompleted("run-new", new ExecutionResult(0, 0, 0.1, 140, 200, 100, null), beeOutcome: 0, slowCalls: 0);
        storage.EnqueueBeeCompleted("run-new", new ExecutionResult(1, 0, 0.3, 350, 502, 0, "Bad gateway on checkout", FailedStep: 1, FailedRep: 0), beeOutcome: 2, slowCalls: 1);
        storage.EnqueueRunFinished("run-new", t1.AddSeconds(3), "Finished", 3.0, null);

        await storage.FlushAsync();
        var oldZip = (await storage.ExportRunAsync("run-old", "parquet"))!.Content;
        var newZip = (await storage.ExportRunAsync("run-new", "parquet"))!.Content;
        return (oldZip, newZip);
    }

    [Fact]
    public async Task Runs_list_supports_url_and_env_var_in_text_and_json()
    {
        var summaries = new List<StoredRunSummary>
        {
            new("run-new", new DateTimeOffset(2026, 11, 25, 11, 5, 0, TimeSpan.Zero), null, "Finished", 3.0, 2, 1, 5, null),
            new("run-old", new DateTimeOffset(2026, 11, 25, 11, 0, 0, TimeSpan.Zero), null, "Finished", 2.0, 2, 0, 2, null),
        };
        var handler = new StubHttpHandler(req =>
        {
            Assert.Equal("/api/runs", req.RequestUri!.AbsolutePath);
            Assert.Equal("?limit=5", req.RequestUri.Query);
            return JsonResponse(summaries);
        });

        var text = await CliAsync(handler, _ => null, "runs", "list", "--url", "http://beesharp.test:5000/", "--limit", "5");
        Assert.Equal(ExitCodes.Ok, text.Exit);
        Assert.Equal("", text.Err);
        Assert.Contains("Runs on http://beesharp.test:5000 (2):", text.Out);
        Assert.Contains("run-new", text.Out);
        Assert.Contains("run-old", text.Out);

        var json = await CliAsync(handler, k => k == "BEESHARP_URL" ? "http://env-host.test" : null, "runs", "list", "--limit", "5", "--json");
        Assert.Equal(ExitCodes.Ok, json.Exit);
        using var doc = JsonDocument.Parse(json.Out);
        Assert.Equal("http://env-host.test", doc.RootElement.GetProperty("url").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("run-new", doc.RootElement.GetProperty("runs")[0].GetProperty("runId").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("runs")[0].GetProperty("okBees").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("runs")[0].GetProperty("failedBees").GetInt32());

        // Missing both --url and BEESHARP_URL -> exit 2
        var missingUrl = await CliAsync(handler, _ => null, "runs", "list");
        Assert.Equal(ExitCodes.CouldNotRun, missingUrl.Exit);
        Assert.Contains("--url", missingUrl.Err);
    }

    [Fact]
    public async Task Summary_errors_and_compare_download_parquet_exports_and_resolve_latest_prev()
    {
        var (oldZip, newZip) = await CreateTwoParquetExportsAsync();
        var recent = new List<StoredRunSummary>
        {
            new("run-new", new DateTimeOffset(2026, 11, 25, 11, 5, 0, TimeSpan.Zero), null, "Finished", 3.0, 2, 1, 2, null),
            new("run-old", new DateTimeOffset(2026, 11, 25, 11, 0, 0, TimeSpan.Zero), null, "Finished", 2.0, 2, 0, 2, null),
        };

        var handler = new StubHttpHandler(req => req.RequestUri!.PathAndQuery switch
        {
            "/api/runs?limit=2" => JsonResponse(recent),
            "/api/runs/run-new/export?format=parquet" => BytesResponse(newZip),
            "/api/runs/run-old/export?format=parquet" => BytesResponse(oldZip),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var dlBefore = Directory.GetFiles(Path.GetTempPath(), "beesharp-dl-*.zip").Length;
        var pqBefore = Directory.GetDirectories(Path.GetTempPath(), "beesharp-inspect-*").Length;

        var summaryRes = await CliAsync(handler, _ => null, "runs", "summary", "latest", "--url", "http://beesharp.test", "--json");
        Assert.Equal(ExitCodes.Ok, summaryRes.Exit);
        using (var sumDoc = JsonDocument.Parse(summaryRes.Out))
        {
            Assert.Equal("run-new", sumDoc.RootElement.GetProperty("runId").GetString());
            Assert.Equal(1, sumDoc.RootElement.GetProperty("failedBees").GetInt32());
            Assert.Equal("Checkout", sumDoc.RootElement.GetProperty("entries")[0].GetProperty("steps")[1].GetProperty("name").GetString());
        }

        var errorsRes = await CliAsync(handler, _ => null, "runs", "errors", "latest", "--url", "http://beesharp.test", "--json");
        Assert.Equal(ExitCodes.Ok, errorsRes.Exit);
        using (var errDoc = JsonDocument.Parse(errorsRes.Out))
        {
            Assert.Equal("run-new", errDoc.RootElement.GetProperty("runId").GetString());
            Assert.Equal("Bad gateway on checkout", errDoc.RootElement.GetProperty("errors")[0].GetProperty("error").GetString());
            Assert.Equal("Checkout", errDoc.RootElement.GetProperty("errors")[0].GetProperty("steps")[0].GetProperty("name").GetString());
        }

        handler.Requests.Clear();
        var cmpRes = await CliAsync(handler, _ => null, "runs", "compare", "prev", "latest", "--url", "http://beesharp.test", "--json");
        Assert.Equal(ExitCodes.Ok, cmpRes.Exit);
        // Should only call /api/runs?limit=2 once when resolving both prev and latest
        Assert.Equal(1, handler.Requests.Count(u => u.PathAndQuery == "/api/runs?limit=2"));
        using (var cmpDoc = JsonDocument.Parse(cmpRes.Out))
        {
            Assert.Equal("run-old", cmpDoc.RootElement.GetProperty("baseline").GetProperty("runId").GetString());
            Assert.Equal("run-new", cmpDoc.RootElement.GetProperty("candidate").GetProperty("runId").GetString());
            Assert.Equal(1, cmpDoc.RootElement.GetProperty("overallDiff").GetProperty("failedBeesDelta").GetInt32());
            Assert.True(cmpDoc.RootElement.GetProperty("overallDiff").GetProperty("p50MsDelta").GetDouble() > 0);
        }

        Assert.Equal(dlBefore, Directory.GetFiles(Path.GetTempPath(), "beesharp-dl-*.zip").Length);
        Assert.Equal(pqBefore, Directory.GetDirectories(Path.GetTempPath(), "beesharp-inspect-*").Length);
    }

    [Fact]
    public async Task Calls_logs_and_bee_query_direct_http_endpoints_and_convert_to_cli_reports()
    {
        var callsResult = new CallEndpointsResult(
            [
                new CallEndpointSummaryDto("POST /pay", "POST", "shop.test", 10, 3, 1, 80, 220, 410, 40, 95, 160, true, [0, 1]),
            ],
            10, 3, 1);

        var logPage = new LogPageResult(
            12, 1, 2,
            [
                new StoredLogRow(101, 1, "B2", 0, 0.55, "Error", "Card declined", "System.Exception: 402", 2, 2, true, true, Step: 1),
            ]);

        var beeDetail = new StoredBeeDetail(
            "run-abc",
            1,
            0,
            0.5,
            410,
            502,
            256,
            "Bad gateway",
            [
                new RecordedCall("POST /pay", 10, 400, 502, 256, "Bad gateway", Step: 1, Rep: 0, Endpoint: "POST /pay", Method: "POST", Host: "shop.test", SlowThresholdMs: 200, Url: "https://shop.test/pay"),
            ],
            [
                new RecordedLog(0.9, "Error", "Checkout error", "Bad gateway", 0, Step: 1, Rep: 0),
            ],
            FailedStep: 1,
            FailedRep: 0,
            StepVisits: [new RecordedStepVisit(0, 0, null), new RecordedStepVisit(1, 0, """{"amount":99}""")]);

        var handler = new StubHttpHandler(req =>
        {
            var pq = req.RequestUri!.PathAndQuery;
            if (pq == "/api/runs?limit=2")
                return JsonResponse(new List<StoredRunSummary> { new("run-abc", DateTimeOffset.UtcNow, null, "Finished", 5, 10, 1, 12, null) });
            if (pq.StartsWith("/api/runs/run-abc/calls/endpoints", StringComparison.Ordinal))
                return JsonResponse(callsResult);
            if (pq.StartsWith("/api/runs/run-abc/logs", StringComparison.Ordinal))
                return JsonResponse(logPage);
            if (pq == "/api/runs/run-abc/bees/1")
                return JsonResponse(beeDetail);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var callsRes = await CliAsync(handler, _ => null, "runs", "calls", "latest", "--url", "http://beesharp.test", "--entry", "1", "--slow", "--failed", "--min-ms", "100", "--search", "pay", "--json");
        Assert.Equal(ExitCodes.Ok, callsRes.Exit);
        Assert.Contains(handler.Requests, u => u.PathAndQuery == "/api/runs/run-abc/calls/endpoints?entry=0&slowOnly=true&failedOnly=true&minMs=100&search=pay");
        using (var cDoc = JsonDocument.Parse(callsRes.Out))
        {
            var ep = Assert.Single(cDoc.RootElement.GetProperty("endpoints").EnumerateArray());
            Assert.Equal("POST /pay", ep.GetProperty("endpoint").GetString());
            // Converted from 0-based [0, 1] -> 1-based [1, 2]
            Assert.Equal(1, ep.GetProperty("entries")[0].GetInt32());
            Assert.Equal(2, ep.GetProperty("entries")[1].GetInt32());
        }

        var logsRes = await CliAsync(handler, _ => null, "runs", "logs", "run-abc", "--url", "http://beesharp.test", "--bee", "B2", "--entry", "1", "--step", "2", "--level", "warn,error", "--search", "declined", "--offset", "1", "--limit", "2", "--json");
        Assert.Equal(ExitCodes.Ok, logsRes.Exit);
        Assert.Contains(handler.Requests, u => u.PathAndQuery == "/api/runs/run-abc/logs?entry=0&step=1&bee=1&levels=Warning%2CError&search=declined&offset=1&limit=2");
        using (var lDoc = JsonDocument.Parse(logsRes.Out))
        {
            Assert.Equal(12, lDoc.RootElement.GetProperty("totalCount").GetInt64());
            Assert.True(lDoc.RootElement.GetProperty("hasMore").GetBoolean());
            var line = Assert.Single(lDoc.RootElement.GetProperty("logs").EnumerateArray());
            Assert.Equal(2, line.GetProperty("bee").GetInt32());
            Assert.Equal(1, line.GetProperty("entry").GetInt32());
            Assert.Equal(2, line.GetProperty("step").GetInt32());
            Assert.Equal("Card declined", line.GetProperty("message").GetString());
        }

        var beeRes = await CliAsync(handler, _ => null, "runs", "bee", "run-abc", "B2", "--url", "http://beesharp.test", "--json");
        Assert.Equal(ExitCodes.Ok, beeRes.Exit);
        using (var bDoc = JsonDocument.Parse(beeRes.Out))
        {
            Assert.Equal(2, bDoc.RootElement.GetProperty("bee").GetInt32());
            Assert.Equal("B2", bDoc.RootElement.GetProperty("beeName").GetString());
            Assert.Equal(2, bDoc.RootElement.GetProperty("failedStep").GetInt32());
            Assert.Equal(1, bDoc.RootElement.GetProperty("failedRep").GetInt32());
            Assert.Equal("https://shop.test/pay", bDoc.RootElement.GetProperty("calls")[0].GetProperty("url").GetString());
            Assert.Equal(2, bDoc.RootElement.GetProperty("stepVisits").GetArrayLength());
        }
    }

    [Fact]
    public async Task Beeswarp_url_env_var_fallback_respects_local_bundle_files()
    {
        var (zipBytes, _) = await CreateTwoParquetExportsAsync();
        var localZip = Path.Combine(workDir, "local.zip");
        await File.WriteAllBytesAsync(localZip, zipBytes);

        var remoteCalls = 0;
        var handler = new StubHttpHandler(req =>
        {
            remoteCalls++;
            if (req.RequestUri!.PathAndQuery == "/api/runs?limit=2")
                return JsonResponse(new List<StoredRunSummary> { new("run-remote", DateTimeOffset.UtcNow, null, "Finished", 1, 1, 0, 0, null) });
            if (req.RequestUri.PathAndQuery == "/api/runs/run-remote/export?format=parquet")
                return BytesResponse(zipBytes);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        Func<string, string?> env = key => key == "BEESHARP_URL" ? "http://beesharp.test" : null;

        // 1. Existing local .zip uses local bundle even when BEESHARP_URL is set.
        var localRes = await CliAsync(handler, env, "runs", "summary", localZip, "--json");
        Assert.Equal(ExitCodes.Ok, localRes.Exit);
        Assert.Equal(0, remoteCalls);

        // 2. Missing .zip path reports missing local file instead of calling BEESHARP_URL.
        var missingZip = Path.Combine(workDir, "does-not-exist.zip");
        var missingRes = await CliAsync(handler, env, "runs", "summary", missingZip);
        Assert.Equal(ExitCodes.CouldNotRun, missingRes.Exit);
        Assert.Contains("was not found", missingRes.Err);
        Assert.Equal(0, remoteCalls);

        // 3. Non-file run alias/id falls back to BEESHARP_URL.
        var remoteRes = await CliAsync(handler, env, "runs", "summary", "latest", "--json");
        Assert.Equal(ExitCodes.Ok, remoteRes.Exit);
        Assert.Equal(2, remoteCalls);
    }

    [Fact]
    public async Task Remote_error_cases_return_exit_code_2_with_clean_messages()
    {
        // 1. Invalid URL
        var badUrl = await CliAsync(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), _ => null,
            "runs", "list", "--url", "ftp://not-http");
        Assert.Equal(ExitCodes.CouldNotRun, badUrl.Exit);
        Assert.Contains("Invalid server URL", badUrl.Err);

        // 2. Connection refused / HttpRequestException
        var connRefused = await CliAsync(
            new StubHttpHandler(_ => throw new HttpRequestException("Connection refused")),
            _ => null,
            "runs", "list", "--url", "http://localhost:59999");
        Assert.Equal(ExitCodes.CouldNotRun, connRefused.Exit);
        Assert.Contains("Could not reach Beesharp server", connRefused.Err);

        // 3. Timeout
        var timeout = await CliAsync(
            new StubHttpHandler(_ => throw new TaskCanceledException("Timed out")),
            _ => null,
            "runs", "list", "--url", "http://localhost:59999");
        Assert.Equal(ExitCodes.CouldNotRun, timeout.Exit);
        Assert.Contains("timed out", timeout.Err);

        // 4. Empty run list for latest / < 2 for prev
        var emptyHandler = new StubHttpHandler(_ => JsonResponse(new List<StoredRunSummary>()));
        Assert.Equal(ExitCodes.CouldNotRun, (await CliAsync(emptyHandler, _ => null, "runs", "summary", "latest", "--url", "http://beesharp.test")).Exit);

        var singleHandler = new StubHttpHandler(_ => JsonResponse(new List<StoredRunSummary>
        {
            new("only-1", DateTimeOffset.UtcNow, null, "Finished", 1, 1, 0, 0, null),
        }));
        var prevErr = await CliAsync(singleHandler, _ => null, "runs", "summary", "prev", "--url", "http://beesharp.test");
        Assert.Equal(ExitCodes.CouldNotRun, prevErr.Exit);
        Assert.Contains("Fewer than 2 runs", prevErr.Err);

        // 5. 404 run or bee not found
        var notFoundHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var bee404 = await CliAsync(notFoundHandler, _ => null, "runs", "bee", "missing-run", "1", "--url", "http://beesharp.test");
        Assert.Equal(ExitCodes.CouldNotRun, bee404.Exit);
        Assert.Contains("was not found", bee404.Err);

        // 6. Help & invalid args for runs list
        var listHelp = await CliAsync(null, null, "runs", "list", "--help");
        Assert.Equal(ExitCodes.Ok, listHelp.Exit);
        Assert.Contains("--url", listHelp.Out);

        var badLimit = await CliAsync(null, null, "runs", "list", "--url", "http://beesharp.test", "--limit", "9999");
        Assert.Equal(ExitCodes.CouldNotRun, badLimit.Exit);

        var emptyUrlFlag = await CliAsync(null, null, "runs", "list", "--url=");
        Assert.Equal(ExitCodes.CouldNotRun, emptyUrlFlag.Exit);
    }
}
