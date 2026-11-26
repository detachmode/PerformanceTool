using System.Text.Json;
using Beesharp.Cli;
using Beesharp.Contracts;

namespace Beesharp.Tracing.E2E.Tests;

/// <summary>
/// Fetching the services' traces into the run (brain 0065), end to end: the run's environment names the test's receiver as trace store (it answers Jaeger's
/// <c>GET /api/v3/traces/{id}</c>), the real Beesharp fetches the traces in the background, and the CLI shows them from the running server.
/// Nothing depends on random behaviour (slow queries, payment failures): a slow query only changes which span is the hotspot, never what is asserted.
/// </summary>
public sealed class FetchedTracesTests(TracingStack stack)
    : IClassFixture<TracingStack>
{
    private async Task<(int Exit, string Out, string Err)> CliAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApp.RunAsync([.. args, "--url", stack.BeesharpUrl], output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private async Task<HotspotsResultDto> HotspotsAsync()
    {
        var result = await CliAsync("runs", "hotspots", stack.RunId, "--json", "--limit", "20");
        Assert.True(result.Exit == 0, result.Err);
        return JsonSerializer.Deserialize<HotspotsResultDto>(result.Out, Web)!;
    }

    [Fact]
    public async Task The_traces_were_fetched_into_the_run_and_the_hosts_log_says_what_happened()
    {
        var log = await stack.HostLogAsync();
        Assert.Contains(log, l => l.StartsWith("[Traces] Trace store: Jaeger at ") && l.Contains(stack.Receiver.Endpoint));
        Assert.Contains(log, l => l.StartsWith("[Traces] fetched "));

        var coverage = (await HotspotsAsync()).Coverage;
        Assert.True(coverage.Selected > 0, "traces were selected");
        Assert.True(coverage.Found == coverage.Selected, $"every selected trace was found in the store: {coverage.Line}");
    }

    [Fact]
    public async Task Hotspots_show_where_the_time_of_the_calls_went_in_shop_api()
    {
        var hotspots = await HotspotsAsync();

        var endpoints = hotspots.Endpoints.Select(e => e.Endpoint).ToList();
        Assert.Contains("GET /products/{id}", endpoints);
        Assert.Contains("POST /pay", endpoints);
        var services = hotspots.Endpoints.SelectMany(e => e.Hotspots).Select(h => h.Service).Distinct().ToList();
        Assert.Contains("shop-api", services);
        Assert.All(services, s => Assert.Contains(s, new[] { "shop-api", "payment-api", TraceAnalytics.OutsideService }));
        Assert.All(hotspots.Endpoints.SelectMany(e => e.Hotspots), h => Assert.True(h.MedianSelfMs > 0 && h.ExampleTraceIds.Count > 0));

        var text = await CliAsync("runs", "hotspots", stack.RunId);
        Assert.Equal(0, text.Exit);
        Assert.Contains("GET /products/{id}", text.Out);
        Assert.Contains("shop-api", text.Out);
        Assert.Contains("beesharp runs trace", text.Out);
    }

    [Fact]
    public async Task A_fetched_trace_shows_the_spans_of_shop_api_and_payment_api_below_the_calls_exactly_as_the_services_exported_them()
    {
        var traceId = (await HotspotsAsync()).Endpoints.SelectMany(e => e.Hotspots).SelectMany(h => h.ExampleTraceIds).First();

        var result = await CliAsync("runs", "trace", stack.RunId, traceId, "--json", "--max-spans", "500");
        Assert.Equal(0, result.Exit);
        var view = JsonSerializer.Deserialize<TraceViewDto>(result.Out, Web)!;
        Assert.True(view.Found);
        Assert.Equal(["beesharp", "payment-api", "shop-api"], view.Services);

        var exported = stack.Receiver.Snapshot().Where(s => s.TraceId == traceId).ToList();
        Assert.Equal(exported.Select(s => s.SpanId).Order(), view.Spans.Select(s => s.SpanId).Order());   // the run holds the whole trace
        Assert.Contains(view.Spans, s => s.Service == "shop-api" && s.Name == "GET /search");
        Assert.Contains(view.Spans, s => s.Service == "payment-api" && s.Name == "POST /charge");
        Assert.All(view.Spans, s => Assert.True(s.SelfMs >= 0 && s.SelfMs <= s.DurationMs + 0.001));

        var text = await CliAsync("runs", "trace", stack.RunId, traceId);
        Assert.Equal(0, text.Exit);
        Assert.Contains("shop-api", text.Out);
        Assert.Contains("payment-api", text.Out);
        Assert.Contains("db.query.text=", text.Out);
    }

    [Fact]
    public async Task One_call_of_a_bee_shows_only_what_happened_below_it()
    {
        // Not every bee is selected: ask by trace ID for one that was, then its Pay call (the 6th).
        var selected = (await HotspotsAsync()).Endpoints.SelectMany(e => e.Hotspots).SelectMany(h => h.ExampleTraceIds).First();
        var pay = await CliAsync("runs", "trace", stack.RunId, selected, "--call", "6", "--json", "--max-spans", "100");
        Assert.Equal(0, pay.Exit);
        var view = JsonSerializer.Deserialize<TraceViewDto>(pay.Out, Web)!;
        Assert.Equal(("call", 6, "POST /pay"), (view.ReferenceKind, view.Call, view.Endpoint));
        Assert.Contains(view.Spans, s => s.Service == "payment-api" && s.Name == "POST /charge");
        Assert.DoesNotContain(view.Spans, s => s.Name == "GET /login");
    }

    [Fact]
    public async Task A_bee_number_that_does_not_exist_is_an_error_not_a_missing_trace()
    {
        Assert.Equal(2, (await CliAsync("runs", "trace", stack.RunId, "9999")).Exit);
    }
}
