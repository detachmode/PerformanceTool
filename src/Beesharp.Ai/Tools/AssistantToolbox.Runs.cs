using System.ComponentModel;
using Beesharp.Ai.Contracts;

namespace Beesharp.Ai;

// Run control and run history (past and current runs, bees, calls, logs).
public sealed partial class AssistantToolbox
{
    private static object RunSummary(RunInfo r) => new
    {
        runId = r.RunId,
        state = r.State,
        elapsedSeconds = Math.Round(r.ElapsedSeconds, 1),
        startedUtc = r.StartedUtc,
        beesTotal = r.TotalBees,
        beesFailed = r.FailedBees,
        beesSlow = r.SlowBees,
        logLines = r.TotalLogs,
        error = r.Error,
        preflight = r.PreflightState,
        preflightError = r.PreflightError,
        entries = r.Entries.Select(e => new
        {
            entry = e.Entry + 1,
            label = e.Label,
            targetBeesPerMinute = e.BeesPerMinute,
            started = e.Starts,
            finished = e.Finished,
            inFlight = e.InFlight,
            failed = e.Failed,
            slow = e.SlowBees,
            dropped = e.Dropped,
        }).ToList(),
    };

    /// <summary>Reads the status and per-entry figures of the current run, or reports when no run exists.</summary>
    /// <returns>The current run summary, or an object indicating <c>state = "None"</c>.</returns>
    [AssistantTool("get_run_status")]
    [Description("Reads the current run: state (Running, Finished, ...), elapsed time and, per scenario entry, how many bees started, finished, are in flight, failed, were slow or were dropped. Also tells when no run exists yet.")]
    public object GetRunStatus()
    {
        var current = run.GetCurrent();
        if (current is null)
        {
            rec.Read("Looked at the run: there is none yet");
            return new { state = "None", message = "There is no run yet." };
        }
        rec.Read($"Looked at the run: {current.State}, {current.TotalBees} bees", RunLink(current.RunId));
        return RunSummary(current);
    }

    /// <summary>Starts a load run of the current Live plan, optionally against a named environment.</summary>
    /// <param name="environmentName">Optional environment name providing placeholder values; omit for none.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summary of the newly started run.</returns>
    [AssistantTool("start_run")]
    [Description("Starts a run of the current plan, optionally against an environment (its {{Key}} values fill the scenario parameters). Fails when a run is already going or the plan has no scenario entries. Check get_plan problems first.")]
    public async Task<object> StartRun([Description("Name of the environment to run against; leave out for none")] string? environmentName = null, CancellationToken ct = default)
    {
        var state = await run.StartAsync(string.IsNullOrWhiteSpace(environmentName) ? null : environmentName.Trim(), ct);
        rec.Change(ChangedAreas.Run, $"Started a run with {state.Entries.Count} scenario entries" + (environmentName is { Length: > 0 } ? $" on {environmentName.Trim()}" : ""), RunLink(state.RunId));
        return RunSummary(state);
    }

    /// <summary>Stops the currently active load run and keeps all recorded results.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summary of the stopped run.</returns>
    [AssistantTool("stop_run")]
    [Description("Stops the current run (also what pausing does in Live mode): no new bees start, bees in flight are cancelled, and the run is kept with what it recorded so far.")]
    public async Task<object> StopRun(CancellationToken ct)
    {
        var state = await run.StopAsync(ct);
        rec.Change(ChangedAreas.Run, $"Stopped the run after {state.TotalBees} bees", RunLink(state.RunId));
        return RunSummary(state);
    }

    /// <summary>Lists past and current runs ordered newest first.</summary>
    /// <param name="limit">Optional maximum number of runs to return (1 to 100; defaults to 20).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of run summary records.</returns>
    [AssistantTool("list_runs")]
    [Description("Lists past and current runs, newest first: id, state, duration, number of bees, how many failed, number of log lines.")]
    public async Task<object> ListRuns([Description("How many runs, 1 to 100 (default 20)")] int? limit = null, CancellationToken ct = default)
    {
        var runs = await history.ListRunsAsync(Bounds.Limit(limit), ct);
        rec.Read($"Looked at {runs.Count} runs");
        return runs.Select(r => new
        {
            runId = r.RunId, state = r.State, startedUtc = r.StartedUtc, elapsedSeconds = Math.Round(r.ElapsedSeconds, 1),
            beesTotal = r.TotalBees, beesFailed = r.FailedBees, logLines = r.TotalLogs, error = r.Error,
        }).ToList();
    }

