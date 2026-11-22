using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Core.Tests;

public sealed class CustomScriptParameterSpecTests
{
    public record SampleApiLoadParams(
        [property: Description("Target endpoint URL")]
        string Endpoint = "https://api.example.com/v1/orders",
        [property: Description("Auth bearer token")]
        string? BearerToken = null,
        [property: Range(1, 5000)]
        int PayloadSizeBytes = 256,
        bool IncludeRandomJitter = true
    );

    [Fact]
    public void ParameterSpec_SupportsPositionalRecordWithDefaultsAndDescription()
    {
        var spec = ParameterSpec.For(typeof(SampleApiLoadParams));
        Assert.Equal(4, spec.Fields.Count);

        var endpoint = spec.Fields.Single(f => f.JsonName == "endpoint");
        Assert.Equal("Target endpoint URL", endpoint.Description);
        Assert.Equal(FieldKind.Text, endpoint.Kind);

        var token = spec.Fields.Single(f => f.JsonName == "bearerToken");
        Assert.Equal("Auth bearer token", token.Description);
        Assert.True(token.Nullable);

        var payload = spec.Fields.Single(f => f.JsonName == "payloadSizeBytes");
        Assert.Equal(FieldKind.Integer, payload.Kind);
        Assert.Equal(1, payload.Min);
        Assert.Equal(5000, payload.Max);

        var jitter = spec.Fields.Single(f => f.JsonName == "includeRandomJitter");
        Assert.Equal(FieldKind.Toggle, jitter.Kind);

        var defaults = spec.DefaultJson();
        Assert.Equal("https://api.example.com/v1/orders", defaults["endpoint"]?.GetValue<string>());
        Assert.Null(defaults["bearerToken"]);
        Assert.Equal(256, defaults["payloadSizeBytes"]?.GetValue<int>());
        Assert.True(defaults["includeRandomJitter"]?.GetValue<bool>());

        // Round-trip through JSON Schema (used by Blazor WASM client for server-compiled scripts)
        var schema = spec.ToJsonSchema();
        var reconstructed = ParameterSpec.FromJsonSchema(schema);
        Assert.True(reconstructed.IsSchemaOnly);
        Assert.Equal(4, reconstructed.Fields.Count);
        Assert.Equal("Target endpoint URL", reconstructed.Fields[0].Description);
        Assert.Equal(256, reconstructed.DefaultJson()["payloadSizeBytes"]?.GetValue<int>());
        Assert.NotEmpty(reconstructed.ValidateByField(new JsonObject { ["payloadSizeBytes"] = 9999 }));
    }
}
