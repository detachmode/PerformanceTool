using System.ComponentModel;
using Beesharp.Ai.Contracts;

namespace Beesharp.Ai;

/// <summary>One bee the assistant picked for the timeline, with a short reason why ("slowest checkout").</summary>
public sealed record BeeNote(
    [property: Description("Bee number from 1")] int Bee,
    [property: Description("Short reason, e.g. slowest checkout")] string? Note = null);

// The show tools (brain 0049): they put data into the chat as small cards instead of pasting it as text. A card holds the query behind it and a
// small preview; the client re-queries live data when the user opens it. Each tool answers the model with a short summary of what is on the card.
public sealed partial class AssistantToolbox
{
    private async Task<IReadOnlyList<string>> EntryLabelsAsync(string runId, CancellationToken ct) =>
        (await history.GetRunAsync(runId, ct))?.Entries.Select(e => e.Label).ToList() ?? [];

    private static ViewBeeDto ViewBee(BeeInfo b, IReadOnlyList<string> labels, string? note = null) =>
        new(b.Sequence + 1, b.Entry + 1, b.Entry >= 0 && b.Entry < labels.Count ? labels[b.Entry] : $"Entry {b.Entry + 1}",
            Math.Round(b.StartedAt, 3), Math.Round(b.DurationMs, 1), b.StatusCode, b.Outcome, b.Error is null ? null : Bounds.Cut(b.Error), note);

    [AssistantTool("show_logs")]
    [Description("Shows log lines to the user as a small card in the chat (a few lines, the total, and a button to open them all). Prefer this over pasting log lines as text. Same filters as query_logs. Answers with a short summary of what the card shows.")]
    public async Task<object> ShowLogs(
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        [Description("Only this scenario entry (number from 1)")] int? entry = null,
        [Description("Only this bee (number from 1)")] int? bee = null,
        [Description("Only logs of failed bees")] bool? failedBees = null,
        [Description("Only logs of slow bees")] bool? slowBees = null,
        [Description("Log levels: Trace, Debug, Information, Warning, Error")] List<string>? levels = null,
        [Description("Text the message must contain")] string? search = null,
        [Description("Card title, e.g. Errors in Checkout")] string? title = null,
        CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var filter = Filter(entry, bee, failedBees, slowBees, levels, search, 0, Bounds.CardLogs);
        var page = await history.QueryLogsAsync(id, filter, ct);
        var lines = page.Items.Select(l => new ViewLogLineDto(l.Sequence + 1, $"B{l.Sequence + 1}", l.Entry + 1, Math.Round(l.AtSeconds, 2), l.Level, Bounds.Cut(l.Message), l.Exception is null ? null : Bounds.Cut(l.Exception, 500))).ToList();
        rec.Show(new ViewItemDto(ViewKinds.Logs, title ?? "Logs", Query(id, entry, bee, failedBees, slowBees, levels, search), page.Total, Logs: lines));
        return new
        {
            shownToUser = $"A logs card with {lines.Count} of {page.Total} matching lines.",
            preview = page.Items.Select(l => new { bee = l.Sequence + 1, level = l.Level, message = Bounds.Cut(l.Message, 160) }).ToList(),
        };
    }

    [AssistantTool("show_bees")]
    [Description("Shows bees to the user as a small card: failed first, then slow, then ok, each as a numbered chip that opens the bee. Prefer this over listing bees as text.")]
    public async Task<object> ShowBees(
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        [Description("Only this scenario entry (number from 1)")] int? entry = null,
        [Description("all, ok, slow or failed")] string? outcome = null,
        [Description("Card title, e.g. Failed bees")] string? title = null,
        CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var labels = await EntryLabelsAsync(id, ct);
        var page = await history.ListBeesAsync(id, entry is { } e ? ToIndex(e, "Entry") : null, ParseOutcome(outcome), 0, Bounds.CardBees, ct);
        rec.Show(new ViewItemDto(ViewKinds.Bees, title ?? "Bees", new ViewQueryDto(id, entry, Outcome: outcome), page.Total,
            Bees: page.Items.Select(b => ViewBee(b, labels)).ToList(), EntryLabels: labels));
        return new { shownToUser = $"A bees card with {page.Items.Count} of {page.Total} bees.", bees = page.Items.Select(BeeRow).ToList() };
    }

