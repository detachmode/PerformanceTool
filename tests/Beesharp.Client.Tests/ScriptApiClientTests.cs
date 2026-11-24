using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beesharp.Client.Data;
using Beesharp.Contracts;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

public sealed class ScriptApiClientTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SyncCatalogAsync_RegistersCompiledHostScenariosWithParametersAndNoScriptId()
    {
        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var schema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "default": "honey" },
                "delayMs": { "type": "integer", "default": 40 }
              }
            }
            """)!.AsObject();

        var payload = new List<ScenarioInfoDto>
        {
            new("http-get", "HTTP GET", "Built-in", IsCustom: false, ScriptId: null, Parameters: new JsonObject()),
            new("shop-checkout", "Shop Checkout", "Compiled host scenario", IsCustom: false, ScriptId: null, Parameters: schema, Steps: [new StepInfo("Search"), new StepInfo("Pay")]),
            new("script-demo", "Script Demo", "Roslyn script", IsCustom: true, ScriptId: "script-demo", Parameters: schema),
        };

        using var http = new HttpClient(new StubHandler(JsonSerializer.Serialize(payload, JsonOpts)))
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        var client = new ScriptApiClient(http, catalog);

        await client.SyncCatalogAsync();

        // Built-in stays the native client built-in instance (not overwritten by RemoteSchemaScenario).
        Assert.IsType<HttpGetScenario>(catalog.Find("http-get"));

        // Compiled .csproj host scenario (IsCustom=false, ScriptId=null) IS registered with full ParameterSpec & Steps!
        var compiled = Assert.IsType<RemoteSchemaScenario>(catalog.Find("shop-checkout"));
        Assert.Null(compiled.ScriptId);
        Assert.Equal(["query", "delayMs"], compiled.Spec.Fields.Select(f => f.JsonName).ToArray());
        Assert.Equal(["Search", "Pay"], compiled.Steps.Select(s => s.Name).ToArray());

        // Roslyn script scenario (IsCustom=true, ScriptId!=null) keeps its ScriptId for "Edit .cs".
        var script = Assert.IsType<RemoteSchemaScenario>(catalog.Find("script-demo"));
        Assert.Equal("script-demo", script.ScriptId);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
