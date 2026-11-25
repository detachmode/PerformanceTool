using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beesharp.Core.Tests;

/// <summary>
/// End-to-end stories for automatic HTTP failure/cancellation tracking via <c>HttpCallDiagnosticTracker</c> (brain 0060),
/// compiled/delegate run program naming (<see cref="PreflightProgram"/>, <see cref="ShutdownProgram"/>),
/// and clean stack-trace formatting (<see cref="ScenarioExceptionFormatter"/>).
/// </summary>
public sealed class HttpCallDiagnosticTrackerTests
{
    private sealed class WarmupApiCachePreflight : PreflightProgram
    {
        public override Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FlushDBShutdownProgram : ShutdownProgram
    {
        public override Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Automatic_http_tracking_captures_network_errors_http_500_and_canceled_requests()
    {
        var calls = new ConcurrentQueue<RecordedCall>();
        var stopped = false;

        // Per-request extension guards reject non-positive thresholds and blank names.
        using var badReq = new HttpRequestMessage(HttpMethod.Get, "https://shop.test/");
        Assert.Throws<ArgumentOutOfRangeException>(() => badReq.SlowAbove(TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => badReq.AsCall("   "));

        await using var flakyServer = await FlakyLoopbackServer.StartAsync();

        // Pick an unbound ephemeral port so the connection is refused deterministically.
        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using var ctxHttp = new HttpClient();
        var ctx = new ScenarioContext(ctxHttp, callSink: calls.Enqueue, runStopped: () => stopped);

        using (ctx.Activate())
        {
            using var plainHttp = new HttpClient();

            // 1. Server replies with HTTP 500 -> recorded with status 500 and error "HTTP 500".
            using var errResp = await plainHttp.GetAsync(flakyServer.Url("/error-500"));
            Assert.Equal(HttpStatusCode.InternalServerError, errResp.StatusCode);

            // 2. Network connection refused -> OnException + OnStop records the socket/HTTP failure with null status.
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                plainHttp.GetAsync($"http://127.0.0.1:{closedPort}/unreachable"));

            // 3. Request canceled by caller while run is still running -> records cancellation error message.
            using (var userCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    plainHttp.GetAsync(flakyServer.Url("/hang"), userCts.Token));
            }

            // 4. Request canceled because the run was stopped -> error is normalized to ExecutionResult.CanceledError (brain 0053).
            stopped = true;
            using (var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    plainHttp.GetAsync(flakyServer.Url("/hang"), stopCts.Token));
            }
        }

        var recorded = calls.ToArray();
        Assert.Equal(4, recorded.Length);

        Assert.Equal(500, recorded[0].StatusCode);
        Assert.Equal("HTTP 500", recorded[0].Error);

        Assert.Null(recorded[1].StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(recorded[1].Error));

        Assert.Null(recorded[2].StatusCode);
        Assert.NotNull(recorded[2].Error);
        Assert.NotEqual(ExecutionResult.CanceledError, recorded[2].Error);

        Assert.Null(recorded[3].StatusCode);
        Assert.Equal(ExecutionResult.CanceledError, recorded[3].Error);
    }

