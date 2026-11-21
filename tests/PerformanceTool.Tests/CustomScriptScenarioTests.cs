using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PerformanceTool.Services;
using PerformanceTool.Services.Environments;
using PerformanceTool.Services.Scenarios;
using PerformanceTool.Services.Scenarios.Spec;

namespace PerformanceTool.Tests;

public sealed class CustomScriptScenarioTests
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

    [Fact]
    public async Task LoadRunner_RunsStartupBackgroundJobAndTeardownHooks()
    {
        var startupCount = 0;
        var teardownCount = 0;
        var bgTicks = 0;
        var bgCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenTokens = new List<string>();

        var scenario = new DelegateScenario<EmptyScenarioParameters>(
            id: "lifecycle-test",
            name: "Lifecycle Test",
            description: "",
            execute: (_, ctx, _) =>
            {
                lock (seenTokens)
                    seenTokens.Add(ctx.State.TryGetValue("token", out var t) ? t?.ToString() ?? "" : "");
                return Task.FromResult(new ScenarioOutcome(200, 10));
            },
            startup: (_, ctx, _) =>
            {
                Interlocked.Increment(ref startupCount);
                ctx.State["token"] = "tok-initial";
                ctx.RunBackground(async ct =>
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            var n = Interlocked.Increment(ref bgTicks);
                            ctx.State["token"] = $"tok-{n}";
                            await Task.Delay(30, ct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        bgCancelled.TrySetResult(true);
                    }
                });
                return Task.CompletedTask;
            },
            teardown: (_, ctx, _) =>
            {
                Interlocked.Increment(ref teardownCount);
                Assert.True(bgCancelled.Task.IsCompleted);
                return Task.CompletedTask;
            });

        var catalog = new ScenarioCatalog([scenario]);
        var runner = new LoadRunner(catalog);
        var plan = new LoadPlan
        {
            DurationSeconds = 1,
            MaxConcurrency = 5,
            TimeoutSeconds = 5,
            Scenarios = [new ScenarioRunConfig { ScenarioId = "lifecycle-test", RatePerMinute = 180 }],
        };

        var results = new List<ExecutionResult>();
        await runner.RunAsync(
            plan,
            r => { lock (results) results.Add(r); return ValueTask.CompletedTask; },
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(1, startupCount);
        Assert.Equal(1, teardownCount);
        Assert.True(bgTicks >= 1);
        Assert.True(results.Count >= 2);
        Assert.All(seenTokens, t => Assert.StartsWith("tok-", t));
    }

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
