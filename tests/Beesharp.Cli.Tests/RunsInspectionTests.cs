using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;
using DuckDB.NET.Data;

namespace Beesharp.Cli.Tests;

/// <summary>
/// Phase 1 of brain 0061: offline inspection of exported <c>.zip</c> (Parquet), <c>.duckdb</c>, and <c>.json</c>
/// run bundles via <c>beesharp runs summary|errors|calls|logs|bee|compare</c>.
/// </summary>
[Collection("RunsTempIsolation")]
public sealed class RunsInspectionTests : IDisposable
{
    private readonly string workDir = Path.Combine(Path.GetTempPath(), $"beesharp-runs-test-{Guid.NewGuid():N}");

    public RunsInspectionTests() => Directory.CreateDirectory(workDir);

    public void Dispose()
    {
        if (!Directory.Exists(workDir)) return;
        foreach (var file in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attr = File.GetAttributes(file);
                if ((attr & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
            }
            catch (IOException) { }
        }
        try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
    }

    private static async Task<(int Exit, string Out, string Err)> CliAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private static string LongUntruncatedError =>
        "Payment gateway returned 503 Service Unavailable during checkout step with correlation-id="
        + new string('X', 160)
        + " [end-of-full-error]";

    private sealed record SeededBundles(string RunId, string ZipPath, string DuckDbPath, string JsonPath);

    private async Task<SeededBundles> SeedRichRunAsync(string suffix, bool slowAndDegraded = false)
    {
        var dbPath = Path.Combine(workDir, $"src-{suffix}.duckdb");
        var runId = $"run-{suffix}";
        await using var storage = new DuckDbRunStorage(dbPath);

        var plan = new LoadPlan
        {
            DurationSeconds = 10,
            MaxConcurrency = 20,
            TimeoutSeconds = 30,
            PreflightScriptId = "warmup.cs",
            ShutdownScriptId = "cleanup.cs",
            Scenarios =
            [
                new ScenarioRunConfig { ScenarioId = "checkout-steps", Label = "Checkout", RatePerMinute = 60 },
                new ScenarioRunConfig { ScenarioId = "http-get", Label = "Health", RatePerMinute = 60 },
            ],
        };

        var steps = new IReadOnlyList<StepInfo>[]
        {
            [new StepInfo("Log in"), new StepInfo("Search"), new StepInfo("Pay")],
            [],
        };

        var started = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        storage.EnqueueRunStarted(runId, started, plan, steps);

        await storage.SaveProgramRunAsync(runId, "preflight", new StoredProgramRunDto(
            "Succeeded", "warmup.cs", null, ["[10:00:00.000] [INF] Preflight warmed cache"]));

        // Bee 1 (seq 0): Entry 0 (Checkout), OK
        var mult = slowAndDegraded ? 3.0 : 1.0;
        storage.EnqueueBeeCompleted(runId, new ExecutionResult(
            Sequence: 0, Entry: 0, StartedAtSeconds: 0.1, ElapsedMs: 100 * mult, StatusCode: 200, Bytes: 512, Error: null,
            Calls:
            [
                new RecordedCall("POST /login", 0, 20 * mult, 200, 64, null, Step: 0, Rep: 0,
                    Endpoint: "POST /login", Method: "POST", Host: "shop.test", Url: "https://shop.test/login"),
                new RecordedCall("GET /products/101?q=shoe", 25 * mult, 30 * mult, 200, 256, null, Step: 1, Rep: 0,
                    Endpoint: "GET /products/{id}", Method: "GET", Host: "shop.test", Url: "https://shop.test/products/101?q=shoe"),
                new RecordedCall("POST /pay", 60 * mult, 40 * mult, 200, 192, null, Step: 2, Rep: 0,
                    Endpoint: "POST /pay", Method: "POST", Host: "shop.test", SlowThresholdMs: 150, Url: "https://shop.test/pay"),
            ],
            Logs:
            [
                new RecordedLog(0.11, "Information", "Logged in user alice", null, 0, Step: 0, Rep: 0),
                new RecordedLog(0.14, "Debug", "Searched shoe catalog", null, 1, Step: 1, Rep: 0),
                new RecordedLog(0.18, "Information", "Order paid cleanly", null, 2, Step: 2, Rep: 0),
            ],
            StepVisits:
            [
                new RecordedStepVisit(0, 0, """{"user":"alice"}"""),
                new RecordedStepVisit(1, 0, """{"query":"shoe"}"""),
                new RecordedStepVisit(2, 0, """{"amount":42}"""),
            ]),
            beeOutcome: 0, slowCalls: 0);

        // Bee 2 (seq 1): Entry 0 (Checkout), Slow + Failed at Pay (step index 2, rep 1) with long untruncated error
        storage.EnqueueBeeCompleted(runId, new ExecutionResult(
            Sequence: 1, Entry: 0, StartedAtSeconds: 0.5, ElapsedMs: 420 * mult, StatusCode: 503, Bytes: 128, Error: LongUntruncatedError,
            Calls:
            [
                new RecordedCall("POST /login", 0, 25 * mult, 200, 64, null, Step: 0, Rep: 0,
                    Endpoint: "POST /login", Method: "POST", Host: "shop.test", Url: "https://shop.test/login"),
                new RecordedCall("GET /products/202?q=hat", 30 * mult, 35 * mult, 200, 128, null, Step: 1, Rep: 0,
                    Endpoint: "GET /products/{id}", Method: "GET", Host: "shop.test", Url: "https://shop.test/products/202?q=hat"),
                new RecordedCall("POST /pay", 70 * mult, 350 * mult, 503, 0, LongUntruncatedError, Step: 2, Rep: 1,
                    Endpoint: "POST /pay", Method: "POST", Host: "shop.test", SlowThresholdMs: 150, Url: "https://shop.test/pay"),
            ],
            Logs:
            [
                new RecordedLog(0.55, "Warning", "Payment gateway latency high", null, 2, Step: 2, Rep: 0),
                new RecordedLog(0.92, "Error", "Checkout payment failed", LongUntruncatedError, 2, Step: 2, Rep: 1),
            ],
            FailedStep: 2, FailedRep: 1,
            StepVisits:
            [
                new RecordedStepVisit(0, 0, """{"user":"bob"}"""),
                new RecordedStepVisit(1, 0, null),
                new RecordedStepVisit(2, 0, """{"attempt":1}"""),
                new RecordedStepVisit(2, 1, """{"attempt":2}"""),
            ]),
            beeOutcome: 2, slowCalls: 1);

        // Bee 3 (seq 2): Entry 0 (Checkout), Slow but OK
        storage.EnqueueBeeCompleted(runId, new ExecutionResult(
            Sequence: 2, Entry: 0, StartedAtSeconds: 1.0, ElapsedMs: 260 * mult, StatusCode: 200, Bytes: 320, Error: null,
            Calls:
            [
                new RecordedCall("POST /pay", 50 * mult, 210 * mult, 200, 128, null, Step: 2, Rep: 0,
                    Endpoint: "POST /pay", Method: "POST", Host: "shop.test", SlowThresholdMs: 150, Url: "https://shop.test/pay"),
            ],
            Logs: [new RecordedLog(1.25, "Warning", "Pay took longer than threshold", null, 0, Step: 2, Rep: 0)]),
            beeOutcome: 1, slowCalls: 1);

        // Bee 4 (seq 3): Entry 0 (Checkout), Canceled at Search (step index 1)
        storage.EnqueueBeeCompleted(runId, new ExecutionResult(
            Sequence: 3, Entry: 0, StartedAtSeconds: 1.4, ElapsedMs: 150 * mult, StatusCode: null, Bytes: 0, Error: ExecutionResult.CanceledError,
            Calls:
            [
                new RecordedCall("GET /products/303", 10, 140, null, 0, ExecutionResult.CanceledError, Step: 1, Rep: 0,
                    Endpoint: "GET /products/{id}", Method: "GET", Host: "shop.test", Url: "https://shop.test/products/303"),
            ],
            Logs: [new RecordedLog(1.55, "Error", "Bee canceled on stop", ExecutionResult.CanceledError, 0, Step: 1, Rep: 0)],
            Canceled: true, FailedStep: 1, FailedRep: 0),
            beeOutcome: 2, slowCalls: 0);

        // Extra failures in degraded run for compare tests
        if (slowAndDegraded)
        {
            storage.EnqueueBeeCompleted(runId, new ExecutionResult(
                Sequence: 4, Entry: 0, StartedAtSeconds: 1.6, ElapsedMs: 500, StatusCode: 503, Bytes: 0, Error: LongUntruncatedError,
                FailedStep: 2, FailedRep: 0),
                beeOutcome: 2, slowCalls: 0);
        }

        // Entry 1 (Health), OK bees
        var healthSeq = slowAndDegraded ? 5 : 4;
        for (var i = 0; i < 3; i++)
        {
            var dur = (20 + i * 10) * mult;
            storage.EnqueueBeeCompleted(runId, new ExecutionResult(
                Sequence: healthSeq + i, Entry: 1, StartedAtSeconds: 2.0 + i * 0.2, ElapsedMs: dur, StatusCode: 200, Bytes: 64, Error: null,
                Calls:
                [
                    new RecordedCall("GET /health", 0, dur, 200, 64, null, Step: 0, Rep: 0,
                        Endpoint: "GET /health", Method: "GET", Host: "shop.test", Url: "https://shop.test/health"),
                ],
                Logs: [new RecordedLog(2.0 + i * 0.2, "Information", $"Health check {i + 1} ok", null, 0)]),
                beeOutcome: 0, slowCalls: 0);
        }

        storage.EnqueueHostLog(runId, 2.8, "Information", "Host tick completed");
        await storage.SaveProgramRunAsync(runId, "shutdown", new StoredProgramRunDto(
            "Succeeded", "cleanup.cs", null, ["[10:00:03.000] [INF] Shutdown cleaned up"]));
        storage.EnqueueRunFinished(runId, started.AddSeconds(3.5), "Finished", 3.5, null);
        await storage.FlushAsync();

        var pq = (await storage.ExportRunAsync(runId, "parquet"))!;
        var dk = (await storage.ExportRunAsync(runId, "duckdb"))!;
        var js = (await storage.ExportRunAsync(runId, "json"))!;

        var zipPath = Path.Combine(workDir, $"{runId}.zip");
        var duckExpPath = Path.Combine(workDir, $"{runId}.duckdb");
        var jsonPath = Path.Combine(workDir, $"{runId}.json");

        await File.WriteAllBytesAsync(zipPath, pq.Content);
        await File.WriteAllBytesAsync(duckExpPath, dk.Content);
        await File.WriteAllBytesAsync(jsonPath, js.Content);

        return new SeededBundles(runId, zipPath, duckExpPath, jsonPath);
    }

    public static TheoryData<string> BundleFormats => new() { "zip", "duckdb", "json" };

    private static string PickPath(SeededBundles b, string format) => format switch
    {
        "zip" => b.ZipPath,
        "duckdb" => b.DuckDbPath,
        "json" => b.JsonPath,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    [Theory, MemberData(nameof(BundleFormats))]
    public async Task Runs_summary_works_across_zip_duckdb_and_json_in_text_and_json(string format)
    {
        var bundles = await SeedRichRunAsync($"sum-{format}");
        var path = PickPath(bundles, format);

        var textRes = await CliAsync("runs", "summary", path);
        Assert.Equal(ExitCodes.Ok, textRes.Exit);
        Assert.Equal("", textRes.Err);
        Assert.Contains($"Run summary: {bundles.RunId}", textRes.Out);
        Assert.Contains("total 7, ok 5, failed 2 (canceled 1), slow 2", textRes.Out);
        Assert.Contains("#1 Checkout", textRes.Out);
        Assert.Contains("#2 Health", textRes.Out);

        var jsonRes = await CliAsync("runs", "summary", "--json", path);
        Assert.Equal(ExitCodes.Ok, jsonRes.Exit);
        Assert.Equal("", jsonRes.Err);
        using var doc = JsonDocument.Parse(jsonRes.Out);
        var root = doc.RootElement;
        Assert.Equal(bundles.RunId, root.GetProperty("runId").GetString());
        Assert.Equal("Finished", root.GetProperty("state").GetString());
        Assert.Equal(3.5, root.GetProperty("elapsedSeconds").GetDouble());
        Assert.Equal(7, root.GetProperty("totalBees").GetInt32());
        Assert.Equal(5, root.GetProperty("okBees").GetInt32());
        Assert.Equal(2, root.GetProperty("failedBees").GetInt32());
        Assert.Equal(1, root.GetProperty("canceledBees").GetInt32());
        Assert.Equal(2, root.GetProperty("slowBees").GetInt32());
        Assert.True(root.GetProperty("minMs").GetDouble() > 0);
        Assert.True(root.GetProperty("p50Ms").GetDouble() >= root.GetProperty("minMs").GetDouble());
        Assert.True(root.GetProperty("p95Ms").GetDouble() >= root.GetProperty("p50Ms").GetDouble());
        Assert.True(root.GetProperty("maxMs").GetDouble() >= root.GetProperty("p95Ms").GetDouble());

        var entries = root.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        var checkout = entries[0];
        Assert.Equal(1, checkout.GetProperty("entry").GetInt32());
        Assert.Equal("Checkout", checkout.GetProperty("label").GetString());
        Assert.Equal(4, checkout.GetProperty("totalBees").GetInt32());
        Assert.Equal(2, checkout.GetProperty("okBees").GetInt32());
        Assert.Equal(2, checkout.GetProperty("failedBees").GetInt32());
        Assert.Equal(1, checkout.GetProperty("canceledBees").GetInt32());
        Assert.Equal(2, checkout.GetProperty("slowBees").GetInt32());

        // Zip and DuckDB include run_steps.parquet / run_steps table with declared step names
        if (format is "zip" or "duckdb")
        {
            var stepArr = checkout.GetProperty("steps");
            Assert.Equal(3, stepArr.GetArrayLength());
            Assert.Equal("Log in", stepArr[0].GetProperty("name").GetString());
            Assert.Equal(0, stepArr[0].GetProperty("failed").GetInt32());
            Assert.Equal("Search", stepArr[1].GetProperty("name").GetString());
            Assert.Equal(1, stepArr[1].GetProperty("failed").GetInt32());
            Assert.Equal("Pay", stepArr[2].GetProperty("name").GetString());
            Assert.Equal(1, stepArr[2].GetProperty("failed").GetInt32());
            Assert.Equal(2, checkout.GetProperty("finishedAllSteps").GetInt32());
        }
    }

    [Theory, MemberData(nameof(BundleFormats))]
    public async Task Runs_errors_returns_grouped_untruncated_errors_and_sample_bees(string format)
    {
        var bundles = await SeedRichRunAsync($"err-{format}");
        var path = PickPath(bundles, format);

        var textRes = await CliAsync("runs", "errors", path);
        Assert.Equal(ExitCodes.Ok, textRes.Exit);
        Assert.Contains(LongUntruncatedError, textRes.Out);
        Assert.Contains(ExecutionResult.CanceledError, textRes.Out);
        Assert.Contains("B2", textRes.Out);
        Assert.Contains("B4", textRes.Out);

        var jsonRes = await CliAsync("runs", "errors", path, "--json");
        Assert.Equal(ExitCodes.Ok, jsonRes.Exit);
        using var doc = JsonDocument.Parse(jsonRes.Out);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("totalFailedBees").GetInt32());
        Assert.Equal(2, root.GetProperty("distinctErrors").GetInt32());

        var errors = root.GetProperty("errors");
        var paymentErr = errors.EnumerateArray().First(e => e.GetProperty("error").GetString() == LongUntruncatedError);
        Assert.Equal(1, paymentErr.GetProperty("count").GetInt32());
        Assert.False(paymentErr.GetProperty("isCanceled").GetBoolean());
        Assert.Equal(2, paymentErr.GetProperty("sampleBees")[0].GetInt32());
        Assert.Equal("B2", paymentErr.GetProperty("sampleBeeNames")[0].GetString());
        Assert.Equal(1, paymentErr.GetProperty("entries")[0].GetProperty("entry").GetInt32());
        Assert.Equal(3, paymentErr.GetProperty("steps")[0].GetProperty("step").GetInt32());
        if (format is "zip" or "duckdb")
            Assert.Equal("Pay", paymentErr.GetProperty("steps")[0].GetProperty("name").GetString());

        var canceledErr = errors.EnumerateArray().First(e => e.GetProperty("isCanceled").GetBoolean());
        Assert.Equal(ExecutionResult.CanceledError, canceledErr.GetProperty("error").GetString());
        Assert.Equal(4, canceledErr.GetProperty("sampleBees")[0].GetInt32());
    }

    [Theory, MemberData(nameof(BundleFormats))]
    public async Task Runs_calls_groups_endpoints_and_applies_filters(string format)
    {
        var bundles = await SeedRichRunAsync($"calls-{format}");
        var path = PickPath(bundles, format);

        var allText = await CliAsync("runs", "calls", path);
        Assert.Equal(ExitCodes.Ok, allText.Exit);
        Assert.Contains("POST /pay", allText.Out);
        Assert.Contains("GET /products/{id}", allText.Out);
        Assert.Contains("GET /health", allText.Out);

        var filteredJson = await CliAsync("runs", "calls", path, "--entry", "1", "--slow", "--min-ms", "200", "--search", "pay", "--json");
        Assert.Equal(ExitCodes.Ok, filteredJson.Exit);
        using var doc = JsonDocument.Parse(filteredJson.Out);
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("totalCalls").GetInt64());
        Assert.Equal(1, root.GetProperty("totalSlowCalls").GetInt64());
        Assert.Equal(1, root.GetProperty("totalFailedCalls").GetInt64());
        var ep = Assert.Single(root.GetProperty("endpoints").EnumerateArray());
        Assert.Equal("POST /pay", ep.GetProperty("endpoint").GetString());
        Assert.Equal("POST", ep.GetProperty("method").GetString());
        Assert.True(ep.GetProperty("hasCustomThreshold").GetBoolean());
        Assert.Equal(150, ep.GetProperty("slowThresholdMs").GetDouble());
        Assert.Equal(1, ep.GetProperty("entries")[0].GetInt32());

        var failedOnly = await CliAsync("runs", "calls", path, "--failed", "--json");
        Assert.Equal(ExitCodes.Ok, failedOnly.Exit);
        using var failDoc = JsonDocument.Parse(failedOnly.Out);
        Assert.Equal(2, failDoc.RootElement.GetProperty("endpoints").GetArrayLength());
    }

