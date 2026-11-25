using System.ComponentModel.DataAnnotations;
using System.Net;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Microsoft.Extensions.Logging;

namespace Beesharp.Core.Tests;

/// <summary>
/// Focused stories for script-defined scenarios (<see cref="Scenario"/>, <see cref="ScenarioScriptHost"/>,
/// <see cref="ScenarioBuilder{TParameters}"/>, <see cref="RemoteSchemaScenario"/>), lifecycle background jobs
/// (<see cref="ScenarioBackgroundHost"/>), and call/log recording (<see cref="ScenarioContext"/>,
/// <see cref="RecordingHttpHandler"/>).
/// </summary>
public sealed class ScriptScenarioAndCallTrackingTests
{
    public sealed record CheckoutParams([property: Range(1, 10)] int Items = 2);

    public sealed record BrowseStep;
    public sealed record BuyStep(int Index);
    public union ShopStep(BrowseStep, BuyStep);

    private sealed class BeeSessionState
    {
        public List<string> Trail { get; } = [];
    }

    [Fact]
    public async Task Static_scenario_dsl_registers_simple_and_typed_scenarios_with_hooks_and_thresholds()
    {
        var host = new ScenarioScriptHost();
        var events = new List<string>();

        using (Scenario.BeginHostScope(host))
        {
            Assert.True(Scenario.IsHostRegistering);

            var ping = Scenario.Define("  Simple Ping! ", _ =>
                {
                    events.Add("ping:exec");
                    return Task.CompletedTask;
                })
                .WithId("custom-ping")
                .WithDescription("Pings the target.")
                .OnStartup((_, _) => { events.Add("ping:startup"); return Task.CompletedTask; })
                .OnTeardown((_, _) => { events.Add("ping:teardown"); return Task.CompletedTask; })
                .SlowAbove(TimeSpan.FromMilliseconds(250))
                .SlowAbove("GET /ping", TimeSpan.FromMilliseconds(50));

            // Inside a host registration scope, DebugRunAsync is a no-op so file-based scripts load cleanly.
            Assert.Equal(0, await ping.DebugRunAsync());

            Scenario.Define<CheckoutParams>("Direct Outcome", (p, _) =>
                Task.FromResult(new ScenarioOutcome(201, p.Items * 10)));
        }

        Assert.False(Scenario.IsHostRegistering);
        Assert.Equal("custom-scenario", ScenarioScriptHost.Slugify("!!!"));
        Assert.Equal("script-order-api-v2", ScenarioScriptHost.Slugify("  Order API v2! "));

        var built = host.BuildAll();
        Assert.Equal(2, built.Count);

        using var http = new HttpClient();
        var ctx = new ScenarioContext(http);

        var registeredPing = built[0];
        Assert.Equal("custom-ping", registeredPing.Id);
        Assert.Equal("Pings the target.", registeredPing.Description);
        Assert.Equal(50, registeredPing.SlowThresholds.ResolveMs("GET /ping", "GET /ping", "GET"));
        Assert.Equal(250, registeredPing.SlowThresholds.ResolveMs("GET /other", "GET /other", "GET"));

        await registeredPing.StartupAsync(new EmptyScenarioParameters(), ctx, CancellationToken.None);
        await registeredPing.ExecuteAsync(new EmptyScenarioParameters(), ctx, CancellationToken.None);
        await registeredPing.TeardownAsync(new EmptyScenarioParameters(), ctx, CancellationToken.None);

        var directOutcome = await built[1].ExecuteAsync(new CheckoutParams(Items: 4), ctx, CancellationToken.None);
        Assert.Equal(201, directOutcome.StatusCode);
        Assert.Equal(40, directOutcome.Bytes);
        Assert.Equal(["ping:startup", "ping:exec", "ping:teardown"], events);
    }

