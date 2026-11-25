using System.Text.Json.Nodes;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Core.Tests;

/// <summary>
/// Stories for <see cref="PlanBuilder"/> and <see cref="ScenarioCatalog"/>:
/// fluently assembling mixed load plans and managing built-in vs custom script scenarios.
/// </summary>
public sealed class PlanBuilderAndCatalogTests
{
    private sealed class WarmupPreflight : PreflightProgram
    {
        public override Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CleanupShutdown : ShutdownProgram
    {
        public override Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnregisteredScenario : Scenario<EmptyScenarioParameters>
    {
        public override string Id => "unregistered";
        public override string Name => "Unregistered";
        public override string Description => "Never added to catalog.";

        protected override Task<ScenarioOutcome> ExecuteAsync(EmptyScenarioParameters p, ScenarioContext ctx, CancellationToken ct) =>
            Task.FromResult(new ScenarioOutcome(200, 0));
    }

    [Fact]
    public void Fluent_plan_builder_composes_linear_and_burst_entries_with_programs_and_auto_numbering()
    {
        var catalog = new ScenarioCatalog([new HttpGetScenario(), new HttpPostJsonScenario()]);
        IPreflightProgram[] preflights = [new WarmupPreflight()];
        IShutdownProgram[] shutdowns = [new CleanupShutdown()];

        var plan = new PlanBuilder()
            .DurationSeconds(90)
            .MaxConcurrency(40)
            .TimeoutSeconds(20)
            .Preflight<WarmupPreflight>()
            .Shutdown<CleanupShutdown>()
            .Add<HttpGetScenario>(ratePerMinute: 30, maxInFlight: 10)
            .Add<HttpGetScenario>(new HttpGetParameters { Url = "https://api.shop.test/catalog" }, ratePerMinute: 45)
            .AddBurst<HttpPostJsonScenario>(beesPerBurst: 8, burstsPerMinute: 3, label: "  Flash Sale Orders  ", maxInFlight: 16)
            .AddBurst<HttpGetScenario>(new HttpGetParameters { Url = "https://api.shop.test/feed" }, beesPerBurst: 5, burstsPerMinute: 2)
            .Add("http-get", ratePerMinute: 12, parameters: new JsonObject { ["url"] = "https://api.shop.test/ping" }, label: "Ping")
            .AddBurst("script-checkout", beesPerBurst: 4, burstsPerMinute: 6, parameters: new JsonObject { ["items"] = 3 })
            .Build(catalog, preflights, shutdowns);

        Assert.Equal(90, plan.DurationSeconds);
        Assert.Equal(40, plan.MaxConcurrency);
        Assert.Equal(20, plan.TimeoutSeconds);
        Assert.Equal("warmup", plan.PreflightScriptId);
        Assert.Equal("cleanup", plan.ShutdownScriptId);
        Assert.Equal(6, plan.Scenarios.Count);

        // Automatic numbering disambiguates repeated scenarios while explicit labels are trimmed.
        Assert.Equal("HTTP GET", plan.Scenarios[0].Label);
        Assert.Equal(10, plan.Scenarios[0].MaxInFlight);
        Assert.Equal("HTTP GET #2", plan.Scenarios[1].Label);
        Assert.Equal("https://api.shop.test/catalog", plan.Scenarios[1].Parameters["url"]?.GetValue<string>());

        Assert.Equal("Flash Sale Orders", plan.Scenarios[2].Label);
        Assert.Equal(SpawningPattern.Bursts, plan.Scenarios[2].Pattern);
        Assert.Equal(8, plan.Scenarios[2].BeesPerBurst);
        Assert.Equal(3, plan.Scenarios[2].BurstsPerMinute);
        Assert.Equal(16, plan.Scenarios[2].MaxInFlight);

        Assert.Equal("HTTP GET #3", plan.Scenarios[3].Label);
        Assert.Equal(SpawningPattern.Bursts, plan.Scenarios[3].Pattern);
        Assert.Equal("https://api.shop.test/feed", plan.Scenarios[3].Parameters["url"]?.GetValue<string>());

        Assert.Equal("Ping", plan.Scenarios[4].Label);
        Assert.Equal("https://api.shop.test/ping", plan.Scenarios[4].Parameters["url"]?.GetValue<string>());

        // Unknown string IDs are still accepted (e.g. scripts compiled later) with their raw parameters cloned.
        Assert.Equal("script-checkout", plan.Scenarios[5].ScenarioId);
        Assert.Equal("script-checkout", plan.Scenarios[5].Label);
        Assert.Equal(3, plan.Scenarios[5].Parameters["items"]?.GetValue<int>());
    }

    [Fact]
    public void Plan_builder_clamps_sub_second_timespans_and_reports_missing_registrations_clearly()
    {
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);

        // Sub-second TimeSpan values clamp cleanly to 1 second; string IDs clear compiled resolvers.
        var clamped = new PlanBuilder()
            .Duration(TimeSpan.FromMilliseconds(200))
            .Timeout(TimeSpan.FromMilliseconds(100))
            .Preflight<WarmupPreflight>()
            .Preflight("custom-pre")
            .Shutdown<CleanupShutdown>()
            .Shutdown("custom-shut")
            .Add("http-get")
            .Build(catalog);

        Assert.Equal(1, clamped.DurationSeconds);
        Assert.Equal(1, clamped.TimeoutSeconds);
        Assert.Equal("custom-pre", clamped.PreflightScriptId);
        Assert.Equal("custom-shut", clamped.ShutdownScriptId);

        // Missing catalog for typed scenario.
        var noCatalog = Assert.Throws<InvalidOperationException>(() =>
            new PlanBuilder().Add<HttpGetScenario>().Build());
        Assert.Contains("ScenarioCatalog is required", noCatalog.Message);

        // Unregistered typed scenario.
        var missingScenario = Assert.Throws<InvalidOperationException>(() =>
            new PlanBuilder().Add<UnregisteredScenario>().Build(catalog));
        Assert.Contains("AddScenario<UnregisteredScenario>", missingScenario.Message);

        // Typed parameters that do not serialize to a JSON object.
        var badParams = Assert.Throws<ArgumentException>(() =>
            new PlanBuilder().Add<HttpGetScenario>("not-an-object").Build(catalog));
        Assert.Contains("JSON object", badParams.Message);

        // Missing preflight program collection or unregistered preflight type.
        var noPreflights = Assert.Throws<InvalidOperationException>(() =>
            new PlanBuilder().Preflight<WarmupPreflight>().Build(catalog));
        Assert.Contains("Registered preflight programs are required", noPreflights.Message);

        var missingShutdown = Assert.Throws<InvalidOperationException>(() =>
            new PlanBuilder().Shutdown<CleanupShutdown>().Build(catalog, shutdowns: []));
        Assert.Contains("AddShutdown<CleanupShutdown>", missingShutdown.Message);
    }

