using System.Net;
using System.Net.Sockets;
using System.Text;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Core.Tests;

public class HttpCallTrackingTests
{
    [Fact]
    public void EndpointNormalizer_collapses_ids_keeps_versions_and_strips_query_and_rep()
    {
        Assert.Equal("GET /orders/{id}", EndpointNormalizer.NormalizeCallName("GET /orders/88231"));
        Assert.Equal("GET /orders/{id}/items", EndpointNormalizer.NormalizeCallName("GET /orders/550e8400-e29b-41d4-a716-446655440000/items"));
        Assert.Equal("POST /api/v2/products/{id}", EndpointNormalizer.NormalizeCallName("POST https://shop.example.com/api/v2/products/SKU-001?currency=EUR"));
        Assert.Equal("GET /cart/{id}", EndpointNormalizer.NormalizeCallName("GET /cart/cart-412"));
        Assert.Equal("GET /reports/{id}", EndpointNormalizer.NormalizeCallName("GET /reports/2026-11-24"));
        Assert.Equal("GET /users/{id}/profile", EndpointNormalizer.NormalizeCallName("GET /users/alice@example.com/profile"));
        Assert.Equal("GET /docs/{id}", EndpointNormalizer.NormalizeCallName("GET /docs/507f1f77bcf86cd799439011"));
        Assert.Equal("POST /cart/items", EndpointNormalizer.NormalizeCallName("POST /cart/items · 2"));
        Assert.Equal("GET /search", EndpointNormalizer.NormalizeCallName("GET /search?q=running+shoes&page=2"));
        Assert.Equal("Place order", EndpointNormalizer.NormalizeCallName("Place order"));

        var norm = EndpointNormalizer.NormalizeHttp(HttpMethod.Get, new Uri("https://api.shop.test:8443/v1/orders/42?expand=items"));
        Assert.Equal("GET /v1/orders/{id}", norm.Endpoint);
        Assert.Equal("GET /v1/orders/42?expand=items", norm.RawName);
        Assert.Equal("GET", norm.Method);
        Assert.Equal("api.shop.test:8443", norm.Host);
        Assert.Equal("https://api.shop.test:8443/v1/orders/42?expand=items", norm.Url);
    }

    [Fact]
    public void EndpointGrouper_adaptively_collapses_high_cardinality_slugs_and_keeps_static_routes()
    {
        var observed = new List<(string RawEndpoint, string? Host)>
        {
            ("GET /catalog/featured", "shop.test"),
            ("GET /catalog/sale", "shop.test"),
            ("GET /catalog/new-arrivals", "shop.test"),
        };
        // Below threshold (<= 10): static routes stay distinct
        var staticMap = EndpointGrouper.BuildMap(observed);
        Assert.Equal("GET /catalog/featured", staticMap[("GET /catalog/featured", "shop.test")]);
        Assert.Equal("GET /catalog/sale", staticMap[("GET /catalog/sale", "shop.test")]);

        // Above threshold (> 10): slugs collapse to {id}; single host omits host
        for (var i = 1; i <= 12; i++)
            observed.Add(($"GET /catalog/product-slug-{i:00}", "shop.test"));
        var collapsed = EndpointGrouper.BuildMap(observed);
        Assert.Equal("GET /catalog/{id}", collapsed[("GET /catalog/featured", "shop.test")]);
        Assert.Equal("GET /catalog/{id}", collapsed[("GET /catalog/product-slug-12", "shop.test")]);

        // Multiple hosts disambiguate
        observed.Add(("GET /health", "auth.test"));
        var multiHost = EndpointGrouper.BuildMap(observed);
        Assert.Equal("GET shop.test/catalog/{id}", multiHost[("GET /catalog/featured", "shop.test")]);
        Assert.Equal("GET auth.test/health", multiHost[("GET /health", "auth.test")]);
    }

