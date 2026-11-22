using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Beesharp.Services.Scenarios.BuiltIn;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Core.Tests;

public class ParameterSpecTests
{
    private static FieldSpec Field<T>(string jsonName) => ParameterSpec.For(typeof(T)).Fields.Single(f => f.JsonName == jsonName);

    [Fact]
    public void Editor_kinds_are_derived_from_property_types_and_attributes()
    {
        Assert.Equal(FieldKind.Url, Field<HttpGetParameters>("url").Kind);
        Assert.Equal(FieldKind.Multiline, Field<HttpPostJsonParameters>("body").Kind);
        Assert.Equal(FieldKind.Choice, Field<CustomRequestParameters>("method").Kind);
        Assert.Equal(FieldKind.KeyValue, Field<CustomRequestParameters>("headers").Kind);
        Assert.Equal(FieldKind.Group, Field<CustomRequestParameters>("auth").Kind);
        Assert.Equal(FieldKind.Secret, Field<AuthParameters>("token").Kind);
        Assert.Equal(FieldKind.List, Field<UserJourneyParameters>("urls").Kind);
        Assert.True(Field<UserJourneyParameters>("urls").ItemsAreUrls);
        Assert.Equal(FieldKind.Duration, Field<UserJourneyParameters>("thinkTime").Kind);
        Assert.Equal(FieldKind.Slider, Field<UserJourneyParameters>("iterations").Kind);
        Assert.Equal(FieldKind.Toggle, Field<UserJourneyParameters>("stopOnFailure").Kind);

        var status = Field<HttpGetParameters>("expectedStatus");
        Assert.Equal(FieldKind.Integer, status.Kind); // range 100..599 is too wide for a slider
        Assert.True(status.Nullable);
        Assert.Equal((100d, 599d), (status.Min!.Value, status.Max!.Value));
    }

    [Fact]
    public void Labels_come_from_Display_or_property_name_and_order_follows_declaration()
    {
        var spec = ParameterSpec.For(typeof(CustomRequestParameters));
        Assert.Equal(["method", "url", "headers", "contentType", "body", "auth", "expectedStatus"], spec.Fields.Select(f => f.JsonName));
        Assert.Equal("URL", Field<HttpGetParameters>("url").Label);
        Assert.Equal("Some value", Field<Unannotated>("someValue").Label);
        Assert.Equal(["None", "Bearer token", "Basic (user + password)"], Field<AuthParameters>("scheme").Options.Select(o => o.Label));
    }

    private sealed class Unannotated { public string SomeValue { get; set; } = ""; }

    private sealed class Unsupported { public Uri Target { get; set; } = new("https://x"); public int[] Numbers { get; set; } = []; }

    [Fact]
    public void Unsupported_property_types_fail_loudly_with_the_property_name()
    {
        var ex = Assert.Throws<NotSupportedException>(() => ParameterSpec.For(typeof(Unsupported)));
        Assert.Contains("Unsupported.Target", ex.Message);
    }

    [Fact]
    public void Defaults_come_from_property_initialisers()
    {
        var json = ParameterSpec.For(typeof(UserJourneyParameters)).DefaultJson();
        Assert.Equal(2, json["urls"]!.AsArray().Count);
        Assert.Equal(1, json["iterations"]!.GetValue<int>());
        Assert.True(json["stopOnFailure"]!.GetValue<bool>());
        Assert.Equal("GET", ParameterSpec.For(typeof(CustomRequestParameters)).DefaultJson()["method"]!.GetValue<string>());
    }

    [Fact]
    public void Legacy_string_values_from_old_presets_are_converted()
    {
        var problems = new List<string>();
        var journey = (UserJourneyParameters)ParameterSpec.For(typeof(UserJourneyParameters))
            .Bind(JsonNode.Parse("""{ "urls": "https://a.test/1\n  https://a.test/2 \n" }""")!.AsObject(), problems);
        Assert.Equal(["https://a.test/1", "https://a.test/2"], journey.Urls);

        var custom = (CustomRequestParameters)ParameterSpec.For(typeof(CustomRequestParameters))
            .Bind(JsonNode.Parse("""{ "method": "post", "headers": "Accept: */*\nX-Test: a:b", "url": "https://a.test" }""")!.AsObject(), problems);
        Assert.Equal(HttpVerb.POST, custom.Method);
        Assert.Equal("a:b", custom.Headers["X-Test"]);
        Assert.Equal("*/*", custom.Headers["Accept"]);
        Assert.Empty(problems);
    }