    [Fact]
    public async Task Static_scenario_dsl_supports_stateful_and_yielding_step_machines()
    {
        var host = new ScenarioScriptHost();
        var trails = new List<string>();

        using (Scenario.BeginHostScope(host))
        {
            Scenario.DefineSteps<CheckoutParams, BeeSessionState, ShopStep>(
                "Stateful Shop",
                init: (_, _) => new BeeSessionState(),
                start: _ => new BrowseStep(),
                run: (step, p, state, _) =>
                {
                    switch (step)
                    {
                        case BrowseStep:
                            state.Trail.Add("browse");
                            return Task.FromResult<ShopStep?>(new BuyStep(p.Items));
                        case BuyStep b:
                            state.Trail.Add($"buy:{b.Index}");
                            trails.Add(string.Join("->", state.Trail));
                            return Task.FromResult<ShopStep?>(null);
                        default:
                            return Task.FromResult<ShopStep?>(null);
                    }
                });

            Scenario.DefineSteps<CheckoutParams, BeeSessionState, ShopStep>(
                "Yielding Stateful Shop",
                init: (_, _) => new BeeSessionState(),
                start: _ => new BrowseStep(),
                run: (step, p, state, _) => YieldFlow(step, p, state, trails));

            Scenario.DefineSteps<CheckoutParams, ShopStep>(
                "Yielding Typed Shop",
                start: _ => new BrowseStep(),
                run: (step, p, _) => YieldSimple(step, p));

            Scenario.DefineSteps<ShopStep>("Paramless Single", () => new BrowseStep(), (_, _) => Task.FromResult<ShopStep?>(null));
            Scenario.DefineSteps<ShopStep>("Paramless Yield", () => new BrowseStep(), (_, _) => EmptyYield());
        }

        var built = host.BuildAll();
        Assert.Equal(5, built.Count);

        using var http = new HttpClient();
        var ctx = new ScenarioContext(http);

        Assert.True((await built[0].ExecuteAsync(new CheckoutParams(Items: 3), ctx, CancellationToken.None)).Success);
        Assert.True((await built[1].ExecuteAsync(new CheckoutParams(Items: 2), ctx, CancellationToken.None)).Success);
        Assert.True((await built[2].ExecuteAsync(new CheckoutParams(Items: 1), ctx, CancellationToken.None)).Success);
        Assert.True((await built[3].ExecuteAsync(new EmptyScenarioParameters(), ctx, CancellationToken.None)).Success);
        Assert.True((await built[4].ExecuteAsync(new EmptyScenarioParameters(), ctx, CancellationToken.None)).Success);

        Assert.Equal(["browse->buy:3", "yield:browse->buy:0->buy:1"], trails);

        static async IAsyncEnumerable<ShopStep> YieldFlow(ShopStep step, CheckoutParams p, BeeSessionState state, List<string> trails)
        {
            await Task.Yield();
            if (step is BrowseStep)
            {
                state.Trail.Add("browse");
                for (var i = 0; i < p.Items; i++) yield return new BuyStep(i);
            }
            else if (step is BuyStep b)
            {
                state.Trail.Add($"buy:{b.Index}");
                if (b.Index == p.Items - 1)
                    trails.Add("yield:" + string.Join("->", state.Trail));
            }
        }

        static async IAsyncEnumerable<ShopStep> YieldSimple(ShopStep step, CheckoutParams p)
        {
            await Task.Yield();
            if (step is BrowseStep && p.Items > 0) yield return new BuyStep(0);
        }

        static async IAsyncEnumerable<ShopStep> EmptyYield()
        {
            await Task.Yield();
            yield break;
        }
    }

    [Fact]
    public async Task Standalone_debug_run_executes_lifecycle_applies_cli_params_and_reports_failures()
    {
        var log = new List<string>();
        var seenItems = 0;

        var passing = Scenario.DefineSteps<CheckoutParams, ShopStep>(
                "Local Checkout",
                start: _ => new BrowseStep(),
                run: async (step, p, ctx) =>
                {
                    seenItems = p.Items;
                    ctx.Logger.LogTrace("trace");
                    ctx.Logger.LogDebug("debug");
                    ctx.Logger.LogWarning("warn");
                    if (step is BrowseStep)
                    {
                        await ctx.MeasureCallAsync("GET /catalog", () => Task.CompletedTask);
                        return new BuyStep(p.Items);
                    }
                    await ctx.MeasureCallAsync("POST /checkout", () => Task.CompletedTask);
                    return null;
                })
            .OnStartup((_, _) => { log.Add("startup"); return Task.CompletedTask; })
            .OnTeardown((_, _) => { log.Add("teardown"); return Task.CompletedTask; });

        var exitOk = await passing.DebugRunAsync(["--param", "Items=5", "--timeout", "10"]);
        Assert.Equal(0, exitOk);
        Assert.Equal(5, seenItems);
        Assert.Equal(["startup", "teardown"], log);

        // Parameter validation fails fast before running startup.
        log.Clear();
        var exitInvalid = await passing.DebugRunAsync(["--param=Items=999"]);
        Assert.Equal(1, exitInvalid);
        Assert.Empty(log);

        // Unhandled execution error still invokes teardown and tolerates a teardown exception.
        var failing = Scenario.Define<CheckoutParams>("Broken Checkout", (_, _) =>
                throw new InvalidOperationException("gateway down"))
            .OnStartup((_, _) => { log.Add("broken:startup"); return Task.CompletedTask; })
            .OnTeardown((_, _) => { log.Add("broken:teardown"); throw new InvalidOperationException("cleanup glitch"); });

        var exitFail = await failing.DebugRunAsync(["-p", "Items=2"]);
        Assert.Equal(1, exitFail);
        Assert.Equal(["broken:startup", "broken:teardown"], log);
    }