    [Fact]
    public async Task Compiled_and_delegate_run_programs_derive_readable_ids_names_and_context_aliases()
    {
        var preflight = new WarmupApiCachePreflight();
        Assert.Equal("warmup-api-cache", preflight.Id);
        Assert.Equal("Warmup api cache", preflight.Name);

        var shutdown = new FlushDBShutdownProgram();
        Assert.Equal("flush-db", shutdown.Id);
        Assert.Equal("Flush DB", shutdown.Name);

        var observedEnv = "";
        var delegatePre = new DelegatePreflightProgram("  seed-tenant ", " Seed Tenant ", (ctx, _) =>
        {
            observedEnv = ctx.Env["Tenant"];
            ctx.Data["seeded"] = true;
            return Task.CompletedTask;
        });
        var delegateShut = new DelegateShutdownProgram(" clean-tenant ", " Clean Tenant ", (ctx, _) =>
        {
            ctx.Data["cleaned"] = ctx.Data.Get<bool>("seeded");
            return Task.CompletedTask;
        });

        Assert.Equal("seed-tenant", delegatePre.Id);
        Assert.Equal("Seed Tenant", delegatePre.Name);
        Assert.Equal("clean-tenant", delegateShut.Id);
        Assert.Equal("Clean Tenant", delegateShut.Name);

        Assert.Throws<ArgumentException>(() => new DelegatePreflightProgram(" ", "Name", (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentException>(() => new DelegateShutdownProgram("id", " ", (_, _) => Task.CompletedTask));

        using var http = new HttpClient();
        var runData = new RunData(1);
        var env = new EnvironmentValues("Staging", new Dictionary<string, string> { ["Tenant"] = "acme" });
        var context = new ProgramContext
        {
            Http = http,
            Logger = NullLogger.Instance,
            Environment = env,
            Data = runData.RunBagFor(new DataWriter(DataWriterKind.Preflight)),
            CancellationToken = CancellationToken.None,
            Log = _ => { },
            Fail = _ => { },
            RunProcessAsync = (_, _) => Task.FromResult(0),
        };

        await delegatePre.ExecuteAsync(context, CancellationToken.None);
        await delegateShut.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal("acme", observedEnv);
        Assert.True(context.Data.Get<bool>("cleaned"));
    }

    [Fact]
    public void Scenario_exception_formatter_keeps_scenario_frames_and_inner_exceptions_while_hiding_plumbing()
    {
        Exception captured;
        try
        {
            OuterScenarioHelper();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        // Passing this test assembly as the scenario assembly isolates its user frames and formats inner exceptions cleanly.
        var formatted = ScenarioExceptionFormatter.Format(captured, typeof(HttpCallDiagnosticTrackerTests).Assembly);

        Assert.Contains("System.InvalidOperationException: Checkout step failed", formatted);
        Assert.Contains("---> System.TimeoutException: Payment gateway timed out", formatted);
        Assert.Contains("--- End of inner exception stack trace ---", formatted);
        Assert.Contains(nameof(OuterScenarioHelper), formatted);
        Assert.Contains(nameof(InnerPaymentCall), formatted);

        // With null scenarioAssembly (Roslyn fallback path), the formatter still emits a clean trace.
        var fallback = ScenarioExceptionFormatter.Format(captured, scenarioAssembly: null);
        Assert.Contains("Checkout step failed", fallback);
        Assert.Contains(nameof(OuterScenarioHelper), fallback);
    }

    private static void OuterScenarioHelper()
    {
        try
        {
            InnerPaymentCall();
        }
        catch (Exception inner)
        {
            throw new InvalidOperationException("Checkout step failed", inner);
        }
    }

    private static void InnerPaymentCall() =>
        throw new TimeoutException("Payment gateway timed out");

    private sealed class FlakyLoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly Task acceptTask;

        private FlakyLoopbackServer(TcpListener listener)
        {
            this.listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptTask = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }
        public Uri Url(string path) => new($"http://127.0.0.1:{Port}{path}");

        public static Task<FlakyLoopbackServer> StartAsync()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return Task.FromResult(new FlakyLoopbackServer(l));
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
                        try { read = await stream.ReadAsync(buf, cts.Token); }
                        catch { return; }

                        var reqText = System.Text.Encoding.ASCII.GetString(buf, 0, read);
                        if (reqText.Contains("/hang", StringComparison.Ordinal))
                        {
                            try { await Task.Delay(TimeSpan.FromSeconds(10), cts.Token); } catch { }
                            return;
                        }

                        var resp = reqText.Contains("/error-500", StringComparison.Ordinal)
                            ? "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 4\r\nConnection: close\r\n\r\nfail"u8.ToArray()
                            : "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();
                        try { await stream.WriteAsync(resp, cts.Token); } catch { }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            listener.Dispose();
            try { await acceptTask; } catch { }
            cts.Dispose();
        }
    }
}
