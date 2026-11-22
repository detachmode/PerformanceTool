using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;

namespace Beesharp.Engine.Tests;

/// <summary>A run resolves {{placeholders}} from the selected environment.</summary>
public sealed class EnvironmentRunTests
{
    public sealed class EchoParameters
    {
        public string Url { get; set; } = "";
        public List<string> Extra { get; set; } = [];
    }

    private sealed class EchoScenario : Scenario<EchoParameters>
    {
        public static readonly ConcurrentBag<(string Url, string Extra, string FromCode)> Seen = [];
        public override string Id => "echo";
        public override string Name => "Echo";
        public override string Description => "Records what it received.";

        protected override Task<ScenarioOutcome> ExecuteAsync(EchoParameters p, ScenarioContext ctx, CancellationToken ct)
        {
            Seen.Add((p.Url, p.Extra.FirstOrDefault() ?? "", ctx.Environment.Get("Tenant", "none")));
            return Task.FromResult(new ScenarioOutcome(200, 0));
        }
    }

    private static LoadPlan EchoPlan(string url)
    {
        var catalog = new ScenarioCatalog([new EchoScenario()]);
        var entry = ScenarioRunConfig.CreateFor(catalog.Find("echo")!);
        entry.RatePerMinute = 600;
        entry.Parameters["url"] = url;
        entry.Parameters["extra"] = new JsonArray("{{Tenant}}-x");
        return new LoadPlan { DurationSeconds = 1, Scenarios = [entry] };
    }

    private static Task Run(LoadPlan plan, EnvironmentDefinition? env) =>
        new LoadRunner(new ScenarioCatalog([new EchoScenario()])).RunAsync(plan, _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask, CancellationToken.None, environment: env);

    [Fact]
    public async Task A_run_fills_placeholders_and_scenario_code_can_read_the_environment()
    {
        EchoScenario.Seen.Clear();
        var plan = EchoPlan("{{BaseUrl}}/ping");
        await Run(plan, new EnvironmentDefinition { Name = "Staging", Variables = [new("BaseUrl", "https://s.example.com"), new("Tenant", "acme")] });

        Assert.Equal(10, EchoScenario.Seen.Count);
        Assert.All(EchoScenario.Seen, s => Assert.Equal(("https://s.example.com/ping", "acme-x", "acme"), (s.Url, s.Extra, s.FromCode)));
        Assert.Equal("{{BaseUrl}}/ping", plan.Scenarios[0].Parameters["url"]!.GetValue<string>()); // the plan itself is not changed
    }

    [Fact]
    public async Task A_run_with_an_unknown_key_refuses_to_start_and_names_the_key()
    {
        EchoScenario.Seen.Clear();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(EchoPlan("{{BaseUrl}}/ping"), new EnvironmentDefinition { Name = "Local", Variables = [new("Tenant", "t")] }));
        Assert.Contains("{{BaseUrl}}", ex.Message);
        Assert.Contains("Local", ex.Message);

        var noEnv = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(EchoPlan("{{BaseUrl}}/ping"), null));
        Assert.Contains("No environment is active", noEnv.Message);
        Assert.Empty(EchoScenario.Seen);
    }

    [Fact]
    public async Task Plans_without_placeholders_run_without_an_environment()
    {
        EchoScenario.Seen.Clear();
        var plan = EchoPlan("https://plain.example.com");
        plan.Scenarios[0].Parameters["extra"] = new JsonArray("plain");
        await Run(plan, null);
        Assert.Equal(10, EchoScenario.Seen.Count);
    }
}
