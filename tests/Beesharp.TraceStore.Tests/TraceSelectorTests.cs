using Beesharp.Services.TraceStore;
using static Beesharp.TraceStore.Tests.Bees;

namespace Beesharp.TraceStore.Tests;

/// <summary>Which traces get fetched (brain 0065): all failed bees (capped), the slowest few calls per endpoint, a few typical ones as the baseline, within a cap.</summary>
public class TraceSelectorTests
{
    private static TraceFetchOptions Options() => new() { MaxFailed = 3, SlowestPerEndpoint = 2, TypicalPerEndpoint = 2, MaxTraces = 100, TypicalMinSample = 1 };

    [Fact]
    public void Failed_bees_come_first_and_only_up_to_the_cap()
    {
        var selector = new TraceSelector(Options());
        for (var i = 0; i < 6; i++) selector.Observe(Bee(i, failed: true));
        selector.Observe(Bee(10));   // a bee without calls that did not fail is not interesting

        var chosen = selector.Select();

        Assert.Equal([TraceId(0), TraceId(1), TraceId(2)], chosen.Select(c => c.TraceId).ToArray());
        Assert.All(chosen, c => Assert.Equal(["failed"], c.Reasons));
    }

    [Fact]
    public void The_slowest_slow_calls_of_each_endpoint_are_chosen_and_the_endpoints_take_turns()
    {
        var selector = new TraceSelector(Options());
        selector.Observe(Bee(1, calls: Call("GET /a", 300, slow: true)));
        selector.Observe(Bee(2, calls: Call("GET /a", 900, slow: true)));
        selector.Observe(Bee(3, calls: Call("GET /a", 500, slow: true)));
        selector.Observe(Bee(4, calls: Call("GET /b", 700, slow: true)));
        selector.Observe(Bee(5, calls: Call("GET /b", 100, slow: true)));
        selector.Observe(Bee(6, calls: Call("GET /b", 150, slow: true)));

        var chosen = selector.Select().Where(c => c.Reasons[0].StartsWith("slowest")).ToList();

        // two per endpoint, the slowest first, endpoints alternating: rank 1 of /a and /b, then rank 2.
        Assert.Equal([TraceId(2), TraceId(4), TraceId(3), TraceId(6)], chosen.Select(c => c.TraceId).ToArray());
        Assert.Equal(["slowest GET /a"], chosen[0].Reasons);
    }

    [Fact]
    public void Typical_calls_are_the_ones_nearest_to_the_median_and_never_slow_or_failed_ones()
    {
        var selector = new TraceSelector(Options());
        for (var i = 0; i < 15; i++) selector.Observe(Bee(i, calls: Call("GET /a", 10 + i)));   // 10..24 ms, median 17
        selector.Observe(Bee(50, calls: Call("GET /a", 17, slow: true)));
        selector.Observe(Bee(51, calls: Call("GET /a", 17, failed: true)));

        var typical = selector.Select().Where(c => c.Reasons[0].StartsWith("typical")).ToList();

        Assert.Equal(2, typical.Count);
        Assert.Equal(TraceId(7), typical[0].TraceId);                                        // exactly 17 ms
        Assert.Contains(typical[1].TraceId, new[] { TraceId(6), TraceId(8) });             // 16 or 18 ms
        Assert.DoesNotContain(typical, c => c.TraceId is "00000000000000000000000000000032" or "00000000000000000000000000000033");
    }

    [Fact]
    public void During_the_run_typical_calls_wait_for_a_sample_that_deserves_the_name_and_the_final_pass_takes_what_there_is()
    {
        var options = Options();
        options.TypicalMinSample = 10;
        var selector = new TraceSelector(options);
        for (var i = 0; i < 4; i++) selector.Observe(Bee(i, calls: Call("GET /rare", 20 + i)));

        Assert.Empty(selector.Select());
        Assert.Equal(2, selector.Select(final: true).Count);
    }

    [Fact]
    public void A_trace_chosen_for_several_reasons_is_fetched_once_with_all_of_them()
    {
        var selector = new TraceSelector(Options());
        selector.Observe(Bee(1, failed: true, calls: [Call("GET /a", 800, slow: true), Call("GET /b", 800, slow: true)]));

        var chosen = Assert.Single(selector.Select());

        Assert.Equal(["failed", "slowest GET /a", "slowest GET /b"], chosen.Reasons);
    }

    [Fact]
    public void The_cap_per_run_keeps_the_priority_failed_then_slowest_then_typical()
    {
        var options = Options();
        options.MaxTraces = 4;
        var selector = new TraceSelector(options);
        selector.Observe(Bee(1, failed: true));
        selector.Observe(Bee(2, failed: true));
        selector.Observe(Bee(3, calls: Call("GET /a", 500, slow: true)));
        selector.Observe(Bee(4, calls: Call("GET /a", 400, slow: true)));
        for (var i = 5; i < 12; i++) selector.Observe(Bee(i, calls: Call("GET /a", 20)));

        var chosen = selector.Select();

        Assert.Equal([TraceId(1), TraceId(2), TraceId(3), TraceId(4)], chosen.Select(c => c.TraceId).ToArray());
    }

    [Fact]
    public void Bees_without_a_trace_id_are_ignored_and_memory_stays_bounded_however_many_bees_finish()
    {
        var options = Options();
        options.MaxEndpoints = 5;
        var selector = new TraceSelector(options);
        selector.Observe(new TraceBeeObservation(null, 0, true, DateTimeOffset.UtcNow, [Call("GET /a", 10)]));
        for (var i = 1; i <= 50_000; i++)
            selector.Observe(Bee(i, failed: i % 1000 == 0, calls: [Call($"GET /e{i % 40}", 10 + i % 97, slow: i % 50 == 0)]));

        var chosen = selector.Select();

        Assert.True(chosen.Count <= options.MaxFailed + 5 * (options.SlowestPerEndpoint + options.TypicalPerEndpoint));   // only 5 endpoints are tracked at all
        Assert.DoesNotContain(chosen, c => c.TraceId == TraceId(0));
    }
}
