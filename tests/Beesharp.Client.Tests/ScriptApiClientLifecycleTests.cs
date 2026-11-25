using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Beesharp.Client.Data;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

/// <summary>
/// Full authoring &amp; test-run stories for <see cref="ScriptApiClient"/>:
/// uploading scripts into the WASM catalog, synchronous &amp; live Try-column test runs,
/// server error extraction, and shared preflight/shutdown endpoints.
/// </summary>
public sealed class ScriptApiClientLifecycleTests
{
    [Fact]
    public async Task Author_compiles_runs_live_tries_and_deletes_scenario_scripts_through_script_api_client()
    {
        var schema = new HttpGetScenario().Spec.ToJsonSchema();
        var summary = new ScriptScenarioSummary("orders", "orders.cs", "script-orders", "Orders", "Runs orders", "code", DateTimeOffset.UtcNow, schema);
        var failMode = 0; // 0 = ok, 1 = json error, 2 = plain text error

        using var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path == "/api/scripts")
                return StubHttpHandler.JsonResponse(new List<ScriptScenarioSummary> { summary });
            if (req.Method == HttpMethod.Post && path == "/api/scripts")
                return StubHttpHandler.JsonResponse(new ScriptCompileResult(true, summary, []));
            if (req.Method == HttpMethod.Delete && path == "/api/scripts/orders")
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (req.Method == HttpMethod.Post && path == "/api/scripts/orders/test-run")
            {
                return failMode switch
                {
                    1 => StubHttpHandler.JsonResponse(new { error = "Unknown placeholder {{ApiKey}}" }, HttpStatusCode.BadRequest),
                    2 => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("raw failure", Encoding.UTF8, "text/plain") },
                    _ => StubHttpHandler.JsonResponse(new ScriptTestRunResult(true, "script-orders", "Orders", 200, 12, 40, 1, 1, null, [], ["ok"])),
                };
            }
            if (req.Method == HttpMethod.Post && path == "/api/scripts/orders/test-run/start")
            {
                return failMode == 1
                    ? StubHttpHandler.JsonResponse(new { error = "Bad param" }, HttpStatusCode.BadRequest)
                    : StubHttpHandler.JsonResponse(new LiveTestRunStatusDto("live-1", true, false, "script-orders", "Orders", null, 0, 5, 1, 0, null, [], []));
            }
            if (req.Method == HttpMethod.Get && path == "/api/scripts/test-runs/live-1")
                return StubHttpHandler.JsonResponse(new LiveTestRunStatusDto("live-1", false, true, "script-orders", "Orders", 200, 12, 42, 1, 1, null, [], []));
            if (req.Method == HttpMethod.Post && path == "/api/scripts/test-runs/live-1/stop")
                return new HttpResponseMessage(HttpStatusCode.OK);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var client = new ScriptApiClient(http, catalog);

        // 1. Compiling a script registers its RemoteSchemaScenario in the client catalog.
        var compiled = await client.CompileAndSaveAsync("orders", "orders.cs", "code");
        Assert.True(compiled.Success);
        Assert.NotNull(catalog.Find("script-orders"));
        Assert.Single(await client.ListScriptsAsync());

        // 2. Synchronous test run succeeds and surfaces structured or plain-text HTTP errors cleanly.
        var okTest = await client.TestRunAsync("orders", new JsonObject(), "Staging");
        Assert.True(okTest.Success);

        failMode = 1;
        var jsonErr = await Assert.ThrowsAsync<InvalidOperationException>(() => client.TestRunAsync("orders", null, null));
        Assert.Equal("Unknown placeholder {{ApiKey}}", jsonErr.Message);
        var liveErr = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartLiveTestRunAsync("orders", null, null));
        Assert.Equal("Bad param", liveErr.Message);

        failMode = 2;
        var plainErr = await Assert.ThrowsAsync<InvalidOperationException>(() => client.TestRunAsync("orders", null, null));
        Assert.Contains("HTTP 500", plainErr.Message);

        // 3. Live Try-column test run: start -> poll -> stop.
        failMode = 0;
        var liveStart = await client.StartLiveTestRunAsync("orders", null, null);
        Assert.Equal("live-1", liveStart.TestRunId);
        var polled = await client.GetLiveTestRunAsync("live-1");
        Assert.True(polled!.Success);
        await client.StopLiveTestRunAsync("live-1");
        Assert.True(handler.HasRequest(r => r.Method == HttpMethod.Post && r.PathAndQuery == "/api/scripts/test-runs/live-1/stop"));

        // 4. Deleting the script unregisters its scenario from the catalog.
        await client.DeleteScriptAsync("orders", "script-orders");
        Assert.Null(catalog.Find("script-orders"));
    }

    [Fact]
    public async Task Preflight_and_shutdown_programs_share_list_save_test_run_and_delete_helpers()
    {
        var summary = new PreflightScriptSummary("ready", "ready.cs", "Ready", "Log(\"ok\");", DateTimeOffset.UtcNow);

        using var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path is "/api/preflights" or "/api/shutdowns")
                return StubHttpHandler.JsonResponse(new List<PreflightScriptSummary> { summary });
            if (req.Method == HttpMethod.Post && path is "/api/preflights" or "/api/shutdowns")
                return StubHttpHandler.JsonResponse(new PreflightCompileResult(true, summary, []));
            if (req.Method == HttpMethod.Post && path is "/api/preflights/ready/test-run" or "/api/shutdowns/ready/test-run")
                return StubHttpHandler.JsonResponse(new PreflightRunResult(true, 18, null, ["ready"]));
            if (req.Method == HttpMethod.Delete && path is "/api/preflights/ready" or "/api/shutdowns/ready")
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };
        var client = new ScriptApiClient(http, new ScenarioCatalog([new HttpGetScenario()]));

        Assert.Single(await client.ListPreflightsAsync(kind: ScriptApiClient.Preflights));
        Assert.Single(await client.ListPreflightsAsync(kind: ScriptApiClient.Shutdowns));

        var savedShutdown = await client.SavePreflightAsync("ready", "ready.cs", "Log(\"ok\");", kind: ScriptApiClient.Shutdowns);
        Assert.True(savedShutdown.Success);

        var runPreflight = await client.TestRunPreflightAsync("ready", "Staging", kind: ScriptApiClient.Preflights);
        Assert.True(runPreflight.Success);

        await client.DeletePreflightAsync("ready", kind: ScriptApiClient.Shutdowns);
        Assert.True(handler.HasRequest(r => r.Method == HttpMethod.Delete && r.PathAndQuery == "/api/shutdowns/ready"));
    }
}
