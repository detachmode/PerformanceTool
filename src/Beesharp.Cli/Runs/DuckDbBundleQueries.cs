using System.Globalization;
using System.Text;
using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using DuckDB.NET.Data;

namespace Beesharp.Cli.Runs;

/// <summary>
/// Shared SQL query engine over DuckDB tables or lazy <c>read_parquet(...)</c> views.
/// Handles v1/v2/v3 schemas and missing optional tables gracefully.
/// </summary>
internal sealed class DuckDbBundleQueries
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly DuckDBConnection conn;
    private readonly HashSet<string> availableTables;
    private readonly Dictionary<string, HashSet<string>> columnCache = new(StringComparer.OrdinalIgnoreCase);

    public DuckDbBundleQueries(DuckDBConnection conn, HashSet<string> availableTables)
    {
        this.conn = conn;
        this.availableTables = availableTables;
    }

    private sealed record RunMetadata(
        string RunId,
        DateTimeOffset StartedUtc,
        DateTimeOffset? FinishedUtc,
        string State,
        double ElapsedSeconds,
        LoadPlan Plan,
        string? Error,
        IReadOnlyList<IReadOnlyList<StepInfo>>? Steps);

    private sealed record EntryAgg(
        int Entry,
        int TotalBees,
        int OkBees,
        int FailedBees,
        int CanceledBees,
        int SlowBees,
        double? MinMs,
        double? P50Ms,
        double? P95Ms,
        double? MaxMs,
        double? AvgMs);

    public RunSummaryReport GetSummary()
    {
        var meta = ReadRunMetadata();
        if (!availableTables.Contains("bees"))
        {
            var emptyEntries = BuildSummaryEntries(meta, new Dictionary<int, EntryAgg>(), new Dictionary<(int Entry, int Step), int>(), new Dictionary<(int Entry, int Step), CallStepMetrics>());
            return new RunSummaryReport(
                meta.RunId, meta.State, meta.StartedUtc, meta.FinishedUtc, meta.ElapsedSeconds, meta.Error,
                0, 0, 0, 0, 0, null, null, null, null, null, emptyEntries);
        }

        var cols = DescribeColumns("bees");
        var isFailedExpr = FailedPredicateExpr(cols);
        var isOkExpr = $"NOT ({isFailedExpr})";
        var isSlowExpr = SlowPredicateExpr(cols);

        int totalBees = 0, okBees = 0, failedBees = 0, canceledBees = 0, slowBees = 0;
        double? minMs = null, p50Ms = null, p95Ms = null, maxMs = null, avgMs = null;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT
                    COUNT(*),
                    COUNT(*) FILTER (WHERE {isOkExpr}),
                    COUNT(*) FILTER (WHERE {isFailedExpr}),
                    COUNT(*) FILTER (WHERE error = $2),
                    COUNT(*) FILTER (WHERE {isSlowExpr}),
                    MIN(duration_ms) FILTER (WHERE {isOkExpr}),
                    quantile_disc(duration_ms, 0.5) FILTER (WHERE {isOkExpr}),
                    quantile_disc(duration_ms, 0.95) FILTER (WHERE {isOkExpr}),
                    MAX(duration_ms) FILTER (WHERE {isOkExpr}),
                    AVG(duration_ms) FILTER (WHERE {isOkExpr})
                FROM bees
                WHERE run_id = $1;
                """;
            cmd.Parameters.Add(new DuckDBParameter(meta.RunId));
            cmd.Parameters.Add(new DuckDBParameter(ExecutionResult.CanceledError));
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                totalBees = Convert.ToInt32(r.GetValue(0));
                okBees = Convert.ToInt32(r.GetValue(1));
                failedBees = Convert.ToInt32(r.GetValue(2));
                canceledBees = Convert.ToInt32(r.GetValue(3));
                slowBees = Convert.ToInt32(r.GetValue(4));
                minMs = GetNullableDouble(r, 5);
                p50Ms = GetNullableDouble(r, 6);
                p95Ms = GetNullableDouble(r, 7);
                maxMs = GetNullableDouble(r, 8);
                avgMs = GetNullableDouble(r, 9);
            }
        }

        var byEntry = new Dictionary<int, EntryAgg>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT
                    entry,
                    COUNT(*),
                    COUNT(*) FILTER (WHERE {isOkExpr}),
                    COUNT(*) FILTER (WHERE {isFailedExpr}),
                    COUNT(*) FILTER (WHERE error = $2),
                    COUNT(*) FILTER (WHERE {isSlowExpr}),
                    MIN(duration_ms) FILTER (WHERE {isOkExpr}),
                    quantile_disc(duration_ms, 0.5) FILTER (WHERE {isOkExpr}),
                    quantile_disc(duration_ms, 0.95) FILTER (WHERE {isOkExpr}),
                    MAX(duration_ms) FILTER (WHERE {isOkExpr}),
                    AVG(duration_ms) FILTER (WHERE {isOkExpr})
                FROM bees
                WHERE run_id = $1
                GROUP BY entry
                ORDER BY entry;
                """;
            cmd.Parameters.Add(new DuckDBParameter(meta.RunId));
            cmd.Parameters.Add(new DuckDBParameter(ExecutionResult.CanceledError));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var entryIdx = r.GetInt32(0);
                byEntry[entryIdx] = new EntryAgg(
                    entryIdx,
                    Convert.ToInt32(r.GetValue(1)),
                    Convert.ToInt32(r.GetValue(2)),
                    Convert.ToInt32(r.GetValue(3)),
                    Convert.ToInt32(r.GetValue(4)),
                    Convert.ToInt32(r.GetValue(5)),
                    GetNullableDouble(r, 6),
                    GetNullableDouble(r, 7),
                    GetNullableDouble(r, 8),
                    GetNullableDouble(r, 9),
                    GetNullableDouble(r, 10));
            }
        }

        var failedPerStep = new Dictionary<(int Entry, int Step), int>();
        if (cols.Contains("failed_step"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT entry, COALESCE(failed_step, -1) AS fs, COUNT(*)
                FROM bees
                WHERE run_id = $1 AND ({isFailedExpr}) AND COALESCE(failed_step, -1) >= 0
                GROUP BY entry, fs
                ORDER BY entry, fs;
                """;
            cmd.Parameters.Add(new DuckDBParameter(meta.RunId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                failedPerStep[(r.GetInt32(0), r.GetInt32(1))] = Convert.ToInt32(r.GetValue(2));
        }

        var stepMetrics = availableTables.Contains("calls")
            ? CallAnalytics.BuildStepMetrics(LoadCallFacts(meta))
            : new Dictionary<(int Entry, int Step), CallStepMetrics>();

        var entries = BuildSummaryEntries(meta, byEntry, failedPerStep, stepMetrics);
        return new RunSummaryReport(
            meta.RunId, meta.State, meta.StartedUtc, meta.FinishedUtc, meta.ElapsedSeconds, meta.Error,
            totalBees, okBees, failedBees, canceledBees, slowBees,
            minMs, p50Ms, p95Ms, maxMs, avgMs, entries);
    }

    public RunErrorsReport GetErrors()
    {
        var meta = ReadRunMetadata();
        if (!availableTables.Contains("bees"))
            return new RunErrorsReport(meta.RunId, meta.Error, 0, 0, []);

        var cols = DescribeColumns("bees");
        var isFailedExpr = FailedPredicateExpr(cols);
        var failedStepExpr = cols.Contains("failed_step") ? "COALESCE(failed_step, -1)" : "-1";

        var groups = new Dictionary<string, (
            int Total,
            SortedDictionary<int, int> Entries,
            SortedDictionary<(int Entry, int Step), int> Steps,
            List<int> SampleBees)>(StringComparer.Ordinal);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COALESCE(error, '(unknown error)') AS err, entry, {failedStepExpr} AS fs, COUNT(*)
                FROM bees
                WHERE run_id = $1 AND ({isFailedExpr})
                GROUP BY err, entry, fs;
                """;
            cmd.Parameters.Add(new DuckDBParameter(meta.RunId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var err = r.GetString(0);
                var entry = r.GetInt32(1);
                var step = r.GetInt32(2);
                var count = Convert.ToInt32(r.GetValue(3));

                if (!groups.TryGetValue(err, out var state))
                    groups[err] = state = (0, new SortedDictionary<int, int>(), new SortedDictionary<(int, int), int>(), []);

                state.Total += count;
                if (entry >= 0)
                    state.Entries[entry] = state.Entries.GetValueOrDefault(entry) + count;
                if (entry >= 0 && step >= 0)
                    state.Steps[(entry, step)] = state.Steps.GetValueOrDefault((entry, step)) + count;
                groups[err] = state;
            }
        }

        if (groups.Count == 0)
            return new RunErrorsReport(meta.RunId, meta.Error, 0, 0, []);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT err, sequence
                FROM (
                    SELECT COALESCE(error, '(unknown error)') AS err,
                           sequence,
                           ROW_NUMBER() OVER (PARTITION BY COALESCE(error, '(unknown error)') ORDER BY sequence) AS rn
                    FROM bees
                    WHERE run_id = $1 AND ({isFailedExpr})
                )
                WHERE rn <= 5
                ORDER BY err, sequence;
                """;
            cmd.Parameters.Add(new DuckDBParameter(meta.RunId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var err = r.GetString(0);
                var seq = r.GetInt32(1);
                if (groups.TryGetValue(err, out var state))
                    state.SampleBees.Add(seq + 1);
            }
        }

        var list = groups
            .Select(kv =>
            {
                var err = kv.Key;
                var st = kv.Value;
                var entries = st.Entries
                    .Select(e => new ErrorEntryAffectDto(e.Key + 1, EntryLabel(meta.Plan, e.Key), e.Value))
                    .ToList();
                var steps = st.Steps
                    .Select(s => new ErrorStepAffectDto(
                        s.Key.Entry + 1,
                        s.Key.Step + 1,
                        StepName(meta.Steps, s.Key.Entry, s.Key.Step) ?? $"Step {s.Key.Step + 1}",
                        s.Value))
                    .ToList();
                var sampleNames = st.SampleBees.Select(n => $"B{n}").ToList();
                return new GroupedErrorDto(err, st.Total, ExecutionResult.IsCanceledError(err), entries, steps, st.SampleBees, sampleNames);
            })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Error, StringComparer.Ordinal)
            .ToList();

        var totalFailed = list.Sum(g => g.Count);
        return new RunErrorsReport(meta.RunId, meta.Error, totalFailed, list.Count, list);
    }

    public RunCallsReport GetCalls(RunsCallsOptions options)
    {
        var meta = ReadRunMetadata();
        if (!availableTables.Contains("calls"))
            return new RunCallsReport(meta.RunId, 0, 0, 0, []);

        var facts = LoadCallFacts(meta);
        var scenarioEntries = string.IsNullOrWhiteSpace(options.Scenario) ? null : CallAnalytics.ResolveScenarioEntries(meta.Plan, options.Scenario);
        var result = CallAnalytics.BuildEndpoints(
            facts,
            options.Entry,
            options.SlowOnly,
            options.FailedOnly,
            options.MinMs,
            options.Search,
            options.Step,
            options.ByStep,
            (e, s) => StepName(meta.Steps, e, s),
            scenarioEntries,
            options.Endpoint);

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
            ep.Entries.Select(e => e + 1).ToArray(),
            ep.Step is { } st ? st + 1 : null,
            ep.StepName)).ToList();

        return new RunCallsReport(meta.RunId, result.TotalCalls, result.TotalSlowCalls, result.TotalFailedCalls, endpoints);
    }

    public RunSlowestCallsReport GetSlowestCalls(RunsCallsOptions options)
    {
        var meta = ReadRunMetadata();
        var limit = options.Slowest ?? 20;
        if (!availableTables.Contains("calls"))
            return new RunSlowestCallsReport(meta.RunId, 0, limit, []);

        var facts = LoadCallFacts(meta);
        var scenarioEntries = string.IsNullOrWhiteSpace(options.Scenario) ? null : CallAnalytics.ResolveScenarioEntries(meta.Plan, options.Scenario);
        var res = CallAnalytics.BuildSlowestCalls(
            facts,
            limit,
            options.Entry,
            scenarioEntries,
            options.Step,
            options.Endpoint,
            options.SlowOnly,
            options.FailedOnly,
            options.MinMs,
            options.Search,
            e => EntryLabel(meta.Plan, e),
            (e, s) => StepName(meta.Steps, e, s));

        var items = res.Calls.Select(c =>
        {
            var hasSteps = HasSteps(meta.Steps, c.Entry);
            return new RunSlowestCallDto(
                c.Sequence + 1,
                c.BeeName,
                c.Entry + 1,
                c.EntryLabel ?? EntryLabel(meta.Plan, c.Entry),
                hasSteps || c.Step > 0 ? c.Step + 1 : null,
                c.StepName,
                hasSteps || c.Rep > 0 ? c.Rep + 1 : null,
                c.StartedAtSeconds,
                c.DurationMs,
                c.StatusCode,
                c.Bytes,
                c.Endpoint,
                c.Url,
                c.IsSlow,
                c.SlowThresholdMs,
                c.TtfbMs,
                c.ResponseReadMs,
                c.Headers,
                c.Error,
                c.TraceId,
                c.SpanId);
        }).ToList();

        return new RunSlowestCallsReport(meta.RunId, res.MatchingCalls, limit, items);
    }

    public RunTimelineReport GetTimeline(RunsTimelineOptions options)
    {
        var meta = ReadRunMetadata();
        var scenarioEntries = string.IsNullOrWhiteSpace(options.Scenario) ? null : CallAnalytics.ResolveScenarioEntries(meta.Plan, options.Scenario);

        bool EntryMatches(int e) =>
            (options.Entry is null || e == options.Entry.Value)
            && (scenarioEntries is null || scenarioEntries.Contains(e));

        var beesList = new List<(double StartedAt, double DurationMs, bool IsFailed, bool IsSlow)>();
        if (availableTables.Contains("bees"))
        {
            var cols = DescribeColumns("bees");
            var isFailedExpr = FailedPredicateExpr(cols);
            var isSlowExpr = SlowPredicateExpr(cols);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT entry, started_at, duration_ms, ({isFailedExpr}), ({isSlowExpr})
                FROM bees
                WHERE run_id = $1
                ORDER BY started_at;
                """;
            cmd.Parameters.Add(new DuckDBParameter(meta.RunId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var e = r.GetInt32(0);
                if (!EntryMatches(e)) continue;
                beesList.Add((r.GetDouble(1), r.GetDouble(2), r.GetBoolean(3), r.GetBoolean(4)));
            }
        }

        var callsList = availableTables.Contains("calls")
            ? LoadCallFacts(meta).Where(c => EntryMatches(c.Entry)).ToList()
            : [];

        return RunsTimelineBuilder.Build(meta.RunId, meta.ElapsedSeconds, options.BucketSeconds, beesList, callsList);
    }

    private List<CallFact> LoadCallFacts(RunMetadata meta)
    {
        var callCols = DescribeColumns("calls");
        var hasBees = availableTables.Contains("bees");

        var idExpr = callCols.Contains("id") ? "c.id" : "ROW_NUMBER() OVER ()";
        var stepExpr = callCols.Contains("step") ? "COALESCE(c.step, 0)" : "0";
        var repExpr = callCols.Contains("rep") ? "COALESCE(c.rep, 0)" : "0";
        var urlExpr = callCols.Contains("url") ? "c.url" : "NULL::VARCHAR";
        var epExpr = callCols.Contains("endpoint") ? "c.endpoint" : "NULL::VARCHAR";
        var methodExpr = callCols.Contains("method") ? "c.method" : "NULL::VARCHAR";
        var hostExpr = callCols.Contains("host") ? "c.host" : "NULL::VARCHAR";
        var thrExpr = callCols.Contains("slow_threshold_ms") ? "c.slow_threshold_ms" : "NULL::DOUBLE";
        var ttfbExpr = callCols.Contains("ttfb_ms") ? "c.ttfb_ms" : "NULL::DOUBLE";
        var readExpr = callCols.Contains("response_read_ms") ? "c.response_read_ms" : "NULL::DOUBLE";
        var hdrExpr = callCols.Contains("headers_json") ? "c.headers_json" : "NULL::VARCHAR";
        var traceExpr = callCols.Contains("trace_id") ? "c.trace_id" : "NULL::VARCHAR";
        var spanExpr = callCols.Contains("span_id") ? "c.span_id" : "NULL::VARCHAR";
        var entryExpr = hasBees ? "COALESCE(b.entry, 0)" : "0";
        var startedExpr = hasBees ? "COALESCE(b.started_at, 0.0)" : "0.0";
        var joinClause = hasBees ? "LEFT JOIN bees b ON c.run_id = b.run_id AND c.sequence = b.sequence" : "";
        var orderClause = callCols.Contains("id") ? "ORDER BY c.id" : "ORDER BY c.sequence, c.call_index";

        var facts = new List<CallFact>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {idExpr}, c.sequence, {entryExpr}, {stepExpr}, {repExpr}, {startedExpr},
                   c.offset_ms, c.duration_ms, c.status_code, c.bytes, c.error, c.name,
                   {urlExpr}, {epExpr}, {methodExpr}, {hostExpr}, {thrExpr},
                   {ttfbExpr}, {readExpr}, {hdrExpr}, {traceExpr}, {spanExpr}
            FROM calls c
            {joinClause}
            WHERE c.run_id = $1
            {orderClause};
            """;
        cmd.Parameters.Add(new DuckDBParameter(meta.RunId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            IReadOnlyDictionary<string, string>? headers = null;
            if (!r.IsDBNull(19) && r.GetString(19) is { Length: > 0 } hJson)
                headers = JsonSerializer.Deserialize<Dictionary<string, string>>(hJson, JsonOpts);
            facts.Add(new CallFact(
                Convert.ToInt64(r.GetValue(0)),
                r.GetInt32(1),
                r.GetInt32(2),
                r.GetInt32(3),
                r.GetInt32(4),
                r.GetDouble(5),
                r.GetDouble(6),
                r.GetDouble(7),
                r.IsDBNull(8) ? null : r.GetInt32(8),
                r.GetInt64(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.GetString(11),
                r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : r.GetString(13),
                r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetString(15),
                r.IsDBNull(16) ? null : r.GetDouble(16),
                r.IsDBNull(17) ? null : r.GetDouble(17),
                r.IsDBNull(18) ? null : r.GetDouble(18),
                headers,
                r.IsDBNull(20) ? null : r.GetString(20),
                r.IsDBNull(21) ? null : r.GetString(21)));
        }

        return facts;
    }

    public RunLogsReport QueryLogs(RunsLogsOptions options)
    {
        var meta = ReadRunMetadata();
        if (!availableTables.Contains("logs"))
            return new RunLogsReport(meta.RunId, 0, options.Offset, options.Limit, false, []);

        var cols = DescribeColumns("logs");
        var stepExpr = cols.Contains("step") ? "COALESCE(step, 0)" : "0";
        var repExpr = cols.Contains("rep") ? "COALESCE(rep, 0)" : "0";
        var callIdxExpr = cols.Contains("call_index") ? "call_index" : "NULL::INTEGER";
        var isFailedExpr = cols.Contains("is_failed") ? "is_failed" : "FALSE";
        var isSlowExpr = cols.Contains("is_slow") ? "is_slow" : "FALSE";

        var where = new StringBuilder("WHERE run_id = $1");
        var args = new List<object> { meta.RunId };
        int NextIdx() => args.Count + 1;

        if (options.Entry is { } entry)
        {
            where.Append($" AND entry = ${NextIdx()}");
            args.Add(entry);
        }
        if (options.Step is { } step)
        {
            where.Append($" AND entry >= 0 AND {stepExpr} = ${NextIdx()}");
            args.Add(step);
        }
        if (options.Bee is { } beeSeq)
        {
            where.Append($" AND sequence = ${NextIdx()}");
            args.Add(beeSeq);
        }
        if (options.Levels is { Count: > 0 } lvls && lvls.Count < 5)
        {
            var ph = new List<string>(lvls.Count);
            foreach (var lv in lvls)
            {
                ph.Add($"${NextIdx()}");
                args.Add(lv);
            }
            where.Append($" AND level IN ({string.Join(",", ph)})");
        }
        if (!string.IsNullOrWhiteSpace(options.Search))
        {
            var p = $"${NextIdx()}";
            where.Append($" AND (message ILIKE {p} OR COALESCE(exception, '') ILIKE {p} OR bee_name ILIKE {p})");
            args.Add($"%{options.Search}%");
        }

        long total;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM logs {where};";
            foreach (var v in args) countCmd.Parameters.Add(new DuckDBParameter(v));
            total = Convert.ToInt64(countCmd.ExecuteScalar());
        }

        var items = new List<RunLogLineDto>();
        if (total > 0 && options.Offset < total)
        {
            using var pageCmd = conn.CreateCommand();
            var limitIdx = args.Count + 1;
            var offsetIdx = args.Count + 2;
            pageCmd.CommandText = $"""
                SELECT id, sequence, bee_name, entry, at_seconds, level, message, exception,
                       {callIdxExpr}, {isFailedExpr}, {isSlowExpr}, {stepExpr}, {repExpr}
                FROM logs {where}
                ORDER BY id
                LIMIT ${limitIdx} OFFSET ${offsetIdx};
                """;
            foreach (var v in args) pageCmd.Parameters.Add(new DuckDBParameter(v));
            pageCmd.Parameters.Add(new DuckDBParameter(options.Limit));
            pageCmd.Parameters.Add(new DuckDBParameter(options.Offset));

            using var r = pageCmd.ExecuteReader();
            while (r.Read())
            {
                var seq = r.GetInt32(1);
                var entryIdx = r.GetInt32(3);
                var stepIdx = r.GetInt32(11);
                var repIdx = r.GetInt32(12);
                var (kind, entryLabel) = DescribeEntryKind(meta.Plan, entryIdx);
                var isScenario = entryIdx >= 0;
                var hasDeclaredSteps = isScenario && HasSteps(meta.Steps, entryIdx);
                items.Add(new RunLogLineDto(
                    r.GetInt64(0),
                    seq >= 0 ? seq + 1 : null,
                    r.GetString(2),
                    isScenario ? entryIdx + 1 : null,
                    kind,
                    entryLabel,
                    isScenario && (hasDeclaredSteps || stepIdx > 0) ? stepIdx + 1 : null,
                    isScenario ? StepName(meta.Steps, entryIdx, stepIdx) : null,
                    isScenario && (hasDeclaredSteps || repIdx > 0) ? repIdx + 1 : null,
                    r.GetDouble(4),
                    r.GetString(5),
                    r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetInt32(8) + 1,
                    r.GetBoolean(9),
                    r.GetBoolean(10)));
            }
        }

        return new RunLogsReport(meta.RunId, total, options.Offset, options.Limit, options.Offset + items.Count < total, items);
    }

    public RunBeeReport GetBee(int beeNumber)
    {
        var meta = ReadRunMetadata();
        if (!availableTables.Contains("bees"))
            throw new CouldNotRunException($"Bee B{beeNumber} (#{beeNumber}) was not found in run '{meta.RunId}' (no bees table).");

        var seq = beeNumber - 1;
        var beeCols = DescribeColumns("bees");
        var failedStepExpr = beeCols.Contains("failed_step") ? "COALESCE(failed_step, -1)" : "-1";
        var failedRepExpr = beeCols.Contains("failed_rep") ? "COALESCE(failed_rep, -1)" : "-1";
        var visitsExpr = beeCols.Contains("step_visits_json") ? "step_visits_json" : "NULL::VARCHAR";
        var isFailedExpr = FailedPredicateExpr(beeCols);
        var isSlowExpr = SlowPredicateExpr(beeCols);
        var slowCallsExpr = beeCols.Contains("slow_calls") ? "COALESCE(slow_calls, 0)" : "0";
        var beeTraceExpr = beeCols.Contains("trace_id") ? "trace_id" : "NULL::VARCHAR";

        int entry, slowCalls, failedStep, failedRep;
        double startedAt, durationMs;
        int? statusCode;
        long bytes;
        string? error;
        string? beeTraceId;
        bool isFailed, isSlow;
        List<RecordedStepVisit>? stepVisits = null;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT entry, started_at, duration_ms, status_code, bytes, error,
                       ({isFailedExpr}), ({isSlowExpr}), {slowCallsExpr},
                       {failedStepExpr}, {failedRepExpr}, {visitsExpr}, {beeTraceExpr}
                FROM bees
                WHERE run_id = $1 AND sequence = $2
                LIMIT 1;
                """;
            cmd.Parameters.Add(new DuckDBParameter(meta.RunId));
            cmd.Parameters.Add(new DuckDBParameter(seq));
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                throw new CouldNotRunException($"Bee B{beeNumber} (#{beeNumber}) was not found in run '{meta.RunId}'.");

            entry = r.GetInt32(0);
            startedAt = r.GetDouble(1);
            durationMs = r.GetDouble(2);
            statusCode = r.IsDBNull(3) ? null : r.GetInt32(3);
            bytes = r.GetInt64(4);
            error = r.IsDBNull(5) ? null : r.GetString(5);
            isFailed = r.GetBoolean(6);
            isSlow = r.GetBoolean(7);
            slowCalls = r.GetInt32(8);
            failedStep = r.GetInt32(9);
            failedRep = r.GetInt32(10);
            if (!r.IsDBNull(11) && r.GetString(11) is { Length: > 0 } svJson)
                stepVisits = JsonSerializer.Deserialize<List<RecordedStepVisit>>(svJson, JsonOpts);
            beeTraceId = r.IsDBNull(12) ? null : r.GetString(12);
        }

        var hasSteps = HasSteps(meta.Steps, entry);
        var visitsDto = (stepVisits ?? [])
            .Select(v => new BeeStepVisitDto(
                v.Step + 1,
                StepName(meta.Steps, entry, v.Step) ?? $"Step {v.Step + 1}",
                v.Rep + 1,
                v.Data))
            .ToList();

        var callsDto = new List<BeeCallDto>();
        if (availableTables.Contains("calls"))
        {
            var callCols = DescribeColumns("calls");
            var stepExpr = callCols.Contains("step") ? "COALESCE(step, 0)" : "0";
            var repExpr = callCols.Contains("rep") ? "COALESCE(rep, 0)" : "0";
            var epExpr = callCols.Contains("endpoint") ? "endpoint" : "NULL::VARCHAR";
            var methodExpr = callCols.Contains("method") ? "method" : "NULL::VARCHAR";
            var hostExpr = callCols.Contains("host") ? "host" : "NULL::VARCHAR";
            var urlExpr = callCols.Contains("url") ? "url" : "NULL::VARCHAR";
            var thrExpr = callCols.Contains("slow_threshold_ms") ? "slow_threshold_ms" : "NULL::DOUBLE";
            var ttfbExpr = callCols.Contains("ttfb_ms") ? "ttfb_ms" : "NULL::DOUBLE";
            var readExpr = callCols.Contains("response_read_ms") ? "response_read_ms" : "NULL::DOUBLE";
            var hdrExpr = callCols.Contains("headers_json") ? "headers_json" : "NULL::VARCHAR";
            var callTraceExpr = callCols.Contains("trace_id") ? "trace_id" : "NULL::VARCHAR";
            var callSpanExpr = callCols.Contains("span_id") ? "span_id" : "NULL::VARCHAR";
            var idxExpr = callCols.Contains("call_index") ? "call_index" : "ROW_NUMBER() OVER () - 1";
            var orderCol = callCols.Contains("call_index") ? "call_index" : "offset_ms";

            using var cCmd = conn.CreateCommand();
            cCmd.CommandText = $"""
                SELECT {idxExpr}, name, offset_ms, duration_ms, status_code, bytes, error,
                       {stepExpr}, {repExpr}, {epExpr}, {methodExpr}, {hostExpr}, {urlExpr}, {thrExpr},
                       {ttfbExpr}, {readExpr}, {hdrExpr}, {callTraceExpr}, {callSpanExpr}
                FROM calls
                WHERE run_id = $1 AND sequence = $2
                ORDER BY {orderCol};
                """;
            cCmd.Parameters.Add(new DuckDBParameter(meta.RunId));
            cCmd.Parameters.Add(new DuckDBParameter(seq));
            using var cr = cCmd.ExecuteReader();
            while (cr.Read())
            {
                var callStep = cr.GetInt32(7);
                var callRep = cr.GetInt32(8);
                var rawName = cr.GetString(1);
                var ep = cr.IsDBNull(9) ? null : cr.GetString(9);
                IReadOnlyDictionary<string, string>? headers = null;
                if (!cr.IsDBNull(16) && cr.GetString(16) is { Length: > 0 } hJson)
                    headers = JsonSerializer.Deserialize<Dictionary<string, string>>(hJson, JsonOpts);
                callsDto.Add(new BeeCallDto(
                    Convert.ToInt32(cr.GetValue(0)) + 1,
                    rawName,
                    string.IsNullOrWhiteSpace(ep) ? EndpointNormalizer.NormalizeCallName(rawName) : ep,
                    cr.IsDBNull(10) ? null : cr.GetString(10),
                    cr.IsDBNull(11) ? null : cr.GetString(11),
                    cr.IsDBNull(12) ? null : cr.GetString(12),
                    hasSteps || callStep > 0 ? callStep + 1 : null,
                    StepName(meta.Steps, entry, callStep),
                    hasSteps || callRep > 0 ? callRep + 1 : null,
                    cr.GetDouble(2),
                    cr.GetDouble(3),
                    cr.IsDBNull(4) ? null : cr.GetInt32(4),
                    cr.GetInt64(5),
                    cr.IsDBNull(13) ? null : cr.GetDouble(13),
                    cr.IsDBNull(6) ? null : cr.GetString(6),
                    cr.IsDBNull(14) ? null : cr.GetDouble(14),
                    cr.IsDBNull(15) ? null : cr.GetDouble(15),
                    headers,
                    cr.IsDBNull(17) ? null : cr.GetString(17),
                    cr.IsDBNull(18) ? null : cr.GetString(18)));
            }
        }

        var logsDto = new List<BeeLogDto>();
        if (availableTables.Contains("logs"))
        {
            var logCols = DescribeColumns("logs");
            var stepExpr = logCols.Contains("step") ? "COALESCE(step, 0)" : "0";
            var repExpr = logCols.Contains("rep") ? "COALESCE(rep, 0)" : "0";
            var callIdxExpr = logCols.Contains("call_index") ? "call_index" : "NULL::INTEGER";

            using var lCmd = conn.CreateCommand();
            lCmd.CommandText = $"""
                SELECT id, at_seconds, level, message, exception, {callIdxExpr}, {stepExpr}, {repExpr}
                FROM logs
                WHERE run_id = $1 AND sequence = $2
                ORDER BY id;
                """;
            lCmd.Parameters.Add(new DuckDBParameter(meta.RunId));
            lCmd.Parameters.Add(new DuckDBParameter(seq));
            using var lr = lCmd.ExecuteReader();
            while (lr.Read())
            {
                var logStep = lr.GetInt32(6);
                var logRep = lr.GetInt32(7);
                logsDto.Add(new BeeLogDto(
                    lr.GetInt64(0),
                    lr.GetDouble(1),
                    lr.GetString(2),
                    lr.GetString(3),
                    lr.IsDBNull(4) ? null : lr.GetString(4),
                    hasSteps || logStep > 0 ? logStep + 1 : null,
                    StepName(meta.Steps, entry, logStep),
                    hasSteps || logRep > 0 ? logRep + 1 : null,
                    lr.IsDBNull(5) ? null : lr.GetInt32(5) + 1));
            }
        }

        var isCanceled = ExecutionResult.IsCanceledError(error);
        var outcome = isCanceled ? "canceled" : isFailed ? "failed" : isSlow ? "slow" : "ok";

        return new RunBeeReport(
            meta.RunId,
            beeNumber,
            $"B{beeNumber}",
            entry + 1,
            EntryLabel(meta.Plan, entry),
            startedAt,
            durationMs,
            statusCode,
            bytes,
            outcome,
            isFailed,
            isCanceled,
            isSlow,
            slowCalls,
            error,
            failedStep >= 0 ? failedStep + 1 : null,
            failedStep >= 0 ? StepName(meta.Steps, entry, failedStep) : null,
            failedStep >= 0 && failedRep >= 0 ? failedRep + 1 : null,
            visitsDto,
            callsDto,
            logsDto,
            beeTraceId ?? callsDto.Select(c => c.TraceId).FirstOrDefault(t => t is not null));
    }

    public RunTraceReport GetTrace(RunsTraceOptions options)
    {
        var meta = ReadRunMetadata();
        var fetches = LoadTraceFetches(meta.RunId);
        return TraceReportBuilder.Build(
            meta.RunId, options, fetches,
            traceId => SequenceOfTraceId(meta.RunId, traceId),
            sequence => GetBee(sequence + 1),
            traceId => LoadTraceSpans(meta.RunId, [traceId]));
    }

    public HotspotsResultDto GetHotspots(RunsHotspotsOptions options)
    {
        var meta = ReadRunMetadata();
        var query = new HotspotsQuery(options.Endpoint, options.SlowOnly, options.Limit);
        var fetches = LoadTraceFetches(meta.RunId);
        if (fetches.Count == 0) return TraceAnalytics.BuildHotspots(meta.RunId, [], [], fetches, query);
        var facts = availableTables.Contains("calls") ? LoadCallFacts(meta) : [];
        return TraceAnalytics.BuildHotspots(meta.RunId, facts, LoadTraceSpans(meta.RunId, null), fetches, query);
    }

    private int? SequenceOfTraceId(string runId, string traceId)
    {
        foreach (var (table, column) in new[] { ("calls", "trace_id"), ("bees", "trace_id") })
        {
            if (!availableTables.Contains(table) || !DescribeColumns(table).Contains(column)) continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT sequence FROM {table} WHERE run_id = $1 AND {column} = $2 LIMIT 1;";
            cmd.Parameters.Add(new DuckDBParameter(runId));
            cmd.Parameters.Add(new DuckDBParameter(traceId));
            if (cmd.ExecuteScalar() is { } value and not DBNull) return Convert.ToInt32(value);
        }
        return null;
    }

    private List<TraceFetchDto> LoadTraceFetches(string runId)
    {
        var list = new List<TraceFetchDto>();
        if (!availableTables.Contains("trace_fetches")) return list;
        var cols = DescribeColumns("trace_fetches");
        var truncatedExpr = cols.Contains("truncated") ? "COALESCE(truncated, FALSE)" : "FALSE";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT trace_id, sequence, status, reason, span_count, attempts, selected_for, fetched_utc, {truncatedExpr}
            FROM trace_fetches
            WHERE run_id = $1
            ORDER BY sequence, trace_id;
            """;
        cmd.Parameters.Add(new DuckDBParameter(runId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            IReadOnlyList<string> selected = !r.IsDBNull(6) && r.GetString(6) is { Length: > 0 } sj
                ? JsonSerializer.Deserialize<List<string>>(sj, JsonOpts) ?? []
                : [];
            list.Add(new TraceFetchDto(
                r.GetString(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4), r.GetInt32(5), selected,
                r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.GetBoolean(8)));
        }
        return list;
    }

    private List<TraceSpanDto> LoadTraceSpans(string runId, IReadOnlyCollection<string>? traceIds)
    {
        var spans = new List<TraceSpanDto>();
        if (!availableTables.Contains("trace_spans")) return spans;
        using var cmd = conn.CreateCommand();
        cmd.Parameters.Add(new DuckDBParameter(runId));
        var filter = "";
        if (traceIds is not null)
        {
            filter = " AND trace_id IN (" + string.Join(", ", traceIds.Select((_, i) => "$" + (i + 2))) + ")";
            foreach (var id in traceIds) cmd.Parameters.Add(new DuckDBParameter(id));
        }
        cmd.CommandText = $"""
            SELECT trace_id, span_id, parent_span_id, service, name, kind, start_unix_nano, duration_ms, COALESCE(is_error, FALSE), status_message, attributes_json
            FROM trace_spans
            WHERE run_id = $1{filter}
            ORDER BY trace_id, start_unix_nano, span_id;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            IReadOnlyDictionary<string, string>? attrs = !r.IsDBNull(10) && r.GetString(10) is { Length: > 0 } aj
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(aj, JsonOpts)
                : null;
            spans.Add(new TraceSpanDto(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6), r.GetDouble(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetString(9), attrs));
        }
        return spans;
    }

    private RunMetadata ReadRunMetadata()
    {
        string runId, state, planJson;
        DateTimeOffset startedUtc;
        DateTimeOffset? finishedUtc;
        double elapsedSeconds;
        string? error;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT run_id, started_utc, finished_utc, state, elapsed_seconds, plan_json, error FROM runs ORDER BY started_utc DESC LIMIT 1;";
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                throw new CouldNotRunException("Run bundle contains no run metadata.");

            runId = r.GetString(0);
            startedUtc = DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            finishedUtc = r.IsDBNull(2) ? null : DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            state = r.GetString(3);
            elapsedSeconds = r.GetDouble(4);
            planJson = r.IsDBNull(5) ? "{}" : r.GetString(5);
            error = r.IsDBNull(6) ? null : r.GetString(6);
        }

        var plan = string.IsNullOrWhiteSpace(planJson)
            ? new LoadPlan()
            : (JsonSerializer.Deserialize<LoadPlan>(planJson, JsonOpts) ?? new LoadPlan());

        IReadOnlyList<IReadOnlyList<StepInfo>>? steps = null;
        if (availableTables.Contains("run_steps"))
        {
            using var sCmd = conn.CreateCommand();
            sCmd.CommandText = "SELECT json FROM run_steps WHERE run_id = $1 LIMIT 1;";
            sCmd.Parameters.Add(new DuckDBParameter(runId));
            if (sCmd.ExecuteScalar() is string stepsJson && !string.IsNullOrWhiteSpace(stepsJson))
                steps = JsonSerializer.Deserialize<List<List<StepInfo>>>(stepsJson, JsonOpts);
        }

        return new RunMetadata(runId, startedUtc, finishedUtc, state, elapsedSeconds, plan, error, steps);
    }

    private HashSet<string> DescribeColumns(string table)
    {
        if (columnCache.TryGetValue(table, out var cached))
            return cached;

        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DESCRIBE {table};";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            cols.Add(r.GetString(0));
        columnCache[table] = cols;
        return cols;
    }

    private static string FailedPredicateExpr(HashSet<string> cols) =>
        cols.Contains("is_failed")
            ? "COALESCE(is_failed, FALSE) OR error IS NOT NULL"
            : "error IS NOT NULL";

    private static string SlowPredicateExpr(HashSet<string> cols) =>
        (cols.Contains("is_slow"), cols.Contains("slow_calls")) switch
        {
            (true, true) => "COALESCE(is_slow, FALSE) OR COALESCE(slow_calls, 0) > 0",
            (true, false) => "COALESCE(is_slow, FALSE)",
            (false, true) => "COALESCE(slow_calls, 0) > 0",
            (false, false) => "FALSE",
        };

    private static double? GetNullableDouble(System.Data.Common.DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static List<RunSummaryEntryDto> BuildSummaryEntries(
        RunMetadata meta,
        Dictionary<int, EntryAgg> byEntry,
        Dictionary<(int Entry, int Step), int> failedPerStep,
        Dictionary<(int Entry, int Step), CallStepMetrics> stepMetrics)
    {
        var maxEntryFromBees = byEntry.Count > 0 ? byEntry.Keys.Max() + 1 : 0;
        var maxEntryFromSteps = meta.Steps?.Count ?? 0;
        var count = Math.Max(meta.Plan.Scenarios.Count, Math.Max(maxEntryFromBees, maxEntryFromSteps));

        var entries = new List<RunSummaryEntryDto>(count);
        for (var i = 0; i < count; i++)
        {
            var agg = byEntry.TryGetValue(i, out var found)
                ? found
                : new EntryAgg(i, 0, 0, 0, 0, 0, null, null, null, null, null);

            var scenarioId = i < meta.Plan.Scenarios.Count && !string.IsNullOrWhiteSpace(meta.Plan.Scenarios[i].ScenarioId)
                ? meta.Plan.Scenarios[i].ScenarioId
                : $"entry-{i + 1}";
            var label = EntryLabel(meta.Plan, i);

            var declaredSteps = meta.Steps is not null && i < meta.Steps.Count ? meta.Steps[i] : [];
            var maxFailedStep = failedPerStep.Keys.Where(k => k.Entry == i).Select(k => k.Step + 1).DefaultIfEmpty(0).Max();
            var maxCallStep = declaredSteps.Count > 0 || maxFailedStep > 0
                ? stepMetrics.Keys.Where(k => k.Entry == i).Select(k => k.Step + 1).DefaultIfEmpty(0).Max()
                : 0;
            var stepCount = Math.Max(declaredSteps.Count, Math.Max(maxFailedStep, maxCallStep));

            var stepDtos = new List<RunSummaryStepDto>(stepCount);
            int? finishedAllSteps = null;
            if (stepCount > 0)
            {
                var priorFailed = 0;
                for (var s = 0; s < stepCount; s++)
                {
                    var name = s < declaredSteps.Count ? declaredSteps[s].Name : $"Step {s + 1}";
                    var failedAtStep = failedPerStep.GetValueOrDefault((i, s));
                    var reached = Math.Max(0, agg.TotalBees - priorFailed);
                    priorFailed += failedAtStep;
                    stepMetrics.TryGetValue((i, s), out var m);
                    stepDtos.Add(new RunSummaryStepDto(
                        s + 1,
                        name,
                        reached,
                        failedAtStep,
                        m?.TotalCalls ?? 0,
                        m?.SlowCalls ?? 0,
                        m?.MinMs,
                        m?.P50Ms,
                        m?.P95Ms,
                        m?.MaxMs,
                        m?.AvgMs));
                }
                finishedAllSteps = Math.Min(agg.OkBees, Math.Max(0, agg.TotalBees - priorFailed));
            }

            entries.Add(new RunSummaryEntryDto(
                i + 1,
                scenarioId,
                label,
                agg.TotalBees,
                agg.OkBees,
                agg.FailedBees,
                agg.CanceledBees,
                agg.SlowBees,
                agg.MinMs,
                agg.P50Ms,
                agg.P95Ms,
                agg.MaxMs,
                agg.AvgMs,
                stepDtos,
                finishedAllSteps));
        }

        return entries;
    }

    internal static string EntryLabel(LoadPlan plan, int entry)
    {
        if (entry == ProgramLogEntry.Preflight) return "Preflight";
        if (entry == ProgramLogEntry.Shutdown) return "Shutdown";
        if (entry == ProgramLogEntry.Host) return "Host";
        if (entry < 0 || entry >= plan.Scenarios.Count) return $"Entry {entry + 1}";
        var cfg = plan.Scenarios[entry];
        return string.IsNullOrWhiteSpace(cfg.Label) ? cfg.ScenarioId : cfg.Label;
    }

    internal static (string Kind, string Label) DescribeEntryKind(LoadPlan plan, int entry) => entry switch
    {
        ProgramLogEntry.Preflight => ("preflight", "Preflight"),
        ProgramLogEntry.Shutdown => ("shutdown", "Shutdown"),
        ProgramLogEntry.Host => ("host", "Host"),
        _ => ("scenario", EntryLabel(plan, entry)),
    };

    internal static bool HasSteps(IReadOnlyList<IReadOnlyList<StepInfo>>? steps, int entry) =>
        steps is not null && entry >= 0 && entry < steps.Count && steps[entry].Count > 0;

    internal static string? StepName(IReadOnlyList<IReadOnlyList<StepInfo>>? steps, int entry, int step) =>
        steps is not null && entry >= 0 && entry < steps.Count && step >= 0 && step < steps[entry].Count
            ? steps[entry][step].Name
            : null;
}