    /// <summary>Reads summary details and per-entry figures for a specific run or the latest run.</summary>
    /// <param name="runId">Optional run identifier; defaults to the current or newest stored run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summary of the requested run including per-entry counters.</returns>
    [AssistantTool("get_run")]
    [Description("Reads one run with the figures of each scenario entry. Without a run id: the current run, or the newest stored one.")]
    public async Task<object> GetRun([Description("Run id from list_runs")] string? runId = null, CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var info = await history.GetRunAsync(id, ct) ?? throw new KeyNotFoundException($"There is no run '{id}'. Use list_runs.");
        rec.Read($"Looked at run {info.RunId}", RunLink(info.RunId));
        return RunSummary(info);
    }

    /// <summary>Lists bees of a run ordered failed first, then slow, then ok, with 1-based bee, entry, and step numbers.</summary>
    /// <param name="runId">Optional run identifier; defaults to the current or newest stored run.</param>
    /// <param name="entry">Optional 1-based scenario entry filter.</param>
    /// <param name="outcome">Optional outcome filter (<c>all</c>, <c>ok</c>, <c>slow</c>, or <c>failed</c>).</param>
    /// <param name="failedStep">Optional 1-based step number filter for bees that failed at that step.</param>
    /// <param name="offset">Optional 0-based paging offset.</param>
    /// <param name="limit">Optional page size (1 to 100; defaults to 20).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A paged result containing total count, paging metadata, and bee rows.</returns>
    [AssistantTool("list_bees")]
    [Description("Lists the bees of a run (a bee is one play-through of a scenario; a bee in flight when the user stopped the run counts as failed with the error Canceled: run stopped), failed first, then slow, then ok. Bee and step numbers start at 1 and match the UI. Paged.")]
    public async Task<object> ListBees(
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        [Description("Only this scenario entry (number from 1)")] int? entry = null,
        [Description("all, ok, slow or failed")] string? outcome = null,
        [Description("Only bees that failed at this step (number from 1)")] int? failedStep = null,
        [Description("Skip this many bees (paging)")] int? offset = null,
        [Description("How many, 1 to 100 (default 20)")] int? limit = null,
        CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var o = Bounds.Offset(offset);
        var l = Bounds.Limit(limit);
        var page = await history.ListBeesAsync(id, entry is { } e ? ToIndex(e, "Entry") : null, ParseOutcome(outcome), o, l, ct, failedStep is { } fs ? ToIndex(fs, "Step") : null);
        rec.Read($"Looked at {page.Items.Count} of {page.Total} bees", RunLink(id));
        return new { runId = id, total = page.Total, offset = o, hasMore = o + page.Items.Count < page.Total, bees = page.Items.Select(BeeRow).ToList() };
    }

    private static object BeeRow(BeeInfo b) => new
    {
        bee = b.Sequence + 1,
        entry = b.Entry + 1,
        startedAtSeconds = Math.Round(b.StartedAt, 2),
        durationMs = Math.Round(b.DurationMs),
        status = b.StatusCode,
        outcome = b.Outcome,
        failedStep = b.FailedStep >= 0 ? b.FailedStep + 1 : (int?)null,
        failedRep = b.FailedStep >= 0 && b.FailedRep > 0 ? b.FailedRep + 1 : (int?)null,
        error = b.Error is null ? null : Bounds.Cut(b.Error),
    };

