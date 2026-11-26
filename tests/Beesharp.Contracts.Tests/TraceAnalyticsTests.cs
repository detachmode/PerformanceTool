using Beesharp.Contracts;

namespace Beesharp.Contracts.Tests;

/// <summary>The trace engine (brain 0065) on hand-made span trees: self time, nesting, the bounded list and the hotspots with their baseline.</summary>
public class TraceAnalyticsTests
{
    private const long BaseNanos = 1_795_000_000_000_000_000;

    private static string Id32(int n) => n.ToString("x32");
    private static string Id16(int n) => n.ToString("x16");

    private static TraceSpanDto Span(string trace, string id, string? parent, string service, string name, double startMs, double durationMs,
        bool error = false, IReadOnlyDictionary<string, string>? attrs = null) =>
        new(trace, id, parent, service, name, "internal", BaseNanos + (long)(startMs * 1_000_000), durationMs, error, error ? "boom" : null, attrs);

    private static TraceFetchDto Found(string trace, int sequence, params string[] why) =>
        new(trace, sequence, TraceFetchStatus.Found, null, 0, 1, why, DateTimeOffset.UnixEpoch);

    // ---- the tree and the flat list ----

    [Fact]
    public void Self_time_is_the_duration_minus_what_the_children_cover_and_overlapping_children_count_once()
    {
        var t = Id32(1);
        var spans = new[]
        {
            Span(t, "a", null, "beesharp", "Shop journey", 0, 100),
            Span(t, "b", "a", "shop-api", "GET /x", 0, 40),
            Span(t, "c", "a", "shop-api", "GET /y", 30, 40),      // overlaps b: together they cover 0 to 70
            Span(t, "d", "b", "shop-api", "SELECT x", 5, 10),
        };

        var view = TraceAnalytics.BuildView("run", t, spans, Found(t, 0), 0, beeDurationMs: 100);

        Assert.True(view.Found);
        Assert.Equal(4, view.TotalSpans);
        var byName = view.Spans.ToDictionary(s => s.Name);
        Assert.Equal(30, byName["Shop journey"].SelfMs, 3);   // 100 - union(0..40, 30..70) = 30
        Assert.Equal(30, byName["GET /x"].SelfMs, 3);          // 40 - 10
        Assert.Equal(40, byName["GET /y"].SelfMs, 3);
        Assert.Equal(10, byName["SELECT x"].SelfMs, 3);
        Assert.Equal(0.3, byName["Shop journey"].Share, 3);    // share of the bee's duration
        Assert.Equal(["Shop journey", "GET /x", "SELECT x", "GET /y"], view.Spans.Select(s => s.Name).ToArray());   // tree order, children by start
        Assert.Equal([0, 1, 2, 1], view.Spans.Select(s => s.Depth).ToArray());
    }

    [Fact]
    public void A_span_whose_parent_is_not_in_the_trace_is_a_root_and_a_child_longer_than_its_parent_never_gives_negative_self_time()
    {
        var t = Id32(2);
        var spans = new[]
        {
            Span(t, "s1", "missing-call-span", "shop-api", "GET /products/{id}", 0, 50),   // Beesharp's own spans were not exported
            Span(t, "s2", "s1", "shop-api", "SELECT product", 0, 80),                      // clock skew: longer than the parent
        };

        var view = TraceAnalytics.BuildView("run", t, spans, Found(t, 0), 0, beeDurationMs: null);

        Assert.Equal([0, 1], view.Spans.Select(s => s.Depth).ToArray());
        Assert.All(view.Spans, s => Assert.True(s.SelfMs >= 0));
        Assert.Equal(0, view.Spans[0].SelfMs, 3);
        Assert.Equal(["shop-api"], view.Services);
    }

