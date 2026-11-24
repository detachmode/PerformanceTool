namespace Beesharp.Services;

/// <summary>
/// Rule for deciding when a call is "slow" and computing its graded green-to-amber ratio (brain 0018, 0022, 0041, 0060).
/// Precedence: an explicit custom threshold (per call, per endpoint rule, or per scenario) wins;
/// otherwise a call is slow when it takes more than <see cref="Factor"/> times the typical duration of that same endpoint
/// (median of the latest durations once at least <see cref="MinSamples"/> were seen).
/// </summary>
public static class SlowCallRule
{
    public const double Factor = 2.0;
    public const int MinSamples = 5;

    /// <summary>The effective slow threshold in ms for a call/endpoint: custom threshold if configured, else <c>2× typical</c>.</summary>
    public static double? EffectiveThresholdMs(double? typicalMs, double? customThresholdMs = null) =>
        customThresholdMs is > 0 ? customThresholdMs
        : typicalMs is > 0 ? Factor * typicalMs.Value
        : null;

    public static bool IsSlow(double durationMs, double? typicalMs, double? customThresholdMs = null) =>
        EffectiveThresholdMs(typicalMs, customThresholdMs) is { } limit && durationMs > limit;

    // ---- the graded colour (brain 0041): the slow colour starts at the typical time and gets stronger the further off a call is ----

    /// <summary>
    /// How many times its typical duration a call took (1 when not slower or no baseline yet).
    /// When a custom threshold is set, scales smoothly from 1 at the baseline (typical, or half the limit) up to <see cref="Factor"/> at the limit.
    /// </summary>
    public static double Ratio(double durationMs, double? typicalMs, double? customThresholdMs = null)
    {
        if (customThresholdMs is > 0)
        {
            var limit = customThresholdMs.Value;
            if (durationMs > limit)
                return Math.Max(Factor, typicalMs is > 0 ? durationMs / typicalMs.Value : Factor * (durationMs / limit));

            var baseline = typicalMs is > 0 && typicalMs.Value < limit ? typicalMs.Value : limit / Factor;
            if (durationMs <= baseline) return 1;
            return 1 + (durationMs - baseline) / (limit - baseline) * (Factor - 1);
        }

        return typicalMs is { } t && t > 0 && durationMs > t ? durationMs / t : 1;
    }

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
/// Incremental "typical duration" per endpoint (or per scenario + call): the median of a small sliding window, refreshed every few samples,
/// so the cost per call stays constant however long the run is (brain 0021, 0060).
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

    /// <summary>Typical duration of an endpoint across the run.</summary>
    public double? Typical(string endpoint) => Typical(-1, endpoint);

    /// <summary>Records an endpoint sample across the run.</summary>
    public void Add(string endpoint, double durationMs) => Add(-1, endpoint, durationMs);

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
