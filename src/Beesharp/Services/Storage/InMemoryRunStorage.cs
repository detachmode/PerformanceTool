using Beesharp.Services;

namespace Beesharp.Services.Storage;

/// <summary>Bounded in-memory <see cref="IRunStorage"/> used when <c>Storage:Provider</c> is set to <c>"Memory"</c>.</summary>
public sealed class InMemoryRunStorage : IRunStorage
{
    private const int MaxRuns = 20;
    private const int MaxLogsPerRun = 50_000;

    private readonly Lock sync = new();
    private readonly List<RunBucket> runs = [];
    private long nextLogId;

    public string ProviderName => "Memory";

    private sealed class RunBucket(string runId, DateTimeOffset startedUtc)
    {
        public string RunId { get; } = runId;
        public DateTimeOffset StartedUtc { get; } = startedUtc;
        public DateTimeOffset? FinishedUtc { get; set; }
        public string State { get; set; } = "Running";
        public double ElapsedSeconds { get; set; }
        public string? Error { get; set; }
        public int FailedBees { get; set; }
        public string? RuntimeJson { get; set; }
        public Dictionary<int, StoredBeeDetail> Bees { get; } = [];
        public List<StoredLogRow> Logs { get; } = [];
    }

    public void EnqueueRunStarted(string runId, DateTimeOffset startedUtc, LoadPlan plan)
    {
        lock (sync)
        {
            runs.RemoveAll(r => r.RunId == runId);
            runs.Insert(0, new RunBucket(runId, startedUtc));
            if (runs.Count > MaxRuns) runs.RemoveAt(runs.Count - 1);
        }
    }

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
                r.Calls ?? [], r.Logs ?? []);
            if (r.Logs is { Count: > 0 })
            {
                foreach (var l in r.Logs)
                {
                    if (bucket.Logs.Count >= MaxLogsPerRun) bucket.Logs.RemoveAt(0);
                    bucket.Logs.Add(new StoredLogRow(
                        Interlocked.Increment(ref nextLogId),
                        r.Sequence, beeName, r.Entry, l.AtSeconds, l.Level, l.Message, l.Exception, l.CallIndex,
                        beeOutcome, isFailed, isSlow));
                }
            }
        }
    }

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

    public Task SaveRuntimeDataAsync(string runId, string json, CancellationToken ct = default)
    {
        lock (sync)
            if (runs.FirstOrDefault(x => x.RunId == runId) is { } bucket) bucket.RuntimeJson = json;
        return Task.CompletedTask;
    }

    public Task<string?> GetRuntimeDataAsync(string runId, CancellationToken ct = default)
    {
        lock (sync)
            return Task.FromResult(runs.FirstOrDefault(x => x.RunId == runId)?.RuntimeJson);
    }

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

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

    public Task<StoredRunData?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        lock (sync)
        {
            var b = runs.FirstOrDefault(x => x.RunId == runId);
            if (b is null) return Task.FromResult<StoredRunData?>(null);
            var bees = b.Bees.Values.OrderBy(a => a.Sequence).Select(a => new StoredBeeSummary(
                a.Sequence, a.Entry, a.StartedAt, a.DurationMs, a.StatusCode, a.Bytes, a.Error, 0)).ToList();
            return Task.FromResult<StoredRunData?>(new StoredRunData(b.RunId, b.StartedUtc, b.State, b.ElapsedSeconds, new LoadPlan(), bees, b.Logs.Count, b.Error));
        }
    }

    public Task<StoredBeeDetail?> GetBeeDetailAsync(string runId, int sequence, CancellationToken ct = default)
    {
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            return Task.FromResult(bucket is not null && bucket.Bees.TryGetValue(sequence, out var d) ? d : null);
        }
    }

    public Task<LogPageResult> QueryLogsAsync(string runId, LogQuery q, CancellationToken ct = default)
    {
        var limit = Math.Clamp(q.Limit, 1, 1000);
        var offset = Math.Max(0, q.Offset);
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null) return Task.FromResult(new LogPageResult(0, offset, limit, []));
            IEnumerable<StoredLogRow> seq = bucket.Logs;
            if (q.Entry is { } e) seq = seq.Where(l => l.Entry == e);
            if (q.Bee is { } a) seq = seq.Where(l => l.Sequence == a);
            if (!string.IsNullOrWhiteSpace(q.BeeName)) seq = seq.Where(l => string.Equals(l.BeeName, q.BeeName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (q.Outcome is { } o) seq = seq.Where(l => l.BeeOutcome == o);
            if (q.Failed is { } fl) seq = seq.Where(l => l.IsFailed == fl);
            if (q.Slow is { } sl) seq = seq.Where(l => l.IsSlow == sl);
            if (q.Levels is { Count: > 0 } lv) seq = seq.Where(l => lv.Contains(l.Level, StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(q.Search))
            {
                var needle = q.Search.Trim();
                seq = seq.Where(l => l.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || (l.Exception?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                    || l.BeeName.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }
            var matched = seq.ToList();
            return Task.FromResult(new LogPageResult(matched.Count, offset, limit, matched.Skip(offset).Take(limit).ToList()));
        }
    }

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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
