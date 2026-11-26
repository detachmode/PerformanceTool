using System.Net;
using Beesharp.Services.TraceStore;

namespace Beesharp.TraceStore.Tests;

/// <summary>The Jaeger access (brain 0065) against a fake of the Jaeger 2.23 query API: what it asks, how it reads the answer, and each way the store can fail.</summary>
public class JaegerTraceStoreTests
{
    private const string Trace = "ea078f0886b9c374ee8c344989cccdb1";
    private const long Nanos = 1_795_640_429_000_000_000;

    private static TraceSpanDto Span(string id, string? parent, string service, string name, double startMs, double durationMs, string kind = "internal",
        bool error = false, Dictionary<string, string>? attrs = null) =>
        new(Trace, id, parent, service, name, kind, Nanos + (long)(startMs * 1_000_000), durationMs, error, error ? "payment declined" : null, attrs);

    private static JaegerTraceStore Store(FakeJaeger fake, string? token = null) =>
        new(new TraceStoreSettings(TraceStoreKinds.Jaeger, "http://jaeger.test:16686", token), fake);

    private static readonly DateTimeOffset From = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_trace_is_fetched_by_id_with_the_time_window_and_read_into_spans()
    {
        var fake = new FakeJaeger();
        fake.AddTrace(Trace,
            Span("1111111111111111", null, "beesharp", "Shop journey", 0, 120),
            Span("2222222222222222", "1111111111111111", "shop-api", "GET /products/{id:int}", 5, 100, "server",
                attrs: new() { ["http.response.status_code"] = "200", ["http.route"] = "/products/{id:int}" }),
            Span("3333333333333333", "2222222222222222", "shop-api", "SELECT product", 10, 80, error: true,
                attrs: new() { ["db.query.text"] = "SELECT product" }));
        using var store = Store(fake, token: "s3cret");

        var lookup = await store.GetTraceAsync(Trace, From, From.AddHours(1), CancellationToken.None);

        Assert.Equal(TraceLookupOutcome.Found, lookup.Outcome);
        var request = Assert.Single(fake.Requests);
        Assert.Equal($"/api/v3/traces/{Trace}", request.RequestUri!.AbsolutePath);
        Assert.Contains("start_time=2026-11-26T10%3A00%3A00Z", request.RequestUri.Query);
        Assert.Contains("end_time=2026-11-26T11%3A00%3A00Z", request.RequestUri.Query);
        Assert.Equal("Bearer s3cret", request.Headers.Authorization!.ToString());

        Assert.Equal(3, lookup.Spans.Count);
        var root = lookup.Spans.Single(s => s.ParentSpanId is null);
        Assert.Equal(("beesharp", "Shop journey", "internal"), (root.Service, root.Name, root.Kind));
        Assert.Equal(120, root.DurationMs, 3);
        var server = lookup.Spans.Single(s => s.Kind == "server");
        Assert.Equal("1111111111111111", server.ParentSpanId);
        Assert.Equal("200", server.Attributes!["http.response.status_code"]);
        var db = lookup.Spans.Single(s => s.Name == "SELECT product");
        Assert.True(db.IsError);
        Assert.Equal("payment declined", db.StatusMessage);
        Assert.Equal(Nanos + 10_000_000, db.StartUnixNanos);
        Assert.DoesNotContain(lookup.Spans.SelectMany(s => s.Attributes?.Keys ?? []), k => k.StartsWith("@jaeger@"));   // Jaeger's own warnings are not span data
    }

    [Fact]
    public async Task A_streamed_answer_with_several_json_values_is_read_as_one_trace()
    {
        var fake = new FakeJaeger();
        fake.AddChunkedTrace(Trace,
            [Span("1111111111111111", null, "beesharp", "bee", 0, 50)],
            [Span("2222222222222222", "1111111111111111", "shop-api", "GET /x", 1, 40, "server"), Span("1111111111111111", null, "beesharp", "bee", 0, 50)]);
        using var store = Store(fake);

        var lookup = await store.GetTraceAsync(Trace, From, From.AddHours(1), CancellationToken.None);

        Assert.Equal(TraceLookupOutcome.Found, lookup.Outcome);
        Assert.Equal(2, lookup.Spans.Count);   // the repeated span is kept once
    }

