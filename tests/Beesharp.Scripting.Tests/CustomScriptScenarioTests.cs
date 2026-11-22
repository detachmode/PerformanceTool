using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Scripting.Tests;

public sealed class CustomScriptScenarioTests
{
    [Fact]
    public async Task ScriptScenarioStore_CompilesUserApiLoadParamsScriptAndTestRunsIt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perftool-scripts-{Guid.NewGuid():N}");
        try
        {
            var catalog = new ScenarioCatalog([]);

            HttpRequestMessage? capturedRequest = null;
            string? capturedBody = null;
            var store = new ScriptScenarioStore(
                tempDir,
                catalog,
                environments: null,
                () => new StubHandler(async req =>
                {
                    capturedRequest = req;
                    capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
                    return new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
                    };
                }));

            const string userScript = """
                // 1. Define parameters (reflected by the backend to build the Blazor form)
                public record ApiLoadParams(
                    [property: Description("Target endpoint URL")]
                    string Endpoint = "https://api.example.com/v1/orders",
                    [property: Description("Auth bearer token")]
                    string? BearerToken = null,
                    [property: Range(1, 5000)]
                    int PayloadSizeBytes = 256,
                    bool IncludeRandomJitter = true
                );

                // 2. Register the scenario using the injected 'Scenario' global
                Scenario.Define<ApiLoadParams>("Order API Stress Test", async (p, ctx) =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, p.Endpoint);
                    if (!string.IsNullOrEmpty(p.BearerToken))
                        request.Headers.Authorization = new("Bearer", p.BearerToken);
                    request.Content = new StringContent(new string('x', p.PayloadSizeBytes));
                    // ctx.HttpClient is a pooled client managed by your backend
                    using var response = await ctx.HttpClient.SendAsync(request, ctx.CancellationToken);
                    response.EnsureSuccessStatusCode();
                    if (p.IncludeRandomJitter)
                        await Task.Delay(Random.Shared.Next(5, 25), ctx.CancellationToken);
                })
                .OnStartup(async (p, ctx) =>
                {
                    ctx.Log("startup-ok");
                    await Task.CompletedTask;
                })
                .OnTeardown(async (p, ctx) =>
                {
                    ctx.Log("teardown-ok");
                    await Task.CompletedTask;
                });
                """;

            var compile = await store.CompileAndRegisterAsync(null, "order-stress.csx", userScript);
            Assert.True(compile.Success, compile.Error + string.Join("; ", compile.Diagnostics.Select(d => d.Message)));
            Assert.NotNull(compile.Script);
            Assert.Equal("Order API Stress Test", compile.Script!.ScenarioName);
            Assert.NotNull(catalog.Find(compile.Script.ScenarioId));

            var testRun = await store.TestRunAsync(
                compile.Script.ScriptId,
                new JsonObject
                {
                    ["endpoint"] = "https://api.example.com/v1/orders",
                    ["bearerToken"] = "secret-123",
                    ["payloadSizeBytes"] = 42,
                    ["includeRandomJitter"] = false,
                },
                environmentName: null);

            Assert.True(testRun.Success, testRun.Error);
            Assert.Equal(201, testRun.StatusCode);
            Assert.Single(testRun.Calls);
            Assert.Equal("POST", testRun.Calls[0].Method);
            Assert.Equal("https://api.example.com/v1/orders", testRun.Calls[0].Url);
            Assert.NotNull(capturedRequest);
            Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
            Assert.Equal("secret-123", capturedRequest.Headers.Authorization?.Parameter);
            Assert.Equal(42, capturedBody?.Length);
            Assert.Contains(testRun.Logs, l => l.Contains("startup-ok"));
            Assert.Contains(testRun.Logs, l => l.Contains("teardown-ok"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ScriptScenarioStore_ReturnsDiagnosticsOnCompileError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perftool-scripts-{Guid.NewGuid():N}");
        try
        {
            var store = new ScriptScenarioStore(tempDir, new ScenarioCatalog([]));
            var compile = await store.CompileAndRegisterAsync(null, "broken.csx", "int x = \"not an int\";");
            Assert.False(compile.Success);
            Assert.NotEmpty(compile.Diagnostics);
            Assert.Contains(compile.Diagnostics, d => d.Severity == "Error");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
