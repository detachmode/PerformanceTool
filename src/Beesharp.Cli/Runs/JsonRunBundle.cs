using System.Globalization;
using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Cli.Runs;

/// <summary>
/// Parses exported <c>.json</c> run files directly without DuckDB.
/// Supports both full DuckDB JSON exports and compact <c>InMemoryRunStorage</c> JSON exports.
/// </summary>
internal sealed class JsonRunBundle : IRunBundle
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string runId;
    private readonly DateTimeOffset startedUtc;
    private readonly DateTimeOffset? finishedUtc;
    private readonly string state;
    private readonly double elapsedSeconds;
    private readonly string? error;
    private readonly LoadPlan plan;
    private readonly IReadOnlyList<IReadOnlyList<StepInfo>>? steps;
    private readonly bool isSummaryOnlyFormat;
    private readonly int summaryOnlyTotalBees;
    private readonly int summaryOnlyFailedBees;
    private readonly List<JsonBee> bees;
    private readonly List<JsonCall> calls;
    private readonly List<JsonLog> logs;

    private sealed record JsonBee(
        int Sequence,
        string BeeName,
        int Entry,
        double StartedAt,
        double DurationMs,
        int? StatusCode,
        long Bytes,
        string? Error,
        bool IsFailed,
        bool IsSlow,
        int SlowCalls,
        int FailedStep,
        int FailedRep,
        IReadOnlyList<RecordedStepVisit>? StepVisits);

    private sealed record JsonCall(
        int Sequence,
        int CallIndex,
        string Name,
        double OffsetMs,
        double DurationMs,
        int? StatusCode,
        long Bytes,
        string? Error,
        int Step,
        int Rep,
        string? Endpoint,
        string? Method,
        string? Host,
        string? Url,
        double? SlowThresholdMs);

    private sealed record JsonLog(
        long Id,
        int Sequence,
        string BeeName,
        int Entry,
        double AtSeconds,
        string Level,
        string Message,
        string? Exception,
        int? CallIndex,
        bool IsFailed,
        bool IsSlow,
        int Step,
        int Rep);

    private JsonRunBundle(
        string runId,
        DateTimeOffset startedUtc,
        DateTimeOffset? finishedUtc,
        string state,
        double elapsedSeconds,
        string? error,
        LoadPlan plan,
        IReadOnlyList<IReadOnlyList<StepInfo>>? steps,
        bool isSummaryOnlyFormat,
        int summaryOnlyTotalBees,
        int summaryOnlyFailedBees,
        List<JsonBee> bees,
        List<JsonCall> calls,
        List<JsonLog> logs)
    {
        this.runId = runId;
        this.startedUtc = startedUtc;
        this.finishedUtc = finishedUtc;
        this.state = state;
        this.elapsedSeconds = elapsedSeconds;
        this.error = error;
        this.plan = plan;
        this.steps = steps;
        this.isSummaryOnlyFormat = isSummaryOnlyFormat;
        this.summaryOnlyTotalBees = summaryOnlyTotalBees;
        this.summaryOnlyFailedBees = summaryOnlyFailedBees;
        this.bees = bees;
        this.calls = calls;
        this.logs = logs;
    }

    public static JsonRunBundle Open(string path)
    {
        JsonDocument doc;
        try
        {
            using var stream = File.OpenRead(path);
            doc = JsonDocument.Parse(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new CouldNotRunException($"Could not read JSON run export '{path}': {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new CouldNotRunException($"Invalid JSON run export '{path}': expected a JSON object.");

            // Format A: InMemoryRunStorage export ({ "run": StoredRunSummary, "logs": StoredLogRow[] })
            if (!TryGetProp(root, "runId", out _) && TryGetProp(root, "run", out var runEl) && runEl.ValueKind == JsonValueKind.Object)
            {
                var memRunId = GetString(runEl, "runId")
                    ?? throw new CouldNotRunException($"Invalid JSON run export '{path}': missing run.runId.");
                var memStarted = ParseDate(GetString(runEl, "startedUtc")) ?? DateTimeOffset.UtcNow;
                var memFinished = ParseDate(GetString(runEl, "finishedUtc"));
                var memState = GetString(runEl, "state") ?? "Finished";
                var memElapsed = GetDouble(runEl, "elapsedSeconds") ?? 0;
                var memError = GetString(runEl, "error");
                var memTotal = GetInt(runEl, "totalBees") ?? 0;
                var memFailed = GetInt(runEl, "failedBees") ?? 0;
                var memLogs = ParseLogs(root);

                return new JsonRunBundle(
                    memRunId, memStarted, memFinished, memState, memElapsed, memError,
                    new LoadPlan(), null, true, memTotal, memFailed, [], [], memLogs);
            }

            // Format B: DuckDbRunStorage JSON export
            var runId = GetString(root, "runId");
            if (string.IsNullOrWhiteSpace(runId))
                throw new CouldNotRunException($"Invalid JSON run export '{path}': missing runId.");

            var startedUtc = ParseDate(GetString(root, "startedUtc")) ?? DateTimeOffset.UtcNow;
            var finishedUtc = ParseDate(GetString(root, "finishedUtc"));
            var state = GetString(root, "state") ?? "Finished";
            var elapsedSeconds = GetDouble(root, "elapsedSeconds") ?? 0;
            var error = GetString(root, "error");

            var plan = TryGetProp(root, "plan", out var planEl) && planEl.ValueKind == JsonValueKind.Object
                ? (JsonSerializer.Deserialize<LoadPlan>(planEl.GetRawText(), JsonOpts) ?? new LoadPlan())
                : new LoadPlan();

            IReadOnlyList<IReadOnlyList<StepInfo>>? steps = null;
            if (TryGetProp(root, "steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
                steps = JsonSerializer.Deserialize<List<List<StepInfo>>>(stepsEl.GetRawText(), JsonOpts);

            var bees = ParseBees(root);
            var calls = ParseCalls(root);
            var logs = ParseLogs(root);

            return new JsonRunBundle(
                runId, startedUtc, finishedUtc, state, elapsedSeconds, error,
                plan, steps, false, bees.Count, bees.Count(b => b.IsFailed), bees, calls, logs);
        }
    }

    public RunSummaryReport GetSummary()
    {
        if (isSummaryOnlyFormat)
        {
            var ok = Math.Max(0, summaryOnlyTotalBees - summaryOnlyFailedBees);
            return new RunSummaryReport(
                runId, state, startedUtc, finishedUtc, elapsedSeconds, error,
                summaryOnlyTotalBees, ok, summaryOnlyFailedBees, 0, 0,
                null, null, null, null, null, []);
        }

        var okTimes = bees.Where(b => !b.IsFailed).Select(b => b.DurationMs).Order().ToArray();
        var totalBees = bees.Count;
        var failedBees = bees.Count(b => b.IsFailed);
        var okBees = totalBees - failedBees;
        var canceledBees = bees.Count(b => ExecutionResult.IsCanceledError(b.Error));
        var slowBees = bees.Count(b => b.IsSlow);

        var maxEntryFromBees = bees.Count > 0 ? bees.Max(b => b.Entry) + 1 : 0;
        var maxEntryFromSteps = steps?.Count ?? 0;
        var entryCount = Math.Max(plan.Scenarios.Count, Math.Max(maxEntryFromBees, maxEntryFromSteps));
        var entries = new List<RunSummaryEntryDto>(entryCount);

        for (var i = 0; i < entryCount; i++)
        {
            var entryBees = bees.Where(b => b.Entry == i).ToList();
            var entryOkTimes = entryBees.Where(b => !b.IsFailed).Select(b => b.DurationMs).Order().ToArray();
            var entryFailed = entryBees.Count(b => b.IsFailed);
            var entryOk = entryBees.Count - entryFailed;
            var entryCanceled = entryBees.Count(b => ExecutionResult.IsCanceledError(b.Error));
            var entrySlow = entryBees.Count(b => b.IsSlow);

            var declaredSteps = steps is not null && i < steps.Count ? steps[i] : [];
            var maxFailedStep = entryBees.Where(b => b.IsFailed && b.FailedStep >= 0).Select(b => b.FailedStep + 1).DefaultIfEmpty(0).Max();
            var stepCount = Math.Max(declaredSteps.Count, maxFailedStep);
            var stepDtos = new List<RunSummaryStepDto>(stepCount);
            int? finishedAllSteps = null;

            if (stepCount > 0)
            {
                var priorFailed = 0;
                for (var s = 0; s < stepCount; s++)
                {
                    var name = s < declaredSteps.Count ? declaredSteps[s].Name : $"Step {s + 1}";
                    var failedAtStep = entryBees.Count(b => b.IsFailed && b.FailedStep == s);
                    var reached = Math.Max(0, entryBees.Count - priorFailed);
                    priorFailed += failedAtStep;
                    stepDtos.Add(new RunSummaryStepDto(s + 1, name, reached, failedAtStep));
                }
                finishedAllSteps = Math.Min(entryOk, Math.Max(0, entryBees.Count - priorFailed));
            }

            var scenarioId = i < plan.Scenarios.Count && !string.IsNullOrWhiteSpace(plan.Scenarios[i].ScenarioId)
                ? plan.Scenarios[i].ScenarioId
                : $"entry-{i + 1}";
            var label = DuckDbBundleQueries.EntryLabel(plan, i);

            entries.Add(new RunSummaryEntryDto(
                i + 1,
                scenarioId,
                label,
                entryBees.Count,
                entryOk,
                entryFailed,
                entryCanceled,
                entrySlow,
                entryOkTimes.Length == 0 ? null : entryOkTimes[0],
                entryOkTimes.Length == 0 ? null : RunSummary.Percentile(entryOkTimes, 0.5),
                entryOkTimes.Length == 0 ? null : RunSummary.Percentile(entryOkTimes, 0.95),
                entryOkTimes.Length == 0 ? null : entryOkTimes[^1],
                entryOkTimes.Length == 0 ? null : entryOkTimes.Average(),
                stepDtos,
                finishedAllSteps));
        }

        return new RunSummaryReport(
            runId, state, startedUtc, finishedUtc, elapsedSeconds, error,
            totalBees, okBees, failedBees, canceledBees, slowBees,
            okTimes.Length == 0 ? null : okTimes[0],
            okTimes.Length == 0 ? null : RunSummary.Percentile(okTimes, 0.5),
            okTimes.Length == 0 ? null : RunSummary.Percentile(okTimes, 0.95),
            okTimes.Length == 0 ? null : okTimes[^1],
            okTimes.Length == 0 ? null : okTimes.Average(),
            entries);
    }

    public RunErrorsReport GetErrors()
    {
        var failed = bees.Where(b => b.IsFailed).ToList();
        if (failed.Count == 0)
            return new RunErrorsReport(runId, error, 0, 0, []);

        var grouped = failed
            .GroupBy(b => string.IsNullOrEmpty(b.Error) ? "(unknown error)" : b.Error!)
            .Select(g =>
            {
                var errText = g.Key;
                var entries = g.Where(b => b.Entry >= 0)
                    .GroupBy(b => b.Entry)
                    .OrderBy(e => e.Key)
                    .Select(e => new ErrorEntryAffectDto(e.Key + 1, DuckDbBundleQueries.EntryLabel(plan, e.Key), e.Count()))
                    .ToList();
                var affectedSteps = g.Where(b => b.Entry >= 0 && b.FailedStep >= 0)
                    .GroupBy(b => (b.Entry, b.FailedStep))
                    .OrderBy(s => s.Key.Entry).ThenBy(s => s.Key.FailedStep)
                    .Select(s => new ErrorStepAffectDto(
                        s.Key.Entry + 1,
                        s.Key.FailedStep + 1,
                        DuckDbBundleQueries.StepName(steps, s.Key.Entry, s.Key.FailedStep) ?? $"Step {s.Key.FailedStep + 1}",
                        s.Count()))
                    .ToList();
                var sampleBees = g.Select(b => b.Sequence + 1).Order().Take(5).ToList();
                var sampleNames = sampleBees.Select(n => $"B{n}").ToList();
                return new GroupedErrorDto(errText, g.Count(), ExecutionResult.IsCanceledError(errText), entries, affectedSteps, sampleBees, sampleNames);
            })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Error, StringComparer.Ordinal)
            .ToList();

        return new RunErrorsReport(runId, error, failed.Count, grouped.Count, grouped);
    }

    public RunCallsReport GetCalls(RunsCallsOptions options)
    {
        if (calls.Count == 0)
            return new RunCallsReport(runId, 0, 0, 0, []);

        var beeBySeq = bees.ToDictionary(b => b.Sequence);
        var facts = new List<CallFact>(calls.Count);
        for (var i = 0; i < calls.Count; i++)
        {
            var c = calls[i];
            var hasBee = beeBySeq.TryGetValue(c.Sequence, out var bee);
            facts.Add(new CallFact(
                i + 1,
                c.Sequence,
                hasBee ? bee!.Entry : 0,
                c.Step,
                c.Rep,
                hasBee ? bee!.StartedAt : 0.0,
                c.OffsetMs,
                c.DurationMs,
                c.StatusCode,
                c.Bytes,
                c.Error,
                c.Name,
                c.Url,
                c.Endpoint,
                c.Method,
                c.Host,
                c.SlowThresholdMs));
        }

        var result = CallAnalytics.BuildEndpoints(facts, options.Entry, options.SlowOnly, options.FailedOnly, options.MinMs, options.Search);
        var endpoints = result.Endpoints.Select(ep => new RunCallEndpointDto(
            ep.Endpoint,
            ep.Method,
            ep.Host,
            ep.TotalCount,
            ep.SlowCount,
            ep.FailedCount,
            ep.TypicalMs,
            ep.P95Ms,
            ep.MaxMs,
            ep.MinMs,
            ep.AvgMs,
            ep.SlowThresholdMs,
            ep.HasCustomThreshold,
            ep.Entries.Select(e => e + 1).ToArray())).ToList();

        return new RunCallsReport(runId, result.TotalCalls, result.TotalSlowCalls, result.TotalFailedCalls, endpoints);
    }

    public RunLogsReport QueryLogs(RunsLogsOptions options)
    {
        IEnumerable<JsonLog> query = logs;
        if (options.Entry is { } entry)
            query = query.Where(l => l.Entry == entry);
        if (options.Step is { } step)
            query = query.Where(l => l.Entry >= 0 && l.Step == step);
        if (options.Bee is { } beeSeq)
            query = query.Where(l => l.Sequence == beeSeq);
        if (options.Levels is { Count: > 0 } lvls && lvls.Count < 5)
            query = query.Where(l => lvls.Contains(l.Level, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(options.Search))
        {
            var needle = options.Search;
            query = query.Where(l =>
                l.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (l.Exception?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || l.BeeName.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var matched = query.OrderBy(l => l.Id).ToList();
        var items = matched.Skip(options.Offset).Take(options.Limit).Select(l =>
        {
            var (kind, entryLabel) = DuckDbBundleQueries.DescribeEntryKind(plan, l.Entry);
            var isScenario = l.Entry >= 0;
            var hasDeclaredSteps = isScenario && DuckDbBundleQueries.HasSteps(steps, l.Entry);
            return new RunLogLineDto(
                l.Id,
                l.Sequence >= 0 ? l.Sequence + 1 : null,
                l.BeeName,
                isScenario ? l.Entry + 1 : null,
                kind,
                entryLabel,
                isScenario && (hasDeclaredSteps || l.Step > 0) ? l.Step + 1 : null,
                isScenario ? DuckDbBundleQueries.StepName(steps, l.Entry, l.Step) : null,
                isScenario && (hasDeclaredSteps || l.Rep > 0) ? l.Rep + 1 : null,
                l.AtSeconds,
                l.Level,
                l.Message,
                l.Exception,
                l.CallIndex is { } ci ? ci + 1 : null,
                l.IsFailed,
                l.IsSlow);
        }).ToList();

        return new RunLogsReport(runId, matched.Count, options.Offset, options.Limit, options.Offset + items.Count < matched.Count, items);
    }

    public RunBeeReport GetBee(int beeNumber)
    {
        if (isSummaryOnlyFormat)
            throw new CouldNotRunException($"Bee B{beeNumber} (#{beeNumber}) was not found in run '{runId}' (this JSON export contains only run summary and logs).");

        var seq = beeNumber - 1;
        var bee = bees.FirstOrDefault(b => b.Sequence == seq)
            ?? throw new CouldNotRunException($"Bee B{beeNumber} (#{beeNumber}) was not found in run '{runId}'.");

        var hasSteps = DuckDbBundleQueries.HasSteps(steps, bee.Entry);
        var visitsDto = (bee.StepVisits ?? [])
            .Select(v => new BeeStepVisitDto(
                v.Step + 1,
                DuckDbBundleQueries.StepName(steps, bee.Entry, v.Step) ?? $"Step {v.Step + 1}",
                v.Rep + 1,
                v.Data))
            .ToList();

        var callsDto = calls
            .Where(c => c.Sequence == seq)
            .OrderBy(c => c.CallIndex)
            .Select(c => new BeeCallDto(
                c.CallIndex + 1,
                c.Name,
                string.IsNullOrWhiteSpace(c.Endpoint) ? EndpointNormalizer.NormalizeCallName(c.Name) : c.Endpoint,
                c.Method,
                c.Host,
                c.Url,
                hasSteps || c.Step > 0 ? c.Step + 1 : null,
                DuckDbBundleQueries.StepName(steps, bee.Entry, c.Step),
                hasSteps || c.Rep > 0 ? c.Rep + 1 : null,
                c.OffsetMs,
                c.DurationMs,
                c.StatusCode,
                c.Bytes,
                c.SlowThresholdMs,
                c.Error))
            .ToList();

        var logsDto = logs
            .Where(l => l.Sequence == seq)
            .OrderBy(l => l.Id)
            .Select(l => new BeeLogDto(
                l.Id,
                l.AtSeconds,
                l.Level,
                l.Message,
                l.Exception,
                hasSteps || l.Step > 0 ? l.Step + 1 : null,
                DuckDbBundleQueries.StepName(steps, bee.Entry, l.Step),
                hasSteps || l.Rep > 0 ? l.Rep + 1 : null,
                l.CallIndex is { } ci ? ci + 1 : null))
            .ToList();

        var isCanceled = ExecutionResult.IsCanceledError(bee.Error);
        var outcome = isCanceled ? "canceled" : bee.IsFailed ? "failed" : bee.IsSlow ? "slow" : "ok";

        return new RunBeeReport(
            runId,
            beeNumber,
            string.IsNullOrWhiteSpace(bee.BeeName) ? $"B{beeNumber}" : bee.BeeName,
            bee.Entry + 1,
            DuckDbBundleQueries.EntryLabel(plan, bee.Entry),
            bee.StartedAt,
            bee.DurationMs,
            bee.StatusCode,
            bee.Bytes,
            outcome,
            bee.IsFailed,
            isCanceled,
            bee.IsSlow,
            bee.SlowCalls,
            bee.Error,
            bee.FailedStep >= 0 ? bee.FailedStep + 1 : null,
            bee.FailedStep >= 0 ? DuckDbBundleQueries.StepName(steps, bee.Entry, bee.FailedStep) : null,
            bee.FailedStep >= 0 && bee.FailedRep >= 0 ? bee.FailedRep + 1 : null,
            visitsDto,
            callsDto,
            logsDto);
    }

    public void Dispose() { }

    private static List<JsonBee> ParseBees(JsonElement root)
    {
        var list = new List<JsonBee>();
        if (!TryGetProp(root, "bees", out var beesEl) || beesEl.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var b in beesEl.EnumerateArray())
        {
            var seq = GetInt(b, "sequence") ?? 0;
            var err = GetString(b, "error");
            var isFailed = GetBool(b, "isFailed") ?? (err is not null);
            var slowCalls = GetInt(b, "slowCalls") ?? 0;
            var isSlow = GetBool(b, "isSlow") ?? (slowCalls > 0);
            IReadOnlyList<RecordedStepVisit>? stepVisits = null;
            if (TryGetProp(b, "stepVisits", out var svEl) && svEl.ValueKind == JsonValueKind.Array)
                stepVisits = JsonSerializer.Deserialize<List<RecordedStepVisit>>(svEl.GetRawText(), JsonOpts);

            list.Add(new JsonBee(
                seq,
                GetString(b, "beeName") ?? $"B{seq + 1}",
                GetInt(b, "entry") ?? 0,
                GetDouble(b, "startedAt") ?? 0,
                GetDouble(b, "durationMs") ?? 0,
                GetInt(b, "statusCode"),
                GetLong(b, "bytes") ?? 0,
                err,
                isFailed,
                isSlow,
                slowCalls,
                GetInt(b, "failedStep") ?? -1,
                GetInt(b, "failedRep") ?? -1,
                stepVisits));
        }

        return list;
    }

    private static List<JsonCall> ParseCalls(JsonElement root)
    {
        var list = new List<JsonCall>();
        if (!TryGetProp(root, "calls", out var callsEl) || callsEl.ValueKind != JsonValueKind.Array)
            return list;

        var fallbackIdx = 0;
        foreach (var c in callsEl.EnumerateArray())
        {
            list.Add(new JsonCall(
                GetInt(c, "sequence") ?? 0,
                GetInt(c, "callIndex") ?? fallbackIdx,
                GetString(c, "name") ?? "",
                GetDouble(c, "offsetMs") ?? 0,
                GetDouble(c, "durationMs") ?? 0,
                GetInt(c, "statusCode"),
                GetLong(c, "bytes") ?? 0,
                GetString(c, "error"),
                GetInt(c, "step") ?? 0,
                GetInt(c, "rep") ?? 0,
                GetString(c, "endpoint"),
                GetString(c, "method"),
                GetString(c, "host"),
                GetString(c, "url"),
                GetDouble(c, "slowThresholdMs")));
            fallbackIdx++;
        }

        return list;
    }

    private static List<JsonLog> ParseLogs(JsonElement root)
    {
        var list = new List<JsonLog>();
        if (!TryGetProp(root, "logs", out var logsEl) || logsEl.ValueKind != JsonValueKind.Array)
            return list;

        long fallbackId = 1;
        foreach (var l in logsEl.EnumerateArray())
        {
            var seq = GetInt(l, "sequence") ?? 0;
            list.Add(new JsonLog(
                GetLong(l, "id") ?? fallbackId,
                seq,
                GetString(l, "beeName") ?? (seq >= 0 ? $"B{seq + 1}" : "Host"),
                GetInt(l, "entry") ?? 0,
                GetDouble(l, "atSeconds") ?? 0,
                GetString(l, "level") ?? "Information",
                GetString(l, "message") ?? "",
                GetString(l, "exception"),
                GetInt(l, "callIndex"),
                GetBool(l, "isFailed") ?? false,
                GetBool(l, "isSlow") ?? false,
                GetInt(l, "step") ?? 0,
                GetInt(l, "rep") ?? 0));
            fallbackId++;
        }

        return list;
    }

    private static bool TryGetProp(JsonElement el, string prop, out JsonElement value)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }
        if (el.TryGetProperty(prop, out value))
            return true;
        var pascal = prop.Length > 0 && char.IsLower(prop[0]) ? char.ToUpperInvariant(prop[0]) + prop[1..] : null;
        if (pascal is not null && el.TryGetProperty(pascal, out value))
            return true;
        value = default;
        return false;
    }

    private static string? GetString(JsonElement el, string prop) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement el, string prop) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var val) ? val : null;

    private static long? GetLong(JsonElement el, string prop) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var val) ? val : null;

    private static double? GetDouble(JsonElement el, string prop) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var val) ? val : null;

    private static bool? GetBool(JsonElement el, string prop) =>
        TryGetProp(el, prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static DateTimeOffset? ParseDate(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;
}