    [Fact]
    public void Unreadable_values_fall_back_to_defaults_and_are_reported()
    {
        var problems = new List<string>();
        var p = (UserJourneyParameters)ParameterSpec.For(typeof(UserJourneyParameters))
            .Bind(JsonNode.Parse("""{ "iterations": "lots", "stopOnFailure": false, "unknown": 1 }""")!.AsObject(), problems);
        Assert.Equal(1, p.Iterations);
        Assert.False(p.StopOnFailure);
        Assert.Single(problems);
        Assert.Contains("Repeat", problems[0]);
    }

    [Fact]
    public void Normalize_produces_the_canonical_shape()
    {
        var json = ParameterSpec.For(typeof(UserJourneyParameters)).Normalize(JsonNode.Parse("""{ "urls": "https://a.test", "thinkTime": "250ms" }""")!.AsObject());
        Assert.IsType<JsonArray>(json["urls"]);
        Assert.Equal("00:00:00.2500000", json["thinkTime"]!.GetValue<string>());
        Assert.NotNull(json["iterations"]);
    }

    [Fact]
    public void Validation_uses_data_annotations_recursively()
    {
        var spec = ParameterSpec.For(typeof(CustomRequestParameters));
        var json = spec.DefaultJson();
        json["url"] = "";
        json["expectedStatus"] = 42;
        var errors = spec.Validate(json);
        Assert.Contains(errors, e => e.Contains("URL") && e.Contains("required"));
        Assert.Contains(errors, e => e.Contains("Expected status"));

        var journey = ParameterSpec.For(typeof(UserJourneyParameters));
        var j = journey.DefaultJson();
        j["urls"] = new JsonArray("https://ok.test", "not a url");
        j["thinkTime"] = "00:05:00";
        errors = journey.Validate(j);
        Assert.Contains(errors, e => e.Contains("every entry"));
        Assert.Contains(errors, e => e.Contains("Think time"));

        j["urls"] = new JsonArray();
        Assert.Contains(journey.Validate(j), e => e.Contains("Steps"));
    }

    [Fact]
    public void Secrets_are_stripped_from_presets_but_env_references_are_kept_and_resolved()
    {
        var spec = ParameterSpec.For(typeof(CustomRequestParameters));
        var json = spec.DefaultJson();
        json["auth"]!["token"] = "super-secret";
        json["auth"]!["password"] = "${env:PT_TEST_PASSWORD}";

        Assert.Equal(1, spec.StripSecrets(json));
        Assert.Equal("", json["auth"]!["token"]!.GetValue<string>());
        Assert.Equal("${env:PT_TEST_PASSWORD}", json["auth"]!["password"]!.GetValue<string>());

        Environment.SetEnvironmentVariable("PT_TEST_PASSWORD", "from-env");
        var bound = (CustomRequestParameters)spec.Bind(json);
        spec.ResolveSecrets(bound);
        Assert.Equal("from-env", bound.Auth.Password);
    }

    [Fact]
    public void Json_schema_describes_types_ranges_and_editors()
    {
        var schema = ParameterSpec.For(typeof(CustomRequestParameters)).ToJsonSchema();
        var props = schema["properties"]!;
        Assert.Equal("uri", props["url"]!["format"]!.GetValue<string>());
        Assert.Equal(7, props["method"]!["enum"]!.AsArray().Count);
        Assert.Equal("object", props["auth"]!["type"]!.GetValue<string>());
        Assert.True(props["auth"]!["properties"]!["token"]!["writeOnly"]!.GetValue<bool>());
        Assert.Equal(["integer", "null"], props["expectedStatus"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("keyvalue", props["headers"]!["x-editor"]!.GetValue<string>());
        Assert.Contains("url", schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("2s", 2000)]
    [InlineData("1.5s", 1500)]
    [InlineData("1m 30s", 90_000)]
    [InlineData("250", 250)]
    [InlineData("00:00:03", 3000)]
    public void Durations_parse(string text, double ms)
    {
        Assert.True(DurationFormat.TryParse(text, out var t));
        Assert.Equal(ms, t.TotalMilliseconds);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("5 parsecs")]
    [InlineData("")]
    public void Invalid_durations_are_rejected(string text) => Assert.False(DurationFormat.TryParse(text, out _));

    [Fact]
    public void Durations_format_readably() =>
        Assert.Equal(["0s", "500ms", "1.5s", "1m 30s"], new[] { 0, 500, 1500, 90_000 }.Select(ms => DurationFormat.Format(TimeSpan.FromMilliseconds(ms))));
}
