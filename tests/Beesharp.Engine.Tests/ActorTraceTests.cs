using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;
using Beesharp.Services.Tracing;

namespace Beesharp.Engine.Tests;

/// <summary>One actor = one trace (brain 0062): the services under test receive the actor's trace in <c>traceparent</c>, with or without an exporter.</summary>
public class ActorTraceTests
{
    public sealed record TraceParams(bool FailAtPay = false);
    public sealed record LoginStep;
    public sealed record BrowseStep;
    public sealed record PayStep;
    public union TraceSteps(LoginStep, BrowseStep, PayStep);

    /// <summary>Login uses <c>ctx.Http</c>, Browse a plain <c>new HttpClient()</c>, Pay <c>MeasureCallAsync</c> around <c>ctx.Http</c>.</summary>
    private sealed class TraceScenario(int port) : Scenario<TraceParams, TraceSteps>
    {
        public override string Id => "trace-scenario";
        public override string Name => "Trace scenario";
        public override string Description => "Test";

        protected override TraceSteps StartStep(TraceParams p) => new LoginStep();

        protected override async Task<TraceSteps?> RunStepAsync(TraceSteps step, TraceParams p, ScenarioContext ctx, CancellationToken ct)
        {
            switch (step)
            {
                case LoginStep:
                    await ctx.Http.GetAsync($"http://127.0.0.1:{port}/login", ct);
                    return new BrowseStep();
                case BrowseStep:
                    using (var plain = new HttpClient())
                        await plain.GetAsync($"http://127.0.0.1:{port}/browse/42", ct);
                    return new PayStep();
                default:
                    await ctx.MeasureCallAsync("pay", async () =>
                    {
                        var response = await ctx.Http.PostAsync($"http://127.0.0.1:{port}/pay", new StringContent("{}"), ct);
                        if (p.FailAtPay) throw new InvalidOperationException("payment declined");
                        _ = response;
                    });
                    return null;
            }
        }
    }

    private sealed record Traceparent(string TraceId, string SpanId, string Flags)
    {
        public static Traceparent Parse(string header)
        {
            var parts = header.Split('-');
            Assert.Equal(4, parts.Length);
            Assert.Equal("00", parts[0]);
            return new Traceparent(parts[1], parts[2], parts[3]);
        }

        public bool Sampled => (Convert.ToInt32(Flags, 16) & 1) == 1;
    }

    private static async Task<(RunManager Manager, InMemoryRunStorage Storage, LiveRunStatusDto Status)> RunAsync(int port, bool failAtPay = false, int actors = 3)
    {
        var scenario = new TraceScenario(port);
        var storage = new InMemoryRunStorage();
        var envs = new EnvironmentStore(Path.Combine(Path.GetTempPath(), $"beesharp-env-trace-{Guid.NewGuid():N}"));
        var manager = new RunManager(new LoadRunner(new ScenarioCatalog([scenario])), envs, storage);
        var entry = ScenarioRunConfig.CreateFor(scenario);
        entry.Label = "Checkout";
        entry.RatePerMinute = actors * 60;
        entry.Parameters["failAtPay"] = failAtPay;
        var plan = new LoadPlan { DurationSeconds = 1, MaxConcurrency = 10, TimeoutSeconds = 10, Scenarios = [entry] };
        await manager.StartAsync(plan, null);
        await manager.WaitUntilFinishedAsync();
        return (manager, storage, manager.GetCurrent());
    }