    [Theory, MemberData(nameof(BundleFormats))]
    public async Task Runs_logs_filters_by_bee_entry_step_level_search_and_paging(string format)
    {
        var bundles = await SeedRichRunAsync($"logs-{format}");
        var path = PickPath(bundles, format);

        var stepLogs = await CliAsync("runs", "logs", path, "--entry", "1", "--step", "3", "--level", "warn,error", "--json");
        Assert.Equal(ExitCodes.Ok, stepLogs.Exit);
        using (var doc = JsonDocument.Parse(stepLogs.Out))
        {
            var root = doc.RootElement;
            Assert.Equal(3, root.GetProperty("totalCount").GetInt64());
            var logs = root.GetProperty("logs");
            Assert.Equal(3, logs.GetArrayLength());
            Assert.All(logs.EnumerateArray(), l => Assert.Equal(3, l.GetProperty("step").GetInt32()));
        }

        // Paging + search + bee name filter
        var paged = await CliAsync("runs", "logs", path, "--bee", "B2", "--search", "payment", "--offset", "1", "--limit", "1", "--json");
        Assert.Equal(ExitCodes.Ok, paged.Exit);
        using (var pDoc = JsonDocument.Parse(paged.Out))
        {
            var root = pDoc.RootElement;
            Assert.Equal(2, root.GetProperty("totalCount").GetInt64());
            Assert.False(root.GetProperty("hasMore").GetBoolean());
            var line = Assert.Single(root.GetProperty("logs").EnumerateArray());
            Assert.Equal(2, line.GetProperty("bee").GetInt32());
            Assert.Equal("Error", line.GetProperty("level").GetString());
            Assert.Equal(LongUntruncatedError, line.GetProperty("exception").GetString());
        }

        if (format is "zip" or "duckdb")
        {
            var preflight = await CliAsync("runs", "logs", path, "--entry", "preflight", "--json");
            Assert.Equal(ExitCodes.Ok, preflight.Exit);
            using var pfDoc = JsonDocument.Parse(preflight.Out);
            var item = Assert.Single(pfDoc.RootElement.GetProperty("logs").EnumerateArray());
            Assert.Equal("preflight", item.GetProperty("entryKind").GetString());
            Assert.Contains("Preflight warmed cache", item.GetProperty("message").GetString());
        }

        var textRes = await CliAsync("runs", "logs", path, "--bee", "1", "--level", "info");
        Assert.Equal(ExitCodes.Ok, textRes.Exit);
        Assert.Contains("Logged in user alice", textRes.Out);
        Assert.DoesNotContain("Searched shoe catalog", textRes.Out);
    }

