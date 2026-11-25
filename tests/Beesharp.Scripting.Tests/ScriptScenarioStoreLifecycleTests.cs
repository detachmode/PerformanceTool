using System.Net;
using System.Text.Json.Nodes;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Scripting.Tests;

/// <summary>
/// Authoring and live-testing stories for <see cref="ScriptScenarioStore"/>:
/// uploading scripts, recovering from compilation/registration mistakes, renaming scenario IDs,
/// running synchronous test runs with environments, and polling/stopping live Try-column test runs.
/// </summary>
public sealed class ScriptScenarioStoreLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beesharp-script-life-{Guid.NewGuid():N}");
    private readonly string scriptsDir;
    private readonly string envsDir;

    public ScriptScenarioStoreLifecycleTests()
    {
        scriptsDir = Path.Combine(root, "scripts");
        envsDir = Path.Combine(root, "environments");
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(envsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static HttpMessageHandler StubOkHandler() =>
        new StubHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"echo:{req.RequestUri?.AbsolutePath}"),
        });

    [Fact]
    public async Task Author_gets_clear_feedback_for_broken_scripts_and_can_rename_and_delete_a_valid_one()
    {
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var store = new ScriptScenarioStore(scriptsDir, catalog);

        // 1. Empty code is rejected before invoking Roslyn.
        var empty = await store.CompileAndRegisterAsync("empty", "empty.cs", "   ");
        Assert.False(empty.Success);
        Assert.Contains("cannot be empty", empty.Error);

        // 2. Syntax error returns mapped Roslyn diagnostics.
        var syntaxErr = await store.CompileAndRegisterAsync("broken", "broken.cs", "this is not valid C#;");
        Assert.False(syntaxErr.Success);
        Assert.Equal("Compilation failed.", syntaxErr.Error);
        Assert.NotEmpty(syntaxErr.Diagnostics);

        // 3. Top-level runtime failure during registration surfaces the exception message.
        var runtimeErr = await store.CompileAndRegisterAsync("boom", "boom.cs", "throw new InvalidOperationException(\"bad setup\");");
        Assert.False(runtimeErr.Success);
        Assert.Contains("bad setup", runtimeErr.Error);

        // 4. Valid C# that forgets to call Scenario.Define explains what is missing.
        var noRegister = await store.CompileAndRegisterAsync("noop", "noop.cs", "var x = 1 + 1;");
        Assert.False(noRegister.Success);
        Assert.Contains("did not register any scenario", noRegister.Error);

        // 5. Trying to claim a built-in scenario ID is refused.
        var builtInClash = await store.CompileAndRegisterAsync("clash", "clash.cs",
            """Scenario.Define("Hijack", ctx => Task.CompletedTask, id: "http-get");""");
        Assert.False(builtInClash.Success);
        Assert.Contains("Cannot override built-in", builtInClash.Error);

        // 6. Upload a good script, inspect it, then update the same scriptId with a new scenario ID.
        var v1 = await store.CompileAndRegisterAsync("orders", "orders.cs",
            """Scenario.Define("Orders V1", ctx => Task.CompletedTask, id: "script-orders-v1");""");
        Assert.True(v1.Success);
        Assert.Equal("orders", store.FindScriptIdForScenario("script-orders-v1"));
        Assert.NotNull(store.Get("orders"));
        Assert.Single(store.List());

        var v2 = await store.CompileAndRegisterAsync("orders", "orders.cs",
            """Scenario.Define("Orders V2", ctx => Task.CompletedTask, id: "script-orders-v2");""");
        Assert.True(v2.Success);
        Assert.Null(catalog.Find("script-orders-v1")); // old ID unregistered cleanly
        Assert.NotNull(catalog.Find("script-orders-v2"));

        // 7. Deleting removes it from memory, catalog, and disk.
        Assert.True(store.Delete("orders"));
        Assert.False(store.Delete("orders"));
        Assert.Null(store.Get("orders"));
        Assert.Null(catalog.Find("script-orders-v2"));
        Assert.False(File.Exists(Path.Combine(scriptsDir, "orders.cs")));
    }

    [Fact]
    public async Task Synchronous_test_run_resolves_placeholders_validates_params_and_captures_failures_and_timeouts()
    {
        var envStore = new EnvironmentStore(envsDir);
        await envStore.SaveAsync(new EnvironmentDefinition
        {
            Name = "Staging",
            Variables = [new EnvironmentVariable { Key = "BaseUrl", Value = "https://staging.shop.test" }],
        });

        var catalog = new ScenarioCatalog([]);
        var store = new ScriptScenarioStore(scriptsDir, catalog, envStore, StubOkHandler);

        var scriptCode = """
            public record TryParams(
                [property: HttpUrl] string Url = "{{BaseUrl}}/orders",
                [property: Range(1, 10)] int Count = 1,
                bool Crash = false,
                bool CrashTeardown = false,
                int DelayMs = 0);

            Scenario.Define<TryParams>("Order Probe", async (p, ctx) =>
            {
                ctx.Logger.LogTrace("trc");
                ctx.Logger.LogDebug("dbg");
                ctx.Logger.LogWarning("wrn");
                ctx.Log($"Probing {p.Url}");
                if (p.DelayMs > 0) await Task.Delay(p.DelayMs, ctx.CancellationToken);
                var resp = await ctx.Http.GetAsync(p.Url, ctx.CancellationToken);
                if (p.Crash) throw new InvalidOperationException("order endpoint exploded");
                ctx.RecordCall("custom-check", 12, (int)resp.StatusCode, 4);
            }, id: "script-order-probe")
            .OnTeardown((p, ctx) =>
            {
                ctx.Log("cleaning up probe");
                if (p.CrashTeardown) throw new InvalidOperationException("teardown hiccup");
                return Task.CompletedTask;
            });
            """;

        var compiled = await store.CompileAndRegisterAsync("order-probe", "order-probe.cs", scriptCode);
        Assert.True(compiled.Success, compiled.Error);

        // 1. Happy path with Staging environment resolves {{BaseUrl}} and records both HTTP and manual calls.
        var ok = await store.TestRunAsync("order-probe", parameters: null, environmentName: "Staging");
        Assert.True(ok.Success, ok.Error);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal(2, ok.Calls.Count);
        Assert.Contains(ok.Calls, c => c.Method == "GET" && c.Url == "https://staging.shop.test/orders");
        Assert.Contains(ok.Calls, c => c.Method == "STEP" && c.Url == "custom-check");
        Assert.Contains(ok.Logs, l => l.Contains("INF] Probing https://staging.shop.test/orders"));
        Assert.Contains(ok.Logs, l => l.Contains("INF] cleaning up probe"));

        // 2. Missing environment leaves {{BaseUrl}} unresolved and fails fast with a helpful message.
        var unresolved = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TestRunAsync("order-probe", parameters: null, environmentName: null));
        Assert.Contains("{{BaseUrl}}", unresolved.Message);

        // 3. Invalid parameter range is rejected before running the scenario.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.TestRunAsync("order-probe", new JsonObject { ["url"] = "https://shop.test/ok", ["count"] = 99 }, environmentName: null));

        // 4. Unknown script/scenario ID throws KeyNotFoundException.
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.TestRunAsync("does-not-exist", null, null));

        // 5. Runtime exception + teardown exception are both captured cleanly in the result logs.
        var crashed = await store.TestRunAsync(
            "order-probe",
            new JsonObject { ["url"] = "https://shop.test/fail", ["count"] = 1, ["crash"] = true, ["crashTeardown"] = true },
            environmentName: null);
        Assert.False(crashed.Success);
        Assert.Equal("order endpoint exploded", crashed.Error);
        Assert.Contains(crashed.Logs, l => l.Contains("Unhandled exception: order endpoint exploded"));
        Assert.Contains(crashed.Logs, l => l.Contains("Teardown error: teardown hiccup"));

        // 6. Timeout cancels the bee and reports Timeout after 1 s.
        var timedOut = await store.TestRunAsync(
            "order-probe",
            new JsonObject { ["url"] = "https://shop.test/slow", ["count"] = 1, ["delayMs"] = 5000 },
            environmentName: null,
            timeoutSeconds: 1);
        Assert.False(timedOut.Success);
        Assert.Contains("Timeout after 1 s", timedOut.Error);
    }

    [Fact]
    public async Task Live_test_run_streams_steps_calls_and_logs_and_supports_failure_and_manual_stop()
    {
        var catalog = new ScenarioCatalog([]);
        var store = new ScriptScenarioStore(scriptsDir, catalog, innerHandlerFactory: StubOkHandler);

        var stepScript = """
            public record LiveParams(bool FailOnPay = false, int WaitMs = 0, bool FailTeardown = false);
            public record LoginStep;
            public record PayStep;
            public union FlowStep(LoginStep, PayStep);

            Scenario.DefineSteps<LiveParams, FlowStep>(
                "Live Checkout",
                start: _ => new LoginStep(),
                run: async (step, p, ctx) =>
                {
                    if (step is LoginStep)
                    {
                        ctx.Log("signing in");
                        await ctx.Http.GetAsync("https://shop.test/login", ctx.CancellationToken);
                        return new PayStep();
                    }
                    ctx.Log("paying");
                    if (p.WaitMs > 0) await Task.Delay(p.WaitMs, ctx.CancellationToken);
                    if (p.FailOnPay) throw new InvalidOperationException("card declined");
                    await ctx.Http.GetAsync("https://shop.test/pay", ctx.CancellationToken);
                    return null;
                },
                id: "script-live-checkout")
            .OnTeardown((p, ctx) =>
            {
                ctx.Log("live teardown");
                if (p.FailTeardown) throw new InvalidOperationException("teardown err");
                return Task.CompletedTask;
            });
            """;

        var compiled = await store.CompileAndRegisterAsync("live-checkout", "live-checkout.cs", stepScript);
        Assert.True(compiled.Success, compiled.Error);

        // 1. Happy live run finishes with steps, calls, logs, and status code.
        var started = await store.StartLiveTestRunAsync("live-checkout", new JsonObject(), environmentName: null);
        var done = await WaitForLiveFinish(store, started.TestRunId);
        Assert.True(done.Success, done.Error);
        Assert.Equal(200, done.StatusCode);
        Assert.Equal(2, done.Calls.Count);
        Assert.Equal(2, done.Steps?.Count);
        Assert.Equal(2, done.StepVisits?.Count);
        Assert.Contains(done.Logs, l => l.Message.Contains("signing in"));
        Assert.Contains(done.Logs, l => l.Message.Contains("live teardown"));

        // 2. Failing on step 2 (Pay) records FailedStep = 1, FailedRep = 0, error, and teardown error log.
        var failingStart = await store.StartLiveTestRunAsync(
            "live-checkout",
            new JsonObject { ["failOnPay"] = true, ["failTeardown"] = true },
            environmentName: null);
        var failed = await WaitForLiveFinish(store, failingStart.TestRunId);
        Assert.False(failed.Success);
        Assert.Equal("card declined", failed.Error);
        Assert.Equal(1, failed.FailedStep);
        Assert.Equal(0, failed.FailedRep);
        Assert.Contains(failed.Logs, l => l.Message.Contains("Teardown error: teardown err"));

        // 3. Stopping a long-running live test run cancels it promptly.
        var longStart = await store.StartLiveTestRunAsync(
            "live-checkout",
            new JsonObject { ["waitMs"] = 10_000 },
            environmentName: null,
            timeoutSeconds: 30);
        Assert.True(store.StopLiveTestRun(longStart.TestRunId));
        var stopped = await WaitForLiveFinish(store, longStart.TestRunId);
        Assert.False(stopped.Success);
        Assert.Contains("Stopped or timed out", stopped.Error);

        // 4. Unknown live test run IDs return null / false cleanly; invalid start rejects unknown script.
        Assert.Null(store.GetLiveTestRun("unknown-id"));
        Assert.False(store.StopLiveTestRun("unknown-id"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.StartLiveTestRunAsync("missing", null, null));
    }

    private static async Task<LiveTestRunStatusDto> WaitForLiveFinish(ScriptScenarioStore store, string testRunId)
    {
        for (var i = 0; i < 200; i++)
        {
            var snap = store.GetLiveTestRun(testRunId);
            if (snap is { Running: false }) return snap;
            await Task.Delay(25);
        }
        return store.GetLiveTestRun(testRunId) ?? throw new InvalidOperationException("Live test run vanished.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