    /// <summary>Reads the summary, recorded calls, and initial log lines of a single bee.</summary>
    /// <param name="bee">1-based bee number (for example <c>12</c> for B12).</param>
    /// <param name="runId">Optional run identifier; defaults to the current or newest stored run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An object containing bee metadata, bounded call list, and bounded log lines.</returns>
    [AssistantTool("get_bee")]
    [Description("Reads one bee: its calls (name, step number from 1, repetition, status, duration, error) and its first log lines.")]
    public async Task<object> GetBee(
        [Description("Bee number from 1 (B12 = 12)")] int bee,
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var seq = ToIndex(bee, "Bee");
        var detail = await history.GetBeeAsync(id, seq, ct) ?? throw new KeyNotFoundException($"Bee {bee} has no stored detail in run {id} (very large runs keep detail only for some bees).");
        rec.Read($"Looked at bee B{bee}", RunLink(id, bee: bee));
        return new
        {
            runId = id,
            bee = BeeRow(detail.Bee),
            calls = detail.Calls.Take(Bounds.MaxList).Select(c => new { name = c.Name, step = c.Step + 1, rep = c.Rep + 1, offsetMs = Math.Round(c.OffsetMs), durationMs = Math.Round(c.DurationMs), status = c.StatusCode, error = c.Error is null ? null : Bounds.Cut(c.Error) }).ToList(),
            callsTotal = detail.Calls.Count,
            logs = detail.Logs.Take(Bounds.DefaultList).Select(l => new { atSeconds = Math.Round(l.AtSeconds, 2), level = l.Level, message = Bounds.Cut(l.Message) }).ToList(),
            logsTotal = detail.Logs.Count,
        };
    }

    /// <summary>Searches log lines of a run using combinable filters and paging.</summary>
    /// <param name="runId">Optional run identifier; defaults to the current or newest stored run.</param>
    /// <param name="entry">Optional 1-based scenario entry filter.</param>
    /// <param name="bee">Optional 1-based bee number filter.</param>
    /// <param name="failedBees">When <see langword="true"/>, return only logs from failed bees.</param>
    /// <param name="slowBees">When <see langword="true"/>, return only logs from slow bees.</param>
    /// <param name="levels">Optional log level names (<c>Trace</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c>).</param>
    /// <param name="search">Optional substring that log messages must contain.</param>
    /// <param name="offset">Optional 0-based paging offset.</param>
    /// <param name="limit">Optional page size (1 to 100; defaults to 20).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A paged result containing matching log lines with bounded message lengths.</returns>
    [AssistantTool("query_logs")]
    [Description("Searches the log lines of a run. Filters combine: scenario entry, one bee, only failed or slow bees, log levels, free text. Paged; messages are cut at 300 characters. Use show_logs instead when the user should see the lines.")]
    public async Task<object> QueryLogs(
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        [Description("Only this scenario entry (number from 1)")] int? entry = null,
        [Description("Only this bee (number from 1)")] int? bee = null,
        [Description("Only logs of failed bees")] bool? failedBees = null,
        [Description("Only logs of slow bees")] bool? slowBees = null,
        [Description("Log levels: Trace, Debug, Information, Warning, Error")] List<string>? levels = null,
        [Description("Text the message must contain")] string? search = null,
        [Description("Skip this many lines (paging)")] int? offset = null,
        [Description("How many, 1 to 100 (default 20)")] int? limit = null,
        CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var filter = Filter(entry, bee, failedBees, slowBees, levels, search, offset, limit);
        var page = await history.QueryLogsAsync(id, filter, ct);
        rec.Read($"Searched logs: {page.Total} lines match", new ChipLinkDto("logs", Query(id, entry, bee, failedBees, slowBees, levels, search)));
        return new
        {
            runId = id,
            total = page.Total,
            offset = filter.Offset,
            hasMore = filter.Offset + page.Items.Count < page.Total,
            lines = page.Items.Select(LogRow).ToList(),
        };
    }

    private static object LogRow(LogLineInfo l) => new
    {
        bee = l.Sequence + 1,
        entry = l.Entry + 1,
        atSeconds = Math.Round(l.AtSeconds, 2),
        level = l.Level,
        message = Bounds.Cut(l.Message),
        exception = l.Exception is null ? null : Bounds.Cut(l.Exception),
    };

