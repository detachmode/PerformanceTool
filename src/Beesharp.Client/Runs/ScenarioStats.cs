namespace Beesharp.Client.Runs;

/// <summary>
/// Compact statistics of one scenario's finished bees for the overview card (brain 0030). Incremental (brain 0021):
/// it reads only the bees finished since the last call, keeps a small sliding window for the typical duration (median) and
/// the slowest 5 % (p95), and a running maximum for the longest. The percentiles are refreshed every few bees, so the cost
/// per render is constant however long the run is.
/// </summary>
public sealed class ScenarioStats
{
    private const int Window = 200, Refresh = 5;

    private readonly double[] window = new double[Window];
    private int seen, count, next, sinceRefresh;

    public double Median { get; private set; }
    public double P95 { get; private set; }
    public double Longest { get; private set; }
    public bool HasData => seen > 0;

    /// <summary>True when enough bees finished for the slow rule to judge durations.</summary>
    public bool Judgeable => count >= SlowCallRule.MinSamples;
    public bool P95Slow => Judgeable && SlowCallRule.IsSlow(P95, Median);
    public bool LongestSlow => Judgeable && SlowCallRule.IsSlow(Longest, Median);

    public void Update(IReadOnlyList<BeeRecord> bees, int finished)
    {
        finished = Math.Min(finished, bees.Count);
        if (finished < seen) { seen = count = next = sinceRefresh = 0; Median = P95 = Longest = 0; } // a new run
        if (finished == seen) return;
        for (; seen < finished; seen++)
        {
            var d = bees[seen].DurationMs;
            window[next] = d;
            next = (next + 1) % Window;
            if (count < Window) count++;
            if (d > Longest) Longest = d;
            sinceRefresh++;
        }
        if (sinceRefresh >= Refresh || Median == 0)
        {
            sinceRefresh = 0;
            var copy = window.AsSpan(0, count).ToArray();
            Array.Sort(copy);
            Median = copy[copy.Length / 2];
            P95 = copy[Math.Min(copy.Length - 1, (int)Math.Ceiling(copy.Length * 0.95) - 1)];
        }
    }
}