    [Fact]
    public async Task A_trace_the_store_does_not_have_is_not_found_not_an_error()
    {
        var fake = new FakeJaeger();
        using var store = Store(fake);

        var lookup = await store.GetTraceAsync(Trace, From, From.AddHours(1), CancellationToken.None);

        Assert.Equal(TraceLookupOutcome.NotFound, lookup.Outcome);
        Assert.Empty(lookup.Spans);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "TraceStore.Token")]
    [InlineData(HttpStatusCode.Forbidden, "TraceStore.Token")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.BadGateway, "502")]
    public async Task A_store_that_answers_with_an_error_is_reported_as_an_error(HttpStatusCode status, string mentions)
    {
        var fake = new FakeJaeger { Status = status };
        using var store = Store(fake);

        var lookup = await store.GetTraceAsync(Trace, From, From.AddHours(1), CancellationToken.None);

        Assert.Equal(TraceLookupOutcome.Error, lookup.Outcome);
        Assert.Contains(mentions, lookup.Message);
        Assert.Contains("jaeger.test:16686", lookup.Message);
    }

    [Fact]
    public async Task A_store_that_is_not_running_is_unreachable_and_never_throws()
    {
        var fake = new FakeJaeger { Down = true };
        using var store = Store(fake);

        var lookup = await store.GetTraceAsync(Trace, From, From.AddHours(1), CancellationToken.None);

        Assert.Equal(TraceLookupOutcome.Unreachable, lookup.Outcome);
        Assert.Contains("could not be reached", lookup.Message);
    }

    [Fact]
    public async Task Garbage_instead_of_otlp_json_is_an_error()
    {
        var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") });
        using var store = new JaegerTraceStore(new TraceStoreSettings(TraceStoreKinds.Jaeger, "http://jaeger.test"), handler);

        var lookup = await store.GetTraceAsync(Trace, From, From.AddHours(1), CancellationToken.None);

        Assert.Equal(TraceLookupOutcome.Error, lookup.Outcome);
    }

    [Fact]
    public async Task An_answer_without_spans_is_not_found()
    {
        var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"result":{"resourceSpans":[]}}""") });
        using var store = new JaegerTraceStore(new TraceStoreSettings(TraceStoreKinds.Jaeger, "http://jaeger.test"), handler);

        var lookup = await store.GetTraceAsync(Trace, From, From.AddHours(1), CancellationToken.None);

        Assert.Equal(TraceLookupOutcome.NotFound, lookup.Outcome);
    }

    [Fact]
    public async Task A_store_that_does_not_answer_in_time_is_unreachable_but_a_canceled_lookup_stays_canceled()
    {
        var handler = new DelegateHandler(_ => throw new TaskCanceledException("timeout"));
        using var store = new JaegerTraceStore(new TraceStoreSettings(TraceStoreKinds.Jaeger, "http://jaeger.test"), handler);

        var lookup = await store.GetTraceAsync(Trace, From, From.AddHours(1), CancellationToken.None);
        Assert.Equal(TraceLookupOutcome.Unreachable, lookup.Outcome);
        Assert.Contains("in time", lookup.Message);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetTraceAsync(Trace, From, From.AddHours(1), cts.Token));
    }

    [Fact]
    public void The_factory_makes_jaeger_stores_and_refuses_other_kinds_by_name()
    {
        var factory = new TraceStoreFactory(() => new FakeJaeger());
        using var store = (JaegerTraceStore)factory.Create(new TraceStoreSettings(TraceStoreKinds.Jaeger, "http://jaeger.test:16686/"));
        Assert.Equal("Jaeger at http://jaeger.test:16686", store.Description);

        var ex = Assert.Throws<NotSupportedException>(() => factory.Create(new TraceStoreSettings("elastic", "http://es:9200")));
        Assert.Contains("jaeger", ex.Message);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