    private static LogFilter Filter(int? entry, int? bee, bool? failedBees, bool? slowBees, List<string>? levels, string? search, int? offset, int? limit) =>
        new(entry is { } e ? ToIndex(e, "Entry") : null, bee is { } b ? ToIndex(b, "Bee") : null, failedBees, slowBees, ParseLevels(levels),
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(), Bounds.Offset(offset), Bounds.Limit(limit));

    private static ViewQueryDto Query(string runId, int? entry, int? bee, bool? failedBees, bool? slowBees, List<string>? levels, string? search) =>
        new(runId, entry, bee, null, ParseLevels(levels), string.IsNullOrWhiteSpace(search) ? null : search.Trim(), failedBees, slowBees);

    /// <summary>Lists normalized HTTP and measured endpoints of a run sorted by slow call count.</summary>
    /// <param name="runId">Optional run identifier; defaults to the current or newest stored run.</param>
    /// <param name="entry">Optional 1-based scenario entry filter.</param>
    /// <param name="slowOnly">When <see langword="true"/>, return only endpoints with slow calls.</param>
    /// <param name="failedOnly">When <see langword="true"/>, return only endpoints with failed calls.</param>
    /// <param name="minMs">Optional minimum call duration filter in milliseconds.</param>
    /// <param name="search">Optional substring filter on endpoint path or method.</param>
    /// <param name="limit">Optional maximum number of endpoints to return (1 to 100; defaults to 20).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated call totals and endpoint latency statistics.</returns>
    [AssistantTool("list_call_endpoints")]
    [Description("Lists auto-detected HTTP and measured endpoints of a run sorted by slow call count, with typical (median), slowest 5% (p95), max duration, slow threshold and failed count. Use show_slow_calls when the user should see them as a card.")]
    public async Task<object> ListCallEndpoints(
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        [Description("Only this scenario entry (number from 1)")] int? entry = null,
        [Description("Only endpoints that have at least one slow call")] bool? slowOnly = null,
        [Description("Only endpoints that have at least one failed call")] bool? failedOnly = null,
        [Description("Minimum max/matching duration in milliseconds")] double? minMs = null,
        [Description("Filter endpoints whose path or method contains this text")] string? search = null,
        [Description("How many endpoints to return, 1 to 100 (default 20)")] int? limit = null,
        CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var entryIdx = entry is { } e ? ToIndex(e, "Entry") : (int?)null;
        var lim = Bounds.Limit(limit);
        var page = await history.ListCallEndpointsAsync(id, entryIdx, slowOnly ?? false, failedOnly ?? false, minMs, string.IsNullOrWhiteSpace(search) ? null : search.Trim(), ct);
        var items = page.Endpoints.Take(lim).ToList();
        var outcome = slowOnly == true ? "slow" : failedOnly == true ? "failed" : null;
        rec.Read($"Looked at {items.Count} of {page.Endpoints.Count} endpoints ({page.TotalSlowCalls} slow calls)", CallsLink(id, entry, outcome: outcome, minMs: minMs));
        return new
        {
            runId = id,
            totalCalls = page.TotalCalls,
            totalSlowCalls = page.TotalSlowCalls,
            totalFailedCalls = page.TotalFailedCalls,
            totalEndpoints = page.Endpoints.Count,
            endpoints = items.Select(ep => new
            {
                endpoint = ep.Endpoint,
                method = ep.Method,
                calls = ep.TotalCount,
                slow = ep.SlowCount,
                failed = ep.FailedCount,
                typicalMs = Math.Round(ep.TypicalMs, 1),
                p95Ms = Math.Round(ep.P95Ms, 1),
                maxMs = Math.Round(ep.MaxMs, 1),
                slowThresholdMs = Math.Round(ep.SlowThresholdMs, 1),
                hasCustomThreshold = ep.HasCustomThreshold,
            }).ToList(),
        };
    }

