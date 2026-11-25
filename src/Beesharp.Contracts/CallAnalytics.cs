using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Contracts;

/// <summary>One flat call record fed into <see cref="CallAnalytics"/> by DuckDB, InMemory storage, or the client's Fake store.</summary>
/// <param name="Id">Unique call record identifier.</param>
/// <param name="Sequence">Zero-based global bee execution sequence number.</param>
/// <param name="Entry">Zero-based scenario entry index in the load plan.</param>
/// <param name="Step">Zero-based step index during which this call was recorded.</param>
/// <param name="Rep">Zero-based repetition index within the step.</param>
/// <param name="StartedAtSeconds">Run-relative start time of the parent bee in seconds.</param>
/// <param name="OffsetMs">Start offset of the call in milliseconds relative to the bee's execution start.</param>
/// <param name="DurationMs">Total call duration in milliseconds.</param>
/// <param name="StatusCode">Optional HTTP or custom status code returned by the call.</param>
/// <param name="Bytes">Number of payload bytes transferred.</param>
/// <param name="Error">Optional error message if the call failed.</param>
/// <param name="RawName">Raw unnormalized call name.</param>
/// <param name="Url">Optional full request URL.</param>
/// <param name="Endpoint">Optional normalized endpoint key; falls back to normalizing <see cref="RawName"/> when blank.</param>
/// <param name="Method">Optional HTTP method (for example <c>GET</c>, <c>POST</c>).</param>
/// <param name="Host">Optional target host name.</param>
/// <param name="SlowThresholdMs">Optional explicit slow threshold in milliseconds for this call.</param>
/// <param name="TtfbMs">Optional time-to-first-byte in milliseconds when HTTP timing split is enabled.</param>
/// <param name="ResponseReadMs">Optional response body read duration in milliseconds when HTTP timing split is enabled.</param>
/// <param name="Headers">Optional captured request/response headers for slow or failed calls.</param>
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
    double? SlowThresholdMs,
    double? TtfbMs = null,
    double? ResponseReadMs = null,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>Gets the run-relative timestamp in seconds at which the call started.</summary>
    public double AtSeconds => StartedAtSeconds + OffsetMs / 1000.0;

    /// <summary>Gets the normalized endpoint key, falling back to normalizing <see cref="RawName"/> when <see cref="Endpoint"/> is blank.</summary>
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

    /// <summary>Resolves 0-based scenario entry indices matching a scenario ID or display label (case-insensitive).</summary>
    /// <param name="plan">The load plan whose scenario entries are searched.</param>
    /// <param name="scenarioFilter">Scenario ID or display label to match (leading/trailing whitespace is trimmed).</param>
    /// <returns>A set of matching 0-based scenario entry indices.</returns>
    public static HashSet<int> ResolveScenarioEntries(LoadPlan plan, string scenarioFilter)
    {
        var needle = scenarioFilter.Trim();
        var set = new HashSet<int>();
        for (var i = 0; i < plan.Scenarios.Count; i++)
        {
            var s = plan.Scenarios[i];
            if (string.Equals(s.ScenarioId, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Label, needle, StringComparison.OrdinalIgnoreCase))
            {
                set.Add(i);
            }
        }
        return set;
    }

    /// <summary>
    /// Groups recorded calls into canonical endpoints (or endpoint/entry/step buckets when <paramref name="byStep"/> is <see langword="true"/>)
    /// and computes latency percentiles, slow/failure counts, and totals.
    /// </summary>
    /// <param name="allCalls">All recorded call facts for the run.</param>
    /// <param name="entry">Optional 0-based scenario entry index filter.</param>
    /// <param name="slowOnly">When <see langword="true"/>, keeps only endpoints with at least one slow call.</param>
    /// <param name="failedOnly">When <see langword="true"/>, keeps only endpoints with at least one failed call.</param>
    /// <param name="minMs">Optional minimum maximum-duration filter in milliseconds.</param>
    /// <param name="search">Optional case-insensitive substring filter matched against endpoint, host, or step name.</param>
    /// <param name="step">Optional 0-based step index filter.</param>
    /// <param name="byStep">When <see langword="true"/>, produces separate summaries per (endpoint, entry, step) instead of merging across steps.</param>
    /// <param name="resolveStepName">Optional callback resolving a human-readable step name from <c>(entry, step)</c>.</param>
    /// <param name="entries">Optional set of allowed 0-based scenario entry indices.</param>
    /// <param name="endpointFilter">Optional case-insensitive substring filter matched strictly against the canonical endpoint name.</param>
    /// <returns>A <see cref="CallEndpointsResult"/> containing sorted summaries and aggregate call totals.</returns>
    public static CallEndpointsResult BuildEndpoints(
        IReadOnlyList<CallFact> allCalls,
        int? entry = null,
        bool slowOnly = false,
        bool failedOnly = false,
        double? minMs = null,
        string? search = null,
        int? step = null,
        bool byStep = false,
        Func<int, int, string?>? resolveStepName = null,
        IReadOnlySet<int>? entries = null,
        string? endpointFilter = null)
    {
        if (allCalls.Count == 0 || (entries is not null && entries.Count == 0))
            return new CallEndpointsResult([], 0, 0, 0);

        var map = EndpointGrouper.BuildMap(allCalls.Select(c => (c.NormalizedEndpoint, c.Host)));
        var needle = search?.Trim() ?? "";
        var epFilter = endpointFilter?.Trim() ?? "";
        var summaries = new List<CallEndpointSummaryDto>();

        if (byStep)
        {
            var groupedByStep = new Dictionary<(string Endpoint, int Entry, int Step), List<CallFact>>(StepGroupKeyComparer.Instance);
            for (var i = 0; i < allCalls.Count; i++)
            {
                var c = allCalls[i];
                if (entry is { } e && c.Entry != e) continue;
                if (entries is not null && !entries.Contains(c.Entry)) continue;
                if (step is { } st && c.Step != st) continue;
                var key = (CanonicalOf(map, c), c.Entry, c.Step);
                if (!groupedByStep.TryGetValue(key, out var list))
                    groupedByStep[key] = list = [];
                list.Add(c);
            }

            foreach (var ((endpoint, grpEntry, grpStep), list) in groupedByStep)
            {
                var stepName = resolveStepName?.Invoke(grpEntry, grpStep);
                var summary = Summarize(endpoint, list, out _, out _, grpStep, stepName);
                if (slowOnly && summary.SlowCount == 0) continue;
                if (failedOnly && summary.FailedCount == 0) continue;
                if (minMs is > 0 && summary.MaxMs < minMs.Value) continue;
                if (epFilter.Length > 0 && !summary.Endpoint.Contains(epFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (needle.Length > 0
                    && !summary.Endpoint.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    && (summary.Host is null || !summary.Host.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    && (summary.StepName is null || !summary.StepName.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                    continue;
                summaries.Add(summary);
            }
        }
        else
        {
            var grouped = new Dictionary<string, List<CallFact>>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < allCalls.Count; i++)
            {
                var c = allCalls[i];
                if (entry is { } e && c.Entry != e) continue;
                if (entries is not null && !entries.Contains(c.Entry)) continue;
                if (step is { } st && c.Step != st) continue;
                var key = CanonicalOf(map, c);
                if (!grouped.TryGetValue(key, out var list))
                    grouped[key] = list = [];
                list.Add(c);
            }

            foreach (var (endpoint, list) in grouped)
            {
                var summary = Summarize(endpoint, list, out _, out _);
                if (slowOnly && summary.SlowCount == 0) continue;
                if (failedOnly && summary.FailedCount == 0) continue;
                if (minMs is > 0 && summary.MaxMs < minMs.Value) continue;
                if (epFilter.Length > 0 && !summary.Endpoint.Contains(epFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (needle.Length > 0
                    && !summary.Endpoint.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    && (summary.Host is null || !summary.Host.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                    continue;
                summaries.Add(summary);
            }
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

    /// <summary>
    /// Returns the top-<paramref name="limit"/> slowest individual calls matching the optional filters,
    /// with slowness evaluated per (endpoint, entry, step) so shared URLs keep independent baselines.
    /// </summary>
    /// <param name="allCalls">All recorded call facts for the run.</param>
    /// <param name="limit">Maximum number of calls to return (clamped to <c>1..1000</c>).</param>
    /// <param name="entry">Optional 0-based scenario entry index filter.</param>
    /// <param name="entries">Optional set of allowed 0-based scenario entry indices.</param>
    /// <param name="step">Optional 0-based step index filter.</param>
    /// <param name="endpointFilter">Optional case-insensitive substring filter matched against the canonical endpoint name.</param>
    /// <param name="slowOnly">When <see langword="true"/>, includes only calls classified as slow.</param>
    /// <param name="failedOnly">When <see langword="true"/>, includes only failed calls.</param>
    /// <param name="minMs">Optional minimum call duration filter in milliseconds.</param>
    /// <param name="search">Optional case-insensitive substring filter matched against endpoint, URL, host, or step name.</param>
    /// <param name="resolveEntryLabel">Optional callback resolving a display label from a 0-based scenario entry index.</param>
    /// <param name="resolveStepName">Optional callback resolving a human-readable step name from <c>(entry, step)</c>.</param>
    /// <returns>A <see cref="SlowestCallsResult"/> containing up to <paramref name="limit"/> calls sorted by descending duration and the total matching count.</returns>
    public static SlowestCallsResult BuildSlowestCalls(
        IReadOnlyList<CallFact> allCalls,
        int limit,
        int? entry = null,
        IReadOnlySet<int>? entries = null,
        int? step = null,
        string? endpointFilter = null,
        bool slowOnly = false,
        bool failedOnly = false,
        double? minMs = null,
        string? search = null,
        Func<int, string?>? resolveEntryLabel = null,
        Func<int, int, string?>? resolveStepName = null)
    {
        if (allCalls.Count == 0 || limit <= 0 || (entries is not null && entries.Count == 0))
            return new SlowestCallsResult([], 0);

        limit = Math.Clamp(limit, 1, 1000);
        var map = EndpointGrouper.BuildMap(allCalls.Select(c => (c.NormalizedEndpoint, c.Host)));
        var groupedByStep = new Dictionary<(string Endpoint, int Entry, int Step), List<CallFact>>(StepGroupKeyComparer.Instance);
        for (var i = 0; i < allCalls.Count; i++)
        {
            var c = allCalls[i];
            var key = (CanonicalOf(map, c), c.Entry, c.Step);
            if (!groupedByStep.TryGetValue(key, out var list))
                groupedByStep[key] = list = [];
            list.Add(c);
        }

        var baselines = new Dictionary<(string Endpoint, int Entry, int Step), (double? AutoTypical, double? GroupCustom)>(StepGroupKeyComparer.Instance);
        foreach (var (key, list) in groupedByStep)
        {
            Summarize(key.Endpoint, list, out var autoTypical, out var custom, key.Step, null);
            baselines[key] = (autoTypical, custom);
        }

        var epFilter = endpointFilter?.Trim() ?? "";
        var needle = search?.Trim() ?? "";
        var matching = new List<(CallFact Call, string Endpoint, bool IsSlow, double? EffLimit, string? StepName)>();

        for (var i = 0; i < allCalls.Count; i++)
        {
            var c = allCalls[i];
            if (entry is { } e && c.Entry != e) continue;
            if (entries is not null && !entries.Contains(c.Entry)) continue;
            if (step is { } st && c.Step != st) continue;
            if (minMs is > 0 && c.DurationMs < minMs.Value) continue;
            if (failedOnly && c.Error is null) continue;

            var ep = CanonicalOf(map, c);
            if (epFilter.Length > 0 && !ep.Contains(epFilter, StringComparison.OrdinalIgnoreCase)) continue;

            var (autoTypical, groupCustom) = baselines[(ep, c.Entry, c.Step)];
            var effLimit = SlowCallRule.EffectiveThresholdMs(autoTypical, c.SlowThresholdMs ?? groupCustom);
            var isSlow = c.Error is null && SlowCallRule.IsSlow(c.DurationMs, autoTypical, c.SlowThresholdMs ?? groupCustom);
            if (slowOnly && !isSlow) continue;

            var stepName = resolveStepName?.Invoke(c.Entry, c.Step);
            if (needle.Length > 0
                && !ep.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && (c.Url is null || !c.Url.Contains(needle, StringComparison.OrdinalIgnoreCase))
                && (c.Host is null || !c.Host.Contains(needle, StringComparison.OrdinalIgnoreCase))
                && (stepName is null || !stepName.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                continue;

            matching.Add((c, ep, isSlow, effLimit, stepName));
        }

        matching.Sort((a, b) =>
        {
            var cmp = b.Call.DurationMs.CompareTo(a.Call.DurationMs);
            if (cmp != 0) return cmp;
            cmp = a.Call.AtSeconds.CompareTo(b.Call.AtSeconds);
            return cmp != 0 ? cmp : a.Call.Id.CompareTo(b.Call.Id);
        });

        var take = Math.Min(limit, matching.Count);
        var items = new List<SlowestCallDto>(take);
        for (var i = 0; i < take; i++)
        {
            var (c, ep, isSlow, effLimit, stepName) = matching[i];
            items.Add(new SlowestCallDto(
                c.Id,
                c.Sequence,
                $"B{c.Sequence + 1}",
                c.Entry,
                resolveEntryLabel?.Invoke(c.Entry),
                c.Step,
                stepName,
                c.Rep,
                c.AtSeconds,
                c.DurationMs,
                c.StatusCode,
                c.Bytes,
                c.Error,
                ep,
                c.Url,
                isSlow,
                effLimit,
                c.TtfbMs,
                c.ResponseReadMs,
                c.Headers));
        }

        return new SlowestCallsResult(items, matching.Count);
    }

    /// <summary>
    /// Aggregates per-(entry, step) call latency percentiles (min/p50/p95/max/avg) and slow/failed counts.
    /// Slowness is evaluated per (endpoint, entry, step) so steps sharing the same URL keep independent baselines.
    /// </summary>
    /// <param name="allCalls">All recorded call facts for the run.</param>
    /// <returns>A dictionary keyed by <c>(Entry, Step)</c> mapping each step to its aggregated <see cref="CallStepMetrics"/>.</returns>
    public static Dictionary<(int Entry, int Step), CallStepMetrics> BuildStepMetrics(IReadOnlyList<CallFact> allCalls)
    {
        var result = new Dictionary<(int Entry, int Step), CallStepMetrics>();
        if (allCalls.Count == 0)
            return result;

        var map = EndpointGrouper.BuildMap(allCalls.Select(c => (c.NormalizedEndpoint, c.Host)));
        var byStep = new Dictionary<(int Entry, int Step), List<CallFact>>();
        for (var i = 0; i < allCalls.Count; i++)
        {
            var c = allCalls[i];
            if (c.Entry < 0 || c.Step < 0) continue;
            var key = (c.Entry, c.Step);
            if (!byStep.TryGetValue(key, out var list))
                byStep[key] = list = [];
            list.Add(c);
        }

        foreach (var ((entry, step), list) in byStep)
        {
            var byEndpoint = new Dictionary<string, List<CallFact>>(StringComparer.OrdinalIgnoreCase);
            var okDurations = new List<double>(list.Count);
            var allDurations = new List<double>(list.Count);
            double sumOk = 0, sumAll = 0;
            var failedCalls = 0;

            foreach (var c in list)
            {
                var ep = CanonicalOf(map, c);
                if (!byEndpoint.TryGetValue(ep, out var epList))
                    byEndpoint[ep] = epList = [];
                epList.Add(c);

                allDurations.Add(c.DurationMs);
                sumAll += c.DurationMs;
                if (c.Error is not null)
                {
                    failedCalls++;
                }
                else
                {
                    okDurations.Add(c.DurationMs);
                    sumOk += c.DurationMs;
                }
            }

            var slowCalls = 0;
            foreach (var (ep, epList) in byEndpoint)
                slowCalls += Summarize(ep, epList, out _, out _, step, null).SlowCount;

            var baseline = okDurations.Count > 0 ? okDurations : allDurations;
            baseline.Sort();
            var minMs = baseline[0];
            var p50Ms = baseline[(baseline.Count - 1) / 2];
            var p95Ms = baseline[Math.Max(0, (int)Math.Ceiling(0.95 * baseline.Count) - 1)];
            var maxMs = baseline[^1];
            var avgMs = okDurations.Count > 0 ? sumOk / okDurations.Count : sumAll / Math.Max(1, list.Count);

            result[(entry, step)] = new CallStepMetrics(
                entry,
                step,
                list.Count,
                slowCalls,
                failedCalls,
                minMs,
                p50Ms,
                p95Ms,
                maxMs,
                avgMs);
        }

        return result;
    }

    /// <summary>
    /// Builds the full drill-down payload for a single canonical endpoint, including summary metrics,
    /// a duration histogram, a bounded duration-over-time timeline, and a paged list of call samples.
    /// </summary>
    /// <param name="allCalls">All recorded call facts for the run.</param>
    /// <param name="endpoint">Canonical endpoint name to inspect.</param>
    /// <param name="runElapsedSeconds">Total elapsed duration of the run in seconds.</param>
    /// <param name="entry">Optional 0-based scenario entry index filter.</param>
    /// <param name="show">Optional outcome filter (<c>"slow"</c> or <c>"failed"</c>).</param>
    /// <param name="minMs">Optional inclusive minimum call duration filter in milliseconds.</param>
    /// <param name="maxMs">Optional exclusive maximum call duration filter in milliseconds.</param>
    /// <param name="fromSec">Optional inclusive lower run-relative time bound in seconds.</param>
    /// <param name="toSec">Optional inclusive upper run-relative time bound in seconds.</param>
    /// <param name="sort">Sort order for paged samples: <c>"time"</c>, <c>"failed"</c>, or default descending duration.</param>
    /// <param name="offset">Zero-based paging offset for the returned <see cref="CallEndpointDetailDto.Calls"/> slice.</param>
    /// <param name="limit">Maximum number of call samples to return per page (clamped to <c>1..1000</c>).</param>
    /// <param name="maxPoints">Maximum number of timeline points/buckets to emit (clamped to <c>20..500</c>).</param>
    /// <returns>The <see cref="CallEndpointDetailDto"/> for <paramref name="endpoint"/>, or <see langword="null"/> if no matching calls exist.</returns>
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
        out double? customThreshold,
        int? step = null,
        string? stepName = null)
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
            entries.ToArray(),
            step,
            stepName);
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
        if (cmp != 0) return cmp;
        cmp = string.Compare(a.Endpoint, b.Endpoint, StringComparison.OrdinalIgnoreCase);
        if (cmp != 0) return cmp;
        var entryA = a.Entries.Count > 0 ? a.Entries[0] : -1;
        var entryB = b.Entries.Count > 0 ? b.Entries[0] : -1;
        cmp = entryA.CompareTo(entryB);
        if (cmp != 0) return cmp;
        return (a.Step ?? -1).CompareTo(b.Step ?? -1);
    }

    private sealed class StepGroupKeyComparer : IEqualityComparer<(string Endpoint, int Entry, int Step)>
    {
        public static readonly StepGroupKeyComparer Instance = new();

        public bool Equals((string Endpoint, int Entry, int Step) x, (string Endpoint, int Entry, int Step) y) =>
            x.Entry == y.Entry
            && x.Step == y.Step
            && string.Equals(x.Endpoint, y.Endpoint, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Endpoint, int Entry, int Step) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Endpoint), obj.Entry, obj.Step);
    }
}
