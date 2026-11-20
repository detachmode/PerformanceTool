namespace PerformanceTool.Services;

public sealed record LatencyStats(int Count, int Errors, double Min, double Avg, double P50, double P95, double P99, double Max)
{
    public static readonly LatencyStats Empty = new(0, 0, 0, 0, 0, 0, 0, 0);

    public static LatencyStats From(IReadOnlyCollection<ExecutionResult> results)
    {
        if (results.Count == 0) return Empty;
        var sorted = results.Select(r => r.ElapsedMs).Order().ToArray();
        double P(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new(sorted.Length, results.Count(r => !r.Success), sorted[0], sorted.Average(), P(0.50), P(0.95), P(0.99), sorted[^1]);
    }
}
