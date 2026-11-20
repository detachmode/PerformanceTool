using PerformanceTool.Client.Services;

namespace PerformanceTool.Client.Components;

/// <summary>
/// The few statistics of the scenario popup (brain 0030) for the actors that started in a time window: typical duration (median), the slowest 5 %
/// (p95), the longest, and the error and slow rates. <see cref="MedianIndex"/> / <see cref="LongestIndex"/> are positions in the finished-actor list
/// (for drilling down to that actor). One pass plus one sort of the durations; the caller recomputes at most every 0.7 s (brain 0021).
/// </summary>
public sealed record ActorStats(int Count, int Failed, int Slow, double Median, double P95, double Longest, int MedianIndex, int LongestIndex)
{
    public static readonly ActorStats Empty = new(0, 0, 0, 0, 0, 0, -1, -1);

    public double ErrorRate => Count == 0 ? 0 : 100.0 * Failed / Count;
    public double SlowRate => Count == 0 ? 0 : 100.0 * Slow / Count;

    /// <summary>True when the slowest 5 % are slow by the one slow rule (<see cref="SlowCallRule"/>), measured against the typical duration.</summary>
    public bool TailIsSlow => Count >= SlowCallRule.MinSamples && SlowCallRule.IsSlow(P95, Median);
    public bool LongestIsSlow => Count >= SlowCallRule.MinSamples && SlowCallRule.IsSlow(Longest, Median);

    public static ActorStats Compute(IReadOnlyList<ActorRecord> actors, int count, double from, double to)
    {
        var durations = new List<(double Ms, int Index)>();
        var failed = 0; var slow = 0;
        for (var i = 0; i < count; i++)
        {
            var a = actors[i];
            if (a.StartedAt < from || a.StartedAt >= to) continue;
            durations.Add((a.DurationMs, i));
            if (a.Failed) failed++; else if (a.SlowCalls > 0) slow++;
        }
        var n = durations.Count;
        if (n == 0) return Empty;
        durations.Sort((x, y) => x.Ms.CompareTo(y.Ms));
        var med = durations[(n - 1) / 2];
        var p95 = durations[Math.Max(0, (int)Math.Ceiling(0.95 * n) - 1)];
        return new(n, failed, slow, med.Ms, p95.Ms, durations[n - 1].Ms, med.Index, durations[n - 1].Index);
    }
}
