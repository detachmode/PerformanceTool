using System.ComponentModel;
using PerformanceTool.Ai.Contracts;

namespace PerformanceTool.Ai;

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

    [AssistantTool("start_run")]
    [Description("Starts a run of the current plan, optionally against an environment (its {{Key}} values fill the scenario parameters). Fails when a run is already going or the plan has no scenario entries. Check get_plan problems first.")]
    public async Task<object> StartRun([Description("Name of the environment to run against; leave out for none")] string? environmentName, CancellationToken ct)
    {
        var state = await run.StartAsync(string.IsNullOrWhiteSpace(environmentName) ? null : environmentName.Trim(), ct);
        rec.Change(ChangedAreas.Run, $"Started a run with {state.Entries.Count} scenario entries" + (environmentName is { Length: > 0 } ? $" on {environmentName.Trim()}" : ""), RunLink(state.RunId));
        return RunSummary(state);
    }

    [AssistantTool("stop_run")]
    [Description("Stops the current run (also what pausing does in Live mode): no new bees start, bees in flight are cancelled, and the run is kept with what it recorded so far.")]
    public async Task<object> StopRun(CancellationToken ct)
    {
        var state = await run.StopAsync(ct);
        rec.Change(ChangedAreas.Run, $"Stopped the run after {state.TotalBees} bees", RunLink(state.RunId));
        return RunSummary(state);
    }

    [AssistantTool("list_runs")]
    [Description("Lists past and current runs, newest first: id, state, duration, number of bees, how many failed, number of log lines.")]
    public async Task<object> ListRuns([Description("How many runs, 1 to 100 (default 20)")] int? limit, CancellationToken ct)
    {
        var runs = await history.ListRunsAsync(Bounds.Limit(limit), ct);
        rec.Read($"Looked at {runs.Count} runs");
        return runs.Select(r => new
        {
            runId = r.RunId, state = r.State, startedUtc = r.StartedUtc, elapsedSeconds = Math.Round(r.ElapsedSeconds, 1),
            beesTotal = r.TotalBees, beesFailed = r.FailedBees, logLines = r.TotalLogs, error = r.Error,
        }).ToList();
    }

    [AssistantTool("get_run")]
    [Description("Reads one run with the figures of each scenario entry. Without a run id: the current run, or the newest stored one.")]
    public async Task<object> GetRun([Description("Run id from list_runs")] string? runId, CancellationToken ct)
    {
        var id = await RunIdAsync(runId, ct);
        var info = await history.GetRunAsync(id, ct) ?? throw new KeyNotFoundException($"There is no run '{id}'. Use list_runs.");
        rec.Read($"Looked at run {info.RunId}", RunLink(info.RunId));
        return RunSummary(info);
    }

    [AssistantTool("list_bees")]
    [Description("Lists the bees of a run (a bee is one play-through of a scenario), failed first, then slow, then ok. Bee numbers start at 1 and match the B-numbers of the UI. Paged.")]
    public async Task<object> ListBees(
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        [Description("Only this scenario entry (number from 1)")] int? entry = null,
        [Description("all, ok, slow or failed")] string? outcome = null,
        [Description("Skip this many bees (paging)")] int? offset = null,
        [Description("How many, 1 to 100 (default 20)")] int? limit = null,
        CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var o = Bounds.Offset(offset);
        var l = Bounds.Limit(limit);
        var page = await history.ListBeesAsync(id, entry is { } e ? ToIndex(e, "Entry") : null, ParseOutcome(outcome), o, l, ct);
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
        error = b.Error is null ? null : Bounds.Cut(b.Error),
    };

    [AssistantTool("get_bee")]
    [Description("Reads one bee: its calls (name, status, duration, error) and its first log lines.")]
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
            calls = detail.Calls.Take(Bounds.MaxList).Select(c => new { name = c.Name, offsetMs = Math.Round(c.OffsetMs), durationMs = Math.Round(c.DurationMs), status = c.StatusCode, error = c.Error is null ? null : Bounds.Cut(c.Error) }).ToList(),
            callsTotal = detail.Calls.Count,
            logs = detail.Logs.Take(Bounds.DefaultList).Select(l => new { atSeconds = Math.Round(l.AtSeconds, 2), level = l.Level, message = Bounds.Cut(l.Message) }).ToList(),
            logsTotal = detail.Logs.Count,
        };
    }

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
}