    [Fact]
    public void Error_spans_http_status_and_key_attributes_are_part_of_the_list()
    {
        var t = Id32(3);
        var spans = new[]
        {
            Span(t, "a", null, "shop-api", "POST /pay", 0, 90, attrs: new Dictionary<string, string> { ["http.response.status_code"] = "502", ["http.route"] = "/pay" }),
            Span(t, "b", "a", "shop-api", "INSERT order", 5, 20, error: true, attrs: new Dictionary<string, string> { ["db.query.text"] = "INSERT order", ["noise"] = "x" }),
        };

        var view = TraceAnalytics.BuildView("run", t, spans, Found(t, 4, "failed"), 4, 100);

        Assert.Equal(1, view.ErrorSpans);
        Assert.Equal("B5", view.BeeName);
        Assert.Equal(["failed"], view.SelectedFor);
        Assert.Equal(502, view.Spans[0].HttpStatus);
        Assert.True(view.Spans[1].IsError);
        Assert.Equal("boom", view.Spans[1].StatusMessage);
        var key = TraceAnalytics.KeyAttributes(view.Spans[1].Attributes);
        Assert.Equal("INSERT order", key["db.query.text"]);
        Assert.DoesNotContain("noise", key.Keys);
    }

    [Fact]
    public void Over_the_bound_the_spans_with_the_largest_self_time_stay_together_with_their_ancestors_in_tree_order()
    {
        var t = Id32(4);
        var spans = new List<TraceSpanDto> { Span(t, "root", null, "beesharp", "bee", 0, 1000) };
        for (var i = 0; i < 10; i++)
            spans.Add(Span(t, $"call{i}", "root", "beesharp", $"call {i}", i * 100, i == 7 ? 90 : 20));
        spans.Add(Span(t, "db", "call7", "shop-api", "SELECT slow", 700, 85));   // the big self time, below call 7

        var view = TraceAnalytics.BuildView("run", t, spans, Found(t, 0), 0, 1000, maxSpans: 3);

        Assert.Equal(12, view.TotalSpans);
        Assert.Equal(3, view.ShownSpans);
        Assert.Equal(["bee", "call 7", "SELECT slow"], view.Spans.Select(s => s.Name).ToArray());   // ancestors kept, still a tree
        Assert.Contains("not shown", view.Note);
    }

    [Fact]
    public void One_call_shows_only_the_spans_below_it_with_the_calls_duration_as_reference()
    {
        var t = Id32(5);
        var spans = new[]
        {
            Span(t, "root", null, "beesharp", "bee", 0, 300),
            Span(t, "call1", "root", "beesharp", "GET /a", 0, 100),
            Span(t, "srv1", "call1", "shop-api", "GET /a", 5, 90),
            Span(t, "call2", "root", "beesharp", "GET /b", 100, 200),
            Span(t, "srv2", "call2", "shop-api", "GET /b", 105, 190),
            Span(t, "db2", "srv2", "shop-api", "SELECT b", 110, 150),
        };

        var withCallSpan = TraceAnalytics.BuildView("run", t, spans, Found(t, 0), 0, 300, call: 2, callSpanId: "call2", callDurationMs: 200, callEndpoint: "GET /b");
        Assert.Equal(["GET /b", "GET /b", "SELECT b"], withCallSpan.Spans.Select(s => s.Name).ToArray());
        Assert.Equal("call", withCallSpan.ReferenceKind);
        Assert.Equal(200, withCallSpan.ReferenceMs);
        Assert.Equal(0.75, withCallSpan.Spans[2].Share, 3);   // 150 of the call's 200 ms
        Assert.Equal(2, withCallSpan.Call);

        // Without Beesharp's own spans in the store the call span is missing: its children are the roots.
        var withoutCallSpan = TraceAnalytics.BuildView("run", t, spans.Where(s => s.SpanId is not ("root" or "call1" or "call2")).ToList(), Found(t, 0), 0, 300,
            call: 2, callSpanId: "call2", callDurationMs: 200, callEndpoint: "GET /b");
        Assert.Equal(["GET /b", "SELECT b"], withoutCallSpan.Spans.Select(s => s.Name).ToArray());
        Assert.Equal([0, 1], withoutCallSpan.Spans.Select(s => s.Depth).ToArray());
    }

