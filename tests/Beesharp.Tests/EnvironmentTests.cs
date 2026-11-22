using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Tests;

public class EnvironmentTests
{
    private static readonly Dictionary<string, string> Values = new() { ["BaseUrl"] = "https://staging.example.com", ["Tenant"] = "acme", ["a.b-c"] = "dotted" };

    // ---------- placeholders ----------

    [Fact]
    public void Known_keys_are_replaced_and_whitespace_inside_the_braces_is_allowed()
    {
        var unknown = new List<string>();
        Assert.Equal("https://staging.example.com/t/acme", Placeholders.Resolve("{{BaseUrl}}/t/{{ Tenant }}", Values, unknown));
        Assert.Equal("dotted", Placeholders.Resolve("{{a.b-c}}", Values, unknown));
        Assert.Empty(unknown);
    }

    [Fact]
    public void Unknown_keys_stay_as_written_and_are_reported_once()
    {
        var unknown = new List<string>();
        var result = Placeholders.Resolve("{{Nope}}/x/{{Nope}}/{{BaseUrl}}", Values, unknown);
        Assert.Equal("{{Nope}}/x/{{Nope}}/https://staging.example.com", result);
        Assert.Equal(["Nope"], unknown);
    }

    [Fact]
    public void Without_an_environment_every_key_is_unknown()
    {
        var unknown = new List<string>();
        Assert.Equal("{{BaseUrl}}", Placeholders.Resolve("{{BaseUrl}}", null, unknown));
        Assert.Equal(["BaseUrl"], unknown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("${env:NAME}")]
    [InlineData("{BaseUrl}")]
    [InlineData("{{ }}")]
    [InlineData("{{1bad}}")]
    [InlineData("{{BaseUrl}")]
    [InlineData("$ {{")]
    public void Text_without_a_placeholder_is_returned_unchanged(string text)
    {
        var unknown = new List<string>();
        Assert.Equal(text, Placeholders.Resolve(text, Values, unknown));
        Assert.Empty(unknown);
        Assert.False(Placeholders.HasPlaceholder(text));
    }

    [Fact]
    public void Null_text_resolves_to_empty_and_dollar_env_references_are_left_alone()
    {
        Assert.Equal("", Placeholders.Resolve(null, Values));
        Assert.Equal("${env:TOKEN}/acme", Placeholders.Resolve("${env:TOKEN}/{{Tenant}}", Values));
    }

    [Fact]
    public void A_backslash_escapes_a_placeholder()
    {
        var unknown = new List<string>();
        Assert.Equal("{{BaseUrl}} and https://staging.example.com", Placeholders.Resolve(@"\{{BaseUrl}} and {{BaseUrl}}", Values, unknown));
        Assert.Empty(unknown);
        Assert.False(Placeholders.HasPlaceholder(@"\{{BaseUrl}}"));
    }

    [Fact]
    public void Inserted_values_are_not_expanded_again()
    {
        var values = new Dictionary<string, string> { ["A"] = "{{B}}", ["B"] = "boom" };
        Assert.Equal("{{B}}", Placeholders.Resolve("{{A}}", values));
    }

    [Fact]
    public void Split_separates_plain_text_from_placeholders()
    {
        var parts = Placeholders.Split(@"{{BaseUrl}}/a/\{{x}}/{{ Tenant }}");
        Assert.Equal(
            [("{{BaseUrl}}", "BaseUrl"), ("/a/{{x}}/", null), ("{{ Tenant }}", "Tenant")],
            parts.Select(p => (p.Text, p.Key)).ToArray());
        Assert.Equal(["BaseUrl", "Tenant"], Placeholders.Keys(@"{{BaseUrl}}/\{{x}}/{{Tenant}}/{{BaseUrl}}"));
    }

    [Fact]
    public void Json_values_are_resolved_deeply_without_changing_the_input()
    {
        var input = JsonNode.Parse("""
            { "url": "{{BaseUrl}}/a", "urls": ["{{BaseUrl}}/1", "x"], "headers": { "{{Tenant}}": "{{Tenant}}" },
              "auth": { "token": "${env:T}" }, "count": 3, "on": true, "none": null }
            """)!.AsObject();
        var unknown = new List<string>();
        var output = Placeholders.ResolveJson(input, Values, unknown);

        Assert.Equal("https://staging.example.com/a", output["url"]!.GetValue<string>());
        Assert.Equal("https://staging.example.com/1", output["urls"]![0]!.GetValue<string>());
        Assert.Equal("acme", output["headers"]!["{{Tenant}}"]!.GetValue<string>()); // only values, not property names
        Assert.Equal("${env:T}", output["auth"]!["token"]!.GetValue<string>());
        Assert.Equal(3, output["count"]!.GetValue<int>());
        Assert.True(output["on"]!.GetValue<bool>());
        Assert.Null(output["none"]);
        Assert.Equal("{{BaseUrl}}/a", input["url"]!.GetValue<string>());
        Assert.Empty(unknown);
        Assert.Equal(["BaseUrl", "Tenant"], Placeholders.KeysIn(input));
    }

    // ---------- environment model ----------

    [Fact]
    public void Validate_reports_empty_name_bad_and_duplicate_keys_and_ignores_blank_rows()
    {
        Assert.Empty(new EnvironmentDefinition { Name = "Local", Variables = [new("BaseUrl", "x"), new("", "")] }.Validate());
        var bad = new EnvironmentDefinition { Name = " ", Variables = [new("A", "1"), new("A", "2"), new("not valid", "3"), new("", "orphan")] };
        Assert.Equal(4, bad.Validate().Count);
    }

    [Fact]
    public void Environment_values_are_readable_by_key_and_a_missing_key_says_which()
    {
        var env = new EnvironmentDefinition { Name = "Staging", Variables = [new("BaseUrl", "https://s"), new(" Tenant ", "a"), new("Tenant", "b")] }.ToValues();
        Assert.Equal("https://s", env["BaseUrl"]);
        Assert.Equal("b", env["Tenant"]); // last one wins
        Assert.Equal("fallback", env.Get("Nope", "fallback"));
        var ex = Assert.Throws<KeyNotFoundException>(() => env["Nope"]);
        Assert.Contains("Staging", ex.Message);
        Assert.Contains("Nope", ex.Message);
        Assert.Throws<KeyNotFoundException>(() => EnvironmentValues.None["x"]);
    }

    // ---------- parameters ----------

    [Fact]
    public void A_url_with_a_placeholder_passes_validation_and_a_placeholder_only_secret_is_kept_in_presets()
    {
        var spec = ParameterSpec.For(typeof(HttpGetParameters));
        var json = spec.DefaultJson();
        json["url"] = "{{BaseUrl}}/health";
        Assert.Empty(spec.Validate(json));
        json["url"] = "ftp://nope";
        Assert.NotEmpty(spec.Validate(json));

        var custom = ParameterSpec.For(typeof(CustomRequestParameters));
        var p = custom.DefaultJson();
        p["auth"]!["token"] = "{{ApiToken}}";
        p["auth"]!["password"] = "literal secret";
        Assert.Equal(1, custom.StripSecrets(p));
        Assert.Equal("{{ApiToken}}", p["auth"]!["token"]!.GetValue<string>());
    }

    // ---------- running with an environment ----------

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

    // ---------- store ----------

    [Fact]
    public async Task The_store_keeps_one_json_file_per_environment()
    {
        var root = Path.Combine(Path.GetTempPath(), "pt-env-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new EnvironmentStore(Path.Combine(root, "envs"), NullLogger<EnvironmentStore>.Instance);
            Assert.Empty(await store.ListAsync());

            await store.SaveAsync(new EnvironmentDefinition { Name = "Staging", Variables = [new("BaseUrl", "https://s"), new("", ""), new("Token", "abc")] });
            await store.SaveAsync(new EnvironmentDefinition { Name = "Local", Variables = [new("BaseUrl", "https://localhost")] });

            Assert.Equal(2, Directory.GetFiles(store.Directory, "*.json").Length);
            var all = await store.ListAsync();
            Assert.Equal(["Local", "Staging"], all.Select(e => e.Name));
            var staging = (await store.GetAsync("Staging"))!;
            Assert.Equal(["BaseUrl", "Token"], staging.Variables.Select(v => v.Key)); // order kept, blank row dropped

            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new EnvironmentDefinition { Name = "Bad", Variables = [new("a b", "x")] }));
            await store.DeleteAsync("Local");
            Assert.Null(await store.GetAsync("Local"));
            Assert.Single(await store.ListAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
