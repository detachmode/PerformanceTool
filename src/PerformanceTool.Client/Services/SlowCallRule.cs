namespace PerformanceTool.Client.Services;

/// <summary>
/// PLACEHOLDER rule for "slow" (brain 0018 leaves the real definition open). Change it here, nowhere else:
/// a call is slow when it takes longer than <see cref="Factor"/> times the typical duration of that same call
/// (median of the latest durations of that call in this run, once at least <see cref="MinSamples"/> were seen).
/// An actor is slow when any of its calls is slow; a scenario is amber when it has slow calls and no errors.
/// </summary>
public static class SlowCallRule
{
    public const double Factor = 2.0;
    public const int MinSamples = 5;

    public static bool IsSlow(double durationMs, double? typicalMs) => typicalMs is { } t && durationMs > Factor * t;
}

/// <summary>
/// Incremental "typical duration" per (scenario, call): the median of a small sliding window, refreshed every few samples,
/// so the cost per call stays constant however long the run is (brain 0021).
/// </summary>
public sealed class CallTypicals
{
    private const int Window = 51, Refresh = 10;

    private sealed class Series
    {
        public readonly double[] Values = new double[Window];
        public int Count, Next, SinceRefresh;
        public double? Median;
    }

    private readonly Dictionary<(int, string), Series> series = [];

    public double? Typical(int entry, string call) =>
        series.TryGetValue((entry, call), out var s) && s.Count >= SlowCallRule.MinSamples ? s.Median : null;

    public void Add(int entry, string call, double durationMs)
    {
        if (!series.TryGetValue((entry, call), out var s)) series[(entry, call)] = s = new Series();
        s.Values[s.Next] = durationMs;
        s.Next = (s.Next + 1) % Window;
        if (s.Count < Window) s.Count++;
        if (s.Median is null || ++s.SinceRefresh >= Refresh)
        {
            s.SinceRefresh = 0;
            var copy = s.Values.AsSpan(0, s.Count).ToArray();
            Array.Sort(copy);
            s.Median = copy[copy.Length / 2];
        }
    }
}
