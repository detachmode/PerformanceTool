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
    int Failed,
    int Calls = 0,
    int Slow = 0,
    double? MinMs = null,
    double? P50Ms = null,
    double? P95Ms = null,
    double? MaxMs = null,
    double? AvgMs = null);

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
                        var callText = s.Calls > 0
                            ? $", {s.Calls} {(s.Calls == 1 ? "call" : "calls")}{(s.Slow > 0 ? $", {s.Slow} slow" : "")}, min {RunSummary.Ms(s.MinMs)}, p50 {RunSummary.Ms(s.P50Ms)}, p95 {RunSummary.Ms(s.P95Ms)}, max {RunSummary.Ms(s.MaxMs)}"
                            : "";
                        sb.AppendLine($"    step {s.Step} {s.Name}: {s.Reached} reached{failText}{callText}");
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
    IReadOnlyList<int> Entries,
    int? Step = null,
    string? StepName = null);

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
        var showStep = Endpoints.Any(e => e.Step is not null);
        var multiEntry = showStep && Endpoints.SelectMany(e => e.Entries).Distinct().Count() > 1;
        string FormatStep(RunCallEndpointDto ep)
        {
            if (ep.Step is not { } st) return "-";
            var entryPrefix = multiEntry && ep.Entries.Count == 1 ? $"#{ep.Entries[0]} " : "";
            return ep.StepName is { Length: > 0 } name ? $"{entryPrefix}step {st} {name}" : $"{entryPrefix}step {st}";
        }

        var epWidth = Math.Max(12, Endpoints.Max(e => e.Endpoint.Length));
        if (showStep)
        {
            var stepWidth = Math.Max(6, Endpoints.Max(e => FormatStep(e).Length));
            sb.AppendLine($"  {"Endpoint".PadRight(epWidth)}  {"Step".PadRight(stepWidth)}  {"Calls",7}  {"Slow",6}  {"Failed",6}  {"Min",8}  {"Typical",8}  {"P95",8}  {"Max",8}  {"Threshold",10}");
            foreach (var ep in Endpoints)
            {
                var thr = ep.SlowThresholdMs is { } t ? RunSummary.Ms(t) + (ep.HasCustomThreshold ? " *" : "") : "-";
                sb.AppendLine(
                    $"  {ep.Endpoint.PadRight(epWidth)}  {FormatStep(ep).PadRight(stepWidth)}  {ep.TotalCount,7}  {ep.SlowCount,6}  {ep.FailedCount,6}  " +
                    $"{RunSummary.Ms(ep.MinMs),8}  {RunSummary.Ms(ep.TypicalMs),8}  {RunSummary.Ms(ep.P95Ms),8}  {RunSummary.Ms(ep.MaxMs),8}  {thr,10}");
            }
        }
        else
        {
            sb.AppendLine($"  {"Endpoint".PadRight(epWidth)}  {"Calls",7}  {"Slow",6}  {"Failed",6}  {"Min",8}  {"Typical",8}  {"P95",8}  {"Max",8}  {"Threshold",10}");
            foreach (var ep in Endpoints)
            {
                var thr = ep.SlowThresholdMs is { } t ? RunSummary.Ms(t) + (ep.HasCustomThreshold ? " *" : "") : "-";
                sb.AppendLine(
                    $"  {ep.Endpoint.PadRight(epWidth)}  {ep.TotalCount,7}  {ep.SlowCount,6}  {ep.FailedCount,6}  " +
                    $"{RunSummary.Ms(ep.MinMs),8}  {RunSummary.Ms(ep.TypicalMs),8}  {RunSummary.Ms(ep.P95Ms),8}  {RunSummary.Ms(ep.MaxMs),8}  {thr,10}");
            }
        }

        return sb.ToString();
    }
}

public sealed record RunSlowestCallDto(
    int Bee,
    string BeeName,
    int Entry,
    string EntryLabel,
    int? Step,
    string? StepName,
    int? Rep,
    double StartedAtSeconds,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string Endpoint,
    string? Url,
    bool IsSlow,
    double? SlowThresholdMs,
    double? TtfbMs = null,
    double? ResponseReadMs = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Error = null);