    [Fact]
    public void CallThresholds_resolves_exact_path_wildcard_and_default_rules()
    {
        var thresholds = new CallThresholds(TimeSpan.FromMilliseconds(800))
            .For("POST /api/payments", TimeSpan.FromSeconds(2))
            .For("/products/{id}/price", TimeSpan.FromMilliseconds(150))
            .For("GET /search*", TimeSpan.FromMilliseconds(450));

        Assert.Equal(2000, thresholds.ResolveMs("POST /api/payments"));
        Assert.Equal(150, thresholds.ResolveMs("GET /products/{id}/price"));
        Assert.Equal(150, thresholds.ResolveMs("GET /products/991/price"));
        Assert.Equal(450, thresholds.ResolveMs("GET /search?q=shoes"));
        Assert.Equal(800, thresholds.ResolveMs("GET /other"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CallThresholds.None.WithDefault(TimeSpan.Zero));
    }

    [Fact]
    public void SlowCallRule_evaluates_custom_threshold_and_graded_ratio()
    {
        Assert.False(SlowCallRule.IsSlow(400, typicalMs: 100, customThresholdMs: 500));
        Assert.True(SlowCallRule.IsSlow(550, typicalMs: 500, customThresholdMs: 500));

        var midRatio = SlowCallRule.Ratio(300, typicalMs: 100, customThresholdMs: 500);
        Assert.Equal(1.5, midRatio, 6);
        Assert.Equal(SlowCallRule.MaxStep, SlowCallRule.Step(SlowCallRule.Ratio(600, typicalMs: 100, customThresholdMs: 500)));
    }

    [Fact]
    public async Task Auto_tracks_plain_new_HttpClient_and_deduplicates_RecordingHttpHandler()
    {
        await using var server = await LoopbackHttpServer.StartAsync();
        var calls = new List<RecordedCall>();

        // Outside ScenarioContext.Activate(), plain HttpClient must NOT record anything.
        using (var external = new HttpClient())
        {
            var r0 = await external.GetAsync(server.Url("/outside-context"));
            Assert.Equal(HttpStatusCode.OK, r0.StatusCode);
        }
        Assert.Empty(calls);

        using var recordingHandler = new RecordingHttpHandler(new SocketsHttpHandler());
        using var ctxHttp = new HttpClient(recordingHandler);
        var thresholds = new CallThresholds(TimeSpan.FromMilliseconds(900))
            .For("GET /orders/{id}", TimeSpan.FromMilliseconds(250));
        var ctx = new ScenarioContext(ctxHttp, callSink: calls.Add, thresholds: thresholds);

        using (ctx.Activate())
        {
            // 1. User creates their own new HttpClient() (not ctx.Http) -> tracked automatically via DiagnosticListener!
            using var customHttp = new HttpClient();
            await customHttp.GetAsync(server.Url("/orders/1042?full=true"));

            // 2. ctx.Http (has RecordingHttpHandler) -> tracked ONCE (no double-counting with DiagnosticListener)!
            await ctx.Http.PostAsync(server.Url("/cart/cart-9"), new StringContent("{}", Encoding.UTF8, "application/json"));

            // 3. CallScope + .AsCall + .SlowAbove overrides
            using (ctx.CallScope("Custom typed call", slowAbove: TimeSpan.FromMilliseconds(120)))
            {
                await customHttp.GetAsync(server.Url("/typed/endpoint"));
            }

            using var explicitReq = new HttpRequestMessage(HttpMethod.Put, server.Url("/items/77"))
                .AsCall("Update item")
                .SlowAbove(TimeSpan.FromMilliseconds(333));
            await customHttp.SendAsync(explicitReq);
        }

        Assert.Equal(4, calls.Count);

        Assert.Equal("GET /orders/{id}", calls[0].Endpoint);
        Assert.Equal("GET /orders/1042?full=true", calls[0].Name);
        Assert.Equal("GET", calls[0].Method);
        Assert.Equal(250, calls[0].SlowThresholdMs);
        Assert.Equal(200, calls[0].StatusCode);

        Assert.Equal("POST /cart/{id}", calls[1].Endpoint);
        Assert.Equal(900, calls[1].SlowThresholdMs);

        Assert.Equal("Custom typed call", calls[2].Endpoint);
        Assert.Equal(120, calls[2].SlowThresholdMs);

        Assert.Equal("Update item", calls[3].Endpoint);
        Assert.Equal(333, calls[3].SlowThresholdMs);
    }

    private sealed class LoopbackHttpServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly Task acceptTask;

        private LoopbackHttpServer(TcpListener listener)
        {
            this.listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptTask = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public Uri Url(string path) => new($"http://127.0.0.1:{Port}{path}");

        public static Task<LoopbackHttpServer> StartAsync()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return Task.FromResult(new LoopbackHttpServer(l));
        }

        private async Task AcceptLoopAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cts.Token); }
                catch { break; }

                _ = Task.Run(async () =>
                {
                    using (client)
                    await using (var stream = client.GetStream())
                    {
                        var buf = new byte[4096];
                        try { _ = await stream.ReadAsync(buf, cts.Token); } catch { return; }
                        var resp = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();
                        try { await stream.WriteAsync(resp, cts.Token); } catch { }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            listener.Stop();
            try { await acceptTask; } catch { }
            cts.Dispose();
        }
    }
}
