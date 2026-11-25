using System.Globalization;
using System.Text;
using System.Text.Json;
using Beesharp.Services;

namespace Beesharp.Cli.Runs;

internal static class RunsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public sealed record RunListItemDto(
    string RunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string State,
    double ElapsedSeconds,
    int TotalBees,
    int OkBees,
    int FailedBees,
    long TotalLogs,
    string? Error);

public sealed record RunListReport(
    string Url,
    int Count,
    IReadOnlyList<RunListItemDto> Runs)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Runs on {Url} ({Count}):");
        if (Runs.Count == 0)
        {
            sb.AppendLine("  No stored runs.");
            return sb.ToString();
        }

        sb.AppendLine();
        var idWidth = Math.Max(6, Runs.Max(r => r.RunId.Length));
        var stateWidth = Math.Max(5, Runs.Max(r => r.State.Length));
        sb.AppendLine($"  {"Run ID".PadRight(idWidth)}  {"State".PadRight(stateWidth)}  {"Started (UTC)",-19}  {"Elapsed",9}  {"Bees",7}  {"Failed",6}  {"Logs",8}");
        foreach (var r in Runs)
        {
            var errPart = string.IsNullOrWhiteSpace(r.Error) ? "" : $"  error: {RunSummary.Shorten(r.Error, 60)}";
            sb.AppendLine(
                $"  {r.RunId.PadRight(idWidth)}  {r.State.PadRight(stateWidth)}  {r.StartedUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss}  " +
                $"{RunSummary.Duration(r.ElapsedSeconds),9}  {r.TotalBees,7}  {r.FailedBees,6}  {r.TotalLogs,8}{errPart}");
        }

        return sb.ToString();
    }
}

public sealed record RunSummaryStepDto(
    int Step,
    string Name,
    int Reached,
    int Failed);

public sealed record RunSummaryEntryDto(
    int Entry,
    string ScenarioId,
    string Label,
    int TotalBees,
    int OkBees,
    int FailedBees,
    int CanceledBees,
    int SlowBees,
    double? MinMs,
    double? P50Ms,
    double? P95Ms,
    double? MaxMs,
    double? AvgMs,
    IReadOnlyList<RunSummaryStepDto> Steps,
    int? FinishedAllSteps);

public sealed record RunSummaryReport(
    string RunId,
    string State,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    double ElapsedSeconds,
    string? Error,
    int TotalBees,
    int OkBees,
    int FailedBees,
    int CanceledBees,
    int SlowBees,
    double? MinMs,
    double? P50Ms,
    double? P95Ms,
    double? MaxMs,
    double? AvgMs,
    IReadOnlyList<RunSummaryEntryDto> Entries)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run summary: {RunId}");
        sb.AppendLine($"  State       {State}");
        sb.AppendLine($"  Started     {StartedUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");
        if (FinishedUtc is { } fin)
            sb.AppendLine($"  Finished    {fin.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"  Elapsed     {RunSummary.Duration(ElapsedSeconds)}");
        if (Error is not null)
            sb.AppendLine($"  Error       {Error}");
        sb.AppendLine();
        sb.AppendLine($"  Bees        total {TotalBees}, ok {OkBees}, failed {FailedBees} (canceled {CanceledBees}), slow {SlowBees}");
        sb.AppendLine($"  Latency     min {RunSummary.Ms(MinMs)}, p50 {RunSummary.Ms(P50Ms)}, p95 {RunSummary.Ms(P95Ms)}, max {RunSummary.Ms(MaxMs)}");

        if (Entries.Count > 0)
        {
            sb.AppendLine();
            var labelWidth = Math.Max(8, Entries.Max(e => $"#{e.Entry} {e.Label}".Length));
            sb.AppendLine($"  {"Scenario".PadRight(labelWidth)}  {"total",7}  {"ok",7}  {"failed",6}  {"slow",6}  {"min",8}  {"p50",8}  {"p95",8}  {"max",8}");
            foreach (var e in Entries)
            {
                var name = $"#{e.Entry} {e.Label}";
                sb.AppendLine(
                    $"  {name.PadRight(labelWidth)}  {e.TotalBees,7}  {e.OkBees,7}  {e.FailedBees,6}  {e.SlowBees,6}  " +
                    $"{RunSummary.Ms(e.MinMs),8}  {RunSummary.Ms(e.P50Ms),8}  {RunSummary.Ms(e.P95Ms),8}  {RunSummary.Ms(e.MaxMs),8}");

                if (e.Steps.Count > 0)
                {
                    foreach (var s in e.Steps)
                    {
                        var failText = s.Failed > 0 ? $", {s.Failed} failed" : "";
                        sb.AppendLine($"    step {s.Step} {s.Name}: {s.Reached} reached{failText}");
                    }
                    if (e.FinishedAllSteps is { } passedAll)
                        sb.AppendLine($"    finished all steps: {passedAll}");
                }
            }
        }

        return sb.ToString();
    }
}

