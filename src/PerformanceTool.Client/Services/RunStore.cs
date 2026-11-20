namespace PerformanceTool.Client.Services;

/// <summary>
/// The bulk data of a run, shared by all snapshots (brain 0021). Actor records are tiny and kept for the whole run; calls and log
/// lines are kept for the latest <see cref="DetailPerScenario"/> actors of each scenario only. Readers read up to the counts of
/// their snapshot. The log index is a time-sorted list that is extended incrementally.
/// </summary>
public sealed class RunStore
{
    public const int DetailPerScenario = 1500;

    private readonly List<ActorRecord>[] actors;
    private readonly Dictionary<int, ActorDetail> details = [];
    private readonly Queue<ActorDetail>[] detailQueues;
    private byte[] outcomes = new byte[1024];
    private readonly List<ActorDetail> unindexed = [];
    private readonly List<FakeLogEntry> lines = [];
    private readonly HashSet<int> evictedIndexed = [];

    public RunStore(int scenarios)
    {
        actors = Enumerable.Range(0, scenarios).Select(_ => new List<ActorRecord>()).ToArray();
        detailQueues = Enumerable.Range(0, scenarios).Select(_ => new Queue<ActorDetail>()).ToArray();
    }

    /// <summary>True once older actors lost their calls and log lines.</summary>
    public bool DetailTrimmed { get; private set; }

    /// <summary>Finished actors of a scenario, in completion order. Read only the first <c>ScenarioView.Finished</c> items.</summary>
    public IReadOnlyList<ActorRecord> Actors(int entry) => actors[entry];

    public ActorOutcome OutcomeOf(int sequence) => sequence >= 0 && sequence < outcomes.Length ? (ActorOutcome)outcomes[sequence] : ActorOutcome.Ok;
    public ActorDetail? Detail(int sequence) => details.GetValueOrDefault(sequence);

    public void Complete(ActorRecord record, ActorDetail detail)
    {
        actors[record.Entry].Add(record);
        if (record.Sequence >= outcomes.Length) Array.Resize(ref outcomes, Math.Max(outcomes.Length * 2, record.Sequence + 1));
        outcomes[record.Sequence] = (byte)record.Outcome;
        details[record.Sequence] = detail;
        unindexed.Add(detail);
        var q = detailQueues[record.Entry];
        q.Enqueue(detail);
        while (q.Count > DetailPerScenario)
        {
            var old = q.Dequeue();
            old.Evicted = true;
            details.Remove(old.Sequence);
            if (old.Indexed) evictedIndexed.Add(old.Sequence);
            DetailTrimmed = true;
        }
    }

    /// <summary>
    /// All kept log lines of the run sorted by time. New actors are merged in at the tail (cheap), lines of trimmed actors are
    /// dropped in batches. The same list instance is returned and extended, so copy what you need to keep.
    /// </summary>
    public IReadOnlyList<FakeLogEntry> Logs()
    {
        var fresh = new List<FakeLogEntry>();
        foreach (var d in unindexed)
        {
            if (d.Evicted) continue;
            d.Indexed = true;
            fresh.AddRange(d.Logs);
        }
        unindexed.Clear();

        if (evictedIndexed.Count >= 300)
        {
            lines.RemoveAll(l => evictedIndexed.Contains(l.Sequence));
            evictedIndexed.Clear();
        }
        if (fresh.Count == 0) return lines;

        fresh.Sort((a, b) => a.AtSeconds.CompareTo(b.AtSeconds));
        // Everything before the earliest new line stays where it is; only the tail is merged.
        int lo = 0, hi = lines.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (lines[mid].AtSeconds < fresh[0].AtSeconds) lo = mid + 1; else hi = mid; }
        var tail = lines.GetRange(lo, lines.Count - lo);
        lines.RemoveRange(lo, lines.Count - lo);
        int i = 0, j = 0;
        while (i < tail.Count || j < fresh.Count)
            lines.Add(j >= fresh.Count || (i < tail.Count && tail[i].AtSeconds <= fresh[j].AtSeconds) ? tail[i++] : fresh[j++]);
        return lines;
    }
}
