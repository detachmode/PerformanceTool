using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Beesharp.Engine.Tests;

/// <summary>
/// Edge-case stories for <see cref="HeadlessRunner"/> and <see cref="RunManager"/>:
/// live failure suppression (>50 failures), step suffixes, level-filtered live logs, pre-run validation &amp; export errors,
/// active-run guardrails, dropped bees under saturation, and historical run restoration with step failures.
/// </summary>
public sealed class HeadlessRunnerAndRunManagerEdgeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beesharp-engine-edge-{Guid.NewGuid():N}");

    public HeadlessRunnerAndRunManagerEdgeTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    public sealed record StepLogin;
    public sealed record StepPay;
    public union PayFlowStep(StepLogin, StepPay);

    private sealed record PayFlowParams(bool FailOnPay = false, int DelayMs = 0, bool EmitAllLevels = false, string Endpoint = "https://shop.test");

    private sealed class PayFlowScenario : Scenario<PayFlowParams, PayFlowStep>
    {
        public override string Id => "pay-flow";
        public override string Name => "Pay Flow";
        public override string Description => "Stepped checkout scenario for headless and manager edge tests.";

        protected override PayFlowStep StartStep(PayFlowParams p) => new StepLogin();

        protected override async Task<PayFlowStep?> RunStepAsync(PayFlowStep step, PayFlowParams p, ScenarioContext ctx, CancellationToken ct)
        {
            if (step is StepLogin)
            {
                if (p.EmitAllLevels)
                {
                    ctx.Logger.LogTrace("login trace");
                    ctx.Logger.LogDebug("login debug");
                    ctx.Logger.LogInformation("login info");
                    ctx.Logger.LogWarning("login warning");
                    ctx.Logger.LogError(new InvalidOperationException("minor"), "login error");
                }
                return new StepPay();
            }

            if (p.DelayMs > 0) await Task.Delay(p.DelayMs, ct);
            if (p.FailOnPay)
            {
                ctx.RecordCall("POST /pay", 5, 502, 0, "HTTP 502");
                throw new InvalidOperationException("gateway 502");
            }

            ctx.RecordCall("POST /pay", 5, 200, 16);
            return null;
        }
    }

    private sealed class MiniServices(Dictionary<Type, object> map, InMemoryRunStorage storage) : IServiceProvider, IAsyncDisposable
    {
        public object? GetService(Type serviceType) => map.GetValueOrDefault(serviceType);
        public ValueTask DisposeAsync() => storage.DisposeAsync();
    }

    private MiniServices BuildServices()
    {
        var catalog = new ScenarioCatalog([new PayFlowScenario()]);
        var envStore = new EnvironmentStore(Path.Combine(root, "environments"));
        var preflightStore = new PreflightStore(Path.Combine(root, "scripts", "preflight"), envStore);
        var shutdownStore = new ShutdownStore(Path.Combine(root, "scripts", "shutdown"), envStore);
        var storage = new InMemoryRunStorage();
        var runner = new LoadRunner(catalog);
        var runManager = new RunManager(runner, envStore, storage, preflightStore, shutdownStore);

        return new MiniServices(new Dictionary<Type, object>
        {
            [typeof(ScenarioCatalog)] = catalog,
            [typeof(EnvironmentStore)] = envStore,
            [typeof(PreflightStore)] = preflightStore,
            [typeof(ShutdownStore)] = shutdownStore,
            [typeof(IRunStorage)] = storage,
            [typeof(LoadRunner)] = runner,
            [typeof(RunManager)] = runManager,
        }, storage);
    }

    [Fact]
    public async Task Headless_runner_suppresses_live_failures_above_50_shows_step_name_and_streams_filtered_logs()
    {
        await using var sp = BuildServices();

        // 1. Burst of 60 failing bees triggers the 50-failure live suppression line and prints the step name ("at Step pay").
        var failingPlan = new PlanBuilder()
            .Duration(TimeSpan.FromSeconds(1))
            .MaxConcurrency(80)
            .AddBurst<PayFlowScenario>(new PayFlowParams(FailOnPay: true), beesPerBurst: 60, burstsPerMinute: 60, label: "Checkout")
            .Build(sp.GetRequiredService<ScenarioCatalog>());

        using (var outWriter = new StringWriter())
        using (var errWriter = new StringWriter())
        {
            var exit = await HeadlessRunner.RunAsync(
                new HeadlessRunRequest
                {
                    Plan = failingPlan,
                    PresetName = "BurstFail",
                    Options = new HeadlessRunOptions { ProgressSeconds = 0.2 },
                    ScriptProblems = [new ScriptLoadProblem("Scenario", "unused.cs", "CS1002")],
                },
                sp,
                outWriter,
                errWriter);

            Assert.Equal(HeadlessExitCodes.BeesFailed, exit);
            var stdout = outWriter.ToString();
            Assert.Contains("FAIL [Checkout]", stdout);
            Assert.Contains("at Step pay", stdout);
            Assert.Contains("further failures suppressed, see summary", stdout);
            Assert.Contains("warning: Scenario unused.cs does not compile and is skipped", errWriter.ToString());
        }

        // 2. Streaming --logs with Trace & Warning levels formats every severity tag and filters lower levels out.
        var loggingPlan = new PlanBuilder()
            .Duration(TimeSpan.FromSeconds(1))
            .Add<PayFlowScenario>(new PayFlowParams(EmitAllLevels: true), ratePerMinute: 120, label: "Logger")
            .Build(sp.GetRequiredService<ScenarioCatalog>());

        using (var traceOut = new StringWriter())
        using (var traceErr = new StringWriter())
        {
            var exit = await HeadlessRunner.RunAsync(
                new HeadlessRunRequest
                {
                    Plan = loggingPlan,
                    PresetName = "AllLogs",
                    Options = new HeadlessRunOptions { ProgressSeconds = 0, LogsLevel = "trace" },
                },
                sp,
                traceOut,
                traceErr);

            Assert.Equal(HeadlessExitCodes.Ok, exit);
            var text = traceOut.ToString();
            Assert.Contains("TRC [Logger]", text);
            Assert.Contains("DBG [Logger]", text);
            Assert.Contains("INF [Logger]", text);
            Assert.Contains("WRN [Logger]", text);
            Assert.Contains("ERR [Logger]", text);
            Assert.Contains("(minor)", text);
        }

        using (var warnOut = new StringWriter())
        using (var warnErr = new StringWriter())
        {
            var exit = await HeadlessRunner.RunAsync(
                new HeadlessRunRequest
                {
                    Plan = loggingPlan,
                    PresetName = "WarnOnly",
                    Options = new HeadlessRunOptions { ProgressSeconds = 0, LogsLevel = "warning" },
                },
                sp,
                warnOut,
                warnErr);

            Assert.Equal(HeadlessExitCodes.Ok, exit);
            var text = warnOut.ToString();
            Assert.DoesNotContain("TRC [Logger]", text);
            Assert.DoesNotContain("INF [Logger]", text);
            Assert.Contains("WRN [Logger]", text);
            Assert.Contains("ERR [Logger]", text);
        }
    }

    [Fact]
    public async Task Headless_runner_reports_missing_shutdown_script_unresolved_placeholders_and_export_errors()
    {
        await using var sp = BuildServices();
        var catalog = sp.GetRequiredService<ScenarioCatalog>();

        // 1. Missing shutdown script fails validation before starting and echoes script compile problems.
        var badShutdown = new PlanBuilder()
            .Duration(TimeSpan.FromSeconds(1))
            .Shutdown("missing-cleanup")
            .Add<PayFlowScenario>(ratePerMinute: 60)
            .Build(catalog);

        using (var o = new StringWriter())
        using (var e = new StringWriter())
        {
            var code = await HeadlessRunner.RunAsync(
                new HeadlessRunRequest
                {
                    Plan = badShutdown,
                    PresetName = "BadShutdown",
                    Options = new HeadlessRunOptions { ProgressSeconds = 0 },
                    ScriptProblems = [new ScriptLoadProblem("Shutdown program", "missing-cleanup.cs", "syntax error")],
                },
                sp, o, e);

            Assert.Equal(HeadlessExitCodes.CouldNotRun, code);
            Assert.Contains("Shutdown script 'missing-cleanup' was not found", e.ToString());
            Assert.Contains("missing-cleanup.cs does not compile", e.ToString());
        }

        // 2. Unresolved placeholder without --env explains clearly that --env is missing.
        var placeholderPlan = new PlanBuilder()
            .Duration(TimeSpan.FromSeconds(1))
            .Add<PayFlowScenario>(new PayFlowParams(Endpoint: "{{ApiHost}}/pay"), ratePerMinute: 60)
            .Build(catalog);

        using (var o = new StringWriter())
        using (var e = new StringWriter())
        {
            var code = await HeadlessRunner.RunAsync(
                new HeadlessRunRequest { Plan = placeholderPlan, PresetName = "NeedsEnv", Options = new HeadlessRunOptions { ProgressSeconds = 0 } },
                sp, o, e);

            Assert.Equal(HeadlessExitCodes.CouldNotRun, code);
            Assert.Contains("{{ApiHost}}", e.ToString());
        }

        // 3. Export & JSON write errors report cleanly on stderr without crashing the run.
        var okPlan = new PlanBuilder()
            .Duration(TimeSpan.FromSeconds(1))
            .Add<PayFlowScenario>(ratePerMinute: 120)
            .Build(catalog);

        var blockingFile = Path.Combine(root, "blocking-file");
        await File.WriteAllTextAsync(blockingFile, "not a directory");
        var dirAsJsonPath = Path.Combine(root, "existing-dir");
        Directory.CreateDirectory(dirAsJsonPath);

        using (var o = new StringWriter())
        using (var e = new StringWriter())
        {
            var code = await HeadlessRunner.RunAsync(
                new HeadlessRunRequest
                {
                    Plan = okPlan,
                    PresetName = "ExportEdge",
                    Options = new HeadlessRunOptions
                    {
                        ProgressSeconds = 0,
                        ExportPath = Path.Combine(blockingFile, "out.json"), // parent is a file -> IOException
                        JsonFile = dirAsJsonPath,                            // target is a directory -> IOException
                    },
                },
                sp, o, e);

            Assert.Equal(HeadlessExitCodes.Ok, code);
            var stderr = e.ToString();
            Assert.Contains("could not export to", stderr);
            Assert.Contains("could not write", stderr);
        }
    }

    [Fact]
    public async Task Run_manager_guards_active_runs_tracks_dropped_bees_and_restores_step_failure_and_slow_counts()
    {
        var catalog = new ScenarioCatalog([new PayFlowScenario()]);
        var envStore = new EnvironmentStore(Path.Combine(root, "environments"));
        await using var storage = new DuckDbRunStorage(Path.Combine(root, "runs.duckdb"));
        var manager = new RunManager(new LoadRunner(catalog), envStore, storage);

        // 1. Empty plan is rejected immediately.
        await Assert.ThrowsAsync<ArgumentException>(() => manager.StartAsync(new LoadPlan(), environmentName: null));

        // 2. Active run blocks concurrent StartAsync & LoadStoredRunAsync, and counts dropped bees when MaxConcurrency is saturated.
        var tightPlan = new PlanBuilder()
            .Duration(TimeSpan.FromSeconds(1))
            .MaxConcurrency(1)
            .AddBurst<PayFlowScenario>(new PayFlowParams(DelayMs: 400), beesPerBurst: 6, burstsPerMinute: 60)
            .Build(catalog);

        var active = await manager.StartAsync(tightPlan, environmentName: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(tightPlan, environmentName: null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.LoadStoredRunAsync(active.RunId));
        await manager.WaitUntilFinishedAsync();

        var afterDrop = manager.GetCurrent();
        Assert.True(afterDrop.Scenarios[0].Dropped > 0);

        // 3. Seed a stored run with both a failed-at-step bee and a slow bee, then load it into RunManager and verify all counters restore!
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        var plan = new PlanBuilder()
            .Duration(TimeSpan.FromSeconds(5))
            .Add<PayFlowScenario>(ratePerMinute: 60, label: "Checkout")
            .Build(catalog);
        storage.EnqueueRunStarted("stored-step-run", started, plan);
        storage.EnqueueBeeCompleted(
            "stored-step-run",
            new ExecutionResult(0, 0, 0.5, 400, 502, 0, "HTTP 502", [], [], FailedStep: 1, FailedRep: 0),
            beeOutcome: 2, slowCalls: 0);
        storage.EnqueueBeeCompleted(
            "stored-step-run",
            new ExecutionResult(1, 0, 1.0, 900, 200, 16, null, [], []),
            beeOutcome: 1, slowCalls: 2);
        storage.EnqueueRunFinished("stored-step-run", started.AddSeconds(5), "Finished", 2, null);
        await storage.FlushAsync();

        var restored = await manager.LoadStoredRunAsync("stored-step-run");
        Assert.NotNull(restored);
        Assert.Equal("stored-step-run", restored!.RunId);
        Assert.Equal(1, restored.Scenarios[0].Failed);
        Assert.Equal(1, restored.Scenarios[0].SlowBees);
        Assert.Equal(2, restored.Scenarios[0].SlowCalls);
        Assert.Equal([0, 1], restored.Scenarios[0].FailedPerStep);
    }
}