    [Theory, MemberData(nameof(BundleFormats))]
    public async Task Runs_bee_returns_full_details_calls_errors_and_logs(string format)
    {
        var bundles = await SeedRichRunAsync($"bee-{format}");
        var path = PickPath(bundles, format);

        var textRes = await CliAsync("runs", "bee", path, "B2");
        Assert.Equal(ExitCodes.Ok, textRes.Exit);
        Assert.Contains("Bee B2 (#2)", textRes.Out);
        Assert.Contains(LongUntruncatedError, textRes.Out);
        Assert.Contains("https://shop.test/products/202?q=hat", textRes.Out);
        Assert.Contains("Checkout payment failed", textRes.Out);

        var jsonRes = await CliAsync("runs", "bee", path, "2", "--json");
        Assert.Equal(ExitCodes.Ok, jsonRes.Exit);
        using var doc = JsonDocument.Parse(jsonRes.Out);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("bee").GetInt32());
        Assert.Equal("B2", root.GetProperty("beeName").GetString());
        Assert.Equal(1, root.GetProperty("entry").GetInt32());
        Assert.Equal("failed", root.GetProperty("outcome").GetString());
        Assert.True(root.GetProperty("isFailed").GetBoolean());
        Assert.True(root.GetProperty("isSlow").GetBoolean());
        Assert.Equal(LongUntruncatedError, root.GetProperty("error").GetString());
        Assert.Equal(3, root.GetProperty("failedStep").GetInt32());
        Assert.Equal(2, root.GetProperty("failedRep").GetInt32());
        Assert.Equal(3, root.GetProperty("calls").GetArrayLength());
        Assert.Equal(2, root.GetProperty("logs").GetArrayLength());

