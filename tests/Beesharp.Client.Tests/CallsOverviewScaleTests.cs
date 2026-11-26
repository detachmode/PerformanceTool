using Beesharp.Client.Calls;
using Beesharp.Contracts;

namespace Beesharp.Client.Tests;

public class CallsOverviewScaleTests
{
    private static CallEndpointSummaryDto MakeEndpoint(
        string endpoint = "GET /test",
        double p50 = 20,
        double p95 = 45,
        double max = 90,
        int total = 100,
        int slow = 0,
        int failed = 0,
        double? slowThresholdMs = null,
        bool hasCustomThreshold = false) =>
        new(endpoint, "GET", null, total, slow, failed, p50, p95, max, 5, p50, slowThresholdMs, hasCustomThreshold, [0]);

    [Fact]
    public void ResolveTargetMs_defaults_to_50_when_no_override_or_scenario_target_is_set()
    {
        var endpoints = new[]
        {
            MakeEndpoint("GET /a", p95: 40),
            MakeEndpoint("POST /payments", p95: 1100, slowThresholdMs: 1200, hasCustomThreshold: true),
        };

        Assert.Equal(50, CallsOverviewScale.ResolveTargetMs(null, null, endpoints));
    }

    [Fact]
    public void ResolveTargetMs_uses_scenario_default_or_shared_custom_threshold_and_user_override_wins()
    {
        var sharedCustom = new[]
        {
            MakeEndpoint("GET /a", p95: 80, slowThresholdMs: 200, hasCustomThreshold: true),
            MakeEndpoint("POST /b", p95: 190, slowThresholdMs: 200, hasCustomThreshold: true),
        };

        Assert.Equal(200, CallsOverviewScale.ResolveTargetMs(null, null, sharedCustom));
        Assert.Equal(150, CallsOverviewScale.ResolveTargetMs(null, 150, sharedCustom));
        Assert.Equal(75, CallsOverviewScale.ResolveTargetMs(75, 150, sharedCustom));
    }

    [Theory]
    [InlineData(20, 50, CallsLatencyTier.Good)]
    [InlineData(50, 50, CallsLatencyTier.Good)]
    [InlineData(65, 50, CallsLatencyTier.Slight)]
    [InlineData(110, 50, CallsLatencyTier.Moderate)]
    [InlineData(180, 50, CallsLatencyTier.High)]
    [InlineData(250, 50, CallsLatencyTier.Slow)]
    public void ClassifyLatency_maps_p95_ratios_to_green_through_amber_tiers(double p95, double target, CallsLatencyTier expected)
    {
        var ep = MakeEndpoint(p95: p95, failed: 99);
        Assert.Equal(expected, CallsOverviewScale.ClassifyLatency(ep, target));
    }

    [Fact]
    public void ClassifyLatency_respects_per_endpoint_custom_threshold_and_ignores_failures()
    {
        var fastWithFailures = MakeEndpoint("GET /ok-ish", p50: 12, p95: 35, failed: 14);
        Assert.Equal(CallsLatencyTier.Good, CallsOverviewScale.ClassifyLatency(fastWithFailures, 50));
        Assert.Equal("var(--good)", CallsOverviewScale.ColorCssVar(CallsOverviewScale.ClassifyLatency(fastWithFailures, 50)));

        var payments = MakeEndpoint("POST /payments", p50: 700, p95: 1150, failed: 5, slowThresholdMs: 1200, hasCustomThreshold: true);
        Assert.Equal(1200, CallsOverviewScale.ReferenceMs(payments, 50));
        Assert.Equal(CallsLatencyTier.Good, CallsOverviewScale.ClassifyLatency(payments, 50));
    }

    [Fact]
    public void ComputeAxis_autoscales_with_round_ticks_covering_max_p95_and_target()
    {
        var empty = CallsOverviewScale.ComputeAxis(0, 0);
        Assert.Equal(100, empty.AxisMaxMs);
        Assert.Equal([0, 50, 100], empty.Ticks);

        var underTarget = CallsOverviewScale.ComputeAxis(32, 50);
        Assert.True(underTarget.AxisMaxMs >= 50);
        Assert.Equal(0, underTarget.Ticks[0]);
        Assert.Equal(underTarget.AxisMaxMs, underTarget.Ticks[^1]);

        var large = CallsOverviewScale.ComputeAxis(685, 50);
        Assert.Equal(800, large.AxisMaxMs);
        Assert.Equal([0, 200, 400, 600, 800], large.Ticks);

        var compact = CallsOverviewScale.ComputeAxis(685, 50, compact: true);
        Assert.True(compact.AxisMaxMs >= 685);
        Assert.True(compact.Ticks.Count <= 6);
        Assert.Equal(compact.AxisMaxMs, compact.Ticks[^1]);
    }

    [Fact]
    public void FormatMsValue_and_FormatTick_handle_sub_10_and_thousands()
    {
        Assert.Equal("0", CallsOverviewScale.FormatMsValue(0));
        Assert.Equal("4.2", CallsOverviewScale.FormatMsValue(4.23));
        Assert.Equal("114", CallsOverviewScale.FormatMsValue(114.4));
        Assert.Equal("1,250", CallsOverviewScale.FormatMsValue(1250));
        Assert.Equal("200ms", CallsOverviewScale.FormatTick(200));
    }
}