    [Theory]
    [InlineData("5", 4, null)]
    [InlineData("B5", 4, null)]
    [InlineData(" b12 ", 11, null)]
    [InlineData("4D7CE6AC47FBE9B3822D67A473CE9774", null, "4d7ce6ac47fbe9b3822d67a473ce9774")]
    public void A_key_is_a_bee_or_a_trace_id(string raw, int? sequence, string? traceId)
    {
        var key = TraceAnalytics.ParseKey(raw);
        Assert.NotNull(key);
        Assert.Equal(sequence, key!.Sequence);
        Assert.Equal(traceId, key.TraceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("B0")]
    [InlineData("hello")]
    [InlineData("4d7ce6ac47fbe9b3822d67a473ce97")]
    public void Anything_else_is_not_a_key(string raw) => Assert.Null(TraceAnalytics.ParseKey(raw));

    [Fact]
    public void A_trace_that_is_not_stored_says_why()
    {
        var bee = new TraceAnalytics.BeeTraceInfo(4, Id32(9), 120, [new(1, "c1", 50, "GET /a")]);

        var noRecords = TraceAnalytics.ComposeView("run", bee, null, 50, [], []);
        Assert.False(noRecords.Found);
        Assert.Equal(TraceFetchReasons.NotConfigured, noRecords.Reason);

        var notSelected = TraceAnalytics.ComposeView("run", bee, null, 50, [Found(Id32(1), 0)], []);
        Assert.Equal(TraceFetchReasons.NotSelected, notSelected.Reason);

        var missing = new TraceFetchDto(Id32(9), 4, TraceFetchStatus.NotFound, TraceFetchReasons.NotArrived, 0, 2, ["failed"], DateTimeOffset.UnixEpoch);
        var notFound = TraceAnalytics.ComposeView("run", bee, null, 50, [missing], []);
        Assert.False(notFound.Found);
        Assert.Equal(TraceFetchReasons.NotArrived, notFound.Reason);
        Assert.Equal(["failed"], notFound.SelectedFor);
        Assert.Contains("30 seconds", notFound.ReasonText);

        var noId = TraceAnalytics.ComposeView("run", bee with { TraceId = null }, null, 50, [], []);
        Assert.Equal(TraceFetchReasons.NoTraceId, noId.Reason);

        Assert.Throws<ArgumentException>(() => TraceAnalytics.ComposeView("run", bee, 7, 50, [Found(Id32(9), 4)], []));
    }

    [Fact]
    public void The_fetch_summary_counts_and_names_the_reasons()
    {
        var records = new[]
        {
            Found(Id32(1), 0), Found(Id32(2), 1),
            new TraceFetchDto(Id32(3), 2, TraceFetchStatus.NotFound, TraceFetchReasons.NotArrived, 0, 2, [], null),
            new TraceFetchDto(Id32(4), 3, TraceFetchStatus.NotFound, TraceFetchReasons.NotArrived, 0, 2, [], null),
            new TraceFetchDto(Id32(5), 4, TraceFetchStatus.NotFound, TraceFetchReasons.Expired, 0, 2, [], null),
        };

        var summary = TraceFetchSummaryDto.From(records);

        Assert.Equal((5, 2, 3, 0), (summary.Selected, summary.Found, summary.NotFound, summary.Errors));
        Assert.Equal("traces: 2 of 5 found, 3 not found: 2 not arrived, 1 expired", summary.Line);
    }

    // ---- hotspots ----

    private sealed class World
    {
        public List<CallFact> Calls { get; } = [];
        public List<TraceSpanDto> Spans { get; } = [];
        public List<TraceFetchDto> Fetches { get; } = [];
        private int next = 1;

        /// <summary>One bee with one call of the endpoint; <paramref name="below"/> builds the spans below the call span (offsets relative to the call's start).</summary>
        public string Call(string endpoint, double durationMs, Func<string, string, List<TraceSpanDto>>? below, bool stored = true, string? error = null)
        {
            var n = next++;
            var trace = Id32(n);
            var callSpan = Id16(n);
            Calls.Add(new CallFact(n, n, 0, 0, 0, n, 0, durationMs, error is null ? 200 : 500, 10, error, endpoint, "https://shop.test" + endpoint[(endpoint.IndexOf(' ') + 1)..],
                endpoint, endpoint[..endpoint.IndexOf(' ')], "shop.test", null, TraceId: trace, SpanId: callSpan));
            if (below is not null && stored)
            {
                Spans.AddRange(below(trace, callSpan));
                Fetches.Add(Found(trace, n - 1, "slowest " + endpoint));
            }
            return trace;
        }
    }

    private static Func<string, string, List<TraceSpanDto>> ServerWithDb(string dbName, double dbMs, double serverMs, int queries = 1) => (trace, call) =>
    {
        var server = Id16(Math.Abs((trace + "s").GetHashCode()));
        var list = new List<TraceSpanDto> { Span(trace, server, call, "shop-api", "GET /products/{id:int}", 1, serverMs) };
        for (var q = 0; q < queries; q++)
            list.Add(Span(trace, Id16(Math.Abs((trace + "q" + q).GetHashCode())), server, "shop-api", dbName, 2 + q * dbMs, dbMs,
                attrs: new Dictionary<string, string> { ["db.query.text"] = dbName }));
        return list;
    };

    [Fact]
    public void Hotspots_name_the_span_that_takes_the_slow_calls_time_and_compare_it_with_the_typical_calls()
    {
        var w = new World();
        for (var i = 0; i < 10; i++) w.Call("GET /products/{id}", 40, ServerWithDb("SELECT product", 30, 38));      // typical: the query takes 30 ms
        var slowTraces = new List<string>
        {
            w.Call("GET /products/{id}", 400, ServerWithDb("SELECT product", 380, 398)),
            w.Call("GET /products/{id}", 450, ServerWithDb("SELECT product", 430, 448)),
            w.Call("GET /products/{id}", 500, ServerWithDb("SELECT product", 480, 498)),
        };
        w.Call("GET /products/{id}", 420, ServerWithDb("SELECT item", 100, 399, queries: 3));                          // N+1: three 100 ms queries add up to 300 ms
        w.Call("GET /products/{id}", 410, below: null);                                                              // slow, but its trace was not found

        var result = TraceAnalytics.BuildHotspots("run", w.Calls, w.Spans, w.Fetches, new HotspotsQuery());

        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal("GET /products/{id}", endpoint.Endpoint);
        Assert.Equal(15, endpoint.TotalCalls);
        Assert.Equal(5, endpoint.SlowCalls);
        Assert.Equal(4, endpoint.SlowCallsWithTrace);       // the fifth slow call has no stored trace
        Assert.Equal(10, endpoint.TypicalCallsWithTrace);
        Assert.Equal("slow", endpoint.Basis);

        var top = endpoint.Hotspots[0];
        Assert.Equal(("shop-api", "SELECT product"), (top.Service, top.Name));
        Assert.Equal(3, top.Explains);
        Assert.Equal(0.75, top.ExplainsShare, 3);
        Assert.Equal(430, top.MedianSelfMs, 3);
        Assert.Equal(30, top.BaselineSelfMs!.Value, 3);
        Assert.Equal(430 / 30.0, top.Factor!.Value, 3);
        Assert.Equal(slowTraces[2], top.ExampleTraceIds[0]);   // the slowest example first
        Assert.Equal("SELECT product", top.ExampleAttributes!["db.query.text"]);

        var nPlusOne = endpoint.Hotspots.Single(h => h.Name == "SELECT item");
        Assert.Equal(1, nPlusOne.Explains);
        Assert.Equal(300, nPlusOne.MedianSelfMs, 3);    // summed over the call
        Assert.Equal(0, nPlusOne.BaselineSelfMs!.Value);  // typical calls have no such query at all
        Assert.Null(nPlusOne.Factor);

        Assert.Equal("traces: 14 of 14 found", result.Coverage.Line);
    }

    [Fact]
    public void Time_that_no_service_span_covers_is_a_hotspot_of_its_own()
    {
        var w = new World();
        for (var i = 0; i < 8; i++) w.Call("GET /search", 50, ServerWithDb("SELECT p", 45, 48));
        w.Call("GET /search", 300, ServerWithDb("SELECT p", 40, 45));                // 255 ms are not covered by any span: network or queueing
        w.Call("GET /search", 310, (trace, call) => []);                              // the service did not join the trace at all

        var result = TraceAnalytics.BuildHotspots("run", w.Calls, w.Spans, w.Fetches, new HotspotsQuery());

        var hotspot = Assert.Single(result.Endpoints).Hotspots[0];
        Assert.True(hotspot.IsOutside);
        Assert.Equal(TraceAnalytics.OutsideService, hotspot.Service);
        Assert.Equal(1, hotspot.Explains);
        Assert.Equal(255, hotspot.MedianSelfMs, 3);
    }

    [Fact]
    public void An_endpoint_without_a_slow_traced_call_shows_where_its_typical_calls_spend_their_time_and_slow_only_hides_it()
    {
        var w = new World();
        for (var i = 0; i < 6; i++) w.Call("POST /pay", 100, (trace, call) =>
        [
            Span(trace, "p" + trace[^6..], call, "payment-api", "POST /charge", 2, 90),
        ]);
        for (var i = 0; i < 8; i++) w.Call("GET /login", 30, ServerWithDb("SELECT user", 25, 28));
        w.Call("GET /login", 400, ServerWithDb("SELECT user", 380, 398));

        var all = TraceAnalytics.BuildHotspots("run", w.Calls, w.Spans, w.Fetches, new HotspotsQuery());
        Assert.Equal(["GET /login", "POST /pay"], all.Endpoints.Select(e => e.Endpoint).ToArray());   // slow ones first
        var pay = all.Endpoints[1];
        Assert.Equal("typical", pay.Basis);
        Assert.Equal(0, pay.SlowCallsWithTrace);
        Assert.Equal(("payment-api", "POST /charge"), (pay.Hotspots[0].Service, pay.Hotspots[0].Name));
        Assert.Null(pay.Hotspots[0].BaselineSelfMs);

        var slowOnly = TraceAnalytics.BuildHotspots("run", w.Calls, w.Spans, w.Fetches, new HotspotsQuery(SlowOnly: true));
        Assert.Equal(["GET /login"], slowOnly.Endpoints.Select(e => e.Endpoint).ToArray());

        var filtered = TraceAnalytics.BuildHotspots("run", w.Calls, w.Spans, w.Fetches, new HotspotsQuery(Endpoint: "pay"));
        Assert.Equal(["POST /pay"], filtered.Endpoints.Select(e => e.Endpoint).ToArray());

        var limited = TraceAnalytics.BuildHotspots("run", w.Calls, w.Spans, w.Fetches, new HotspotsQuery(Limit: 1));
        Assert.Single(limited.Endpoints);
        Assert.Equal(2, limited.EndpointsTotal);
    }

    [Fact]
    public void Failed_calls_and_calls_without_a_stored_trace_are_not_analysed()
    {
        var w = new World();
        for (var i = 0; i < 8; i++) w.Call("GET /x", 40, ServerWithDb("SELECT x", 30, 38));
        w.Call("GET /x", 900, ServerWithDb("SELECT x", 880, 898), error: "HTTP 500");     // failed: neither slow nor typical

        var result = TraceAnalytics.BuildHotspots("run", w.Calls, w.Spans, w.Fetches, new HotspotsQuery());

        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal(0, endpoint.SlowCallsWithTrace);
        Assert.Equal(8, endpoint.TypicalCallsWithTrace);
    }

    [Fact]
    public void Without_fetched_traces_the_answer_says_so()
    {
        var w = new World();
        for (var i = 0; i < 6; i++) w.Call("GET /x", 40, ServerWithDb("SELECT x", 30, 38));

        var none = TraceAnalytics.BuildHotspots("run", w.Calls, [], [], new HotspotsQuery());
        Assert.Empty(none.Endpoints);
        Assert.Contains("No traces are stored", none.Note);

        var allMissing = TraceAnalytics.BuildHotspots("run", w.Calls, [],
            [new TraceFetchDto(Id32(1), 0, TraceFetchStatus.NotFound, TraceFetchReasons.NotInStore, 0, 2, [], null)], new HotspotsQuery());
        Assert.Empty(allMissing.Endpoints);
        Assert.Contains("None of the selected traces was found", allMissing.Note);
    }
}
