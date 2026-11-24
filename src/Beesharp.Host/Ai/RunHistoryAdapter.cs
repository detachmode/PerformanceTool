using Beesharp.Ai;
using Beesharp.Services;
using Beesharp.Services.Storage;

namespace Beesharp.AiAdapters;

/// <summary><see cref="IRunHistoryPort"/> over <see cref="RunManager"/> (the live run) and <see cref="IRunStorage"/> (stored runs, calls and logs).</summary>
public sealed class RunHistoryAdapter(RunManager runs, IRunStorage storage) : IRunHistoryPort
{
    private const int MaxBeesScanned = 200_000;

    public async Task<string?> LatestRunIdAsync(CancellationToken ct)
    {
        var current = runs.GetCurrent();
        if (!string.IsNullOrEmpty(current.RunId)) return current.RunId;
        return (await storage.ListRunsAsync(1, ct)).FirstOrDefault()?.RunId;
    }

    public async Task<IReadOnlyList<RunInfo>> ListRunsAsync(int limit, CancellationToken ct) =>
        (await storage.ListRunsAsync(limit, ct)).Select(r => new RunInfo(r.RunId, r.State, r.ElapsedSeconds, r.StartedUtc, r.TotalBees, r.FailedBees, 0, r.TotalLogs, r.Error, [])).ToList();

    public async Task<RunInfo?> GetRunAsync(string runId, CancellationToken ct)
    {
        if (IsCurrent(runId)) return RunAdapter.ToRunInfo(runs.GetCurrent());
        var data = await storage.GetRunAsync(runId, ct);
        if (data is null) return null;

        var entries = data.Plan.Scenarios.Select((c, i) =>
        {
            var mine = data.Bees.Where(b => b.Entry == i).ToList();
            return new EntryFigures(i, string.IsNullOrWhiteSpace(c.Label) ? c.ScenarioId : c.Label, mine.Count, mine.Count, 0,
                mine.Count(b => b.Error is not null), mine.Count(b => b.Error is null && b.SlowCalls > 0), mine.Sum(b => b.SlowCalls), 0, c.EffectiveRatePerMinute());
        }).ToList();
        return new RunInfo(data.RunId, data.State, data.ElapsedSeconds, data.StartedUtc, data.Bees.Count, entries.Sum(e => e.Failed), entries.Sum(e => e.SlowBees), data.TotalLogs, data.Error, entries);
    }

    public async Task<BeePage> ListBeesAsync(string runId, int? entry, BeeOutcomeFilter outcome, int offset, int limit, CancellationToken ct, int? failedStep = null)
    {
        var all = await AllBeesAsync(runId, ct);
        var filtered = all.Where(b => (entry is null || b.Entry == entry)
            && (failedStep is null || (b.Outcome == "failed" && b.FailedStep == failedStep))
            && outcome switch
        {
            BeeOutcomeFilter.Failed => b.Outcome == "failed",
            BeeOutcomeFilter.Slow => b.Outcome == "slow",
            BeeOutcomeFilter.Ok => b.Outcome == "ok",
            _ => true,
        });
        // Failed first, then slow, then ok, each by start time (brain 0028).
        var ordered = filtered.OrderByDescending(b => b.Outcome == "failed").ThenByDescending(b => b.Outcome == "slow").ThenBy(b => b.StartedAt).ToList();
        return new BeePage(ordered.Count, ordered.Skip(offset).Take(limit).ToList());
    }

    public async Task<IReadOnlyList<BeeInfo>> GetBeesAsync(string runId, IReadOnlyList<int> sequences, CancellationToken ct)
    {
        var wanted = sequences.ToHashSet();
        return (await AllBeesAsync(runId, ct)).Where(b => wanted.Contains(b.Sequence)).ToList();
    }

    public async Task<BeeDetailInfo?> GetBeeAsync(string runId, int sequence, CancellationToken ct)
    {
        if (IsCurrent(runId)) await storage.FlushAsync(ct);
        var d = await storage.GetBeeDetailAsync(runId, sequence, ct);
        if (d is null) return null;
        return new BeeDetailInfo(
            new BeeInfo(d.Sequence, d.Entry, d.StartedAt, d.DurationMs, d.StatusCode, d.Bytes, d.Error, d.Calls.Count(c => c.Error is null && SlowCallRule.IsSlow(c.DurationMs, null, c.SlowThresholdMs)), d.FailedStep, d.FailedRep),
            d.Calls.Select(c => new BeeCallInfo(c.Name, c.OffsetMs, c.DurationMs, c.StatusCode, c.Error, c.Step, c.Rep, c.Endpoint ?? c.Name, c.Error is null && SlowCallRule.IsSlow(c.DurationMs, null, c.SlowThresholdMs))).ToList(),
            d.Logs.Select(l => new BeeLogInfo(l.AtSeconds, l.Level, l.Message, l.Exception, l.CallIndex)).ToList());
    }

