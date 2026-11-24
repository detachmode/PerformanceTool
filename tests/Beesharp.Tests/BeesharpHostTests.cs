using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Tests;

public class BeesharpHostTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), "beesharp-host-" + Guid.NewGuid().ToString("N"));

    public BeesharpHostTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private sealed class TestTokenCache
    {
        private int hits;
        public int Hits => Volatile.Read(ref hits);

        public string GetToken()
        {
            Interlocked.Increment(ref hits);
            return "bearer-test-token";
        }
    }

    private sealed record CheckoutParams(int OpenCount = 1);

    private sealed class CheckoutScenario(TestTokenCache tokens) : Scenario<CheckoutParams>
    {
        public override string Id => "checkout";
        public override string Name => "Checkout";
        public override string Description => "Test scenario with DI singleton.";

        protected override Task<ScenarioOutcome> ExecuteAsync(CheckoutParams parameters, ScenarioContext context, CancellationToken cancellationToken)
        {
            var token = tokens.GetToken();
            return Task.FromResult(new ScenarioOutcome(200, token.Length + parameters.OpenCount));
        }
    }

    private sealed record OrderHistoryParams;

    private sealed class OrderHistoryScenario : Scenario<OrderHistoryParams>
    {
        public override string Id => "order-history";
        public override string Name => "Order History";
        public override string Description => "Parameterless test scenario.";

        protected override Task<ScenarioOutcome> ExecuteAsync(OrderHistoryParams parameters, ScenarioContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ScenarioOutcome(200, 42));
    }

    [Fact]
    public async Task Embedded_host_registers_di_scenarios_fluent_default_preset_and_serves_api()
    {
        var builder = BeesharpApp.CreateBuilder();
        builder.WebApplicationBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        });

        builder.Services.AddSingleton<TestTokenCache>();
        builder.AddScenario<CheckoutScenario>();
        builder.AddScenario<OrderHistoryScenario>();
        builder.AddPreset("Smoke", plan => plan
            .Duration(TimeSpan.FromMinutes(2))
            .MaxConcurrency(20)
            .Timeout(TimeSpan.FromSeconds(15))
            .Add<CheckoutScenario>(new CheckoutParams(OpenCount: 3), ratePerMinute: 30)
            .Add<OrderHistoryScenario>(ratePerMinute: 60))
            .AsDefault();

        await using var app = builder.Build();
        await app.InitializeAsync();
        await app.WebApplication.StartAsync();

        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var baseUrl = addresses.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            var scenarios = await http.GetFromJsonAsync<List<ScenarioInfoDto>>("/api/scenarios", Json);
            Assert.NotNull(scenarios);
            Assert.Contains(scenarios!, s => s.Id == "checkout");
            Assert.Contains(scenarios!, s => s.Id == "order-history");
            Assert.Contains(scenarios!, s => s.Id == "http-get");

            var presets = await http.GetFromJsonAsync<List<Preset>>("/api/presets", Json);
            Assert.NotNull(presets);
            var smoke = Assert.Single(presets!, p => p.Name == "Smoke");
            Assert.Equal(120, smoke.Plan.DurationSeconds);
            Assert.Equal(20, smoke.Plan.MaxConcurrency);
            Assert.Equal(15, smoke.Plan.TimeoutSeconds);
            Assert.Equal(2, smoke.Plan.Scenarios.Count);
            Assert.Equal("checkout", smoke.Plan.Scenarios[0].ScenarioId);
            Assert.Equal(3, smoke.Plan.Scenarios[0].Parameters["openCount"]?.GetValue<int>());
            Assert.Equal(30, smoke.Plan.Scenarios[0].RatePerMinute);
            Assert.Equal("order-history", smoke.Plan.Scenarios[1].ScenarioId);
            Assert.Equal(60, smoke.Plan.Scenarios[1].RatePerMinute);

            var planState = await http.GetFromJsonAsync<PlanWorkspaceState>("/api/plan", Json);
            Assert.NotNull(planState);
            Assert.Equal("Smoke", planState!.PresetName);
            Assert.False(planState.HasUnsavedChanges);
            Assert.Equal(2, planState.Plan.Scenarios.Count);

            var missing = await http.GetAsync("/api/does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await app.WebApplication.StopAsync();
        }
    }

    [Fact]
    public async Task Embedded_host_binds_inline_plan_and_default_environment_from_configuration()
    {
        var builder = BeesharpApp.CreateBuilder();
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
            ["Plan:DefaultEnvironment"] = "staging",
            ["Plan:DurationSeconds"] = "90",
            ["Plan:MaxConcurrency"] = "15",
            ["Plan:TimeoutSeconds"] = "12",
            ["Plan:Scenarios:0:ScenarioId"] = "http-get",
            ["Plan:Scenarios:0:Label"] = "API Health",
            ["Plan:Scenarios:0:RatePerMinute"] = "45",
            ["Plan:Scenarios:0:Parameters:url"] = "https://staging.example.test/health",
        });

        await using var app = builder.Build();
        await app.InitializeAsync();

        var workspace = app.Services.GetRequiredService<PlanWorkspace>();
        Assert.Equal("staging", workspace.DefaultEnvironment);
        var state = workspace.GetState();
        Assert.Equal(90, state.Plan.DurationSeconds);
        Assert.Equal(15, state.Plan.MaxConcurrency);
        Assert.Equal(12, state.Plan.TimeoutSeconds);
        var entry = Assert.Single(state.Plan.Scenarios);
        Assert.Equal("http-get", entry.ScenarioId);
        Assert.Equal("API Health", entry.Label);
        Assert.Equal(45, entry.RatePerMinute);
        Assert.Equal("https://staging.example.test/health", entry.Parameters["url"]?.GetValue<string>());
    }

    [Fact]
    public async Task Embedded_host_runs_headless_cli_mode_with_di_scenario()
    {
        var builder = BeesharpApp.CreateBuilder(["run", "--preset", "Smoke", "--duration", "1"]);
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        });

        builder.Services.AddSingleton<TestTokenCache>();
        builder.AddScenario<CheckoutScenario>();
        builder.AddPreset("Smoke", plan => plan
            .Duration(TimeSpan.FromSeconds(1))
            .Add<CheckoutScenario>(new CheckoutParams(OpenCount: 2), ratePerMinute: 120));

        await using var app = builder.Build();
        Assert.True(app.IsHeadlessRun);

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await app.RunHeadlessAsync(["run", "--preset", "Smoke", "--duration", "1"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        var text = output.ToString();
        Assert.Contains("Summary", text);
        Assert.Contains("#1 Checkout", text);
        Assert.Contains("Result: all ", text);
        Assert.True(app.Services.GetRequiredService<TestTokenCache>().Hits >= 1);
    }

    [Fact]
    public async Task Embedded_host_headless_run_supports_help_json_and_export()
    {
        var builder = BeesharpApp.CreateBuilder(["run", "--preset", "Smoke", "--duration", "1"]);
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        });

        builder.Services.AddSingleton<TestTokenCache>();
        builder.AddScenario<CheckoutScenario>();
        builder.AddPreset("Smoke", plan => plan
            .Duration(TimeSpan.FromSeconds(1))
            .Add<CheckoutScenario>(new CheckoutParams(OpenCount: 2), ratePerMinute: 120))
            .AsDefault();

        await using var app = builder.Build();

        // --help prints usage and exits 0
        using (var helpOut = new StringWriter())
        using (var helpErr = new StringWriter())
        {
            var helpCode = await app.RunHeadlessAsync(["run", "--help"], helpOut, helpErr);
            Assert.Equal(0, helpCode);
            Assert.Contains("--export", helpOut.ToString());
            Assert.Contains("--logs", helpOut.ToString());
            Assert.Equal("", helpErr.ToString());
        }

        // --json + --export writes valid JSON to stdout and exports run file
        var exportFile = Path.Combine(root, "exports", "host-run.json");
        using var jsonOut = new StringWriter();
        using var jsonErr = new StringWriter();
        var exitCode = await app.RunHeadlessAsync(["run", "--duration", "1", "--progress", "0", "--json", "--export", exportFile], jsonOut, jsonErr);

        Assert.Equal(0, exitCode);
        Assert.Equal("", jsonErr.ToString());
        var summaryDoc = JsonSerializer.Deserialize<JsonElement>(jsonOut.ToString());
        Assert.Equal(0, summaryDoc.GetProperty("exitCode").GetInt32());
        Assert.Equal(exportFile, summaryDoc.GetProperty("exportedTo").GetString());
        Assert.True(summaryDoc.GetProperty("exportedBytes").GetInt64() > 0);
        Assert.True(File.Exists(exportFile));
    }

    [Fact]
    public void LoadPlan_and_PlanWorkspace_allow_more_than_7_scenarios()
    {
        var catalog = new ScenarioCatalog([new OrderHistoryScenario()]);
        var workspace = new PlanWorkspace(catalog, new PresetStore(Path.Combine(root, "presets")));

        for (var i = 0; i < 10; i++)
            workspace.AddEntry("order-history");

        var state = workspace.GetState();
        Assert.Equal(10, state.Plan.Scenarios.Count);
        Assert.Empty(state.Plan.Validate(catalog));
    }

    private sealed class ProgramTrace
    {
        public List<string> Events { get; } = [];
        public string? ScenarioObservedSeed { get; set; }
        public string? ShutdownObservedSeed { get; set; }
        public bool ShouldFailPreflight { get; set; }
    }

    private sealed class SeedDataPreflight(ProgramTrace trace) : PreflightProgram
    {
        public override Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken)
        {
            trace.Events.Add("preflight");
            context.Log("Seeding test data");
            if (trace.ShouldFailPreflight)
                context.Fail("Preflight intentionally aborted");
            context.Data["SeedKey"] = "seeded-value-42";
            return Task.CompletedTask;
        }
    }

    private sealed class CleanupDataShutdown(ProgramTrace trace) : ShutdownProgram
    {
        public override Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken)
        {
            trace.Events.Add("shutdown");
            trace.ShutdownObservedSeed = context.Data.Get<string>("SeedKey");
            context.Log("Cleaning up test data");
            return Task.CompletedTask;
        }
    }

    private sealed class BagReadingScenario(ProgramTrace trace) : Scenario<OrderHistoryParams>
    {
        public override string Id => "bag-reader";
        public override string Name => "Bag Reader";
        public override string Description => "Reads a key written by preflight from RunBag.";

        protected override Task<ScenarioOutcome> ExecuteAsync(OrderHistoryParams parameters, ScenarioContext context, CancellationToken cancellationToken)
        {
            trace.Events.Add("scenario");
            var value = context.RunBag.Get<string>("SeedKey");
            trace.ScenarioObservedSeed = value;
            if (value != "seeded-value-42")
                throw new InvalidOperationException($"Expected seeded value, got '{value}'.");
            return Task.FromResult(new ScenarioOutcome(200, value.Length));
        }
    }

    [Fact]
    public async Task Compiled_preflight_and_shutdown_programs_work_via_api_and_headless_run()
    {
        var builder = BeesharpApp.CreateBuilder();
        builder.WebApplicationBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        });

        builder.Services.AddSingleton<ProgramTrace>();
        builder.AddScenario<BagReadingScenario>();
        builder.AddPreflight<SeedDataPreflight>();
        builder.AddShutdown<CleanupDataShutdown>();
        builder.AddPreset("WithPrograms", plan => plan
            .Duration(TimeSpan.FromSeconds(1))
            .Preflight<SeedDataPreflight>()
            .Shutdown<CleanupDataShutdown>()
            .Add<BagReadingScenario>(ratePerMinute: 120))
            .AsDefault();

        await using var app = builder.Build();
        await app.InitializeAsync();
        await app.WebApplication.StartAsync();

        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            using var http = new HttpClient { BaseAddress = new Uri(addresses.Addresses.First()) };

            var preflights = await http.GetFromJsonAsync<List<PreflightScriptSummary>>("/api/preflights", Json);
            var seed = Assert.Single(preflights!, p => p.ScriptId == "seed-data");
            Assert.Equal("Seed data", seed.Name);
            Assert.Equal("SeedDataPreflight.cs", seed.FileName);

            var shutdowns = await http.GetFromJsonAsync<List<PreflightScriptSummary>>("/api/shutdowns", Json);
            var cleanup = Assert.Single(shutdowns!, s => s.ScriptId == "cleanup-data");
            Assert.Equal("Cleanup data", cleanup.Name);
            Assert.Equal("CleanupDataShutdown.cs", cleanup.FileName);

            // Compiled programs cannot be deleted via API
            var deleteResp = await http.DeleteAsync("/api/preflights/seed-data");
            Assert.Equal(HttpStatusCode.NotFound, deleteResp.StatusCode);

            // Test-run endpoint executes the compiled preflight
            var testRunResp = await http.PostAsJsonAsync("/api/preflights/seed-data/test-run", new TestRunPreflightRequest(null, 30), Json);
            testRunResp.EnsureSuccessStatusCode();
            var testRun = await testRunResp.Content.ReadFromJsonAsync<PreflightRunResult>(Json);
            Assert.NotNull(testRun);
            Assert.True(testRun!.Success);
            Assert.Contains(testRun.Logs, l => l.Contains("Seeding test data"));
        }
        finally
        {
            await app.WebApplication.StopAsync();
        }

        // Now verify full execution order and RunBag sharing via headless run
        var trace = app.Services.GetRequiredService<ProgramTrace>();
        trace.Events.Clear();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await app.RunHeadlessAsync(["run", "--preset", "WithPrograms", "--duration", "1", "--progress", "0"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        Assert.Equal("preflight", trace.Events.First());
        Assert.Contains("scenario", trace.Events);
        Assert.Equal("shutdown", trace.Events.Last());
        Assert.Equal("seeded-value-42", trace.ScenarioObservedSeed);
        Assert.Equal("seeded-value-42", trace.ShutdownObservedSeed);
    }

    [Fact]
    public async Task Failing_compiled_preflight_aborts_scenarios_but_still_runs_compiled_shutdown()
    {
        var builder = BeesharpApp.CreateBuilder(["run", "--preset", "Abort", "--duration", "1"]);
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        });

        builder.Services.AddSingleton(new ProgramTrace { ShouldFailPreflight = true });
        builder.AddScenario<BagReadingScenario>();
        builder.AddPreflight<SeedDataPreflight>();
        builder.AddShutdown<CleanupDataShutdown>();
        builder.AddPreset("Abort", plan => plan
            .Duration(TimeSpan.FromSeconds(1))
            .Preflight<SeedDataPreflight>()
            .Shutdown<CleanupDataShutdown>()
            .Add<BagReadingScenario>(ratePerMinute: 120));

        await using var app = builder.Build();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await app.RunHeadlessAsync(["run", "--preset", "Abort", "--duration", "1", "--progress", "0"], output, error);

        Assert.NotEqual(0, exitCode);
        var trace = app.Services.GetRequiredService<ProgramTrace>();
        Assert.Equal(["preflight", "shutdown"], trace.Events);
    }

    [Fact]
    public async Task Inline_delegate_preflight_and_shutdown_execute_in_headless_run()
    {
        var events = new List<string>();
        var builder = BeesharpApp.CreateBuilder(["run", "--preset", "Inline", "--duration", "1"]);
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        });

        builder.AddScenario<OrderHistoryScenario>();
        builder.AddPreflight("inline-pre", "Inline Pre", (ctx, _) =>
        {
            events.Add("inline-pre");
            ctx.Data["FromInline"] = "ok";
            return Task.CompletedTask;
        });
        builder.AddShutdown("inline-shut", "Inline Shut", (ctx, _) =>
        {
            events.Add("inline-shut:" + ctx.Data.Get<string>("FromInline"));
            return Task.CompletedTask;
        });
        builder.AddPreset("Inline", plan => plan
            .Duration(TimeSpan.FromSeconds(1))
            .Preflight("inline-pre")
            .Shutdown("inline-shut")
            .Add<OrderHistoryScenario>(ratePerMinute: 120));

        await using var app = builder.Build();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await app.RunHeadlessAsync(["run", "--preset", "Inline", "--duration", "1", "--progress", "0"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Equal(["inline-pre", "inline-shut:ok"], events);
    }
}
