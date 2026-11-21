namespace PerformanceTool.Client.Services;

/// <summary>
/// PLACEHOLDER rule for "slow" (brain 0018 leaves the real definition open). Change it here, nowhere else:
/// a call is slow when it takes longer than <see cref="Factor"/> times the typical duration of that same call
/// (median of the latest durations of that call in this run, once at least <see cref="MinSamples"/> were seen).
/// A bee is slow when any of its calls is slow; a scenario is amber when it has slow calls and no errors.
/// </summary>
public static class SlowCallRule
{
    public const double Factor = 2.0;
    public const int MinSamples = 5;

    public static bool IsSlow(double durationMs, double? typicalMs) => typicalMs is { } t && durationMs > Factor * t;

    // ---- the graded colour (brain 0041): the slow colour starts at the typical time and gets stronger the further off a call is ----

    /// <summary>How many times its typical duration a call took (1 when it was not slower, or no typical is known yet).</summary>
    public static double Ratio(double durationMs, double? typicalMs) =>
        typicalMs is { } t && t > 0 && durationMs > t ? durationMs / t : 1;

    /// <summary>The number of colour steps above green: step 0 is pure green (typical or faster), <see cref="MaxStep"/> is full amber (slow).</summary>
    public const int MaxStep = 4;

    /// <summary>
    /// The colour step of a ratio: 0 at typical (pure green), then every ratio above 1 gets at least step 1, rising evenly up to the slow line
    /// (<see cref="Factor"/>); below the slow line the step stays under <see cref="MaxStep"/>, so full amber always means slow.
    /// </summary>
    public static int Step(double ratio)
    {
        if (!(ratio > 1)) return 0;
        if (ratio >= Factor) return MaxStep;
        return Math.Clamp((int)Math.Ceiling((ratio - 1) / (Factor - 1) * MaxStep), 1, MaxStep - 1);
    }

    private const double CodeScale = 32;

    /// <summary>The ratio in one byte (1/32 steps up to about 8.9x), rounded down so a call under the slow line never reads as slow. 0 is "at typical".</summary>
    public static byte Encode(double ratio) => (byte)Math.Clamp(Math.Floor((Math.Max(1, ratio) - 1) * CodeScale), 0, 255);

    public static double Decode(byte code) => 1 + code / CodeScale;
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
