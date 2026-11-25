using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Beesharp.Ai;
using Beesharp.Ai.Contracts;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Tests;

/// <summary>
/// Focused HTTP REST stories against a live in-process <see cref="BeesharpApp"/>:
/// plan &amp; preset CRUD, environment CRUD, script + live test-run endpoints,
/// run lifecycle + query endpoints, unconfigured assistant endpoints, and nested Plan config binding.
/// </summary>
public sealed class HostApiEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beesharp-api-{Guid.NewGuid():N}");

    public HostApiEndpointsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private sealed record FastParams(int DelayMs = 15, bool Fail = false);

    private sealed class FastCheckScenario : Scenario<FastParams>
    {
        public override string Id => "fast-check";
        public override string Name => "Fast Check";
        public override string Description => "Fast deterministic scenario for host REST tests.";

        protected override async Task<ScenarioOutcome> ExecuteAsync(FastParams p, ScenarioContext ctx, CancellationToken ct)
        {
            ctx.Log("running fast check");
            if (p.DelayMs > 0) await Task.Delay(p.DelayMs, ct);
            ctx.RecordCall("GET /health", p.DelayMs, p.Fail ? 500 : 200, 16, p.Fail ? "HTTP 500" : null);
            return p.Fail ? ScenarioOutcome.Failed("Check failed", 500) : new ScenarioOutcome(200, 16);
        }
    }

    private BeesharpAppBuilder CreateHostBuilder(Dictionary<string, string?>? extraConfig = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        };
        if (extraConfig is not null)
            foreach (var (k, v) in extraConfig) config[k] = v;

        var builder = BeesharpApp.CreateBuilder();
        builder.WebApplicationBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        builder.Configuration.AddInMemoryCollection(config);
        builder.AddScenario<FastCheckScenario>();
        return builder;
    }

    private static HttpClient CreateClientFor(BeesharpApp app)
    {
        var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> responseTask)
    {
        using var resp = await responseTask;
        return resp.StatusCode;
    }

    private static async Task EnsureOkAsync(Task<HttpResponseMessage> responseTask)
    {
        using var resp = await responseTask;
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<T> ReadOkJsonAsync<T>(Task<HttpResponseMessage> responseTask)
    {
        using var resp = await responseTask;
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<T>(Json))!;
    }

    [Fact]
    public async Task Plan_preset_and_environment_endpoints_handle_full_crud_and_not_found_paths()
    {
        await using var app = CreateHostBuilder().Build();
        await app.InitializeAsync();
        await app.WebApplication.StartAsync();

        try
        {
            using var http = CreateClientFor(app);

            // 1. Plan polling returns 204 when knownVersion matches, 400 for unknown scenarios, 404 for out-of-range deletes.
            var initial = await http.GetFromJsonAsync<PlanWorkspaceState>("/api/plan", Json);
            Assert.NotNull(initial);
            Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(http.GetAsync($"/api/plan?knownVersion={initial!.Version}")));
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(http.PostAsJsonAsync("/api/plan/entries", new AddEntryRequest("nope"), Json)));

            var afterAdd = await ReadOkJsonAsync<PlanWorkspaceState>(http.PostAsJsonAsync("/api/plan/entries", new AddEntryRequest("fast-check"), Json));
            Assert.Single(afterAdd.Plan.Scenarios);

            // 2. Save preset, edit plan via PUT, delete entry, reload preset, then delete preset.
            await EnsureOkAsync(http.PutAsJsonAsync("/api/presets/Smoke", afterAdd.Plan, Json));

            var putPlan = afterAdd.Plan.Clone();
            putPlan.DurationSeconds = 45;
            await EnsureOkAsync(http.PutAsJsonAsync("/api/plan", putPlan, Json));

            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.DeleteAsync("/api/plan/entries/99")));
            await EnsureOkAsync(http.DeleteAsync("/api/plan/entries/0"));

            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.PostAsync("/api/presets/Missing/load", null)));
            var reloaded = await ReadOkJsonAsync<PlanWorkspaceState>(http.PostAsync("/api/presets/Smoke/load", null));
            Assert.Single(reloaded.Plan.Scenarios);

            Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(http.DeleteAsync("/api/presets/Smoke")));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.DeleteAsync("/api/presets/Smoke")));

            // 3. Environment CRUD over /api/environments.
            var env = new EnvironmentDefinition { Name = "Staging", Variables = [new("BaseUrl", "https://staging.shop.test")] };
            Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(http.PutAsJsonAsync("/api/environments/Staging", env, Json)));

            var allEnvs = await http.GetFromJsonAsync<List<EnvironmentDefinition>>("/api/environments", Json);
            Assert.Contains(allEnvs!, e => e.Name == "Staging");

            var staging = await http.GetFromJsonAsync<EnvironmentDefinition>("/api/environments/Staging", Json);
            Assert.Equal("https://staging.shop.test", staging!.Variables[0].Value);
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.GetAsync("/api/environments/Missing")));
            Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(http.DeleteAsync("/api/environments/Staging")));
        }
        finally
        {
            await app.WebApplication.StopAsync();
        }
    }

    [Fact]
    public async Task Script_and_assistant_endpoints_cover_json_multipart_sync_and_live_stop_flows()
    {
        await using var app = CreateHostBuilder().Build();
        await app.InitializeAsync();
        await app.WebApplication.StartAsync();

        try
        {
            using var http = CreateClientFor(app);

            const string scriptCode = """
                public record ProbeParams(int DelayMs = 10);
                Scenario.Define<ProbeParams>("Mini Probe", async (p, ctx) =>
                {
                    ctx.Log("probe running");
                    if (p.DelayMs > 0) await Task.Delay(p.DelayMs, ctx.CancellationToken);
                    ctx.RecordCall("GET /probe", p.DelayMs, 200, 8);
                }, id: "script-mini-probe");
                """;

            // 1. Invalid script returns 400; JSON upload & multipart upload register cleanly; empty multipart fails 400.
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(http.PostAsJsonAsync("/api/scripts", new UploadScriptRequest("bad", "bad.cs", "not c#;"), Json)));
            await EnsureOkAsync(http.PostAsJsonAsync("/api/scripts", new UploadScriptRequest("mini-probe", "mini-probe.cs", scriptCode), Json));

            var scripts = await http.GetFromJsonAsync<List<ScriptScenarioSummary>>("/api/scripts", Json);
            Assert.Contains(scripts!, s => s.ScriptId == "mini-probe");
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(http.GetAsync("/api/scripts/mini-probe")));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.GetAsync("/api/scripts/missing")));

            using (var emptyForm = new MultipartFormDataContent())
            {
                emptyForm.Add(new StringContent("empty"), "id");
                Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(http.PostAsync("/api/scripts", emptyForm)));
            }
            using (var form = new MultipartFormDataContent())
            {
                var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(scriptCode));
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
                form.Add(new StringContent("mini-probe"), "id");
                form.Add(fileContent, "file", "mini-probe.cs");
                await EnsureOkAsync(http.PostAsync("/api/scripts", form));
            }

            // 2. Synchronous test-run + 404 on missing script.
            var syncRun = await ReadOkJsonAsync<ScriptTestRunResult>(http.PostAsJsonAsync("/api/scripts/mini-probe/test-run", new TestRunScriptRequest(null, null, 15), Json));
            Assert.True(syncRun.Success);
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.PostAsJsonAsync("/api/scripts/missing/test-run", new TestRunScriptRequest(), Json)));

            // 3. Live test-run: complete a short run AND actively stop a long-running one over HTTP!
            var quickLive = await ReadOkJsonAsync<LiveTestRunStatusDto>(http.PostAsJsonAsync("/api/scripts/mini-probe/test-run/start", new TestRunScriptRequest(null, null, 15), Json));
            var quickDone = await PollLiveTestRun(http, quickLive.TestRunId);
            Assert.True(quickDone.Success);

            var slowStart = await ReadOkJsonAsync<LiveTestRunStatusDto>(http.PostAsJsonAsync(
                "/api/scripts/mini-probe/test-run/start",
                new TestRunScriptRequest(new System.Text.Json.Nodes.JsonObject { ["delayMs"] = 10_000 }, null, 30),
                Json));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(http.PostAsync($"/api/scripts/test-runs/{slowStart.TestRunId}/stop", null)));
            var slowStopped = await PollLiveTestRun(http, slowStart.TestRunId);
            Assert.False(slowStopped.Success);
            Assert.Contains("Stopped or timed out", slowStopped.Error);

            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.GetAsync("/api/scripts/test-runs/missing")));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.PostAsync("/api/scripts/test-runs/missing/stop", null)));

            Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(http.DeleteAsync("/api/scripts/mini-probe")));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.DeleteAsync("/api/scripts/mini-probe")));

            // 4. Unconfigured AI assistant endpoints return Configured=false, 503 on chat, and 404 on unknown session delete.
            var aiStatus = await http.GetFromJsonAsync<AssistantStatusDto>("/api/assistant/status", Json);
            Assert.False(aiStatus!.Configured);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await StatusAsync(http.PostAsJsonAsync("/api/assistant/chat", new AssistantChatRequest(null, "hi"), Json)));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.DeleteAsync("/api/assistant/sessions/nope")));
        }
        finally
        {
            await app.WebApplication.StopAsync();
        }
    }

    [Fact]
    public async Task Run_and_run_query_endpoints_execute_a_run_and_serve_summary_bees_logs_facets_and_exports()
    {
        var builder = CreateHostBuilder();
        builder.AddPreset("QuickRun", p => p
            .Duration(TimeSpan.FromSeconds(1))
            .Add<FastCheckScenario>(new FastParams(DelayMs: 10, Fail: false), ratePerMinute: 240, label: "Healthy")
            .Add<FastCheckScenario>(new FastParams(DelayMs: 10, Fail: true), ratePerMinute: 120, label: "Failing"))
            .AsDefault();

        await using var app = builder.Build();
        await app.InitializeAsync();
        await app.WebApplication.StartAsync();

        try
        {
            using var http = CreateClientFor(app);

            // Before any run exists, /api/runs/current/runtime returns 204 NoContent.
            Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(http.GetAsync("/api/runs/current/runtime")));

            // Start the default plan run and wait for completion.
            await EnsureOkAsync(http.PostAsJsonAsync("/api/runs/start", new StartRunRequest(), Json));

            var runManager = app.Services.GetRequiredService<RunManager>();
            await runManager.WaitUntilFinishedAsync();
            var runId = runManager.GetCurrent().RunId;

            // Summary endpoint (200 for real run, 404 for unknown run).
            var summary = await http.GetFromJsonAsync<RunInfo>($"/api/runs/{runId}/summary", Json);
            Assert.NotNull(summary);
            Assert.Equal(runId, summary!.RunId);
            Assert.Equal(2, summary.Entries.Count);
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.GetAsync("/api/runs/does-not-exist/summary")));

            // Bees endpoint: outcome filter, explicit sequence lookup, invalid sequences (400), missing run (404).
            var failedBees = await http.GetFromJsonAsync<BeePage>($"/api/runs/{runId}/bees?outcome=failed", Json);
            Assert.True(failedBees!.Total > 0);
            Assert.All(failedBees.Items, b => Assert.Equal("failed", b.Outcome));

            var okBees = await http.GetFromJsonAsync<BeePage>($"/api/runs/{runId}/bees?outcome=ok", Json);
            Assert.True(okBees!.Total > 0);

            var seq = failedBees.Items[0].Sequence;
            var picked = await http.GetFromJsonAsync<BeePage>($"/api/runs/{runId}/bees?sequences={seq}", Json);
            Assert.Single(picked!.Items);
            Assert.Equal(seq, picked.Items[0].Sequence);

            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(http.GetAsync($"/api/runs/{runId}/bees?sequences=notanumber")));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.GetAsync("/api/runs/does-not-exist/bees")));

            // Logs, log facets, runtime data, calls detail validation, and JSON export endpoints.
            var logs = await http.GetFromJsonAsync<LogPageResult>($"/api/runs/{runId}/logs?limit=10", Json);
            Assert.True(logs!.TotalCount > 0);

            var facets = await http.GetFromJsonAsync<LogFacets>($"/api/runs/{runId}/logs/facets", Json);
            Assert.True(facets!.Total > 0);

            Assert.Equal(HttpStatusCode.OK, await StatusAsync(http.GetAsync("/api/runs/current/runtime")));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(http.GetAsync($"/api/runs/{runId}/runtime")));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.GetAsync("/api/runs/does-not-exist/runtime")));

            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(http.GetAsync($"/api/runs/{runId}/calls/detail")));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.GetAsync($"/api/runs/{runId}/calls/detail?endpoint=GET+/missing")));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.PostAsync("/api/runs/does-not-exist/load", null)));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.GetAsync("/api/runs/does-not-exist/export?format=json")));

            using var exportResp = await http.GetAsync($"/api/runs/{runId}/export?format=json");
            exportResp.EnsureSuccessStatusCode();
            Assert.Contains(runId, await exportResp.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.WebApplication.StopAsync();
        }
    }

    [Fact]
    public async Task Configuration_binder_parses_nested_parameter_arrays_objects_scalars_and_burst_settings()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            ["Plan:DurationSeconds"] = "60",
            ["Plan:ForceDefault"] = "true",
            ["Plan:Scenarios:0:ScenarioId"] = "user-journey",
            ["Plan:Scenarios:0:Label"] = "Shopper Journey",
            ["Plan:Scenarios:0:Pattern"] = "Bursts",
            ["Plan:Scenarios:0:BeesPerBurst"] = "8",
            ["Plan:Scenarios:0:BurstsPerMinute"] = "4.5",
            ["Plan:Scenarios:0:MaxInFlight"] = "20",
            ["Plan:Scenarios:0:Parameters:urls:0"] = "https://shop.test/home",
            ["Plan:Scenarios:0:Parameters:urls:1"] = "https://shop.test/catalog",
            ["Plan:Scenarios:0:Parameters:iterations"] = "2",
            ["Plan:Scenarios:0:Parameters:stopOnFailure"] = "false",
            ["Plan:Scenarios:1:ScenarioId"] = "http-custom",
            ["Plan:Scenarios:1:Parameters:headers:X-Region"] = "eu",
            ["Plan:Scenarios:1:Parameters:headers:Accept"] = "application/json",
        });

        await using var app = builder.Build();
        await app.InitializeAsync();

        var plan = app.Services.GetRequiredService<PlanWorkspace>().GetPlan();
        Assert.Equal(2, plan.Scenarios.Count);

        var journey = plan.Scenarios[0];
        Assert.Equal("Shopper Journey", journey.Label);
        Assert.Equal(SpawningPattern.Bursts, journey.Pattern);
        Assert.Equal(8, journey.BeesPerBurst);
        Assert.Equal(4.5, journey.BurstsPerMinute);
        Assert.Equal(20, journey.MaxInFlight);
        Assert.Equal(2, journey.Parameters["urls"]!.AsArray().Count);
        Assert.Equal(2, journey.Parameters["iterations"]!.GetValue<int>());
        Assert.False(journey.Parameters["stopOnFailure"]!.GetValue<bool>());

        var custom = plan.Scenarios[1];
        Assert.Equal("eu", custom.Parameters["headers"]!["X-Region"]!.GetValue<string>());
    }

    private static async Task<LiveTestRunStatusDto> PollLiveTestRun(HttpClient http, string testRunId)
    {
        LiveTestRunStatusDto? polled = null;
        for (var i = 0; i < 120; i++)
        {
            polled = await http.GetFromJsonAsync<LiveTestRunStatusDto>($"/api/scripts/test-runs/{testRunId}", Json);
            if (polled is { Running: false }) return polled;
            await Task.Delay(25);
        }
        Assert.Fail("Live test run did not finish in time.");
        return polled!;
    }
}
