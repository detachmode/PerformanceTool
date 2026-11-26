using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Services.Storage;

/// <summary>Bounded in-memory <see cref="IRunStorage"/> used when <c>Storage:Provider</c> is set to <c>"Memory"</c>.</summary>
public sealed class InMemoryRunStorage : IRunStorage
{
    private const int MaxRuns = 20;
    private const int MaxLogsPerRun = 50_000;

    private readonly Lock sync = new();
    private readonly List<RunBucket> runs = [];
    private long nextLogId;

    /// <inheritdoc />
    public string ProviderName => "Memory";

    private sealed class RunBucket(string runId, DateTimeOffset startedUtc, LoadPlan plan, IReadOnlyList<IReadOnlyList<StepInfo>>? steps)
    {
        public string RunId { get; } = runId;
        public DateTimeOffset StartedUtc { get; } = startedUtc;
        public LoadPlan Plan { get; } = plan;
        public IReadOnlyList<IReadOnlyList<StepInfo>>? Steps { get; } = steps;
        public DateTimeOffset? FinishedUtc { get; set; }
        public string State { get; set; } = "Running";
        public double ElapsedSeconds { get; set; }
        public string? Error { get; set; }
        public int FailedBees { get; set; }
        public string? RuntimeJson { get; set; }
        public StoredProgramRunDto? Preflight { get; set; }
        public StoredProgramRunDto? Shutdown { get; set; }
        public Dictionary<int, StoredBeeDetail> Bees { get; } = [];
        public List<StoredLogRow> Logs { get; } = [];
        public Dictionary<string, StoredTrace> Traces { get; } = new(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public void EnqueueRunStarted(string runId, DateTimeOffset startedUtc, LoadPlan plan, IReadOnlyList<IReadOnlyList<StepInfo>>? steps = null)
    {
        lock (sync)
        {
            runs.RemoveAll(r => r.RunId == runId);
            runs.Insert(0, new RunBucket(runId, startedUtc, plan.Clone(), steps));
            if (runs.Count > MaxRuns) runs.RemoveAt(runs.Count - 1);
        }
    }

    /// <inheritdoc />
    public void EnqueueBeeCompleted(string runId, ExecutionResult r, int beeOutcome, int slowCalls)
    {
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null) return;
            var isFailed = beeOutcome == 2;
            var isSlow = slowCalls > 0;
            if (isFailed) bucket.FailedBees++;
            var beeName = $"B{r.Sequence + 1}";
            bucket.Bees[r.Sequence] = new StoredBeeDetail(
                runId, r.Sequence, r.Entry, r.StartedAtSeconds, r.ElapsedMs, r.StatusCode, r.Bytes, r.Error,
                r.Calls ?? [], r.Logs ?? [], r.FailedStep, r.FailedRep, r.StepVisits, TraceId: r.TraceId);
            if (r.Logs is { Count: > 0 })
            {
                foreach (var l in r.Logs)
                {
                    if (bucket.Logs.Count >= MaxLogsPerRun) bucket.Logs.RemoveAt(0);
                    bucket.Logs.Add(new StoredLogRow(
                        Interlocked.Increment(ref nextLogId),
                        r.Sequence, beeName, r.Entry, l.AtSeconds, l.Level, l.Message, l.Exception, l.CallIndex,
                        beeOutcome, isFailed, isSlow, l.Step));
                }
            }
        }
    }

