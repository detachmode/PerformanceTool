using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Contracts;

/// <summary>One flat call record fed into <see cref="CallAnalytics"/> by DuckDB, InMemory storage, or the client's Fake store.</summary>
public readonly record struct CallFact(
    long Id,
    int Sequence,
    int Entry,
    int Step,
    int Rep,
    double StartedAtSeconds,
    double OffsetMs,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    string RawName,
    string? Url,
    string? Endpoint,
    string? Method,
    string? Host,
    double? SlowThresholdMs)
{
    public double AtSeconds => StartedAtSeconds + OffsetMs / 1000.0;
    public string NormalizedEndpoint => string.IsNullOrWhiteSpace(Endpoint) ? EndpointNormalizer.NormalizeCallName(RawName) : Endpoint.Trim();
}

/// <summary>
/// Shared endpoint-centric call aggregation used identically across DuckDB storage, in-memory storage, and Fake mode (brain 0060).
/// Handles multi-host disambiguation, adaptive slug collapse, per-endpoint/per-call slow thresholds, bounded timelines, and duration histograms.
/// </summary>
public static class CallAnalytics
{
    private static readonly double[] NiceTimeWidths = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600, 7200];
    private static readonly double[] NiceMsSteps = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10_000, 20_000, 50_000, 100_000];

    public static CallEndpointsResult BuildEndpoints(
        IReadOnlyList<CallFact> allCalls,
        int? entry = null,
        bool slowOnly = false,
        bool failedOnly = false,
        double? minMs = null,
        string? search = null)
    {
        if (allCalls.Count == 0)
            return new CallEndpointsResult([], 0, 0, 0);

        var map = EndpointGrouper.BuildMap(allCalls.Select(c => (c.NormalizedEndpoint, c.Host)));
        var grouped = new Dictionary<string, List<CallFact>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < allCalls.Count; i++)
        {
            var c = allCalls[i];
            if (entry is { } e && c.Entry != e) continue;
            var key = CanonicalOf(map, c);
            if (!grouped.TryGetValue(key, out var list))
                grouped[key] = list = [];
            list.Add(c);
        }

        var needle = search?.Trim() ?? "";
        var summaries = new List<CallEndpointSummaryDto>(grouped.Count);

        foreach (var (endpoint, list) in grouped)
        {
            var summary = Summarize(endpoint, list, out _, out _);
            if (slowOnly && summary.SlowCount == 0) continue;
            if (failedOnly && summary.FailedCount == 0) continue;
            if (minMs is > 0 && summary.MaxMs < minMs.Value) continue;
            if (needle.Length > 0
                && !summary.Endpoint.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && (summary.Host is null || !summary.Host.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                continue;
            summaries.Add(summary);
        }

        summaries.Sort(CompareSummaries);

        long total = 0, slow = 0, failed = 0;
        foreach (var s in summaries)
        {
            total += s.TotalCount;
            slow += s.SlowCount;
            failed += s.FailedCount;
        }
        return new CallEndpointsResult(summaries, total, slow, failed);
    }

    public static CallEndpointDetailDto? BuildEndpointDetail(
        IReadOnlyList<CallFact> allCalls,
        string endpoint,
        double runElapsedSeconds,
        int? entry = null,
        string? show = null,
        double? minMs = null,
        double? maxMs = null,
        double? fromSec = null,
        double? toSec = null,
        string? sort = null,
        int offset = 0,
        int limit = 200,
        int maxPoints = 240)
    {
        if (allCalls.Count == 0 || string.IsNullOrWhiteSpace(endpoint))
            return null;

        var map = EndpointGrouper.BuildMap(allCalls.Select(c => (c.NormalizedEndpoint, c.Host)));
        var target = endpoint.Trim();
        var endpointCalls = new List<CallFact>();

        for (var i = 0; i < allCalls.Count; i++)
        {
            var c = allCalls[i];
            if (entry is { } e && c.Entry != e) continue;
            if (string.Equals(CanonicalOf(map, c), target, StringComparison.OrdinalIgnoreCase))
                endpointCalls.Add(c);
        }

        if (endpointCalls.Count == 0)
            return null;

        var summary = Summarize(target, endpointCalls, out var autoTypical, out var groupCustomThreshold);

        var showNorm = show?.Trim().ToLowerInvariant();
        var evaluated = new List<(CallFact Call, bool IsSlow, double? EffectiveThresholdMs)>(endpointCalls.Count);
        foreach (var c in endpointCalls)
        {
            var effLimit = SlowCallRule.EffectiveThresholdMs(autoTypical, c.SlowThresholdMs ?? groupCustomThreshold);
            var isSlow = c.Error is null && SlowCallRule.IsSlow(c.DurationMs, autoTypical, c.SlowThresholdMs ?? groupCustomThreshold);
            evaluated.Add((c, isSlow, effLimit));
        }

        // Histogram respects outcome + time window so clicking a duration bucket keeps the full distribution visible.
        var histBase = new List<(CallFact Call, bool IsSlow, double? EffectiveThresholdMs)>(evaluated.Count);
        foreach (var item in evaluated)
        {
            if (showNorm == "slow" && !item.IsSlow) continue;
            if (showNorm == "failed" && item.Call.Error is null) continue;
            var t = item.Call.AtSeconds;
            if (fromSec is { } fs && t < fs) continue;
            if (toSec is { } ts && t > ts) continue;
            histBase.Add(item);
        }

        var histogram = BuildHistogram(histBase);

        var matching = new List<(CallFact Call, bool IsSlow, double? EffectiveThresholdMs)>(histBase.Count);
        foreach (var item in histBase)
        {
            if (minMs is { } lo && item.Call.DurationMs < lo) continue;
            if (maxMs is { } hi && item.Call.DurationMs >= hi) continue;
            matching.Add(item);
        }

        var timeline = BuildTimeline(matching, summary.TypicalMs, maxPoints);

        switch (sort?.Trim().ToLowerInvariant())
        {
            case "time":
                matching.Sort((a, b) =>
                {
                    var cmp = a.Call.AtSeconds.CompareTo(b.Call.AtSeconds);
                    return cmp != 0 ? cmp : a.Call.Id.CompareTo(b.Call.Id);
                });
                break;
            case "failed":
                matching.Sort((a, b) =>
                {
                    var cmp = (b.Call.Error is not null).CompareTo(a.Call.Error is not null);
                    if (cmp != 0) return cmp;
                    cmp = b.IsSlow.CompareTo(a.IsSlow);
                    if (cmp != 0) return cmp;
                    cmp = b.Call.DurationMs.CompareTo(a.Call.DurationMs);
                    return cmp != 0 ? cmp : a.Call.Id.CompareTo(b.Call.Id);
                });
                break;
            default:
                matching.Sort((a, b) =>
                {
                    var cmp = b.Call.DurationMs.CompareTo(a.Call.DurationMs);
                    if (cmp != 0) return cmp;
                    return a.Call.AtSeconds.CompareTo(b.Call.AtSeconds);
                });
                break;
        }

        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);
        var paged = new List<CallSampleDto>(Math.Min(limit, Math.Max(0, matching.Count - offset)));
        for (var i = offset; i < matching.Count && paged.Count < limit; i++)
        {
            var (c, isSlow, effLimit) = matching[i];
            paged.Add(new CallSampleDto(
                c.Id,
                c.Sequence,
                $"B{c.Sequence + 1}",
                c.Entry,
                c.Step,
                c.Rep,
                c.AtSeconds,
                c.OffsetMs,
                c.DurationMs,
                c.StatusCode,
                c.Bytes,
                c.Error,
                c.RawName,
                c.Url,
                summary.Endpoint,
                isSlow,
                summary.TypicalMs > 0 ? summary.TypicalMs : null,
                effLimit));
        }

        return new CallEndpointDetailDto(
            summary,
            runElapsedSeconds,
            timeline,
            histogram,
            matching.Count,
            offset,
            limit,
            paged);
    }

    private static string CanonicalOf(Dictionary<(string RawEndpoint, string? Host), string> map, in CallFact c)
    {
        var host = string.IsNullOrWhiteSpace(c.Host) ? null : c.Host.Trim();
        return map.TryGetValue((c.NormalizedEndpoint, host), out var canonical) ? canonical : c.NormalizedEndpoint;
    }

    private static CallEndpointSummaryDto Summarize(
        string endpoint,
        List<CallFact> list,
        out double? autoTypical,
        out double? customThreshold)
    {
        var entries = new SortedSet<int>();
        string? method = null;
        string? singleHost = null;
        var mixedHosts = false;
        double? custom = null;

        var okDurations = new List<double>(list.Count);
        var allDurations = new List<double>(list.Count);
        double minMs = double.MaxValue, maxMs = 0, sumOk = 0, sumAll = 0;
        var failedCount = 0;

        foreach (var c in list)
        {
            if (c.Entry >= 0) entries.Add(c.Entry);
            method ??= string.IsNullOrWhiteSpace(c.Method) ? EndpointNormalizer.TryExtractMethod(endpoint) : c.Method;

            if (!mixedHosts && !string.IsNullOrWhiteSpace(c.Host))
            {
                var h = c.Host.Trim();
                if (singleHost is null) singleHost = h;
                else if (!string.Equals(singleHost, h, StringComparison.OrdinalIgnoreCase)) mixedHosts = true;
            }

            if (c.SlowThresholdMs is > 0)
                custom = custom is { } prev ? Math.Min(prev, c.SlowThresholdMs.Value) : c.SlowThresholdMs.Value;

            allDurations.Add(c.DurationMs);
            sumAll += c.DurationMs;
            if (c.DurationMs < minMs) minMs = c.DurationMs;
            if (c.DurationMs > maxMs) maxMs = c.DurationMs;

            if (c.Error is not null)
            {
                failedCount++;
            }
            else
            {
                okDurations.Add(c.DurationMs);
                sumOk += c.DurationMs;
            }
        }

        var baseline = okDurations.Count > 0 ? okDurations : allDurations;
        baseline.Sort();
        var typicalMs = baseline[(baseline.Count - 1) / 2];
        var p95Ms = baseline[Math.Max(0, (int)Math.Ceiling(0.95 * baseline.Count) - 1)];
        var avgMs = okDurations.Count > 0 ? sumOk / okDurations.Count : sumAll / Math.Max(1, list.Count);
        if (minMs == double.MaxValue) minMs = 0;

        autoTypical = okDurations.Count >= SlowCallRule.MinSamples ? typicalMs : null;
        customThreshold = custom;

        var slowCount = 0;
        foreach (var c in list)
        {
            if (c.Error is null && SlowCallRule.IsSlow(c.DurationMs, autoTypical, c.SlowThresholdMs ?? custom))
                slowCount++;
        }

        var effectiveThreshold = SlowCallRule.EffectiveThresholdMs(autoTypical, custom);

        return new CallEndpointSummaryDto(
            endpoint,
            method,
            mixedHosts ? null : singleHost,
            list.Count,
            slowCount,
            failedCount,
            typicalMs,
            p95Ms,
            maxMs,
            minMs,
            avgMs,
            effectiveThreshold,
            custom is > 0,
            entries.ToArray());
    }

    private static List<CallHistogramBucketDto> BuildHistogram(List<(CallFact Call, bool IsSlow, double? EffectiveThresholdMs)> items)
    {
        if (items.Count == 0) return [];
        double max = 0;
        foreach (var item in items)
            if (item.Call.DurationMs > max) max = item.Call.DurationMs;

        var targetStep = Math.Max(1, max) / 14.0;
        var step = NiceMsSteps.FirstOrDefault(s => s >= targetStep, Math.Ceiling(targetStep / 10_000) * 10_000);
        if (step <= 0) step = 1;

        var binCount = Math.Clamp((int)Math.Floor(max / step) + 1, 1, 24);
        if ((int)Math.Floor(max / step) + 1 > 24)
        {
            step = NiceMsSteps.FirstOrDefault(s => (int)Math.Floor(max / s) + 1 <= 24, Math.Ceiling((max + 1) / 20.0));
            binCount = Math.Clamp((int)Math.Floor(max / step) + 1, 1, 24);
        }

        var total = new int[binCount];
        var slow = new int[binCount];
        var failed = new int[binCount];

        foreach (var item in items)
        {
            var idx = Math.Clamp((int)Math.Floor(Math.Max(0, item.Call.DurationMs) / step), 0, binCount - 1);
            total[idx]++;
            if (item.Call.Error is not null) failed[idx]++;
            else if (item.IsSlow) slow[idx]++;
        }

        var buckets = new List<CallHistogramBucketDto>(binCount);
        for (var i = 0; i < binCount; i++)
            buckets.Add(new CallHistogramBucketDto(i * step, (i + 1) * step, total[i], slow[i], failed[i]));
        return buckets;
    }

    private static List<CallTimelinePointDto> BuildTimeline(
        List<(CallFact Call, bool IsSlow, double? EffectiveThresholdMs)> items,
        double endpointTypicalMs,
        int maxPoints)
    {
        if (items.Count == 0) return [];
        maxPoints = Math.Clamp(maxPoints, 20, 500);

        var sorted = new List<(CallFact Call, bool IsSlow)>(items.Count);
        foreach (var item in items) sorted.Add((item.Call, item.IsSlow));
        sorted.Sort((a, b) =>
        {
            var cmp = a.Call.AtSeconds.CompareTo(b.Call.AtSeconds);
            return cmp != 0 ? cmp : a.Call.Id.CompareTo(b.Call.Id);
        });

        if (sorted.Count <= maxPoints)
        {
            var points = new List<CallTimelinePointDto>(sorted.Count);
            foreach (var (c, isSlow) in sorted)
            {
                points.Add(new CallTimelinePointDto(
                    c.AtSeconds,
                    0,
                    c.DurationMs,
                    endpointTypicalMs,
                    1,
                    isSlow ? 1 : 0,
                    c.Error is not null ? 1 : 0,
                    c.Id,
                    c.Sequence,
                    c.Entry,
                    c.StatusCode,
                    c.RawName,
                    c.Error));
            }
            return points;
        }

        var minT = sorted[0].Call.AtSeconds;
        var maxT = sorted[^1].Call.AtSeconds;
        var span = Math.Max(0.1, maxT - minT);
        var width = NiceTimeWidths.FirstOrDefault(w => span / w < maxPoints, Math.Ceiling(span / maxPoints));

        var buckets = new SortedDictionary<long, List<(CallFact Call, bool IsSlow)>>();
        foreach (var item in sorted)
        {
            var slot = (long)Math.Floor(Math.Max(0, item.Call.AtSeconds) / width);
            if (!buckets.TryGetValue(slot, out var list))
                buckets[slot] = list = [];
            list.Add(item);
        }

        var result = new List<CallTimelinePointDto>(buckets.Count);
        var durs = new List<double>();
        foreach (var (slot, list) in buckets)
        {
            durs.Clear();
            double maxDur = 0;
            int slow = 0, failed = 0;
            foreach (var (c, isSlow) in list)
            {
                durs.Add(c.DurationMs);
                if (c.DurationMs > maxDur) maxDur = c.DurationMs;
                if (c.Error is not null) failed++;
                else if (isSlow) slow++;
            }
            durs.Sort();
            var median = durs[(durs.Count - 1) / 2];
            result.Add(new CallTimelinePointDto(Math.Round(slot * width, 3), width, maxDur, median, list.Count, slow, failed));
        }
        return result;
    }

    private static int CompareSummaries(CallEndpointSummaryDto a, CallEndpointSummaryDto b)
    {
        var cmp = b.SlowCount.CompareTo(a.SlowCount);
        if (cmp != 0) return cmp;
        cmp = b.FailedCount.CompareTo(a.FailedCount);
        if (cmp != 0) return cmp;
        cmp = b.P95Ms.CompareTo(a.P95Ms);
        if (cmp != 0) return cmp;
        cmp = b.MaxMs.CompareTo(a.MaxMs);
        return cmp != 0 ? cmp : string.Compare(a.Endpoint, b.Endpoint, StringComparison.OrdinalIgnoreCase);
    }
}
