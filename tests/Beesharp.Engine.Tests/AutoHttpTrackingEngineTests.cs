using System.Net;
using System.Net.Sockets;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;

namespace Beesharp.Engine.Tests;

public class AutoHttpTrackingEngineTests
{
    private sealed record EmptyParams;

    private sealed class CustomNewHttpClientScenario(int port) : Scenario<EmptyParams>
    {
        public override string Id => "custom-new-http";
        public override string Name => "Custom New HttpClient";
        public override string Description => "Uses new HttpClient() directly without ctx.Http or MeasureCallAsync.";

        public override CallThresholds SlowThresholds { get; } = new CallThresholds(TimeSpan.FromMilliseconds(500))
            .For("GET /slow-endpoint", TimeSpan.FromMilliseconds(35));

        protected override async Task<ScenarioOutcome> ExecuteAsync(EmptyParams p, ScenarioContext ctx, CancellationToken ct)
        {
            using var myClient = new HttpClient();
            var r1 = await myClient.GetAsync($"http://127.0.0.1:{port}/fast/{Random.Shared.Next(100, 999)}", ct);
            var r2 = await myClient.GetAsync($"http://127.0.0.1:{port}/slow-endpoint", ct);

            // Also test ctx.Http + CallScope to verify zero double-counting
            using (ctx.CallScope(slowAbove: TimeSpan.FromMilliseconds(40)))
                await ctx.Http.GetAsync($"http://127.0.0.1:{port}/scoped", ct);

            return new ScenarioOutcome((int)r2.StatusCode, (r1.Content.Headers.ContentLength ?? 0) + (r2.Content.Headers.ContentLength ?? 0));
        }
    }

    [Fact]
    public async Task LoadRunner_and_RunManager_automatically_track_custom_new_HttpClient_and_apply_custom_thresholds()
    {
        await using var server = await LoopbackSlowServer.StartAsync();
        var scenario = new CustomNewHttpClientScenario(server.Port);
        var catalog = new ScenarioCatalog([scenario]);
        var storage = new InMemoryRunStorage();
        var envs = new EnvironmentStore(Path.Combine(Path.GetTempPath(), $"beesharp-env-auto-{Guid.NewGuid():N}"));
        var manager = new RunManager(new LoadRunner(catalog), envs, storage);

        var entry = ScenarioRunConfig.CreateFor(scenario);
        entry.RatePerMinute = 180;
        var plan = new LoadPlan { DurationSeconds = 1, MaxConcurrency = 5, TimeoutSeconds = 10, Scenarios = [entry] };

        await manager.StartAsync(plan, null);
        await manager.WaitUntilFinishedAsync();

        var status = manager.GetCurrent();
        Assert.NotEmpty(status.NewBees);
        var runId = status.RunId;

        var firstDetail = await storage.GetBeeDetailAsync(runId, status.NewBees[0].Sequence);
        Assert.NotNull(firstDetail);
        // Exactly 3 calls per bee (2 via new HttpClient(), 1 via ctx.Http) - zero double counting!
        Assert.Equal(3, firstDetail!.Calls.Count);
        Assert.Equal(["GET /fast/{id}", "GET /slow-endpoint", "GET /scoped"], firstDetail.Calls.Select(c => c.Endpoint ?? "").ToArray());

        var endpoints = await storage.GetCallEndpointsAsync(runId, new CallEndpointsQuery());
        Assert.Equal(3, endpoints.Endpoints.Count);

        var slowEp = endpoints.Endpoints.Single(e => e.Endpoint == "GET /slow-endpoint");
        Assert.True(slowEp.HasCustomThreshold);
        Assert.Equal(35, slowEp.SlowThresholdMs);
        Assert.True(slowEp.SlowCount > 0);
        Assert.True(status.Scenarios[0].SlowCalls > 0);
    }

    private sealed class LoopbackSlowServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly Task acceptTask;

        private LoopbackSlowServer(TcpListener listener)
        {
            this.listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptTask = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public static Task<LoopbackSlowServer> StartAsync()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return Task.FromResult(new LoopbackSlowServer(l));
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
                        int read;
                        try { read = await stream.ReadAsync(buf, cts.Token); } catch { return; }
                        var reqText = System.Text.Encoding.ASCII.GetString(buf, 0, read);
                        if (reqText.Contains("/slow-endpoint", StringComparison.Ordinal))
                            await Task.Delay(75, cts.Token);
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
