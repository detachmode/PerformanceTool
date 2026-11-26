using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.TraceStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Tests;

/// <summary>The REST endpoints behind <c>beesharp runs trace --url</c> and <c>runs hotspots --url</c> (brain 0065), against a live in-process host whose run fetched traces.</summary>
public sealed class HostTraceEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beesharp-trace-api-{Guid.NewGuid():N}");

    public HostTraceEndpointsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private sealed record Params(int DelayMs = 20);

    private sealed class CallScenario : Scenario<Params>
    {
        public override string Id => "call-scenario";
        public override string Name => "Call scenario";
        public override string Description => "One measured call per bee.";

        protected override async Task<ScenarioOutcome> ExecuteAsync(Params p, ScenarioContext ctx, CancellationToken ct)
        {
            await ctx.MeasureCallAsync("GET /items/1", () => Task.Delay(p.DelayMs, ct));
            return new ScenarioOutcome(200, 0);
        }
    }

    private sealed class FakeStore : ITraceStore
    {
        public string Kind => "fake";
        public string Description => "Fake store";

        public Task<TraceLookup> GetTraceAsync(string traceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            Task.FromResult(new TraceLookup(TraceLookupOutcome.Found,
                [new TraceSpanDto(traceId, "00000000000000a1", null, "beesharp", "bee", "internal", 1_000_000_000, 30), new TraceSpanDto(traceId, "00000000000000a2", "00000000000000a1", "items-api", "GET /items", "server", 1_001_000_000, 20)]));
    }

    private sealed class FakeFactory : ITraceStoreFactory
    {
        public ITraceStore Create(TraceStoreSettings settings) => new FakeStore();
    }

    [Fact]
    public async Task A_run_that_fetched_traces_serves_them_as_a_trace_view_and_as_hotspots_with_status_codes_for_the_mistakes()
    {
        var builder = BeesharpApp.CreateBuilder();
        builder.WebApplicationBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        });
        builder.Services.AddSingleton<ITraceStoreFactory>(new FakeFactory());
        builder.Services.AddSingleton(new TraceFetchOptions { SettleDelay = TimeSpan.Zero, PollInterval = TimeSpan.FromMilliseconds(20), RetryDelay = TimeSpan.FromMilliseconds(30), RequestInterval = TimeSpan.FromMilliseconds(1), TypicalMinSample = 2 });
        builder.AddScenario<CallScenario>();
        builder.AddPreset("Calls", p => p.Duration(TimeSpan.FromSeconds(1)).Add<CallScenario>(new Params(), ratePerMinute: 300, label: "Calls")).AsDefault();

        await using var app = builder.Build();
        await app.InitializeAsync();
        await app.WebApplication.StartAsync();
        try
        {
            var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            (await http.PutAsJsonAsync("/api/environments/Dev", new EnvironmentDefinition { Name = "Dev", Variables = [new("TraceStore.Url", "http://trace.test")] }, Json)).EnsureSuccessStatusCode();

            (await http.PostAsJsonAsync("/api/runs/start", new StartRunRequest(null, "Dev"), Json)).EnsureSuccessStatusCode();
            var runs = app.Services.GetRequiredService<RunManager>();
            await runs.WaitUntilFinishedAsync();
            var runId = runs.GetCurrent().RunId;

            var view = await http.GetFromJsonAsync<TraceViewDto>($"/api/runs/{runId}/traces/1?maxSpans=10", Json);
            Assert.True(view!.Found);
            Assert.Equal(("B1", 2, "bee"), (view.BeeName, view.TotalSpans, view.ReferenceKind));
            Assert.Equal(["beesharp", "items-api"], view.Services);
            var byId = await http.GetFromJsonAsync<TraceViewDto>($"/api/runs/{runId}/traces/{view.TraceId}", Json);
            Assert.Equal(view.TraceId, byId!.TraceId);

            var hotspots = await http.GetFromJsonAsync<HotspotsResultDto>($"/api/runs/{runId}/hotspots?limit=5", Json);
            Assert.Matches(@"traces: (\d+) of \1 found", hotspots!.Coverage.Line);

            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/runs/{runId}/traces/9999")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/runs/does-not-exist/traces/1")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/runs/does-not-exist/hotspots")).StatusCode);
            var bad = await http.GetAsync($"/api/runs/{runId}/traces/banana");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("neither a bee", await bad.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"/api/runs/{runId}/traces/1?call=9")).StatusCode);
        }
        finally
        {
            await app.WebApplication.StopAsync();
        }
    }
}