    [Fact]
    public async Task Every_actor_has_its_own_trace_and_its_calls_send_that_trace_with_the_sampled_flag_even_without_an_exporter()
    {
        await using var server = RecordingServer.Start();
        var (_, storage, status) = await RunAsync(server.Port);

        Assert.Equal(3, status.NewBees.Count);
        var traceIds = new HashSet<string>();
        var spanIds = new HashSet<string>();
        var httpSpanIds = new HashSet<string>();
        foreach (var bee in status.NewBees)
        {
            var detail = await storage.GetBeeDetailAsync(status.RunId, bee.Sequence);
            Assert.NotNull(detail);
            Assert.Matches("^[0-9a-f]{32}$", detail!.TraceId);
            Assert.True(traceIds.Add(detail.TraceId!), "a trace ID is used by one actor only");

            // Pay is MeasureCallAsync around a ctx.Http call: the HTTP call is recorded first, then the measured call around it.
            Assert.Equal(["GET /login", "GET /browse/{id}", "POST /pay", "pay"], detail.Calls.Select(c => c.Endpoint ?? c.Name).ToArray());
            foreach (var call in detail.Calls)
            {
                Assert.Equal(detail.TraceId, call.TraceId);
                Assert.Matches("^[0-9a-f]{16}$", call.SpanId);
                Assert.True(spanIds.Add(call.SpanId!));
                if (call.Url is not null) httpSpanIds.Add(call.SpanId!);
            }
        }

        // The services under test saw exactly the spans of the HTTP calls as the parent of their server spans, all sampled,
        // for all three ways of calling: ctx.Http, a plain new HttpClient() and ctx.Http inside MeasureCallAsync.
        var seen = server.Requests.Select(r => Traceparent.Parse(r.Traceparent ?? "")).ToList();
        Assert.Equal(9, seen.Count);
        Assert.All(seen, t => Assert.True(t.Sampled));
        Assert.Equal(httpSpanIds, seen.Select(t => t.SpanId).ToHashSet());
        Assert.Equal(traceIds, seen.Select(t => t.TraceId).ToHashSet());
        Assert.Equal(3, server.Requests.Count(r => r.Path == "/browse/42"));
    }

    [Fact]
    public async Task Spans_nest_actor_then_step_then_call_and_a_failed_actor_carries_the_error()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == BeesharpTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (stopped) stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        await using var server = RecordingServer.Start();
        var (_, storage, status) = await RunAsync(server.Port, failAtPay: true, actors: 2);
        Assert.Equal(2, status.NewBees.Count);