    [AssistantTool("show_bee")]
    [Description("Shows one bee to the user as a small card: its outcome, error and its calls as a little waterfall, with a button to open the bee with its logs.")]
    public async Task<object> ShowBee(
        [Description("Bee number from 1 (B12 = 12)")] int bee,
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var labels = await EntryLabelsAsync(id, ct);
        var detail = await history.GetBeeAsync(id, ToIndex(bee, "Bee"), ct) ?? throw new KeyNotFoundException($"Bee {bee} has no stored detail in run {id}.");
        var row = ViewBee(detail.Bee, labels);
        rec.Show(new ViewItemDto(ViewKinds.Bee, $"Bee B{bee}", new ViewQueryDto(id, detail.Bee.Entry + 1, bee), detail.Calls.Count,
            Bee: new ViewBeeDetailDto(row, detail.Calls.Take(Bounds.MaxList).Select(c => new ViewCallDto(c.Name, Math.Round(c.OffsetMs, 1), Math.Round(c.DurationMs, 1), c.StatusCode, c.Error is null ? null : Bounds.Cut(c.Error), c.Endpoint, c.IsSlow)).ToList()),
            EntryLabels: labels));
        return new { shownToUser = $"A bee card for B{bee} ({row.Outcome}) with {detail.Calls.Count} calls.", error = row.Error };
    }

    [AssistantTool("show_slow_calls")]
    [Description("Shows slow endpoints or individual slow/failing calls of one endpoint to the user as a card with a button to open /calls. Leave endpoint empty for the slowest endpoints overall, or pass an endpoint (e.g. 'POST /payments') for its slowest individual calls.")]
    public async Task<object> ShowSlowCalls(
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        [Description("Normalized endpoint name (for example 'GET /orders/{id}') to show its individual calls; omit to show endpoints overall")] string? endpoint = null,
        [Description("Only this scenario entry (number from 1)")] int? entry = null,
        [Description("all, slow or failed")] string? outcome = null,
        [Description("Only calls slower than this many milliseconds")] double? minMs = null,
        [Description("Filter endpoint name when endpoint is omitted")] string? search = null,
        [Description("Card title, e.g. Slowest endpoints")] string? title = null,
        CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var entryIdx = entry is { } e ? ToIndex(e, "Entry") : (int?)null;
        var outcomeNorm = ParseEndpointShow(outcome);
        var labels = await EntryLabelsAsync(id, ct);

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var detail = await history.GetCallEndpointDetailAsync(id, endpoint.Trim(), entryIdx, outcomeNorm, minMs, null, "slowest", 0, Bounds.CardCalls, ct)
                ?? throw new KeyNotFoundException($"Endpoint '{endpoint.Trim()}' has no calls in run {id}.");
            var epView = ToViewEndpoint(detail.Summary);
            var samples = detail.Calls.Select(c =>
            {
                var label = c.Entry >= 0 && c.Entry < labels.Count ? labels[c.Entry] : $"Entry {c.Entry + 1}";
                double? ratio = c.Error is null && c.TypicalMs is > 0
                    ? Math.Round(Beesharp.Services.SlowCallRule.Ratio(c.DurationMs, c.TypicalMs, c.SlowThresholdMs), 1)
                    : null;
                return new ViewCallSampleDto(c.Sequence + 1, c.Entry + 1, label, Math.Round(c.AtSeconds, 2), Math.Round(c.DurationMs, 1), c.StatusCode, c.Error is null ? null : Bounds.Cut(c.Error), c.IsSlow, ratio, Bounds.Cut(c.Url, 200));
            }).ToList();

            rec.Show(new ViewItemDto(
                ViewKinds.Calls,
                title ?? detail.Summary.Endpoint,
                new ViewQueryDto(id, entry, Outcome: outcomeNorm, Search: search, Endpoint: detail.Summary.Endpoint, MinMs: minMs),
                detail.MatchingCalls,
                EntryLabels: labels,
                CallEndpoints: [epView],
                CallSamples: samples));
            return new
            {
                shownToUser = $"A calls card for {detail.Summary.Endpoint} showing {samples.Count} of {detail.MatchingCalls} matching calls.",
                slow = detail.Summary.SlowCount,
                failed = detail.Summary.FailedCount,
                typicalMs = Math.Round(detail.Summary.TypicalMs, 1),
            };
        }