public sealed record RunSlowestCallsReport(
    string RunId,
    long MatchingCalls,
    int Limit,
    IReadOnlyList<RunSlowestCallDto> Calls)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Slowest calls: {RunId}");
        sb.AppendLine($"  Showing top {Calls.Count} of {MatchingCalls} matching call(s):");
        if (Calls.Count == 0)
        {
            sb.AppendLine("  No matching calls.");
            return sb.ToString();
        }

        sb.AppendLine();
        var hasTtfb = Calls.Any(c => c.TtfbMs is not null);
        var hasStep = Calls.Any(c => c.Step is not null);
        string FormatStep(RunSlowestCallDto c) =>
            c.Step is { } st
                ? (c.StepName is { Length: > 0 } name ? $"step {st} {name}" : $"step {st}")
                : "-";

        var beeWidth = Math.Max(4, Calls.Max(c => c.BeeName.Length));
        var stepWidth = hasStep ? Math.Max(4, Calls.Max(c => FormatStep(c).Length)) : 0;
        var epWidth = Math.Max(8, Calls.Max(c => c.Endpoint.Length));

        var header = new StringBuilder();
        header.Append($"  {"Bee".PadRight(beeWidth)}");
        if (hasStep) header.Append($"  {"Step".PadRight(stepWidth)}");
        header.Append($"  {"Started",9}  {"Duration",10}");
        if (hasTtfb) header.Append($"  {"TTFB",10}  {"Read",10}");
        header.Append($"  {"Status",6}  {"Endpoint".PadRight(epWidth)}");
        sb.AppendLine(header.ToString());

        foreach (var c in Calls)
        {
            var statusStr = c.StatusCode is { } sc ? sc.ToString(CultureInfo.InvariantCulture) : (c.Error is not null ? "ERR" : "-");
            var row = new StringBuilder();
            row.Append($"  {c.BeeName.PadRight(beeWidth)}");
            if (hasStep) row.Append($"  {FormatStep(c).PadRight(stepWidth)}");
            row.Append($"  {c.StartedAtSeconds,7:0.00} s  {RunSummary.Ms(c.DurationMs),10}");
            if (hasTtfb) row.Append($"  {RunSummary.Ms(c.TtfbMs),10}  {RunSummary.Ms(c.ResponseReadMs),10}");
            row.Append($"  {statusStr,6}  {c.Endpoint}");
            if (c.Error is not null) row.Append($"  ERROR: {RunSummary.Shorten(c.Error, 80)}");
            sb.AppendLine(row.ToString());

            if (c.Headers is { Count: > 0 })
            {
                var hdrText = string.Join(", ", c.Headers.Select(kv => $"{kv.Key}={kv.Value}"));
                sb.AppendLine($"    headers: {hdrText}");
            }
        }

        return sb.ToString();
    }
}

public sealed record RunTimelineBucketDto(
    double FromSeconds,
    double ToSeconds,
    int ActiveBees,
    int StartedBees,
    int Calls,
    double RequestsPerSecond,
    double? P50Ms,
    double? P95Ms,
    double? MaxMs,
    int Slow,
    int Failed);

public sealed record RunTimelineReport(
    string RunId,
    double ElapsedSeconds,
    double BucketSeconds,
    IReadOnlyList<RunTimelineBucketDto> Buckets)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run timeline: {RunId} ({BucketSeconds:0.##} s buckets, elapsed {RunSummary.Duration(ElapsedSeconds)})");
        if (Buckets.Count == 0)
        {
            sb.AppendLine("  No timeline data.");
            return sb.ToString();
        }

        sb.AppendLine();
        string FormatRange(RunTimelineBucketDto b) => $"{b.FromSeconds:0.#}-{b.ToSeconds:0.#}s";
        var rangeWidth = Math.Max(7, Buckets.Max(b => FormatRange(b).Length));
        sb.AppendLine($"  {"Elapsed".PadRight(rangeWidth)}  {"Active",6}  {"Req/s",8}  {"P50",9}  {"P95",9}  {"Max",9}  {"Slow",6}  {"Failed",6}");
        foreach (var b in Buckets)
        {
            sb.AppendLine(
                $"  {FormatRange(b).PadRight(rangeWidth)}  {b.ActiveBees,6}  {b.RequestsPerSecond,8:0.0}  " +
                $"{RunSummary.Ms(b.P50Ms),9}  {RunSummary.Ms(b.P95Ms),9}  {RunSummary.Ms(b.MaxMs),9}  {b.Slow,6}  {b.Failed,6}");
        }

        return sb.ToString();
    }
}