    [Fact]
    public void Remote_schema_scenario_reconstructs_parameter_spec_and_blocks_local_execution()
    {
        var local = Scenario.Define<CheckoutParams>("Remote Checkout", (_, _) => Task.CompletedTask, id: "script-remote").Build();
        var schema = local.Spec.ToJsonSchema();

        var remote = new RemoteSchemaScenario("script-remote", "Remote Checkout", "Runs on server", schema, scriptId: "remote-checkout");

        Assert.Equal("script-remote", remote.Id);
        Assert.Equal("remote-checkout", remote.ScriptId);
        Assert.Single(remote.Spec.Fields);
        Assert.Equal("items", remote.Spec.Fields[0].JsonName);

        using var http = new HttpClient();
        Assert.Throws<NotSupportedException>(() =>
            remote.ExecuteAsync(new object(), new ScenarioContext(http), CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Scenario_background_host_runs_startup_jobs_cancels_them_on_dispose_and_rejects_misuse()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var bgHost = new ScenarioBackgroundHost())
        {
            using var httpBg = new HttpClient();
            var startupCtx = new ScenarioContext(httpBg, background: bgHost);

            startupCtx.RunBackground(async ct =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            });
            startupCtx.RunBackground(_ => throw new InvalidOperationException("transient background error"));

            await bgHost.DisposeAsync();
            await bgHost.DisposeAsync(); // idempotent
        }

        Assert.True(cancelled.Task.IsCompletedSuccessfully);

        // Calling RunBackground outside StartupAsync throws clearly.
        using var plainHttp = new HttpClient();
        var execCtx = new ScenarioContext(plainHttp);
        Assert.Throws<InvalidOperationException>(() => execCtx.RunBackground(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task Scenario_context_records_plain_logs_and_manual_calls_with_call_scope_overrides()
    {
        var plainLogs = new List<string>();
        var calls = new List<RecordedCall>();
        var stopped = false;

        using var http = new HttpClient();
        var ctx = new ScenarioContext(
            http,
            logSink: plainLogs.Add,
            callSink: calls.Add,
            runStopped: () => stopped);

        Assert.Same(ctx.Http, ctx.HttpClient);
        Assert.Null(ctx.SnapshotStepVisits());
        Assert.Throws<ArgumentOutOfRangeException>(() => ctx.CallScope(slowAbove: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => ctx.RecordCall("x", 10, slowAbove: TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.MeasureCallAsync("x", () => Task.CompletedTask, slowAbove: TimeSpan.Zero));

        ctx.Logger.LogTrace("t");
        ctx.Logger.LogDebug("d");
        ctx.Log("info line");
        ctx.Logger.LogWarning("warn line");
        ctx.Logger.LogError(new InvalidOperationException("db timeout"), "query failed");

        Assert.Contains("[Information] info line", plainLogs);
        Assert.Contains("[Error] query failed (db timeout)", plainLogs);

        using (ctx.CallScope("Checkout API", TimeSpan.FromMilliseconds(120)))
        {
            ctx.RecordCall("manual-step", durationMs: 45, statusCode: 200, bytes: 12);
            stopped = true;
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                ctx.MeasureCallAsync("POST /pay", () => throw new OperationCanceledException("stop")));
        }

        Assert.Equal(2, calls.Count);
        Assert.Equal("Checkout API", calls[0].Endpoint);
        Assert.Equal(120, calls[0].SlowThresholdMs);
        Assert.Equal(ExecutionResult.CanceledError, calls[1].Error);
    }

    [Fact]
    public async Task Nested_recording_http_handlers_deduplicate_calls_and_capture_http_and_cancel_errors()
    {
        var calls = new List<RecordedCall>();
        var stopped = false;

        using var innerStub = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/fail-500")
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("oops") };
            if (req.RequestUri.AbsolutePath == "/cancel")
                throw new OperationCanceledException("aborted");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        using var innerRecorder = new RecordingHttpHandler(innerStub);
        using var outerRecorder = new RecordingHttpHandler(innerRecorder);
        using var trackedHttp = new HttpClient(outerRecorder);

        // Outside an active ScenarioContext, requests pass through unrecorded.
        using var passthrough = await trackedHttp.GetAsync("https://shop.test/before-context");
        Assert.Equal(HttpStatusCode.OK, passthrough.StatusCode);
        Assert.Empty(calls);

        var ctx = new ScenarioContext(trackedHttp, callSink: calls.Add, runStopped: () => stopped);
        using (ctx.Activate())
        {
            using var errResp = await trackedHttp.GetAsync("https://shop.test/fail-500");
            Assert.Equal(HttpStatusCode.InternalServerError, errResp.StatusCode);

            stopped = true;
            await Assert.ThrowsAsync<OperationCanceledException>(() => trackedHttp.GetAsync("https://shop.test/cancel"));
        }

        Assert.Equal(2, calls.Count);
        Assert.Equal("HTTP 500", calls[0].Error);
        Assert.Equal(500, calls[0].StatusCode);
        Assert.Equal(ExecutionResult.CanceledError, calls[1].Error);
        Assert.Null(calls[1].StatusCode);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
