using Beesharp.Contracts;

namespace Beesharp.Contracts.Tests;

public class CallAnalyticsTests
{
    [Fact]
    public void BuildEndpoints_aggregates_normalizes_collapses_slugs_and_applies_thresholds()
    {
        var calls = new List<CallFact>();
        long id = 1;

        // 12 distinct slugs under /catalog/{slug} -> adaptively collapses to GET /catalog/{id}
        for (var i = 0; i < 12; i++)
        {
            calls.Add(new CallFact(
                id++, i, 0, 0, 0, i * 1.0, 0, i == 11 ? 650 : 120, 200, 100, null,
                $"GET /catalog/slug-item-{i:00}", $"https://shop.test/catalog/slug-item-{i:00}",
                $"GET /catalog/slug-item-{i:00}", "GET", "shop.test", null));
        }

        // Endpoint with explicit 300ms threshold -> 450ms is slow even with only 2 samples
        calls.Add(new CallFact(id++, 20, 1, 0, 0, 20, 0, 180, 200, 50, null, "POST /pay", "https://shop.test/pay", "POST /pay", "POST", "shop.test", 300));
        calls.Add(new CallFact(id++, 21, 1, 0, 0, 21, 0, 450, 200, 50, null, "POST /pay", "https://shop.test/pay", "POST /pay", "POST", "shop.test", 300));
        calls.Add(new CallFact(id++, 22, 1, 0, 0, 22, 0, 510, 500, 0, "HTTP 500", "POST /pay", "https://shop.test/pay", "POST /pay", "POST", "shop.test", 300));

        var res = CallAnalytics.BuildEndpoints(calls);
        Assert.Equal(2, res.Endpoints.Count);
        Assert.Equal(15, res.TotalCalls);
        Assert.Equal(2, res.TotalSlowCalls); // 1 on /catalog/{id} (>2*120) + 1 on POST /pay (>300ms)
        Assert.Equal(1, res.TotalFailedCalls);

        var pay = res.Endpoints.Single(e => e.Endpoint == "POST /pay");
        Assert.True(pay.HasCustomThreshold);
        Assert.Equal(300, pay.SlowThresholdMs);
        Assert.Equal(1, pay.SlowCount);
        Assert.Equal(1, pay.FailedCount);
        Assert.Equal([1], pay.Entries);

        var catalog = res.Endpoints.Single(e => e.Endpoint == "GET /catalog/{id}");
        Assert.False(catalog.HasCustomThreshold);
        Assert.Equal(12, catalog.TotalCount);
        Assert.Equal(1, catalog.SlowCount);
        Assert.Equal(240, catalog.SlowThresholdMs);
    }

    [Fact]
    public void BuildEndpointDetail_filters_sorts_builds_histogram_and_bounds_timeline()
    {
        var calls = new List<CallFact>();
        for (var i = 0; i < 300; i++)
        {
            var dur = i == 250 ? 950 : 100 + (i % 5) * 10;
            calls.Add(new CallFact(
                i + 1, i, 0, 0, 0, i * 0.5, 0, dur, 200, 64, null,
                $"GET /products/{1000 + i}", $"https://shop.test/products/{1000 + i}",
                "GET /products/{id}", "GET", "shop.test", null));
        }

        var detail = CallAnalytics.BuildEndpointDetail(calls, "GET /products/{id}", 150, maxPoints: 50);
        Assert.NotNull(detail);
        Assert.Equal(300, detail!.MatchingCalls);
        Assert.True(detail.Timeline.Count <= 55); // bucketed neatly
        Assert.NotEmpty(detail.Histogram);
        // Sorted slowest first by default
        Assert.Equal(950, detail.Calls[0].DurationMs);
        Assert.True(detail.Calls[0].IsSlow);
        Assert.Equal("GET /products/1250", detail.Calls[0].RawName);

        // MinMs filter keeps summary typical/threshold intact while filtering calls & timeline
        var filtered = CallAnalytics.BuildEndpointDetail(calls, "GET /products/{id}", 150, minMs: 500);
        Assert.NotNull(filtered);
        Assert.Equal(1, filtered!.MatchingCalls);
        Assert.Single(filtered.Calls);
        Assert.Single(filtered.Timeline);
        Assert.True(filtered.Histogram.Count > 1); // histogram keeps full distribution for clicking buckets
    }

