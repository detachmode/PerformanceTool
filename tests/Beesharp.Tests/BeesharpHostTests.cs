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
        Assert.Contains("beesharp run finished", output.ToString());
        Assert.True(app.Services.GetRequiredService<TestTokenCache>().Hits >= 1);
    }
}
