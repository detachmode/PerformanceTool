using System.Globalization;
using Beesharp.Contracts;

namespace Beesharp.Client.Calls;

/// <summary>Latency colour tier relative to the active target or per-endpoint threshold (brain 0018, 0041, 0067).</summary>
public enum CallsLatencyTier
{
    Good = 0,
    Slight = 1,
    Moderate = 2,
    High = 3,
    Slow = 4,
}

/// <summary>Linear ms X-axis bounds and round tick marks for <see cref="CallsOverviewChart"/>.</summary>
public readonly record struct CallsAxisScale(double AxisMaxMs, IReadOnlyList<double> Ticks);

/// <summary>Pure calculation helpers for the <c>/calls</c> p50-vs-p95 overview chart (brain 0067).</summary>
public static class CallsOverviewScale
{
    /// <summary>Default target latency in milliseconds when neither scenario nor user configures one.</summary>
    public const double DefaultTargetMs = 50.0;

    /// <summary>
    /// Resolves the chart-wide target reference line in milliseconds:
    /// explicit user override first, then scenario-configured default, then a shared custom threshold across all endpoints, else 50 ms.
    /// </summary>
    public static double ResolveTargetMs(
        double? overrideTargetMs,
        double? scenarioDefaultMs,
        IReadOnlyList<CallEndpointSummaryDto> endpoints)
    {
        if (overrideTargetMs is > 0 && !double.IsNaN(overrideTargetMs.Value) && !double.IsInfinity(overrideTargetMs.Value))
            return overrideTargetMs.Value;

        if (scenarioDefaultMs is > 0 && !double.IsNaN(scenarioDefaultMs.Value) && !double.IsInfinity(scenarioDefaultMs.Value))
            return scenarioDefaultMs.Value;

        if (endpoints.Count > 0)
        {
            double? shared = null;
            var allCustom = true;
            for (var i = 0; i < endpoints.Count; i++)
            {
                var ep = endpoints[i];
                if (!ep.HasCustomThreshold || ep.SlowThresholdMs is not > 0)
                {
                    allCustom = false;
                    break;
                }

                if (shared is null)
                    shared = ep.SlowThresholdMs.Value;
                else if (Math.Abs(shared.Value - ep.SlowThresholdMs.Value) > 0.01)
                {
                    allCustom = false;
                    break;
                }
            }

            if (allCustom && shared is > 0)
                return shared.Value;
        }

        return DefaultTargetMs;
    }

    /// <summary>Returns the reference threshold (in ms) used to evaluate an endpoint's latency colour.</summary>
    public static double ReferenceMs(CallEndpointSummaryDto endpoint, double targetMs) =>
        endpoint.HasCustomThreshold && endpoint.SlowThresholdMs is > 0
            ? endpoint.SlowThresholdMs.Value
            : Math.Max(1.0, targetMs);

    /// <summary>Classifies an endpoint's p95 latency into a green-to-amber tier; failures never affect bar colour.</summary>
    public static CallsLatencyTier ClassifyLatency(CallEndpointSummaryDto endpoint, double targetMs) =>
        ClassifyRatio(endpoint.P95Ms / ReferenceMs(endpoint, targetMs));

    /// <summary>Maps a <c>p95 / reference</c> ratio to a green-to-amber colour tier.</summary>
    public static CallsLatencyTier ClassifyRatio(double ratio)
    {
        if (double.IsNaN(ratio) || ratio <= 1.0) return CallsLatencyTier.Good;
        if (ratio <= 1.6) return CallsLatencyTier.Slight;
        if (ratio <= 2.6) return CallsLatencyTier.Moderate;
        if (ratio <= 4.0) return CallsLatencyTier.High;
        return CallsLatencyTier.Slow;
    }

    /// <summary>Returns the CSS custom property representing the latency colour tier.</summary>
    public static string ColorCssVar(CallsLatencyTier tier) => tier switch
    {
        CallsLatencyTier.Good => "var(--good)",
        CallsLatencyTier.Slight => "var(--g1)",
        CallsLatencyTier.Moderate => "var(--g2)",
        CallsLatencyTier.High => "var(--g3)",
        _ => "var(--slow)",
    };

    /// <summary>Computes an autoscaled linear axis with nice round tick intervals covering <paramref name="maxP95Ms"/> and <paramref name="targetMs"/>.</summary>
    public static CallsAxisScale ComputeAxis(double maxP95Ms, double targetMs, bool compact = false)
    {
        var maxVal = Math.Max(maxP95Ms, targetMs);
        if (maxVal <= 0 || double.IsNaN(maxVal) || double.IsInfinity(maxVal))
            return new CallsAxisScale(100, [0, 50, 100]);

        var targetTicks = compact ? 4.0 : 6.0;
        var rawStep = maxVal / targetTicks;
        var pow = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var norm = rawStep / pow;
        var nice = norm <= 1.0 ? 1.0
            : norm <= 2.0 ? 2.0
            : norm <= 2.5 ? 2.5
            : norm <= 5.0 ? 5.0
            : 10.0;
        var step = nice * pow;
        var axisMax = Math.Ceiling(maxVal / step) * step;
        if (axisMax <= 0) axisMax = step;

        var ticks = new List<double>();
        var eps = step * 1e-6;
        for (var v = 0.0; v <= axisMax + eps && ticks.Count < 16; v += step)
            ticks.Add(Math.Round(v, 4));

        return new CallsAxisScale(Math.Round(axisMax, 4), ticks);
    }

    /// <summary>Formats a duration value in milliseconds with tabular readability.</summary>
    public static string FormatMsValue(double ms)
    {
        if (ms <= 0 || double.IsNaN(ms)) return "0";
        if (ms < 10) return ms.ToString("0.#", CultureInfo.InvariantCulture);
        return Math.Round(ms).ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>Formats an X-axis tick label in milliseconds.</summary>
    public static string FormatTick(double ms) => $"{FormatMsValue(ms)}ms";
}