internal static class RunsTimelineBuilder
{
    public static RunTimelineReport Build(
        string runId,
        double elapsedSeconds,
        double bucketSeconds,
        IReadOnlyList<(double StartedAt, double DurationMs, bool IsFailed, bool IsSlow)> bees,
        IReadOnlyList<CallFact> calls)
    {
        var maxEnd = elapsedSeconds;
        foreach (var b in bees)
        {
            var end = b.StartedAt + b.DurationMs / 1000.0;
            if (end > maxEnd) maxEnd = end;
        }
        foreach (var c in calls)
        {
            if (c.AtSeconds > maxEnd) maxEnd = c.AtSeconds;
        }

        if (bees.Count == 0 && calls.Count == 0 && maxEnd <= 0)
            return new RunTimelineReport(runId, elapsedSeconds, bucketSeconds, []);

        var bucketCount = Math.Max(1, (int)Math.Ceiling(Math.Max(maxEnd, bucketSeconds) / bucketSeconds));
        var useCalls = calls.Count > 0;

        Dictionary<long, bool>? callSlowLookup = null;
        if (useCalls)
        {
            callSlowLookup = new Dictionary<long, bool>(calls.Count);
            var slowestAll = CallAnalytics.BuildSlowestCalls(calls, calls.Count);
            foreach (var sc in slowestAll.Calls)
                callSlowLookup[sc.Id] = sc.IsSlow;
        }

        var buckets = new List<RunTimelineBucketDto>(bucketCount);
        var durs = new List<double>();
        var allDurs = new List<double>();

        for (var i = 0; i < bucketCount; i++)
        {
            var from = Math.Round(i * bucketSeconds, 3);
            var to = Math.Round((i + 1) * bucketSeconds, 3);
            var isLast = i == bucketCount - 1;

            int active = 0, started = 0;
            int slow = 0, failed = 0;
            durs.Clear();
            allDurs.Clear();

            foreach (var b in bees)
            {
                var end = b.StartedAt + b.DurationMs / 1000.0;
                if (b.StartedAt < to && end >= from) active++;
                var inStart = b.StartedAt >= from && (isLast ? b.StartedAt <= to : b.StartedAt < to);
                if (inStart)
                {
                    started++;
                    if (!useCalls)
                    {
                        allDurs.Add(b.DurationMs);
                        if (b.IsFailed) failed++;
                        else durs.Add(b.DurationMs);
                        if (b.IsSlow) slow++;
                    }
                }
            }

            var callCount = 0;
            if (useCalls)
            {
                foreach (var c in calls)
                {
                    var t = c.AtSeconds;
                    if (t >= from && (isLast ? t <= to : t < to))
                    {
                        callCount++;
                        allDurs.Add(c.DurationMs);
                        if (c.Error is not null) failed++;
                        else durs.Add(c.DurationMs);
                        if (callSlowLookup is not null && callSlowLookup.TryGetValue(c.Id, out var isSlow) && isSlow)
                            slow++;
                    }
                }
            }

            var sample = durs.Count > 0 ? durs : allDurs;
            double? p50 = null, p95 = null, max = null;
            if (sample.Count > 0)
            {
                sample.Sort();
                p50 = sample[(sample.Count - 1) / 2];
                p95 = sample[Math.Max(0, (int)Math.Ceiling(0.95 * sample.Count) - 1)];
                max = sample[^1];
            }

            var reqPerSec = Math.Round((useCalls ? callCount : started) / bucketSeconds, 2);
            buckets.Add(new RunTimelineBucketDto(from, to, active, started, callCount, reqPerSec, p50, p95, max, slow, failed));
        }

        return new RunTimelineReport(runId, elapsedSeconds, bucketSeconds, buckets);
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
    string? Error,
    double? TtfbMs = null,
    double? ResponseReadMs = null,
    IReadOnlyDictionary<string, string>? Headers = null);

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
                var timingPart = c.TtfbMs is { } ttfb
                    ? $" (ttfb {RunSummary.Ms(ttfb)}, read {RunSummary.Ms(c.ResponseReadMs)})"
                    : "";
                var urlPart = string.IsNullOrWhiteSpace(c.Url) || string.Equals(c.Url, c.Name, StringComparison.Ordinal) ? "" : $"  {c.Url}";
                var errPart = string.IsNullOrEmpty(c.Error) ? "" : $"  ERROR: {c.Error}";
                sb.AppendLine($"    #{c.Call}{stepPart} +{c.OffsetMs:0.0} ms  {c.Name}{statusPart}  {RunSummary.Ms(c.DurationMs)}{timingPart}{urlPart}{errPart}");
                if (c.Headers is { Count: > 0 })
                    sb.AppendLine($"      headers: {string.Join(", ", c.Headers.Select(kv => $"{kv.Key}={kv.Value}"))}");
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
