using Beesharp.Ai;
using Beesharp.Services;
using Beesharp.Services.Storage;

namespace Beesharp.AiAdapters;

/// <summary>
/// Adapts <see cref="RunManager"/> (live run state) and <see cref="IRunStorage"/> (persisted runs, bees, calls, and logs)
/// to <see cref="IRunHistoryPort"/> for the AI assistant.
/// </summary>
/// <param name="runs">The active run manager used for the current in-memory run.</param>
/// <param name="storage">The persistent storage used for historical runs, bee details, call endpoints, and logs.</param>
public sealed class RunHistoryAdapter(RunManager runs, IRunStorage storage) : IRunHistoryPort
{
    private const int MaxBeesScanned = 200_000;

    /// <summary>Gets the identifier of the current run, or of the most recent stored run if no current run exists.</summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The latest run identifier, or <see langword="null"/> when no runs exist.</returns>
    public async Task<string?> LatestRunIdAsync(CancellationToken ct)
    {
        var current = runs.GetCurrent();
        if (!string.IsNullOrEmpty(current.RunId)) return current.RunId;
        return (await storage.ListRunsAsync(1, ct)).FirstOrDefault()?.RunId;
    }

    /// <summary>Lists recent stored runs in reverse chronological order.</summary>
    /// <param name="limit">Maximum number of runs to return.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A read-only list of <see cref="RunInfo"/> summaries.</returns>
    public async Task<IReadOnlyList<RunInfo>> ListRunsAsync(int limit, CancellationToken ct) =>
        (await storage.ListRunsAsync(limit, ct)).Select(r => new RunInfo(r.RunId, r.State, r.ElapsedSeconds, r.StartedUtc, r.TotalBees, r.FailedBees, 0, r.TotalLogs, r.Error, [])).ToList();

    /// <summary>Gets summary information and per-entry figures for a live or stored run.</summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The matching <see cref="RunInfo"/>, or <see langword="null"/> if the run does not exist.</returns>
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

    /// <summary>
    /// Lists a page of bees for a run, filtered by entry, outcome, and optional failed step,
    /// ordered failed first, then slow, then ok, and by start time within each group.
    /// </summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="entry">Optional 0-based scenario entry index to filter by.</param>
    /// <param name="outcome">The bee outcome filter (<see cref="BeeOutcomeFilter"/>).</param>
    /// <param name="offset">The 0-based number of matching bees to skip.</param>
    /// <param name="limit">The maximum number of bees to return in the page.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <param name="failedStep">Optional 0-based step index; when set, matches only failed bees that failed at that step.</param>
    /// <returns>A <see cref="BeePage"/> with the total matching count and requested slice of bees.</returns>
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

    /// <summary>Gets compact bee summaries for specific 0-based bee sequence numbers in a run.</summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="sequences">The 0-based bee sequence numbers to retrieve; unknown sequences are omitted.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A read-only list of matching <see cref="BeeInfo"/> records.</returns>
    public async Task<IReadOnlyList<BeeInfo>> GetBeesAsync(string runId, IReadOnlyList<int> sequences, CancellationToken ct)
    {
        var wanted = sequences.ToHashSet();
        return (await AllBeesAsync(runId, ct)).Where(b => wanted.Contains(b.Sequence)).ToList();
    }

    /// <summary>Gets full execution details (recorded HTTP calls and logs) for a single bee in a run.</summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="sequence">The 0-based bee sequence number.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The <see cref="BeeDetailInfo"/> for the bee, or <see langword="null"/> if not found.</returns>
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

    /// <summary>Lists aggregated HTTP call endpoint statistics for a run.</summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="entry">Optional 0-based scenario entry index to filter calls by.</param>
    /// <param name="slowOnly">When <see langword="true"/>, includes only endpoints with slow calls.</param>
    /// <param name="failedOnly">When <see langword="true"/>, includes only endpoints with failed calls.</param>
    /// <param name="minMs">Optional minimum call duration in milliseconds.</param>
    /// <param name="search">Optional substring filter matched against endpoint names.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="CallEndpointsPage"/> with totals and per-endpoint statistics.</returns>
    public async Task<CallEndpointsPage> ListCallEndpointsAsync(string runId, int? entry, bool slowOnly, bool failedOnly, double? minMs, string? search, CancellationToken ct)
    {
        if (IsCurrent(runId)) await storage.FlushAsync(ct);
        var res = await storage.GetCallEndpointsAsync(runId, new CallEndpointsQuery(entry, slowOnly, failedOnly, minMs, search), ct);
        return new CallEndpointsPage(res.TotalCalls, res.TotalSlowCalls, res.TotalFailedCalls, res.Endpoints.Select(ToEndpointInfo).ToList());
    }

    /// <summary>Gets summary metrics and a paged list of individual call samples for a single endpoint in a run.</summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="endpoint">The normalized endpoint name.</param>
    /// <param name="entry">Optional 0-based scenario entry index to filter by.</param>
    /// <param name="show">Optional call outcome filter (for example, slow or failed).</param>
    /// <param name="minMs">Optional minimum call duration in milliseconds.</param>
    /// <param name="maxMs">Optional maximum call duration in milliseconds.</param>
    /// <param name="sort">Optional sort order for matching call samples.</param>
    /// <param name="offset">The 0-based number of matching calls to skip.</param>
    /// <param name="limit">The maximum number of call samples to return.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The <see cref="CallEndpointDetailInfo"/> for the endpoint, or <see langword="null"/> if not found.</returns>
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

    /// <summary>Queries a paged slice of log entries for a run using the specified filter.</summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="f">The log filter and pagination options.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="LogPageInfo"/> containing the total matching log count and log lines for the page.</returns>
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