    public async Task<CallEndpointsPage> ListCallEndpointsAsync(string runId, int? entry, bool slowOnly, bool failedOnly, double? minMs, string? search, CancellationToken ct)
    {
        if (IsCurrent(runId)) await storage.FlushAsync(ct);
        var res = await storage.GetCallEndpointsAsync(runId, new CallEndpointsQuery(entry, slowOnly, failedOnly, minMs, search), ct);
        return new CallEndpointsPage(res.TotalCalls, res.TotalSlowCalls, res.TotalFailedCalls, res.Endpoints.Select(ToEndpointInfo).ToList());
    }

    public async Task<CallEndpointDetailInfo?> GetCallEndpointDetailAsync(
        string runId,
        string endpoint,
        int? entry,
        string? show,
        double? minMs,
        double? maxMs,
        string? sort,
        int offset,
        int limit,
        CancellationToken ct)
    {
        if (IsCurrent(runId)) await storage.FlushAsync(ct);
        var detail = await storage.GetCallEndpointDetailAsync(runId, new CallDetailQuery(endpoint, entry, show, minMs, maxMs, null, null, sort, offset, limit, MaxPoints: 1), ct);
        if (detail is null) return null;
        return new CallEndpointDetailInfo(
            ToEndpointInfo(detail.Summary),
            detail.MatchingCalls,
            detail.Offset,
            detail.Calls.Select(c => new CallSampleInfo(c.Sequence, c.Entry, c.AtSeconds, c.DurationMs, c.StatusCode, c.Error, c.IsSlow, c.TypicalMs, c.SlowThresholdMs, !string.IsNullOrWhiteSpace(c.Url) ? c.Url : c.RawName)).ToList());
    }

    private static CallEndpointInfo ToEndpointInfo(CallEndpointSummaryDto s) =>
        new(s.Endpoint, s.Method ?? "", s.TotalCount, s.SlowCount, s.FailedCount, s.TypicalMs, s.P95Ms, s.MaxMs,
            s.SlowThresholdMs ?? SlowCallRule.EffectiveThresholdMs(s.TypicalMs) ?? 0, s.HasCustomThreshold);

    public async Task<LogPageInfo> QueryLogsAsync(string runId, LogFilter f, CancellationToken ct)
    {
        if (IsCurrent(runId)) await storage.FlushAsync(ct);
        var page = await storage.QueryLogsAsync(runId, new LogQuery(f.Entry, f.Bee, null, null, f.FailedBees, f.SlowBees, f.Levels, f.Search, f.Offset, f.Limit), ct);
        return new LogPageInfo(page.TotalCount, page.Items.Select(l => new LogLineInfo(l.Id, l.Sequence, l.Entry, l.AtSeconds, l.Level, l.Message, l.Exception)).ToList());
    }

    private bool IsCurrent(string runId) => string.Equals(runs.GetCurrent(maxBees: 1).RunId, runId, StringComparison.Ordinal);

    /// <summary>All compact bees of a run: the live ones from memory for the current run, the stored ones otherwise.</summary>
    private async Task<IReadOnlyList<BeeInfo>> AllBeesAsync(string runId, CancellationToken ct)
    {
        if (IsCurrent(runId))
            return runs.GetCurrent(-1, 10_000).NewBees.Select(b => new BeeInfo(b.Sequence, b.Entry, b.StartedAt, b.DurationMs, b.StatusCode, b.Bytes, b.Error, b.SlowCalls, b.FailedStep, b.FailedRep)).ToList();
        var data = await storage.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException($"There is no run '{runId}'.");
        return data.Bees.Take(MaxBeesScanned).Select(b => new BeeInfo(b.Sequence, b.Entry, b.StartedAt, b.DurationMs, b.StatusCode, b.Bytes, b.Error, b.SlowCalls, b.FailedStep, b.FailedRep)).ToList();
    }
}