    [Fact]
    public void Scenario_catalog_protects_built_ins_and_manages_custom_script_scenarios_atomically()
    {
        // Duplicate built-in ids fail fast at startup.
        Assert.Throws<InvalidOperationException>(() =>
            new ScenarioCatalog([new HttpGetScenario(), new HttpGetScenario()]));

        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var changes = 0;
        catalog.Changed += () => changes++;

        Assert.False(catalog.IsCustom("http-get"));
        Assert.NotNull(catalog.Find("HTTP-GET")); // case-insensitive

        // Register a custom script scenario.
        var host = new ScenarioScriptHost();
        var checkoutV1 = host.Define("Checkout flow", _ => Task.CompletedTask, id: "script-checkout").Build();
        catalog.RegisterCustom(checkoutV1);

        Assert.True(catalog.IsCustom("script-checkout"));
        Assert.Equal(2, catalog.All.Count);
        Assert.Equal(1, changes);

        // Built-in scenarios cannot be overridden or unregistered.
        var builtInSpoof = host.Define("Spoof", _ => Task.CompletedTask, id: "http-get").Build();
        Assert.Throws<InvalidOperationException>(() => catalog.RegisterCustom(builtInSpoof));
        Assert.False(catalog.UnregisterCustom("http-get"));
        Assert.Equal(1, changes);

        // Replacing all custom scenarios atomically swaps the custom set while keeping built-ins intact.
        var search = host.Define("Search flow", _ => Task.CompletedTask, id: "script-search").Build();
        catalog.ReplaceCustomScenarios([search, builtInSpoof]);

        Assert.Null(catalog.Find("script-checkout"));
        Assert.NotNull(catalog.Find("script-search"));
        Assert.IsType<HttpGetScenario>(catalog.Find("http-get"));
        Assert.Equal(2, changes);

        // Unregistering removes custom scenarios and ignores unknown ids.
        Assert.True(catalog.UnregisterCustom("script-search"));
        Assert.False(catalog.UnregisterCustom("does-not-exist"));
        Assert.Equal(3, changes);
        Assert.Single(catalog.All);
    }
}