    /// <inheritdoc />
    public void EnqueueRunFinished(string runId, DateTimeOffset finishedUtc, string state, double elapsedSeconds, string? error)
    {
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null) return;
            bucket.FinishedUtc = finishedUtc;
            bucket.State = state;
            bucket.ElapsedSeconds = elapsedSeconds;
            bucket.Error = error;
        }
    }

    /// <inheritdoc />
    public Task SaveRuntimeDataAsync(string runId, string json, CancellationToken ct = default)
    {
        lock (sync)
            if (runs.FirstOrDefault(x => x.RunId == runId) is { } bucket) bucket.RuntimeJson = json;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetRuntimeDataAsync(string runId, CancellationToken ct = default)
    {
        lock (sync)
            return Task.FromResult(runs.FirstOrDefault(x => x.RunId == runId)?.RuntimeJson);
    }

    /// <inheritdoc />
    public Task SaveProgramRunAsync(string runId, string kind, StoredProgramRunDto program, CancellationToken ct = default)
    {
        var (normalized, entry, beeName) = ProgramLogHelper.ResolveKind(kind);
        var isFailed = string.Equals(program.State, "Failed", StringComparison.OrdinalIgnoreCase);
        var outcome = isFailed ? 2 : 0;
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null) return Task.CompletedTask;
            if (normalized == "preflight") bucket.Preflight = program;
            else bucket.Shutdown = program;

            bucket.Logs.RemoveAll(l => l.Entry == entry);
            var atSeconds = normalized == "shutdown" ? bucket.ElapsedSeconds : 0.0;
            foreach (var raw in program.Logs)
            {
                if (bucket.Logs.Count >= MaxLogsPerRun) bucket.Logs.RemoveAt(0);
                var (level, message) = ProgramLogHelper.ParseLine(raw);
                bucket.Logs.Add(new StoredLogRow(
                    Interlocked.Increment(ref nextLogId),
                    entry, beeName, entry, atSeconds, level, message, null, null,
                    outcome, isFailed, false, 0));
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void EnqueueHostLog(string runId, double atSeconds, string level, string message, string? exception = null)
    {
        var isFailed = string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase);
        var isSlow = string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase);
        var outcome = isFailed ? 2 : isSlow ? 1 : 0;
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null) return;
            if (bucket.Logs.Count >= MaxLogsPerRun) bucket.Logs.RemoveAt(0);
            bucket.Logs.Add(new StoredLogRow(
                Interlocked.Increment(ref nextLogId),
                ProgramLogEntry.Host, "Host", ProgramLogEntry.Host,
                atSeconds, level, message, exception, null,
                outcome, isFailed, isSlow, 0));
        }
    }

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredRunSummary>> ListRunsAsync(int limit = 50, CancellationToken ct = default)
    {
        lock (sync)
        {
            var result = runs.Take(Math.Clamp(limit, 1, 500)).Select(r => new StoredRunSummary(
                r.RunId, r.StartedUtc, r.FinishedUtc, r.State, r.ElapsedSeconds,
                r.Bees.Count, r.FailedBees, r.Logs.Count, r.Error)).ToList();
            return Task.FromResult<IReadOnlyList<StoredRunSummary>>(result);
        }
    }

    /// <inheritdoc />
    public Task<StoredRunData?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        lock (sync)
        {
            var b = runs.FirstOrDefault(x => x.RunId == runId);
            if (b is null) return Task.FromResult<StoredRunData?>(null);
            var bees = b.Bees.Values.OrderBy(a => a.Sequence).Select(a => new StoredBeeSummary(
                a.Sequence, a.Entry, a.StartedAt, a.DurationMs, a.StatusCode, a.Bytes, a.Error, 0, a.FailedStep, a.FailedRep)).ToList();
            return Task.FromResult<StoredRunData?>(new StoredRunData(b.RunId, b.StartedUtc, b.State, b.ElapsedSeconds, b.Plan.Clone(), bees, b.Logs.Count, b.Error, b.Steps, b.Preflight, b.Shutdown));
        }
    }

    /// <inheritdoc />
    public Task<StoredBeeDetail?> GetBeeDetailAsync(string runId, int sequence, CancellationToken ct = default)
    {
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null || !bucket.Bees.TryGetValue(sequence, out var d))
                return Task.FromResult<StoredBeeDetail?>(null);

            var entryLabel = d.Entry >= 0 && d.Entry < bucket.Plan.Scenarios.Count
                ? (string.IsNullOrWhiteSpace(bucket.Plan.Scenarios[d.Entry].Label) ? bucket.Plan.Scenarios[d.Entry].ScenarioId : bucket.Plan.Scenarios[d.Entry].Label)
                : null;
            var entrySteps = bucket.Steps is not null && d.Entry >= 0 && d.Entry < bucket.Steps.Count
                ? bucket.Steps[d.Entry]
                : null;
            return Task.FromResult<StoredBeeDetail?>(d with { EntryLabel = entryLabel, Steps = entrySteps });
        }
    }

    /// <inheritdoc />
    public Task<LogPageResult> QueryLogsAsync(string runId, LogQuery q, CancellationToken ct = default)
    {
        var limit = Math.Clamp(q.Limit, 1, 1000);
        var offset = Math.Max(0, q.Offset);
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null) return Task.FromResult(new LogPageResult(0, offset, limit, []));
            IEnumerable<StoredLogRow> seq = bucket.Logs.Where(l => Matches(l, q, entry: true, step: true, levels: true));
            var matched = seq.ToList();
            return Task.FromResult(new LogPageResult(matched.Count, offset, limit, matched.Skip(offset).Take(limit).ToList()));
        }
    }

    private static bool Matches(StoredLogRow l, LogQuery q, bool entry, bool step, bool levels)
    {
        if (entry && q.Entry is { } e && l.Entry != e) return false;
        if (step && q.Step is { } st && l.Step != st) return false;
        if (q.Bee is { } a && l.Sequence != a) return false;
        if (!string.IsNullOrWhiteSpace(q.BeeName) && !string.Equals(l.BeeName, q.BeeName.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (q.Outcome is { } o && l.BeeOutcome != o) return false;
        if (q.Failed is { } fl && l.IsFailed != fl) return false;
        if (q.Slow is { } sl && l.IsSlow != sl) return false;
        if (levels && q.Levels is { Count: > 0 } lv && !lv.Contains(l.Level, StringComparer.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var needle = q.Search.Trim();
            if (!(l.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (l.Exception?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || l.BeeName.Contains(needle, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    /// <inheritdoc />
    public Task<LogFacets> GetLogFacetsAsync(string runId, LogQuery q, CancellationToken ct = default)
    {
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null) return Task.FromResult(new LogFacets(0, new Dictionary<string, long>(), [], []));
            var total = 0L;
            var preflightCount = 0L;
            var shutdownCount = 0L;
            var hostCount = 0L;
            var levelCounts = new Dictionary<string, long>();
            var entryCounts = new List<long>();
            var stepCounts = new SortedDictionary<int, (long Count, long Errors)>();
            foreach (var l in bucket.Logs)
            {
                if (Matches(l, q, entry: true, step: true, levels: true)) total++;
                if (Matches(l, q, entry: true, step: true, levels: false)) levelCounts[l.Level] = levelCounts.GetValueOrDefault(l.Level) + 1;
                if (Matches(l, q, entry: false, step: false, levels: true))
                {
                    if (l.Entry == ProgramLogEntry.Preflight) preflightCount++;
                    else if (l.Entry == ProgramLogEntry.Shutdown) shutdownCount++;
                    else if (l.Entry == ProgramLogEntry.Host) hostCount++;
                    else if (l.Entry >= 0)
                    {
                        while (entryCounts.Count <= l.Entry) entryCounts.Add(0);
                        entryCounts[l.Entry]++;
                    }
                }
                if (q.Entry is >= 0 && Matches(l, q, entry: true, step: false, levels: false))
                {
                    var (c, err) = stepCounts.GetValueOrDefault(l.Step);
                    stepCounts[l.Step] = (c + 1, err + (string.Equals(l.Level, "Error", StringComparison.OrdinalIgnoreCase) ? 1 : 0));
                }
            }
            return Task.FromResult(new LogFacets(total, levelCounts, entryCounts, stepCounts.Select(kv => new LogStepCount(kv.Key, kv.Value.Count, kv.Value.Errors)).ToList(), preflightCount, shutdownCount, hostCount));
        }
    }

    /// <inheritdoc />
    public Task<CallEndpointsResult> GetCallEndpointsAsync(string runId, CallEndpointsQuery query, CancellationToken ct = default)
    {
        List<CallFact> facts;
        IReadOnlyList<IReadOnlyList<StepInfo>>? steps;
        LoadPlan plan;
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null) return Task.FromResult(new CallEndpointsResult([], 0, 0, 0));
            facts = ExtractCallFacts(bucket);
            steps = bucket.Steps;
            plan = bucket.Plan;
        }
        var scenarioEntries = string.IsNullOrWhiteSpace(query.Scenario) ? null : CallAnalytics.ResolveScenarioEntries(plan, query.Scenario);
        return Task.FromResult(CallAnalytics.BuildEndpoints(
            facts,
            query.Entry,
            query.SlowOnly,
            query.FailedOnly,
            query.MinMs,
            query.Search,
            query.Step,
            query.ByStep,
            steps is null ? null : (e, s) => e >= 0 && e < steps.Count && s >= 0 && s < steps[e].Count ? steps[e][s].Name : null,
            scenarioEntries,
            query.Endpoint));
    }

    /// <inheritdoc />
    public Task<SlowestCallsResult> GetSlowestCallsAsync(string runId, SlowestCallsQuery query, CancellationToken ct = default)
    {
        List<CallFact> facts;
        IReadOnlyList<IReadOnlyList<StepInfo>>? steps;
        LoadPlan plan;
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null) return Task.FromResult(new SlowestCallsResult([], 0));
            facts = ExtractCallFacts(bucket);
            steps = bucket.Steps;
            plan = bucket.Plan;
        }
        var scenarioEntries = string.IsNullOrWhiteSpace(query.Scenario) ? null : CallAnalytics.ResolveScenarioEntries(plan, query.Scenario);
        return Task.FromResult(CallAnalytics.BuildSlowestCalls(
            facts,
            query.Limit,
            query.Entry,
            scenarioEntries,
            query.Step,
            query.Endpoint,
            query.SlowOnly,
            query.FailedOnly,
            query.MinMs,
            query.Search,
            e => e >= 0 && e < plan.Scenarios.Count ? (string.IsNullOrWhiteSpace(plan.Scenarios[e].Label) ? plan.Scenarios[e].ScenarioId : plan.Scenarios[e].Label) : null,
            steps is null ? null : (e, s) => e >= 0 && e < steps.Count && s >= 0 && s < steps[e].Count ? steps[e][s].Name : null));
    }

    /// <inheritdoc />
    public Task<CallEndpointDetailDto?> GetCallEndpointDetailAsync(string runId, CallDetailQuery query, CancellationToken ct = default)
    {
        List<CallFact> facts;
        double elapsed;
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null) return Task.FromResult<CallEndpointDetailDto?>(null);
            elapsed = bucket.ElapsedSeconds;
            facts = ExtractCallFacts(bucket);
        }
        return Task.FromResult(CallAnalytics.BuildEndpointDetail(
            facts, query.Endpoint, elapsed, query.Entry, query.Show, query.MinMs, query.MaxMs,
            query.FromSec, query.ToSec, query.Sort, query.Offset, query.Limit, query.MaxPoints));
    }

    private static List<CallFact> ExtractCallFacts(RunBucket bucket)
    {
        var facts = new List<CallFact>();
        foreach (var bee in bucket.Bees.Values)
        {
            for (var i = 0; i < bee.Calls.Count; i++)
            {
                var c = bee.Calls[i];
                var id = ((long)bee.Sequence << 20) + i + 1;
                facts.Add(new CallFact(
                    id, bee.Sequence, bee.Entry, c.Step, c.Rep, bee.StartedAt,
                    c.OffsetMs, c.DurationMs, c.StatusCode, c.Bytes, c.Error,
                    c.Name, c.Url, c.Endpoint, c.Method, c.Host, c.SlowThresholdMs,
                    c.TtfbMs, c.ResponseReadMs, c.Headers, c.TraceId, c.SpanId));
            }
        }
        return facts;
    }

    /// <inheritdoc />
    public Task SaveTracesAsync(string runId, IReadOnlyList<StoredTrace> traces, CancellationToken ct = default)
    {
        lock (sync)
        {
            if (runs.FirstOrDefault(x => x.RunId == runId) is not { } bucket) return Task.CompletedTask;
            foreach (var trace in traces) bucket.Traces[trace.Fetch.TraceId] = trace;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TraceFetchDto>> GetTraceFetchesAsync(string runId, CancellationToken ct = default)
    {
        lock (sync)
        {
            IReadOnlyList<TraceFetchDto> result = runs.FirstOrDefault(x => x.RunId == runId) is { } bucket
                ? bucket.Traces.Values.Select(t => t.Fetch).OrderBy(f => f.Sequence).ThenBy(f => f.TraceId, StringComparer.Ordinal).ToList()
                : [];
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TraceSpanDto>> GetTraceSpansAsync(string runId, IReadOnlyCollection<string>? traceIds = null, CancellationToken ct = default)
    {
        lock (sync)
        {
            IReadOnlyList<TraceSpanDto> result = runs.FirstOrDefault(x => x.RunId == runId) is { } bucket
                ? bucket.Traces.Values
                    .Where(t => traceIds is null || traceIds.Contains(t.Fetch.TraceId))
                    .OrderBy(t => t.Fetch.TraceId, StringComparer.Ordinal)
                    .SelectMany(t => t.Spans.OrderBy(s => s.StartUnixNanos))
                    .ToList()
                : [];
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CallFact>> GetCallFactsAsync(string runId, CancellationToken ct = default)
    {
        lock (sync)
        {
            IReadOnlyList<CallFact> result = runs.FirstOrDefault(x => x.RunId == runId) is { } bucket ? ExtractCallFacts(bucket) : [];
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public async Task<RunExportResult?> ExportRunAsync(string runId, string format, CancellationToken ct = default)
    {
        var runs = await ListRunsAsync(500, ct);
        var summary = runs.FirstOrDefault(r => r.RunId == runId);
        if (summary is null) return null;
        var logs = await QueryLogsAsync(runId, new LogQuery(Offset: 0, Limit: 1000), ct);
        var safeRunId = new string(runId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        var payload = new { run = summary, logs = logs.Items };
        return new RunExportResult($"{safeRunId}.json", "application/json; charset=utf-8", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload));
    }

    /// <inheritdoc />
    public Task<StoredRunSummary> ImportRunAsync(string fileName, Stream content, CancellationToken ct = default) =>
        throw new NotSupportedException("Importing run files requires DuckDB storage.");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