        foreach (var bee in status.NewBees)
        {
            var detail = (await storage.GetBeeDetailAsync(status.RunId, bee.Sequence))!;
            Activity[] spans;
            lock (stopped) spans = stopped.Where(a => a.TraceId.ToHexString() == detail.TraceId).ToArray();

            var root = Assert.Single(spans, a => a.Parent is null && a.ParentId is null);
            Assert.Equal("Checkout", root.DisplayName);
            Assert.Equal(status.RunId, root.GetTagItem("beesharp.run.id"));
            Assert.Equal(bee.Sequence + 1, root.GetTagItem("beesharp.actor.number"));
            Assert.Equal("failed", root.GetTagItem("beesharp.outcome"));
            Assert.Equal(ActivityStatusCode.Error, root.Status);
            Assert.Equal("payment declined", root.StatusDescription);

            var steps = spans.Where(a => a.ParentSpanId == root.SpanId).OrderBy(a => a.StartTimeUtc).ToArray();
            Assert.Equal(3, steps.Length);
            Assert.Equal(["Login step", "Browse step", "Pay step"], steps.Select(s => s.DisplayName).ToArray());
            Assert.Equal(ActivityStatusCode.Error, steps[2].Status);
            Assert.NotEqual(ActivityStatusCode.Error, steps[0].Status);

            Assert.Equal(4, detail.Calls.Count);
            var measured = spans.Single(a => a.SpanId.ToHexString() == detail.Calls[3].SpanId);
            foreach (var call in detail.Calls)
            {
                var span = Assert.Single(spans, a => a.SpanId.ToHexString() == call.SpanId);
                // The HTTP call made inside the measured call nests below it; every other call hangs below its step.
                Assert.Equal(call == detail.Calls[2] ? measured.SpanId : steps[call.Step].SpanId, span.ParentSpanId);
                Assert.Equal(ActivityKind.Client, span.Kind);
                Assert.Equal(call.Endpoint ?? call.Name, span.DisplayName);
            }

            // The measured pay call failed with the scenario's error; the HTTP call inside it succeeded.
            Assert.Equal(ActivityStatusCode.Error, measured.Status);
            Assert.NotEqual(ActivityStatusCode.Error, spans.Single(a => a.SpanId.ToHexString() == detail.Calls[2].SpanId).Status);
        }
    }

    [Fact]
    public async Task Calls_outside_steps_hang_directly_below_the_actor()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == BeesharpTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (stopped) stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        await using var server = RecordingServer.Start();
        using var trace = ActorTrace.Start(new ActorTraceInfo("Plain", "run-x", "plain", 1, 7));
        Assert.NotNull(trace);
        var calls = new List<RecordedCall>();
        var ctx = new ScenarioContext(new HttpClient(new RecordingHttpHandler(new SocketsHttpHandler())), callSink: calls.Add, trace: trace);
        using (ctx.Activate())
        {
            await ctx.Http.GetAsync($"http://127.0.0.1:{server.Port}/health");
            ctx.RecordCall("GET /simulated", 25);
        }
        trace!.Complete(true, null, false);
        trace.Dispose();

        Assert.Equal(2, calls.Count);
        Activity[] spans;
        lock (stopped) spans = stopped.Where(a => a.TraceId.ToHexString() == trace.TraceId).ToArray();
        var root = Assert.Single(spans, a => a.ParentId is null);
        Assert.Equal("ok", root.GetTagItem("beesharp.outcome"));
        Assert.All(calls, c =>
        {
            Assert.Equal(trace.TraceId, c.TraceId);
            Assert.Equal(root.SpanId, spans.Single(s => s.SpanId.ToHexString() == c.SpanId).ParentSpanId);
        });
        // A call that was recorded after the fact is back-dated to when it started.
        var simulated = spans.Single(s => s.SpanId.ToHexString() == calls[1].SpanId);
        Assert.InRange(simulated.Duration.TotalMilliseconds, 20, 200);
    }

    [Fact]
    public async Task Without_a_trace_nothing_is_stamped_and_no_traceparent_is_made_up()
    {
        await using var server = RecordingServer.Start();
        var calls = new List<RecordedCall>();
        var ctx = new ScenarioContext(new HttpClient(new RecordingHttpHandler(new SocketsHttpHandler())), callSink: calls.Add);
        using (ctx.Activate())
            await ctx.Http.GetAsync($"http://127.0.0.1:{server.Port}/health");

        var call = Assert.Single(calls);
        Assert.Null(call.TraceId);
        Assert.Null(call.SpanId);
        Assert.Null(ctx.TraceId);
    }

    /// <summary>Loopback HTTP server that remembers the path and <c>traceparent</c> of every request.</summary>
    private sealed class RecordingServer : IAsyncDisposable
    {
        public sealed record Seen(string Path, string? Traceparent);

        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new();
        private readonly List<Seen> requests = [];
        private Task? accept;

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public IReadOnlyList<Seen> Requests { get { lock (requests) return requests.ToArray(); } }

        public static RecordingServer Start()
        {
            var s = new RecordingServer();
            s.listener.Start();
            s.accept = Task.Run(s.AcceptLoopAsync);
            return s;
        }

        private async Task AcceptLoopAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cts.Token); }
                catch { break; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var buf = new byte[8192];
                var read = 0;
                try
                {
                    while (!Encoding.ASCII.GetString(buf, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var n = await stream.ReadAsync(buf.AsMemory(read), cts.Token);
                        if (n == 0) return;
                        read += n;
                    }
                }
                catch { return; }

                var lines = Encoding.ASCII.GetString(buf, 0, read).Split("\r\n");
                var path = lines[0].Split(' ')[1];
                var tp = lines.FirstOrDefault(l => l.StartsWith("traceparent:", StringComparison.OrdinalIgnoreCase))?["traceparent:".Length..].Trim();
                lock (requests) requests.Add(new Seen(path, tp));
                try { await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), cts.Token); } catch { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            listener.Stop();
            try { if (accept is not null) await accept; } catch { }
            cts.Dispose();
        }
    }
}