    [Fact]
    public void BuildEndpoints_byStep_and_step_filter_separate_shared_urls_with_independent_baselines()
    {
        var calls = new List<CallFact>();
        long id = 1;
        // Three steps all hitting POST /rpc/orders with different latency profiles:
        // Step 0 (Create): ~100ms (with 1 slow at 320ms)
        // Step 1 (Update): ~40ms (with 1 slow at 120ms)
        // Step 2 (Delete): ~15ms (no slow, 1 failed)
        double[] createDurs = [95, 100, 102, 98, 105, 320];
        double[] updateDurs = [38, 40, 42, 39, 41, 120];
        double[] delDurs = [14, 15, 16, 15, 14, 18];

        for (var i = 0; i < 6; i++)
        {
            calls.Add(new CallFact(id++, i, 0, 0, 0, i, 0, createDurs[i], 200, 64, null,
                "POST /rpc/orders", "https://api.test/rpc/orders", "POST /rpc/orders", "POST", "api.test", null));
            calls.Add(new CallFact(id++, i, 0, 1, 0, i, 100, updateDurs[i], 200, 64, null,
                "POST /rpc/orders", "https://api.test/rpc/orders", "POST /rpc/orders", "POST", "api.test", null));
            calls.Add(new CallFact(id++, i, 0, 2, 0, i, 200, delDurs[i], i == 5 ? 500 : 200, 64, i == 5 ? "HTTP 500" : null,
                "POST /rpc/orders", "https://api.test/rpc/orders", "POST /rpc/orders", "POST", "api.test", null));
        }

        string? ResolveName(int entry, int step) => step switch { 0 => "Create", 1 => "Update", 2 => "Delete", _ => null };

        // Grouped by step -> 3 separate rows with independent typical/slow thresholds
        var byStep = CallAnalytics.BuildEndpoints(calls, byStep: true, resolveStepName: ResolveName);
        Assert.Equal(3, byStep.Endpoints.Count);
        Assert.Equal(18, byStep.TotalCalls);
        Assert.Equal(2, byStep.TotalSlowCalls); // 1 in Create (320 > 2*100) + 1 in Update (120 > 2*40)
        Assert.Equal(1, byStep.TotalFailedCalls);

        var create = byStep.Endpoints.Single(e => e.Step == 0);
        Assert.Equal("Create", create.StepName);
        Assert.Equal(1, create.SlowCount);
        Assert.Equal(100, create.TypicalMs);

        var next = byStep.Endpoints.Single(e => e.Step == 1);
        Assert.Equal("Update", next.StepName);
        Assert.Equal(1, next.SlowCount);
        Assert.Equal(40, next.TypicalMs);

        var del = byStep.Endpoints.Single(e => e.Step == 2);
        Assert.Equal("Delete", del.StepName);
        Assert.Equal(0, del.SlowCount);
        Assert.Equal(1, del.FailedCount);
        Assert.Equal(15, del.TypicalMs);

        // Step filter alone narrows to Update
        var stepOnly = CallAnalytics.BuildEndpoints(calls, step: 1);
        var onlyEp = Assert.Single(stepOnly.Endpoints);
        Assert.Equal(6, onlyEp.TotalCount);
        Assert.Equal(1, onlyEp.SlowCount);
        Assert.Equal(40, onlyEp.TypicalMs);

        // Search by step name works when byStep is enabled
        var searchStep = CallAnalytics.BuildEndpoints(calls, search: "Upd", byStep: true, resolveStepName: ResolveName);
        Assert.Equal("Update", Assert.Single(searchStep.Endpoints).StepName);

        // BuildStepMetrics agrees with byStep slow counts and latencies
        var metrics = CallAnalytics.BuildStepMetrics(calls);
        Assert.Equal(3, metrics.Count);
        Assert.Equal(6, metrics[(0, 0)].TotalCalls);
        Assert.Equal(1, metrics[(0, 0)].SlowCalls);
        Assert.Equal(95, metrics[(0, 0)].MinMs);
        Assert.Equal(100, metrics[(0, 0)].P50Ms);
        Assert.Equal(320, metrics[(0, 0)].MaxMs);
        Assert.Equal(1, metrics[(0, 1)].SlowCalls);
        Assert.Equal(40, metrics[(0, 1)].P50Ms);
        Assert.Equal(0, metrics[(0, 2)].SlowCalls);
        Assert.Equal(1, metrics[(0, 2)].FailedCalls);
        Assert.Equal(15, metrics[(0, 2)].P50Ms);
    }
}
