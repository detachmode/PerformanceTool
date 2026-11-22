using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Core.Tests;

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
}
