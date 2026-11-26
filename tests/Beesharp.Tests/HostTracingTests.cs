using System.Net;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Beesharp.Tests;

/// <summary>The embedded host and the actors' traces (brain 0063): DI-made HttpClients join the trace, export is off unless configured.</summary>
public class HostTracingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "beesharp-host-trace-" + Guid.NewGuid().ToString("N"));

    public HostTracingTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private sealed class ShopClient(HttpClient http)
    {
        public Task<HttpResponseMessage> GetAsync(string path) => http.GetAsync("http://shop.test" + path);
    }

    private sealed record ShopParams;

    private sealed class ShopScenario(ShopClient shop) : Scenario<ShopParams>
    {
        public override string Id => "shop";
        public override string Name => "Shop";
        public override string Description => "Calls a service through a typed HttpClient from DI.";

        protected override async Task<ScenarioOutcome> ExecuteAsync(ShopParams p, ScenarioContext ctx, CancellationToken ct)
        {
            await shop.GetAsync("/products/1");
            await shop.GetAsync("/cart");
            return new ScenarioOutcome(200, 0);
        }
    }

    /// <summary>The service under test: remembers the traceparent of every request it gets.</summary>
    private sealed class ServiceUnderTest : HttpMessageHandler
    {
        public List<string?> Traceparents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Traceparents) Traceparents.Add(request.Headers.TryGetValues("traceparent", out var v) ? v.Single() : null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private BeesharpAppBuilder NewBuilder(string[] args, params (string Key, string Value)[] settings)
    {
        var builder = BeesharpApp.CreateBuilder(args);
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        var config = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        };
        foreach (var (key, value) in settings) config[key] = value;
        builder.Configuration.AddInMemoryCollection(config);
        return builder;
    }

    [Fact]
    public async Task Typed_http_clients_from_di_send_the_actors_trace_with_the_sampled_flag()
    {
        var service = new ServiceUnderTest();
        var builder = NewBuilder(["run", "--preset", "Smoke", "--duration", "1"]);
        builder.Services.AddHttpClient<ShopClient>().ConfigurePrimaryHttpMessageHandler(() => service);
        builder.AddScenario<ShopScenario>();
        builder.AddPreset("Smoke", plan => plan.Duration(TimeSpan.FromSeconds(1)).Add<ShopScenario>(ratePerMinute: 120));

        await using var app = builder.Build();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await app.RunHeadlessAsync(["run", "--preset", "Smoke", "--duration", "1"], output, error));

        var storage = app.Services.GetRequiredService<IRunStorage>();
        var run = (await storage.ListRunsAsync(1)).Single();
        var traceIds = new HashSet<string>();
        var spanIds = new HashSet<string>();
        for (var seq = 0; seq < run.TotalBees; seq++)
        {
            var bee = (await storage.GetBeeDetailAsync(run.RunId, seq))!;
            Assert.Matches("^[0-9a-f]{32}$", bee.TraceId);
            Assert.True(traceIds.Add(bee.TraceId!), "one trace per actor");
            Assert.Equal(2, bee.Calls.Count);
            Assert.All(bee.Calls, c =>
            {
                Assert.Equal(bee.TraceId, c.TraceId);
                spanIds.Add(c.SpanId!);
            });
        }
        Assert.True(traceIds.Count >= 2);

        // The service got each call's span as parent, in the actor's trace, sampled.
        string?[] seen;
        lock (service.Traceparents) seen = service.Traceparents.ToArray();
        Assert.Equal(traceIds.Count * 2, seen.Length);
        foreach (var header in seen)
        {
            var parts = header!.Split('-');
            Assert.Contains(parts[1], traceIds);
            Assert.Contains(parts[2], spanIds);
            Assert.True((Convert.ToInt32(parts[3], 16) & 1) == 1, "sampled flag");
        }
    }

    [Fact]
    public async Task Trace_export_is_off_unless_an_endpoint_is_configured()
    {
        await using (var plain = NewBuilder([]).Build())
            Assert.Null(plain.Services.GetService<TracerProvider>());

        await using (var configured = NewBuilder([], ("Tracing:OtlpEndpoint", "http://127.0.0.1:4318")).Build())
        {
            await configured.InitializeAsync();
            Assert.NotNull(configured.Services.GetService<TracerProvider>());
        }

        // Also the standard OpenTelemetry environment variable name works as configuration key.
        await using (var standard = NewBuilder([], ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4318")).Build())
            Assert.NotNull(standard.Services.GetService<TracerProvider>());
    }
}