        var page = await history.ListCallEndpointsAsync(id, entryIdx, outcomeNorm == "slow", outcomeNorm == "failed", minMs, string.IsNullOrWhiteSpace(search) ? null : search.Trim(), ct);
        var eps = page.Endpoints.Take(Bounds.CardEndpoints).Select(ToViewEndpoint).ToList();
        rec.Show(new ViewItemDto(
            ViewKinds.Calls,
            title ?? (outcomeNorm == "failed" ? "Failing endpoints" : "Slowest endpoints"),
            new ViewQueryDto(id, entry, Outcome: outcomeNorm, Search: string.IsNullOrWhiteSpace(search) ? null : search.Trim(), MinMs: minMs),
            page.Endpoints.Count,
            EntryLabels: labels,
            CallEndpoints: eps));
        return new
        {
            shownToUser = $"A calls card showing {eps.Count} of {page.Endpoints.Count} endpoints ({page.TotalSlowCalls} slow calls across {page.TotalCalls} calls).",
        };
    }

    private static ViewCallEndpointDto ToViewEndpoint(CallEndpointInfo ep) =>
        new(ep.Endpoint, ep.Method, ep.TotalCount, ep.SlowCount, ep.FailedCount, Math.Round(ep.TypicalMs, 1), Math.Round(ep.P95Ms, 1), Math.Round(ep.MaxMs, 1), Math.Round(ep.SlowThresholdMs, 1), ep.HasCustomThreshold);

    [AssistantTool("show_scenario")]
    [Description("Shows one scenario entry of a run as a small card: its figures (started, finished, in flight, failed, slow) and its first bees. Every figure opens the bees behind it.")]
    public async Task<object> ShowScenario(
        [Description("Scenario entry number from 1")] int entry,
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var index = ToIndex(entry, "Entry");
        var info = await history.GetRunAsync(id, ct) ?? throw new KeyNotFoundException($"There is no run '{id}'.");
        var figures = info.Entries.FirstOrDefault(e => e.Entry == index) ?? throw new ArgumentException($"Run {id} has no entry {entry}; it has {info.Entries.Count}.");
        var labels = info.Entries.Select(e => e.Label).ToList();
        var page = await history.ListBeesAsync(id, index, BeeOutcomeFilter.All, 0, Bounds.CardBees, ct);
        var run = new ViewRunDto(info.RunId, info.State, info.ElapsedSeconds, info.TotalBees, info.FailedBees, info.SlowBees, info.TotalLogs, [ViewEntry(figures)]);
        rec.Show(new ViewItemDto(ViewKinds.Scenario, figures.Label, new ViewQueryDto(id, entry), page.Total, Bees: page.Items.Select(b => ViewBee(b, labels)).ToList(), Run: run, EntryLabels: labels));
        return new { shownToUser = $"A scenario card for {figures.Label}.", figures = new { started = figures.Starts, finished = figures.Finished, inFlight = figures.InFlight, failed = figures.Failed, slow = figures.SlowBees } };
    }

    [AssistantTool("show_run")]
    [Description("Shows a whole run as a small card: state, time, and the figures of every scenario entry. Every figure opens the bees behind it.")]
    public async Task<object> ShowRun(
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        CancellationToken ct = default)
    {
        var id = await RunIdAsync(runId, ct);
        var info = await history.GetRunAsync(id, ct) ?? throw new KeyNotFoundException($"There is no run '{id}'.");
        var run = new ViewRunDto(info.RunId, info.State, info.ElapsedSeconds, info.TotalBees, info.FailedBees, info.SlowBees, info.TotalLogs, info.Entries.Select(ViewEntry).ToList());
        rec.Show(new ViewItemDto(ViewKinds.Run, $"Run {info.RunId}", new ViewQueryDto(id), info.TotalBees, Run: run, EntryLabels: info.Entries.Select(e => e.Label).ToList()));
        return new { shownToUser = $"A run card for {info.RunId} ({info.State}, {info.TotalBees} bees, {info.FailedBees} failed)." };
    }

    private static ViewEntryDto ViewEntry(EntryFigures e) =>
        new(e.Entry + 1, e.Label, e.Starts, e.Finished, e.InFlight, e.Failed, e.SlowBees, e.Dropped, e.BeesPerMinute);

    [AssistantTool("show_plan_changes")]
    [Description("Shows what changed in the plan as a small card (entries added, removed or changed, with the old and new values) and a button to open the editor. Compares with the plan at the start of this conversation turn when you edited it, otherwise with the last saved preset.")]
    public object ShowPlanChanges()
    {
        var state = plan.Get();
        var before = rec.PlanBefore ?? state.SavedPlan;
        var changes = PlanFormat.Diff(PlanFormat.Stripped(before, catalog), PlanFormat.Stripped(state.Plan, catalog));
        rec.Show(new ViewItemDto(ViewKinds.PlanChanges, "Plan changes", new ViewQueryDto(), changes.Count, Changes: changes));
        return new { shownToUser = changes.Count == 0 ? "A plan card saying nothing changed." : $"A plan changes card with {changes.Count} changes.", changes = changes.Select(c => new { c.What, c.Entry, c.Label, c.Details }).ToList() };
    }

    [AssistantTool("show_bee_timeline")]
    [Description("Shows a hand-picked set of bees together on one time line, drawn like the scenario chart (bars, colours, time axis), also across scenario entries: for example the five slowest checkouts plus the failed bees around them. The user can click a bar to see that bee's calls and logs, zoom, and step through failed and slow. Up to 60 bees. Give each bee a short reason in notes.")]
    public async Task<object> ShowBeeTimeline(
        [Description("The bees to put on the timeline, with an optional short reason each")] List<BeeNote> bees,
        [Description("Run id; leave out for the current or newest run")] string? runId = null,
        [Description("Card title, e.g. Slowest checkouts and nearby failures")] string? title = null,
        CancellationToken ct = default)
    {
        if (bees is not { Count: > 0 }) throw new ArgumentException("Give at least one bee.");
        var id = await RunIdAsync(runId, ct);
        var notes = new Dictionary<int, string?>();
        foreach (var b in bees.Take(Bounds.MaxTimelineBees)) notes[ToIndex(b.Bee, "Bee")] = b.Note;
        var found = await history.GetBeesAsync(id, notes.Keys.ToList(), ct);
        if (found.Count == 0) throw new KeyNotFoundException($"None of those bees exist in run {id}.");
        var labels = await EntryLabelsAsync(id, ct);
        var rows = found.OrderBy(b => b.StartedAt).Select(b => ViewBee(b, labels, notes.GetValueOrDefault(b.Sequence))).ToList();
        var missing = notes.Keys.Except(found.Select(b => b.Sequence)).Select(s => s + 1).ToList();
        rec.Show(new ViewItemDto(ViewKinds.Timeline, title ?? "Bee timeline", new ViewQueryDto(id), rows.Count, Bees: rows, EntryLabels: labels));
        return new
        {
            shownToUser = $"A timeline card with {rows.Count} bees ({rows.Count(r => r.Outcome == "failed")} failed, {rows.Count(r => r.Outcome == "slow")} slow).",
            missingBees = missing.Count > 0 ? missing : null,
        };
    }
}