public sealed record ErrorEntryAffectDto(int Entry, string Label, int Count);

public sealed record ErrorStepAffectDto(int Entry, int Step, string Name, int Count);

public sealed record GroupedErrorDto(
    string Error,
    int Count,
    bool IsCanceled,
    IReadOnlyList<ErrorEntryAffectDto> Entries,
    IReadOnlyList<ErrorStepAffectDto> Steps,
    IReadOnlyList<int> SampleBees,
    IReadOnlyList<string> SampleBeeNames);

public sealed record RunErrorsReport(
    string RunId,
    string? RunError,
    int TotalFailedBees,
    int DistinctErrors,
    IReadOnlyList<GroupedErrorDto> Errors)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run errors: {RunId}");
        if (RunError is not null)
            sb.AppendLine($"  Run error: {RunError}");
        sb.AppendLine($"  Failed bees: {TotalFailedBees} across {DistinctErrors} distinct error(s)");

        if (Errors.Count == 0)
        {
            sb.AppendLine("  No bee errors.");
            return sb.ToString();
        }

        sb.AppendLine();
        foreach (var g in Errors)
        {
            sb.AppendLine($"  {g.Count}x  {g.Error}");
            if (g.Entries.Count > 0)
                sb.AppendLine("    entries: " + string.Join(", ", g.Entries.Select(e => $"#{e.Entry} {e.Label} ({e.Count})")));
            if (g.Steps.Count > 0)
                sb.AppendLine("    steps:   " + string.Join(", ", g.Steps.Select(s => $"#{s.Entry} step {s.Step} {s.Name} ({s.Count})")));
            if (g.SampleBeeNames.Count > 0)
                sb.AppendLine("    samples: " + string.Join(", ", g.SampleBeeNames));
        }

        return sb.ToString();
    }
}

public sealed record RunCallEndpointDto(
    string Endpoint,
    string? Method,
    string? Host,
    int TotalCount,
    int SlowCount,
    int FailedCount,
    double TypicalMs,
    double P95Ms,
    double MaxMs,
    double MinMs,
    double AvgMs,
    double? SlowThresholdMs,
    bool HasCustomThreshold,
    IReadOnlyList<int> Entries);

public sealed record RunCallsReport(
    string RunId,
    long TotalCalls,
    long TotalSlowCalls,
    long TotalFailedCalls,
    IReadOnlyList<RunCallEndpointDto> Endpoints)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run calls: {RunId}");
        sb.AppendLine($"  Total calls: {TotalCalls}, slow: {TotalSlowCalls}, failed: {TotalFailedCalls}, endpoints: {Endpoints.Count}");

        if (Endpoints.Count == 0)
        {
            sb.AppendLine("  No matching calls.");
            return sb.ToString();
        }

        sb.AppendLine();
        var epWidth = Math.Max(12, Endpoints.Max(e => e.Endpoint.Length));
        sb.AppendLine($"  {"Endpoint".PadRight(epWidth)}  {"Calls",7}  {"Slow",6}  {"Failed",6}  {"Min",8}  {"Typical",8}  {"P95",8}  {"Max",8}  {"Threshold",10}");
        foreach (var ep in Endpoints)
        {
            var thr = ep.SlowThresholdMs is { } t ? RunSummary.Ms(t) + (ep.HasCustomThreshold ? " *" : "") : "-";
            sb.AppendLine(
                $"  {ep.Endpoint.PadRight(epWidth)}  {ep.TotalCount,7}  {ep.SlowCount,6}  {ep.FailedCount,6}  " +
                $"{RunSummary.Ms(ep.MinMs),8}  {RunSummary.Ms(ep.TypicalMs),8}  {RunSummary.Ms(ep.P95Ms),8}  {RunSummary.Ms(ep.MaxMs),8}  {thr,10}");
        }

        return sb.ToString();
    }
}

public sealed record RunLogLineDto(
    long Id,
    int? Bee,
    string BeeName,
    int? Entry,
    string EntryKind,
    string EntryLabel,
    int? Step,
    string? StepName,
    int? Rep,
    double AtSeconds,
    string Level,
    string Message,
    string? Exception,
    int? Call,
    bool IsFailed,
    bool IsSlow);

