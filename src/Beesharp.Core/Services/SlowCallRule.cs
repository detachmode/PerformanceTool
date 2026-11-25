namespace Beesharp.Services;

/// <summary>
/// Rule for deciding when a call is "slow" and computing its graded green-to-amber ratio (brain 0018, 0022, 0041, 0060).
/// Precedence: an explicit custom threshold (per call, per endpoint rule, or per scenario) wins;
/// otherwise a call is slow when it takes more than <see cref="Factor"/> times the typical duration of that same endpoint
/// (median of the latest durations once at least <see cref="MinSamples"/> were seen).
/// </summary>
public static class SlowCallRule
{
    /// <summary>Multiplier applied to the typical duration when no explicit custom threshold is configured.</summary>
    public const double Factor = 2.0;

    /// <summary>Minimum number of samples required before a typical duration baseline is established.</summary>
    public const int MinSamples = 5;

    /// <summary>The effective slow threshold in ms for a call/endpoint: custom threshold if configured, else <c>2× typical</c>.</summary>
    /// <param name="typicalMs">The typical (median) duration in milliseconds for the endpoint, or <c>null</c> if not yet established.</param>
    /// <param name="customThresholdMs">An optional explicit threshold in milliseconds configured per call, endpoint, or scenario.</param>
    /// <returns>The slow threshold in milliseconds, or <c>null</c> when neither a custom threshold nor a typical duration is available.</returns>
    public static double? EffectiveThresholdMs(double? typicalMs, double? customThresholdMs = null) =>
        customThresholdMs is > 0 ? customThresholdMs
        : typicalMs is > 0 ? Factor * typicalMs.Value
        : null;

    /// <summary>Determines whether a call duration exceeds its effective slow threshold.</summary>
    /// <param name="durationMs">The measured call duration in milliseconds.</param>
    /// <param name="typicalMs">The typical (median) duration in milliseconds for the endpoint, or <c>null</c> if unavailable.</param>
    /// <param name="customThresholdMs">An optional explicit threshold in milliseconds overriding the automatic rule.</param>
    /// <returns><c>true</c> if an effective threshold exists and <paramref name="durationMs"/> is strictly greater than it; otherwise, <c>false</c>.</returns>
    public static bool IsSlow(double durationMs, double? typicalMs, double? customThresholdMs = null) =>
        EffectiveThresholdMs(typicalMs, customThresholdMs) is { } limit && durationMs > limit;

    // ---- the graded colour (brain 0041): the slow colour starts at the typical time and gets stronger the further off a call is ----

    /// <summary>
    /// How many times its typical duration a call took (1 when not slower or no baseline yet).
    /// When a custom threshold is set, scales smoothly from 1 at the baseline (typical, or half the limit) up to <see cref="Factor"/> at the limit.
    /// </summary>
    /// <param name="durationMs">The measured call duration in milliseconds.</param>
    /// <param name="typicalMs">The typical duration in milliseconds, or <c>null</c> if unavailable.</param>
    /// <param name="customThresholdMs">An optional explicit slow threshold in milliseconds.</param>
    /// <returns>A ratio greater than or equal to 1 representing how far above the baseline the call ran.</returns>
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
    /// <param name="ratio">The duration ratio computed by <see cref="Ratio"/>.</param>
    /// <returns>An integer colour step from <c>0</c> (green) to <see cref="MaxStep"/> (full amber).</returns>
    public static int Step(double ratio)
    {
        if (!(ratio > 1)) return 0;
        if (ratio >= Factor) return MaxStep;
        return Math.Clamp((int)Math.Ceiling((ratio - 1) / (Factor - 1) * MaxStep), 1, MaxStep - 1);
    }

    private const double CodeScale = 32;

    /// <summary>The ratio in one byte (1/32 steps up to about 8.9x), rounded down so a call under the slow line never reads as slow. 0 is "at typical".</summary>
    /// <param name="ratio">The duration ratio to encode.</param>
    /// <returns>The quantized byte code representing the ratio.</returns>
    public static byte Encode(double ratio) => (byte)Math.Clamp(Math.Floor((Math.Max(1, ratio) - 1) * CodeScale), 0, 255);

    /// <summary>Decodes a byte ratio code previously produced by <see cref="Encode"/> back into a floating-point ratio.</summary>
    /// <param name="code">The encoded byte ratio.</param>
    /// <returns>The decoded ratio (greater than or equal to 1).</returns>
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
    /// <param name="endpoint">The normalized endpoint name.</param>
    /// <returns>The median duration in milliseconds once at least <see cref="SlowCallRule.MinSamples"/> samples exist; otherwise, <c>null</c>.</returns>
    public double? Typical(string endpoint) => Typical(-1, endpoint);

    /// <summary>Records an endpoint sample across the run.</summary>
    /// <param name="endpoint">The normalized endpoint name.</param>
    /// <param name="durationMs">The measured call duration in milliseconds.</param>
    public void Add(string endpoint, double durationMs) => Add(-1, endpoint, durationMs);

    /// <summary>Returns the typical duration of a call within a specific scenario entry.</summary>
    /// <param name="entry">The zero-based scenario entry index (or <c>-1</c> for run-wide endpoint tracking).</param>
    /// <param name="call">The call or endpoint name.</param>
    /// <returns>The median duration in milliseconds once at least <see cref="SlowCallRule.MinSamples"/> samples exist; otherwise, <c>null</c>.</returns>
    public double? Typical(int entry, string call) =>
        series.TryGetValue((entry, call), out var s) && s.Count >= SlowCallRule.MinSamples ? s.Median : null;

    /// <summary>Records a duration sample for a call within a specific scenario entry.</summary>
    /// <param name="entry">The zero-based scenario entry index (or <c>-1</c> for run-wide endpoint tracking).</param>
    /// <param name="call">The call or endpoint name.</param>
    /// <param name="durationMs">The measured call duration in milliseconds.</param>
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