    /// <summary>Inspects individual call samples for a single normalized endpoint.</summary>
    /// <param name="endpoint">Normalized endpoint name (for example <c>GET /orders/{id}</c>).</param>
    /// <param name="runId">Optional run identifier; defaults to the current or newest stored run.</param>
    /// <param name="entry">Optional 1-based scenario entry filter.</param>
    /// <param name="show">Optional outcome filter (<c>all</c>, <c>slow</c>, or <c>failed</c>).</param>
    /// <param name="minMs">Optional minimum call duration filter in milliseconds.</param>
    /// <param name="sort">Optional sort order (<c>slowest</c>, <c>time</c>, or <c>failed</c>).</param>
    /// <param name="offset">Optional 0-based paging offset.</param>
    /// <param name="limit">Optional page size (1 to 100; defaults to 20).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Endpoint latency percentiles and paged individual call samples.</returns>
    [AssistantTool("get_call_endpoint")]
    [Description("Inspects individual calls for one endpoint (normalized name like 'GET /orders/{id}'), returning exact URLs, durations, slow ratio, status codes and bee numbers (1-based). Use show_slow_calls to show a card.")]
    public async Task<object> GetCallEndpoint(
        [Description("Normalized endpoint name from list_call_endpoints (for example 'GET /orders/{id}')")] string endpoint,
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        [Description("Only this scenario entry (number from 1)")] int? entry = null,
        [Description("all, slow or failed")] string? show = null,
        [Description("Only calls slower than this many milliseconds")] double? minMs = null,
        [Description("slowest, time or failed (default slowest)")] string? sort = null,
        [Description("Skip this many calls (paging)")] int? offset = null,
        [Description("How many calls, 1 to 100 (default 20)")] int? limit = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("endpoint is required.");
        var id = await RunIdAsync(runId, ct);
        var entryIdx = entry is { } e ? ToIndex(e, "Entry") : (int?)null;
        var showNorm = ParseEndpointShow(show);
        var o = Bounds.Offset(offset);
        var l = Bounds.Limit(limit);
        var detail = await history.GetCallEndpointDetailAsync(id, endpoint.Trim(), entryIdx, showNorm, minMs, null, sort, o, l, ct)
            ?? throw new KeyNotFoundException($"Endpoint '{endpoint.Trim()}' has no calls in run {id}.");
        rec.Read($"Inspected {detail.Summary.Endpoint}: {detail.MatchingCalls} calls", CallsLink(id, entry, detail.Summary.Endpoint, showNorm, minMs));
        return new
        {
            runId = id,
            endpoint = detail.Summary.Endpoint,
            typicalMs = Math.Round(detail.Summary.TypicalMs, 1),
            p95Ms = Math.Round(detail.Summary.P95Ms, 1),
            maxMs = Math.Round(detail.Summary.MaxMs, 1),
            slowThresholdMs = Math.Round(detail.Summary.SlowThresholdMs, 1),
            hasCustomThreshold = detail.Summary.HasCustomThreshold,
            matching = detail.MatchingCalls,
            offset = o,
            hasMore = o + detail.Calls.Count < detail.MatchingCalls,
            calls = detail.Calls.Select(c => new
            {
                bee = c.Sequence + 1,
                entry = c.Entry + 1,
                atSeconds = Math.Round(c.AtSeconds, 2),
                durationMs = Math.Round(c.DurationMs, 1),
                ratio = c.Error is null && c.TypicalMs is > 0 ? Math.Round(Beesharp.Services.SlowCallRule.Ratio(c.DurationMs, c.TypicalMs, c.SlowThresholdMs), 1) : (double?)null,
                status = c.StatusCode,
                isSlow = c.IsSlow,
                error = c.Error is null ? null : Bounds.Cut(c.Error),
                url = Bounds.Cut(c.Url, 240),
            }).ToList(),
        };
    }

    private static string? ParseEndpointShow(string? show) => show?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => null,
        "slow" => "slow",
        "failed" or "fail" or "error" => "failed",
        _ => throw new ArgumentException("show must be all, slow or failed."),
    };
}