public sealed record RunLogsReport(
    string RunId,
    long TotalCount,
    int Offset,
    int Limit,
    bool HasMore,
    IReadOnlyList<RunLogLineDto> Logs)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run logs: {RunId}");
        if (Logs.Count == 0)
        {
            sb.AppendLine($"  0 of {TotalCount} matching log lines (offset {Offset}).");
            return sb.ToString();
        }

        var from = Offset + 1;
        var to = Offset + Logs.Count;
        sb.AppendLine($"  Showing {from}-{to} of {TotalCount} matching log lines{(HasMore ? " (more available)" : "")}:");
        sb.AppendLine();

        foreach (var l in Logs)
        {
            var shortLvl = l.Level.ToLowerInvariant() switch
            {
                "trace" => "TRC",
                "debug" => "DBG",
                "information" => "INF",
                "warning" => "WRN",
                "error" => "ERR",
                _ => l.Level.Length >= 3 ? l.Level[..3].ToUpperInvariant() : l.Level.ToUpperInvariant(),
            };
            var stepPart = l.Step is { } st
                ? (l.StepName is not null ? $" / step {st} {l.StepName}" : $" / step {st}")
                : "";
            var excPart = string.IsNullOrEmpty(l.Exception) ? "" : $" | {l.Exception}";
            sb.AppendLine($"  [{RunSummary.FormatClock(l.AtSeconds)}] {shortLvl} [{l.EntryLabel}{stepPart}] {l.BeeName}: {l.Message}{excPart}");
        }

        return sb.ToString();
    }
}

public sealed record BeeStepVisitDto(int Step, string Name, int Rep, string? Data);

public sealed record BeeCallDto(
    int Call,
    string Name,
    string? Endpoint,
    string? Method,
    string? Host,
    string? Url,
    int? Step,
    string? StepName,
    int? Rep,
    double OffsetMs,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    double? SlowThresholdMs,
    string? Error);

public sealed record BeeLogDto(
    long Id,
    double AtSeconds,
    string Level,
    string Message,
    string? Exception,
    int? Step,
    string? StepName,
    int? Rep,
    int? Call);

public sealed record RunBeeReport(
    string RunId,
    int Bee,
    string BeeName,
    int Entry,
    string EntryLabel,
    double StartedAtSeconds,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string Outcome,
    bool IsFailed,
    bool IsCanceled,
    bool IsSlow,
    int SlowCalls,
    string? Error,
    int? FailedStep,
    string? FailedStepName,
    int? FailedRep,
    IReadOnlyList<BeeStepVisitDto> StepVisits,
    IReadOnlyList<BeeCallDto> Calls,
    IReadOnlyList<BeeLogDto> Logs)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Bee {BeeName} (#{Bee}) in run {RunId}");
        sb.AppendLine($"  Entry       #{Entry} {EntryLabel}");
        sb.AppendLine($"  Outcome     {Outcome}{(IsSlow ? $" ({SlowCalls} slow call(s))" : "")}");
        sb.AppendLine($"  Started at  {StartedAtSeconds:0.000} s   Duration  {RunSummary.Ms(DurationMs)}");
        sb.AppendLine($"  Status      {(StatusCode is { } sc ? sc.ToString(CultureInfo.InvariantCulture) : "-")}   Bytes  {RunSummary.FormatBytes(Bytes)}");
        if (FailedStep is { } fs)
        {
            var repPart = FailedRep is { } fr ? $" (visit {fr})" : "";
            sb.AppendLine($"  Failed at   step {fs}{(FailedStepName is not null ? $" {FailedStepName}" : "")}{repPart}");
        }
        if (Error is not null)
            sb.AppendLine($"  Error       {Error}");

        if (StepVisits.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Step visits ({StepVisits.Count}):");
            foreach (var v in StepVisits)
            {
                var repText = v.Rep > 1 ? $" (visit {v.Rep})" : "";
                var dataText = string.IsNullOrWhiteSpace(v.Data) ? "" : $"  {v.Data}";
                sb.AppendLine($"    step {v.Step} {v.Name}{repText}{dataText}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"  Calls ({Calls.Count}):");
        if (Calls.Count == 0)
        {
            sb.AppendLine("    (none)");
        }
        else
        {
            foreach (var c in Calls)
            {
                var stepPart = c.Step is { } st ? $" [step {st}{(c.StepName is not null ? $" {c.StepName}" : "")}]" : "";
                var statusPart = c.StatusCode is { } code ? $" HTTP {code}" : "";
                var urlPart = string.IsNullOrWhiteSpace(c.Url) || string.Equals(c.Url, c.Name, StringComparison.Ordinal) ? "" : $"  {c.Url}";
                var errPart = string.IsNullOrEmpty(c.Error) ? "" : $"  ERROR: {c.Error}";
                sb.AppendLine($"    #{c.Call}{stepPart} +{c.OffsetMs:0.0} ms  {c.Name}{statusPart}  {RunSummary.Ms(c.DurationMs)}{urlPart}{errPart}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"  Logs ({Logs.Count}):");
        if (Logs.Count == 0)
        {
            sb.AppendLine("    (none)");
        }
        else
        {
            foreach (var l in Logs)
            {
                var stepPart = l.Step is { } st ? $" [step {st}{(l.StepName is not null ? $" {l.StepName}" : "")}]" : "";
                var excPart = string.IsNullOrEmpty(l.Exception) ? "" : $" | {l.Exception}";
                sb.AppendLine($"    [{RunSummary.FormatClock(l.AtSeconds)}] {l.Level}{stepPart} {l.Message}{excPart}");
            }
        }

        return sb.ToString();
    }
}
