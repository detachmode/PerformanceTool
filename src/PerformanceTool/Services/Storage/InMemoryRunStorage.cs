using PerformanceTool.Services;

namespace PerformanceTool.Services.Storage;

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
        public int FailedActors { get; set; }
        public Dictionary<int, StoredActorDetail> Actors { get; } = [];
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

    public void EnqueueActorCompleted(string runId, ExecutionResult r, int actorOutcome, int slowCalls)
    {
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            if (bucket is null) return;
            var isFailed = actorOutcome == 2;
            var isSlow = slowCalls > 0;
            if (isFailed) bucket.FailedActors++;
            var actorName = $"Actor #{r.Sequence + 1}";
            bucket.Actors[r.Sequence] = new StoredActorDetail(
                runId, r.Sequence, r.Entry, r.StartedAtSeconds, r.ElapsedMs, r.StatusCode, r.Bytes, r.Error,
                r.Calls ?? [], r.Logs ?? []);
            if (r.Logs is { Count: > 0 })
            {
                foreach (var l in r.Logs)
                {
                    if (bucket.Logs.Count >= MaxLogsPerRun) bucket.Logs.RemoveAt(0);
                    bucket.Logs.Add(new StoredLogRow(
                        Interlocked.Increment(ref nextLogId),
                        r.Sequence, actorName, r.Entry, l.AtSeconds, l.Level, l.Message, l.Exception, l.CallIndex,
                        actorOutcome, isFailed, isSlow));
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

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<StoredRunSummary>> ListRunsAsync(int limit = 50, CancellationToken ct = default)
    {
        lock (sync)
        {
            var result = runs.Take(Math.Clamp(limit, 1, 500)).Select(r => new StoredRunSummary(
                r.RunId, r.StartedUtc, r.FinishedUtc, r.State, r.ElapsedSeconds,
                r.Actors.Count, r.FailedActors, r.Logs.Count, r.Error)).ToList();
            return Task.FromResult<IReadOnlyList<StoredRunSummary>>(result);
        }
    }

    public Task<StoredActorDetail?> GetActorDetailAsync(string runId, int sequence, CancellationToken ct = default)
    {
        lock (sync)
        {
            var bucket = runs.FirstOrDefault(x => x.RunId == runId);
            return Task.FromResult(bucket is not null && bucket.Actors.TryGetValue(sequence, out var d) ? d : null);
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
            if (q.Actor is { } a) seq = seq.Where(l => l.Sequence == a);
            if (!string.IsNullOrWhiteSpace(q.ActorName)) seq = seq.Where(l => string.Equals(l.ActorName, q.ActorName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (q.Outcome is { } o) seq = seq.Where(l => l.ActorOutcome == o);
            if (q.Failed is { } fl) seq = seq.Where(l => l.IsFailed == fl);
            if (q.Slow is { } sl) seq = seq.Where(l => l.IsSlow == sl);
            if (q.Levels is { Count: > 0 } lv) seq = seq.Where(l => lv.Contains(l.Level, StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(q.Search))
            {
                var needle = q.Search.Trim();
                seq = seq.Where(l => l.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || (l.Exception?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                    || l.ActorName.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }
            var matched = seq.ToList();
            return Task.FromResult(new LogPageResult(matched.Count, offset, limit, matched.Skip(offset).Take(limit).ToList()));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
