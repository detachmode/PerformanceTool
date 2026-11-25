using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Core.Tests;

/// <summary>
/// Stories for <see cref="ParameterSpec"/> schema-only mode (used by the WASM client for remote server scripts)
/// and per-field inline validation (<see cref="ParameterSpec.ValidateByField"/>) plus loose legacy string binding.
/// </summary>
public sealed class ParameterSpecSchemaAndValidationTests
{
    public enum DeliveryMode { Standard, Express }

    public sealed class RetrySettings
    {
        [Display(Name = "Max attempts"), Range(1, 5)]
        public int MaxAttempts { get; set; } = 3;

        [Display(Name = "Backoff")]
        public TimeSpan Backoff { get; set; } = TimeSpan.FromMilliseconds(250);
    }

    public sealed class RemoteOrderParams
    {
        [Display(Name = "Target URL"), Required, HttpUrl]
        public string Url { get; set; } = "https://shop.test/orders";

        [Display(Name = "Batch size"), Range(1, 100)]
        public int BatchSize { get; set; } = 10;

        [Display(Name = "Optional cap")]
        public int? OptionalCap { get; set; }

        [Display(Name = "Dry run")]
        public bool DryRun { get; set; }

        [Display(Name = "Mode")]
        public DeliveryMode Mode { get; set; } = DeliveryMode.Standard;

        [Display(Name = "Think time")]
        public TimeSpan ThinkTime { get; set; } = TimeSpan.FromSeconds(1);

        [Display(Name = "Tags"), Required]
        public List<string> Tags { get; set; } = ["smoke"];

        [Display(Name = "Headers")]
        public Dictionary<string, string> Headers { get; set; } = [];

        [Display(Name = "Retries")]
        public RetrySettings Retries { get; set; } = new();
    }

    [Fact]
    public void Schema_only_parameter_spec_generates_defaults_normalizes_and_validates_inline_fields_for_wasm_editor()
    {
        // Server exports JSON Schema -> WASM client reconstructs a schema-only ParameterSpec.
        var serverSpec = ParameterSpec.For(typeof(RemoteOrderParams));
        var schema = serverSpec.ToJsonSchema();
        var clientSpec = ParameterSpec.FromJsonSchema(schema);
        Assert.True(clientSpec.IsSchemaOnly);

        // 1. DefaultJson produces defaults for every field kind including nested groups.
        var defaults = clientSpec.DefaultJson();
        Assert.Equal("https://shop.test/orders", defaults["url"]?.GetValue<string>());
        Assert.Equal(10, defaults["batchSize"]?.GetValue<int>());
        Assert.NotNull(defaults["retries"]?.AsObject());
        Assert.Equal(3, defaults["retries"]!["maxAttempts"]?.GetValue<int>());

        // 2. Normalize preserves known values, fills missing defaults, normalizes nested groups, and drops unknown keys.
        var incoming = new JsonObject
        {
            ["url"] = "https://staging.shop.test/orders",
            ["retries"] = new JsonObject { ["maxAttempts"] = 4, ["obsolete"] = true },
            ["strayKey"] = "ignored",
        };
        var normalized = clientSpec.Normalize(incoming);
        Assert.Equal("https://staging.shop.test/orders", normalized["url"]?.GetValue<string>());
        Assert.Equal(4, normalized["retries"]!["maxAttempts"]?.GetValue<int>());
        Assert.False(normalized.ContainsKey("strayKey"));
        Assert.IsType<JsonObject>(clientSpec.Bind(incoming));

        // 3. Schema-only Validate & ValidateByField catch missing required strings/arrays and out-of-range numbers (including nested groups).
        var bad = new JsonObject
        {
            ["url"] = "   ",
            ["batchSize"] = 500,
            ["tags"] = new JsonArray(),
            ["retries"] = new JsonObject { ["maxAttempts"] = 99 },
        };
        var byField = clientSpec.ValidateByField(bad);
        Assert.True(byField.ContainsKey("url"));
        Assert.True(byField.ContainsKey("batchSize"));
        Assert.True(byField.ContainsKey("tags"));
        Assert.True(byField.ContainsKey("retries-maxAttempts"));
        Assert.Equal(4, clientSpec.Validate(bad).Count);

        // 4. Schema without explicit defaults falls back to type-appropriate defaults for every field kind.
        var bareSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["flag"] = new JsonObject { ["type"] = "boolean" },
                ["count"] = new JsonObject { ["type"] = "integer" },
                ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["headers"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
                ["choice"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("A", "B") },
                ["wait"] = new JsonObject { ["type"] = "string", ["x-editor"] = "duration" },
                ["ratio"] = new JsonObject { ["type"] = "number" },
                ["endpoint"] = new JsonObject { ["type"] = "string", ["format"] = "uri" },
                ["note"] = new JsonObject { ["type"] = "string" },
            },
        };
        var bareDefaults = ParameterSpec.FromJsonSchema(bareSchema).DefaultJson();
        Assert.False(bareDefaults["flag"]!.GetValue<bool>());
        Assert.Equal(0, bareDefaults["count"]!.GetValue<int>());
        Assert.Empty(bareDefaults["tags"]!.AsArray());
        Assert.Empty(bareDefaults["headers"]!.AsObject());
        Assert.Equal("A", bareDefaults["choice"]!.GetValue<string>());
        Assert.Equal("00:00:00", bareDefaults["wait"]!.GetValue<string>());
        Assert.Equal("", bareDefaults["note"]!.GetValue<string>());
    }

    [Fact]
    public void Typed_parameter_spec_coerces_legacy_strings_and_maps_nested_validation_errors_by_field()
    {
        var spec = ParameterSpec.For(typeof(RemoteOrderParams));

        // Legacy string shapes (e.g. CLI --param or old presets) coerce cleanly into bool, enum, and duration.
        var problems = new List<string>();
        var bound = (RemoteOrderParams)spec.Bind(new JsonObject
        {
            ["url"] = "https://shop.test/orders",
            ["dryRun"] = "true",
            ["mode"] = "express",
            ["thinkTime"] = "750ms",
            ["optionalCap"] = null,
            ["retries"] = "not-an-object",
        }, problems);

        Assert.True(bound.DryRun);
        Assert.Equal(DeliveryMode.Express, bound.Mode);
        Assert.Equal(TimeSpan.FromMilliseconds(750), bound.ThinkTime);
        Assert.Null(bound.OptionalCap);
        Assert.Contains(problems, p => p.Contains("expected a group of settings"));

        // Typed ValidateByField keys nested errors as "retries-maxAttempts".
        var typedErrors = spec.ValidateByField(new JsonObject
        {
            ["url"] = "ftp://invalid",
            ["batchSize"] = 0,
            ["retries"] = new JsonObject { ["maxAttempts"] = 99 },
        });
        Assert.True(typedErrors.ContainsKey("url"));
        Assert.True(typedErrors.ContainsKey("batchSize"));
        Assert.True(typedErrors.ContainsKey("retries-maxAttempts"));
    }
}
