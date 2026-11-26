using System.Diagnostics;
using System.Net;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Tracing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Beesharp.Tracing.Tests;

/// <summary>What leaves Beesharp (brain 0063): actor, step and call spans, only when export is configured, and never a reason for a run to fail or wait.</summary>
public class TracingExportTests
{
    public sealed record Params;
    public sealed record SignIn;
    public sealed record Pay;
    public union TwoSteps(SignIn, Pay);

    private sealed class TwoStepScenario : Scenario<Params, TwoSteps>
    {
        public override string Id => "two-steps";
        public override string Name => "Two steps";
        public override string Description => "Test";
        protected override TwoSteps StartStep(Params p) => new SignIn();

        protected override async Task<TwoSteps?> RunStepAsync(TwoSteps step, Params p, ScenarioContext ctx, CancellationToken ct)
        {
            switch (step)
            {
                case SignIn:
                    await ctx.Http.GetAsync("http://shop.test/login", ct);
                    return new Pay();
                default:
                    await ctx.Http.PostAsync("http://shop.test/pay", new StringContent("{}"), ct);
                    return null;
            }
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static async Task<string> PlayActorAsync()
    {
        var scenario = new TwoStepScenario();
        using var trace = ActorTrace.Start(new ActorTraceInfo("Checkout", "run-1", scenario.Id, 1, 1, scenario.Steps))!;
        using var http = new HttpClient(new RecordingHttpHandler(new StubHandler()));
        var ctx = new ScenarioContext(http, trace: trace);
        using (ctx.Activate())
            await ((IScenario)scenario).ExecuteAsync(new Params(), ctx, CancellationToken.None);
        trace.Complete(true, null, false);
        return trace.TraceId;
    }

    [Fact]
    public void Export_is_off_by_default_nothing_is_registered()
    {
        var services = new ServiceCollection().AddBeesharpTracing(Config());
        using var provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<TracingSettings>().Enabled);
        Assert.Null(provider.GetService<TracerProvider>());
        Assert.Null(TracingRegistration.BuildBeesharpTracerProvider(TracingSettings.Off));
    }

    [Fact]
    public async Task Calls_still_get_a_trace_id_when_no_exporter_is_configured()
    {
        // No tracer provider at all: the always-on listener inside Beesharp keeps the trace context.
        var traceId = await PlayActorAsync();
        Assert.Matches("^[0-9a-f]{32}$", traceId);
    }

    [Fact]
    public async Task With_an_exporter_the_actor_step_and_call_spans_are_exported_and_nothing_else()
    {
        var exported = new List<Activity>();
        var services = new ServiceCollection().AddBeesharpTracing(Config(), b => b.AddInMemoryExporter(exported));
        using var provider = services.BuildServiceProvider();
        using var tracer = provider.GetRequiredService<TracerProvider>();

        // Traffic that is not an actor's: another source, and an HttpClient request outside any actor (Beesharp's own UI/API/assistant traffic looks like this).
        using (var other = new ActivitySource("Microsoft.AspNetCore"))
        using (other.StartActivity("GET /api/runs/current"))
            await new HttpClient(new StubHandler()).GetAsync("http://assistant.test/v1/chat");

        var traceId = await PlayActorAsync();
        tracer.ForceFlush();

        Activity[] spans;
        lock (exported) spans = exported.ToArray();
        Assert.All(spans, s => Assert.Equal(BeesharpTracing.SourceName, s.Source.Name));
        Assert.All(spans, s => Assert.Equal(traceId, s.TraceId.ToHexString()));
        Assert.Equal(5, spans.Length); // actor, 2 steps, 2 calls
        var actor = Assert.Single(spans, s => s.ParentSpanId == default);
        Assert.Equal("Checkout", actor.DisplayName);
        var steps = spans.Where(s => s.ParentSpanId == actor.SpanId).ToArray();
        Assert.Equal(2, steps.Length);
        Assert.Equal(2, spans.Count(s => steps.Any(st => st.SpanId == s.ParentSpanId)));
        Assert.All(spans, s => Assert.True((s.ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0));
    }

    [Fact]
    public async Task A_collector_that_is_down_never_fails_or_slows_the_actor()
    {
        var config = Config(("Tracing:OtlpEndpoint", "http://127.0.0.1:9"));
        var services = new ServiceCollection().AddBeesharpTracing(config);
        using var provider = services.BuildServiceProvider();
        var tracer = provider.GetRequiredService<TracerProvider>();

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) await PlayActorAsync();
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"20 actors took {watch.Elapsed}: the exporter must not be in their way");

        // Shutting down gives up on the dead collector after its 3 second timeout instead of hanging.
        var shutdown = Stopwatch.StartNew();
        tracer.Dispose();
        Assert.True(shutdown.Elapsed < TimeSpan.FromSeconds(30), $"shutdown took {shutdown.Elapsed}");
    }

    [Fact]
    public void Without_a_host_the_same_provider_can_be_built_for_the_command_line_tool()
    {
        var exported = new List<Activity>();
        using var tracer = TracingRegistration.BuildBeesharpTracerProvider(TracingSettings.Off, b => b.AddInMemoryExporter(exported));
        Assert.NotNull(tracer);
    }
}
