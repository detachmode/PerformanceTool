using System.Globalization;
using System.Text;
using Beesharp.Services;

namespace Beesharp.Cli.Runs;

/// <summary>
/// <c>beesharp runs trace</c> (brain 0065): one bee's trace (or the part below one call) as a flat, bounded span list. The bee number is 1-based like everywhere in the CLI;
/// the spans are the shared <see cref="TraceSpanViewDto"/>.
/// </summary>
public sealed record RunTraceReport(
    string RunId,
    string TraceId,
    bool Found,
    string? Reason,
    string? ReasonText,
    int? Bee,
    string? BeeName,
    int? Call,
    string? Endpoint,
    double ReferenceMs,
    string ReferenceKind,
    int TotalSpans,
    int ShownSpans,
    int ErrorSpans,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> SelectedFor,
    DateTimeOffset? FetchedUtc,
    string? Note,
    IReadOnlyList<TraceSpanViewDto> Spans)
{
    /// <summary>Converts the shared view (0-based sequence) to the CLI's report (1-based bee).</summary>
    /// <param name="view">The view from the storage or the server.</param>
    /// <returns>The report.</returns>
    public static RunTraceReport From(TraceViewDto view) => new(
        view.RunId, view.TraceId, view.Found, view.Reason, view.ReasonText, view.Sequence + 1, view.BeeName, view.Call, view.Endpoint,
        view.ReferenceMs, view.ReferenceKind, view.TotalSpans, view.ShownSpans, view.ErrorSpans, view.Services, view.SelectedFor, view.FetchedUtc, view.Note, view.Spans);

    /// <summary>The exit code: 0 when the trace is shown, 3 when it is not stored with the run.</summary>
    public int ExitCode => Found ? ExitCodes.Ok : ExitCodes.TraceNotFound;

    /// <summary>The text for terminals: summary first, then the bounded span list.</summary>
    /// <returns>Multi-line text without colour codes.</returns>
    public string ToText()
    {
        var sb = new StringBuilder();
        var who = BeeName is null ? "trace" : $"trace of bee {BeeName}";
        if (!Found)
        {
            sb.AppendLine($"The {who} is not stored with run {RunId}.");
            if (!string.IsNullOrEmpty(TraceId)) sb.AppendLine($"  Trace ID   {TraceId}");
            sb.AppendLine($"  Reason     {ReasonText}");
            if (SelectedFor.Count > 0) sb.AppendLine($"  Selected   {string.Join("; ", SelectedFor)}");
            if (FetchedUtc is { } fetched) sb.AppendLine($"  Asked at   {fetched.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");
            return sb.ToString();
        }

        sb.AppendLine($"Trace {BeeName}{(Call is { } c ? $" call #{c}" : "")}  {TraceId}   (run {RunId})");
        var shown = ShownSpans == TotalSpans ? "all shown" : $"showing {ShownSpans}";
        sb.AppendLine($"  Spans       {TotalSpans} ({shown}) in {Services.Count} service(s): {string.Join(", ", Services)}{(ErrorSpans > 0 ? $"; {ErrorSpans} with error" : "")}");
        sb.AppendLine(Call is { } n
            ? $"  Reference   call #{n} {Endpoint}, {RunSummary.Ms(ReferenceMs)}: shares are of the call's duration"
            : $"  Reference   the bee's duration {RunSummary.Ms(ReferenceMs)}: shares are of that");
        if (SelectedFor.Count > 0 || FetchedUtc is not null)
            sb.AppendLine($"  Fetched     {(FetchedUtc is { } f ? f.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "")}{(SelectedFor.Count > 0 ? $"  selected for: {string.Join("; ", SelectedFor)}" : "")}");
        if (Note is not null) sb.AppendLine($"  Note        {Note}");
        if (Spans.Count == 0) return sb.ToString();

        sb.AppendLine();
        const int maxName = 56;
        string Name(TraceSpanViewDto s)
        {
            var text = new string(' ', Math.Min(s.Depth, 12) * 2) + s.Name;
            return text.Length <= maxName ? text : text[..(maxName - 1)] + "…";
        }
        var nameWidth = Math.Max(4, Spans.Max(s => Name(s).Length));
        var serviceWidth = Math.Max(7, Spans.Max(s => s.Service.Length));
        sb.AppendLine($"  {"Span".PadRight(nameWidth)}  {"Service".PadRight(serviceWidth)}  {"Duration",9}  {"Self",9}  {"Share",6}  Status");
        foreach (var s in Spans)
        {
            var status = s.IsError ? "ERROR" : s.HttpStatus is { } http ? $"HTTP {http}" : "";
            sb.AppendLine($"  {Name(s).PadRight(nameWidth)}  {s.Service.PadRight(serviceWidth)}  {RunSummary.Ms(s.DurationMs),9}  {RunSummary.Ms(s.SelfMs),9}  {Percent.Of(s.Share),6}  {status}".TrimEnd());
            var key = TraceAnalytics.KeyAttributes(s.Attributes);
            if (key.Count > 0)
                sb.AppendLine($"  {new string(' ', Math.Min(s.Depth, 12) * 2 + 2)}{string.Join("  ", key.Select(kv => $"{kv.Key}={RunSummary.Shorten(kv.Value, 120)}"))}");
            if (s.IsError && !string.IsNullOrWhiteSpace(s.StatusMessage))
                sb.AppendLine($"  {new string(' ', Math.Min(s.Depth, 12) * 2 + 2)}error: {RunSummary.Shorten(s.StatusMessage, 160)}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Builds the trace report of a bundle (offline) with the same <see cref="TraceAnalytics"/> engine the server uses: the bundle reader supplies how to read a bee, the fetch records and the spans.
/// </summary>
internal static class TraceReportBuilder
{
    public static RunTraceReport Build(
        string runId,
        RunsTraceOptions options,
        IReadOnlyList<TraceFetchDto> fetches,
        Func<string, int?> sequenceOfTraceId,
        Func<int, RunBeeReport> readBee,
        Func<string, IReadOnlyList<TraceSpanDto>> readSpans)
    {
        var key = TraceAnalytics.ParseKey(options.Key)
            ?? throw new CouldNotRunException($"'{options.Key}' is neither a bee (5 or B5) nor a trace ID (32 hex characters).");
        var sequence = key.Sequence
            ?? fetches.FirstOrDefault(f => f.TraceId == key.TraceId)?.Sequence
            ?? sequenceOfTraceId(key.TraceId!)
            ?? throw new CouldNotRunException($"Trace ID {key.TraceId} does not belong to any bee of run '{runId}'.");

        var bee = readBee(sequence);
        var info = new TraceAnalytics.BeeTraceInfo(
            sequence, bee.TraceId, bee.DurationMs,
            bee.Calls.Select(c => new TraceAnalytics.CallTraceInfo(c.Call, c.SpanId, c.DurationMs, c.Endpoint ?? c.Name)).ToList());
        var spans = bee.TraceId is null ? [] : readSpans(bee.TraceId);
        try { return RunTraceReport.From(TraceAnalytics.ComposeView(runId, info, options.Call, options.MaxSpans, fetches, spans)); }
        catch (ArgumentException ex) { throw new CouldNotRunException(ex.Message); }
    }
}

internal static class Percent
{
    /// <summary>A share (0 to 1) as <c>37%</c>; <c>&lt;1%</c> for something small but not zero.</summary>
    public static string Of(double share) => share <= 0 ? "0%" : share < 0.005 ? "<1%" : $"{share * 100:0}%";
}

/// <summary>Text of <c>beesharp runs hotspots</c>; the JSON is the shared <see cref="HotspotsResultDto"/> as it is.</summary>
public static class HotspotsText
{
    /// <summary>The text for terminals: coverage first, then per endpoint its hotspots, slowest explanations first.</summary>
    /// <param name="result">The hotspots of a run.</param>
    /// <returns>Multi-line text without colour codes.</returns>
    public static string ToText(HotspotsResultDto result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Hotspots in run {result.RunId}: where the time of slow calls went");
        sb.AppendLine($"  {result.Coverage.Line}");
        if (result.Note is not null) sb.AppendLine($"  {result.Note}");
        if (result.Endpoints.Count == 0) return sb.ToString();
        sb.AppendLine(result.EndpointsTotal > result.Endpoints.Count
            ? $"  Showing {result.Endpoints.Count} of {result.EndpointsTotal} endpoints (use --limit)"
            : $"  {result.EndpointsTotal} endpoint(s) with fetched traces");

        foreach (var e in result.Endpoints)
        {
            sb.AppendLine();
            var threshold = e.SlowThresholdMs is { } t ? $", slow above {RunSummary.Ms(t)}" : "";
            sb.AppendLine($"{e.Endpoint}: {e.SlowCalls} of {e.TotalCalls} calls slow (typical {RunSummary.Ms(e.TypicalMs)}{threshold})");
            sb.AppendLine(e.Basis == "slow"
                ? $"  {e.SlowCallsWithTrace} slow call(s) with a trace (median {RunSummary.Ms(e.SlowMedianMs)}), {e.TypicalCallsWithTrace} typical call(s) as the baseline"
                : $"  no slow call with a trace; showing where its {e.TypicalCallsWithTrace} typical call(s) with a trace spend their time");
            var rank = 0;
            foreach (var h in e.Hotspots)
            {
                rank++;
                var who = h.IsOutside ? $"{h.Service}: {h.Name}" : $"{h.Service}  {h.Name}";
                var explains = e.Basis == "slow"
                    ? $"explains {h.Explains} of {e.SlowCallsWithTrace} slow calls ({Percent.Of(h.ExplainsShare)})"
                    : $"largest part of {h.Explains} of {e.TypicalCallsWithTrace} typical calls ({Percent.Of(h.ExplainsShare)})";
                var baseline = h.BaselineSelfMs is { } b
                    ? $", {(h.Factor is { } f ? $"{f:0.#}x " : "")}the baseline of {RunSummary.Ms(b)}"
                    : "";
                sb.AppendLine($"  {rank}. {who}");
                sb.AppendLine($"     {explains}; typically {RunSummary.Ms(h.MedianSelfMs)} = {Percent.Of(h.MedianShare)} of the call{baseline}");
                if (h.ExampleAttributes is { Count: > 0 })
                    sb.AppendLine($"     {string.Join("  ", h.ExampleAttributes.Select(kv => $"{kv.Key}={RunSummary.Shorten(kv.Value, 120)}"))}");
                if (h.ExampleTraceIds.Count > 0)
                    sb.AppendLine($"     traces: {string.Join(", ", h.ExampleTraceIds)}  (beesharp runs trace <run> <id>)");
            }
        }
        return sb.ToString();
    }
}