        if (format is "zip" or "duckdb")
        {
            Assert.Equal("Pay", root.GetProperty("failedStepName").GetString());
            Assert.Equal(4, root.GetProperty("stepVisits").GetArrayLength());
            Assert.Equal("""{"attempt":2}""", root.GetProperty("stepVisits")[3].GetProperty("data").GetString());
        }
    }

    [Theory, MemberData(nameof(BundleFormats))]
    public async Task Runs_compare_computes_overall_and_per_scenario_diffs(string format)
    {
        var baseline = await SeedRichRunAsync($"cmp-a-{format}", slowAndDegraded: false);
        var candidate = await SeedRichRunAsync($"cmp-b-{format}", slowAndDegraded: true);

        var pathA = PickPath(baseline, format);
        var pathB = PickPath(candidate, format);

        var textRes = await CliAsync("runs", "compare", pathA, pathB);
        Assert.Equal(ExitCodes.Ok, textRes.Exit);
        Assert.Contains("baseline A", textRes.Out);
        Assert.Contains("candidate B", textRes.Out);
        Assert.Contains("Per scenario", textRes.Out);
        Assert.Contains("Checkout", textRes.Out);

        var jsonRes = await CliAsync("runs", "compare", pathA, pathB, "--json");
        Assert.Equal(ExitCodes.Ok, jsonRes.Exit);
        using var doc = JsonDocument.Parse(jsonRes.Out);
        var root = doc.RootElement;
        var diff = root.GetProperty("overallDiff");
        Assert.Equal(1, diff.GetProperty("totalBeesDelta").GetInt32());
        Assert.Equal(1, diff.GetProperty("failedBeesDelta").GetInt32());
        Assert.True(diff.GetProperty("errorRateDeltaPp").GetDouble() > 0);
        Assert.True(diff.GetProperty("p50MsDelta").GetDouble() > 0);
        Assert.True(diff.GetProperty("p95MsDelta").GetDouble() > 0);
        Assert.True(diff.GetProperty("maxMsDelta").GetDouble() > 0);

        var scenarios = root.GetProperty("scenarios");
        Assert.Equal(2, scenarios.GetArrayLength());
        Assert.Equal("matched", scenarios[0].GetProperty("status").GetString());
        Assert.True(scenarios[0].GetProperty("diff").GetProperty("p95MsDelta").GetDouble() > 0);
    }

    [Fact]
    public async Task Lazy_extraction_skips_unneeded_parquet_tables_and_cleans_up_temp_dirs()
    {
        var bundles = await SeedRichRunAsync("lazy");

        // Replace calls.parquet & logs.parquet with invalid bytes to prove summary/errors/compare never touch them.
        var noLogsOrCallsZip = Path.Combine(workDir, "corrupt-logs-calls.zip");
        File.Copy(bundles.ZipPath, noLogsOrCallsZip, overwrite: true);
        using (var zip = ZipFile.Open(noLogsOrCallsZip, ZipArchiveMode.Update))
        {
            OverwriteEntry(zip, "calls.parquet", "NOT_A_PARQUET_FILE");
            OverwriteEntry(zip, "logs.parquet", "NOT_A_PARQUET_FILE");
        }

        // Also omit run_steps.parquet completely (older bundle compatibility)
        var minimalZip = Path.Combine(workDir, "minimal-no-steps.zip");
        File.Copy(bundles.ZipPath, minimalZip, overwrite: true);
        using (var zip = ZipFile.Open(minimalZip, ZipArchiveMode.Update))
        {
            zip.GetEntry("run_steps.parquet")?.Delete();
            zip.GetEntry("calls.parquet")?.Delete();
            zip.GetEntry("logs.parquet")?.Delete();
        }

        var beforeTempCount = Directory.GetDirectories(Path.GetTempPath(), "beesharp-inspect-*").Length;

        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "summary", noLogsOrCallsZip, "--json")).Exit);
        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "errors", noLogsOrCallsZip, "--json")).Exit);
        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "compare", noLogsOrCallsZip, minimalZip, "--json")).Exit);
        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "summary", minimalZip)).Exit);
        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "bee", minimalZip, "1", "--json")).Exit);

        // Corrupt bees.parquet & calls.parquet must not stop runs logs from reading logs.parquet.
        var logsOnlyZip = Path.Combine(workDir, "corrupt-bees-calls.zip");
        File.Copy(bundles.ZipPath, logsOnlyZip, overwrite: true);
        using (var zip = ZipFile.Open(logsOnlyZip, ZipArchiveMode.Update))
        {
            OverwriteEntry(zip, "bees.parquet", "CORRUPT");
            OverwriteEntry(zip, "calls.parquet", "CORRUPT");
        }
        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "logs", logsOnlyZip, "--json")).Exit);

        // Corrupt logs.parquet must not stop runs calls from reading bees.parquet + calls.parquet.
        var callsOnlyZip = Path.Combine(workDir, "corrupt-logs.zip");
        File.Copy(bundles.ZipPath, callsOnlyZip, overwrite: true);
        using (var zip = ZipFile.Open(callsOnlyZip, ZipArchiveMode.Update))
            OverwriteEntry(zip, "logs.parquet", "CORRUPT");
        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "calls", callsOnlyZip, "--json")).Exit);

        var afterTempCount = Directory.GetDirectories(Path.GetTempPath(), "beesharp-inspect-*").Length;
        Assert.Equal(beforeTempCount, afterTempCount);
    }

    [Fact]
    public async Task Read_only_duckdb_and_v1_schema_and_inmemory_json_exports_are_supported()
    {
        var bundles = await SeedRichRunAsync("ro");

        // Mark exported .duckdb as read-only and verify inspection succeeds without creating a .wal
        File.SetAttributes(bundles.DuckDbPath, File.GetAttributes(bundles.DuckDbPath) | FileAttributes.ReadOnly);
        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "summary", bundles.DuckDbPath, "--json")).Exit);
        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "bee", bundles.DuckDbPath, "1", "--json")).Exit);
        Assert.False(File.Exists(bundles.DuckDbPath + ".wal"));

        // Seed a v1 schema DuckDB file (no run_steps, no step/endpoint columns)
        var v1Db = Path.Combine(workDir, "v1-legacy.duckdb");
        using (var conn = new DuckDBConnection($"Data Source={v1Db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE runs (run_id VARCHAR PRIMARY KEY, started_utc VARCHAR NOT NULL, finished_utc VARCHAR, state VARCHAR NOT NULL, elapsed_seconds DOUBLE NOT NULL, plan_json VARCHAR NOT NULL, error VARCHAR);
                CREATE TABLE bees (run_id VARCHAR NOT NULL, sequence INTEGER NOT NULL, bee_name VARCHAR NOT NULL, entry INTEGER NOT NULL, started_at DOUBLE NOT NULL, duration_ms DOUBLE NOT NULL, status_code INTEGER, bytes BIGINT NOT NULL, error VARCHAR, outcome INTEGER NOT NULL, is_failed BOOLEAN NOT NULL, is_slow BOOLEAN NOT NULL, slow_calls INTEGER NOT NULL, call_count INTEGER NOT NULL, log_count INTEGER NOT NULL);
                CREATE TABLE calls (id BIGINT NOT NULL, run_id VARCHAR NOT NULL, sequence INTEGER NOT NULL, call_index INTEGER NOT NULL, name VARCHAR NOT NULL, offset_ms DOUBLE NOT NULL, duration_ms DOUBLE NOT NULL, status_code INTEGER, bytes BIGINT NOT NULL, error VARCHAR);
                CREATE TABLE logs (id BIGINT NOT NULL, run_id VARCHAR NOT NULL, sequence INTEGER NOT NULL, bee_name VARCHAR NOT NULL, entry INTEGER NOT NULL, at_seconds DOUBLE NOT NULL, level_rank INTEGER NOT NULL, level VARCHAR NOT NULL, message VARCHAR NOT NULL, exception VARCHAR, call_index INTEGER, bee_outcome INTEGER NOT NULL, is_failed BOOLEAN NOT NULL, is_slow BOOLEAN NOT NULL);
                INSERT INTO runs VALUES ('legacy-1', '2026-11-01T00:00:00+00:00', NULL, 'Finished', 2.0, '{"scenarios":[{"scenarioId":"http-get","label":"Legacy","ratePerMinute":60}]}', NULL);
                INSERT INTO bees VALUES ('legacy-1', 0, 'B1', 0, 0.1, 45.0, 200, 10, NULL, 0, FALSE, FALSE, 0, 1, 1);
                INSERT INTO calls VALUES (1, 'legacy-1', 0, 0, 'GET /items/42', 0, 45.0, 200, 10, NULL);
                INSERT INTO logs VALUES (1, 'legacy-1', 0, 'B1', 0, 0.1, 2, 'Information', 'legacy ok', NULL, 0, 0, FALSE, FALSE);
                """;
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "summary", v1Db, "--json")).Exit);
        var v1Calls = await CliAsync("runs", "calls", v1Db, "--json");
        Assert.Equal(ExitCodes.Ok, v1Calls.Exit);
        Assert.Contains("GET /items/{id}", v1Calls.Out);
        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "logs", v1Db, "--json")).Exit);
        Assert.Equal(ExitCodes.Ok, (await CliAsync("runs", "bee", v1Db, "1", "--json")).Exit);

        // InMemoryRunStorage JSON format ({ "run": ..., "logs": ... })
        var mem = new InMemoryRunStorage();
        mem.EnqueueRunStarted("mem-1", DateTimeOffset.UtcNow, new LoadPlan());
        mem.EnqueueBeeCompleted("mem-1", new ExecutionResult(0, 0, 0.1, 30, 200, 10, null,
            Logs: [new RecordedLog(0.1, "Information", "mem line")]), 0, 0);
        mem.EnqueueRunFinished("mem-1", DateTimeOffset.UtcNow, "Finished", 1.0, null);
        var memExp = (await mem.ExportRunAsync("mem-1", "json"))!;
        var memJsonPath = Path.Combine(workDir, "mem-export.json");
        await File.WriteAllBytesAsync(memJsonPath, memExp.Content);

        var memSum = await CliAsync("runs", "summary", memJsonPath, "--json");
        Assert.Equal(ExitCodes.Ok, memSum.Exit);
        Assert.Contains("\"mem-1\"", memSum.Out);
        var memLogs = await CliAsync("runs", "logs", memJsonPath, "--json");
        Assert.Equal(ExitCodes.Ok, memLogs.Exit);
        Assert.Contains("mem line", memLogs.Out);
        Assert.Equal(ExitCodes.CouldNotRun, (await CliAsync("runs", "bee", memJsonPath, "1")).Exit);
    }

    [Fact]
    public async Task End_to_end_cli_run_export_can_be_inspected_with_runs_commands()
    {
        var root = Path.Combine(workDir, "e2e-root");
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        Directory.CreateDirectory(Path.Combine(root, "presets"));
        Directory.CreateDirectory(Path.Combine(root, "environments"));
        await File.WriteAllTextAsync(Path.Combine(root, "scripts", "ping.cs"), """
            Scenario.Define("Ping", async ctx =>
            {
                ctx.Log("ping start");
                ctx.RecordCall("GET /ping/123", durationMs: 12, statusCode: 200, bytes: 16);
                await Task.Delay(5, ctx.CancellationToken);
            }, id: "ping");
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "presets", "smoke.json"), """
            { "name": "Smoke", "savedAt": "2026-11-25T10:00:00Z",
              "plan": { "durationSeconds": 1, "maxConcurrency": 10, "timeoutSeconds": 10,
                "scenarios": [ { "scenarioId": "ping", "label": "Ping", "ratePerMinute": 300 } ] } }
            """);

        var zipPath = Path.Combine(workDir, "e2e-export.zip");
        var runRes = await CliAsync("run", "--preset", "Smoke", "--root", root, "--progress", "0", "--export", zipPath);
        Assert.Equal(ExitCodes.Ok, runRes.Exit);
        Assert.True(File.Exists(zipPath));

        var sumRes = await CliAsync("runs", "summary", zipPath, "--json");
        Assert.Equal(ExitCodes.Ok, sumRes.Exit);
        using var sumDoc = JsonDocument.Parse(sumRes.Out);
        Assert.True(sumDoc.RootElement.GetProperty("totalBees").GetInt32() > 0);
        Assert.Equal(0, sumDoc.RootElement.GetProperty("failedBees").GetInt32());

        var callsRes = await CliAsync("runs", "calls", zipPath, "--json");
        Assert.Equal(ExitCodes.Ok, callsRes.Exit);
        Assert.Contains("GET /ping/{id}", callsRes.Out);

        var logsRes = await CliAsync("runs", "logs", zipPath, "--bee", "1", "--json");
        Assert.Equal(ExitCodes.Ok, logsRes.Exit);
        Assert.Contains("ping start", logsRes.Out);
    }

    [Fact]
    public async Task Help_and_error_exit_codes_behave_correctly()
    {
        foreach (var args in new[]
        {
            new[] { "--help" },
            new[] { "runs", "--help" },
            new[] { "runs", "-h" },
            new[] { "runs", "help" },
            new[] { "runs", "summary", "--help" },
            new[] { "runs", "errors", "--help" },
            new[] { "runs", "calls", "--help" },
            new[] { "runs", "logs", "--help" },
            new[] { "runs", "bee", "--help" },
            new[] { "runs", "compare", "--help" },
        })
        {
            var res = await CliAsync(args);
            Assert.Equal(ExitCodes.Ok, res.Exit);
            Assert.Equal("", res.Err);
            Assert.False(string.IsNullOrWhiteSpace(res.Out));
        }

        // Invalid argument shapes -> exit 2
        foreach (var badArgs in new[]
        {
            new[] { "runs" },
            new[] { "runs", "nope" },
            new[] { "runs", "summary" },
            new[] { "runs", "summary", "a.zip", "extra.zip" },
            new[] { "runs", "summary", "a.zip", "--json=true" },
            new[] { "runs", "errors" },
            new[] { "runs", "calls", "a.zip", "--entry", "0" },
            new[] { "runs", "calls", "a.zip", "--min-ms", "-5" },
            new[] { "runs", "calls", "a.zip", "--slow=1" },
            new[] { "runs", "logs", "a.zip", "--bee", "0" },
            new[] { "runs", "logs", "a.zip", "--entry", "0" },
            new[] { "runs", "logs", "a.zip", "--step", "0" },
            new[] { "runs", "logs", "a.zip", "--level", "nope" },
            new[] { "runs", "logs", "a.zip", "--offset", "-1" },
            new[] { "runs", "logs", "a.zip", "--limit", "0" },
            new[] { "runs", "bee", "a.zip" },
            new[] { "runs", "bee", "a.zip", "0" },
            new[] { "runs", "bee", "a.zip", "B0" },
            new[] { "runs", "bee", "a.zip", "abc" },
            new[] { "runs", "compare", "a.zip" },
        })
        {
            var res = await CliAsync(badArgs);
            Assert.Equal(ExitCodes.CouldNotRun, res.Exit);
            Assert.Equal("", res.Out);
            Assert.False(string.IsNullOrWhiteSpace(res.Err));
        }

        // Missing / corrupt / unknown bee -> exit 2
        var missing = await CliAsync("runs", "summary", Path.Combine(workDir, "missing.zip"));
        Assert.Equal(ExitCodes.CouldNotRun, missing.Exit);
        Assert.Equal("", missing.Out);

        var unsupported = Path.Combine(workDir, "notes.txt");
        await File.WriteAllTextAsync(unsupported, "hi");
        Assert.Equal(ExitCodes.CouldNotRun, (await CliAsync("runs", "summary", unsupported)).Exit);

        var corruptZip = Path.Combine(workDir, "corrupt.zip");
        await File.WriteAllTextAsync(corruptZip, "not a zip");
        Assert.Equal(ExitCodes.CouldNotRun, (await CliAsync("runs", "summary", corruptZip)).Exit);

        var corruptJson = Path.Combine(workDir, "corrupt.json");
        await File.WriteAllTextAsync(corruptJson, "{ broken");
        Assert.Equal(ExitCodes.CouldNotRun, (await CliAsync("runs", "summary", corruptJson)).Exit);

        var bundles = await SeedRichRunAsync("missing-bee");
        var missingBee = await CliAsync("runs", "bee", bundles.ZipPath, "999");
        Assert.Equal(ExitCodes.CouldNotRun, missingBee.Exit);
        Assert.Contains("was not found", missingBee.Err);
    }

    private static void OverwriteEntry(ZipArchive zip, string name, string content)
    {
        zip.GetEntry(name)?.Delete();
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
