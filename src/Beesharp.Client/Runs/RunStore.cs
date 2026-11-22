namespace Beesharp.Client.Runs;

/// <summary>
/// The bulk data of a run, shared by all snapshots (brain 0021, 0028). Bee records are tiny and kept for the whole run; calls and log
/// lines are kept for at most <see cref="RunStoreOptions.MaxDetailedBeesPerScenario"/> bees of each scenario. When that is exceeded the
/// detail of successful bees goes first (oldest first), then slow ones, then failed ones (brain 0028). Only finished bees are in the
/// store, so running bees are never evicted. Readers read up to the counts of their snapshot. The log index is a time-sorted list
/// that is extended incrementally. <see cref="Errors"/> holds each distinct error text once.
/// </summary>
public sealed class RunStore
{
    /// <summary>The default limit. Prefer <see cref="Options"/> (<see cref="RunStoreOptions.MaxDetailedBeesPerScenario"/>) for the value in use.</summary>
    public const int DetailPerScenario = RunStoreOptions.DefaultMaxDetailedBeesPerScenario;

    private readonly List<BeeRecord>[] bees;
    private readonly Dictionary<int, BeeDetail> details = [];
    // Per scenario one queue per outcome (indexed by BeeOutcome), each oldest first, plus the total kept.
    private readonly Queue<BeeDetail>[][] detailQueues;
    private readonly int[] detailCounts;
    private byte[] outcomes = new byte[1024];
    private byte[] ratios = new byte[1024];
    private readonly List<BeeDetail> unindexed = [];
    private int unindexedCompactAt = 2000;
    private readonly List<FakeLogEntry> lines = [];
    private readonly HashSet<int> evictedIndexed = [];

    public RunStore(int scenarios, RunStoreOptions? options = null)
    {
        Options = options ?? new RunStoreOptions();
        bees = Enumerable.Range(0, scenarios).Select(_ => new List<BeeRecord>()).ToArray();
        detailQueues = Enumerable.Range(0, scenarios).Select(_ => new[] { new Queue<BeeDetail>(), new Queue<BeeDetail>(), new Queue<BeeDetail>() }).ToArray();
        detailCounts = new int[scenarios];
    }

    /// <summary>The limits in use. Settable at any time; applies from the next bee that finishes.</summary>
    public RunStoreOptions Options { get; }

    /// <summary>Each distinct error text of the run, stored once (brain 0028).</summary>
    public ErrorDictionary Errors { get; } = new();

    /// <summary>True once older bees lost their calls and log lines.</summary>
    public bool DetailTrimmed { get; private set; }

    /// <summary>Finished bees of a scenario, in completion order. Read only the first <c>ScenarioView.Finished</c> items.</summary>
    public IReadOnlyList<BeeRecord> Bees(int entry) => bees[entry];

    public BeeOutcome OutcomeOf(int sequence) => sequence >= 0 && sequence < outcomes.Length ? (BeeOutcome)outcomes[sequence] : BeeOutcome.Ok;
    /// <summary>The colour step of a finished bee (brain 0041), by sequence; 0 for an unknown bee.</summary>
    public int TintOf(int sequence) => OutcomeOf(sequence) == BeeOutcome.Slow ? SlowCallRule.MaxStep
        : sequence >= 0 && sequence < ratios.Length ? SlowCallRule.Step(SlowCallRule.Decode(ratios[sequence])) : 0;
    /// <summary>How many times its typical duration the worst call of a finished bee took (1 when unknown).</summary>
    public double RatioOf(int sequence) => sequence >= 0 && sequence < ratios.Length ? SlowCallRule.Decode(ratios[sequence]) : 1;
    public BeeDetail? Detail(int sequence) => details.GetValueOrDefault(sequence);

    /// <summary>Replaces a bee's detail once lazily fetched from server run storage (brain 0045).</summary>
    public void ReplaceDetail(BeeDetail detail) => details[detail.Sequence] = detail;

    /// <summary>Cheap check whether the calls and log lines of a bee are still kept (false once evicted, or for an unknown bee).</summary>
    public bool HasDetail(int sequence) => details.ContainsKey(sequence);

    public void Complete(BeeRecord record, BeeDetail detail)
    {
        bees[record.Entry].Add(record);
        if (record.Sequence >= outcomes.Length)
        {
            var size = Math.Max(outcomes.Length * 2, record.Sequence + 1);
            Array.Resize(ref outcomes, size);
            Array.Resize(ref ratios, size);
        }
        outcomes[record.Sequence] = (byte)record.Outcome;
        ratios[record.Sequence] = record.RatioCode;
        details[record.Sequence] = detail;
        unindexed.Add(detail);
        var queues = detailQueues[record.Entry];
        queues[(int)record.Outcome].Enqueue(detail);
        detailCounts[record.Entry]++;
        var max = Math.Max(0, Options.MaxDetailedBeesPerScenario);
        while (detailCounts[record.Entry] > max)
        {
            // Successful first, then slow, then failed; oldest first within each.
            var q = queues[(int)BeeOutcome.Ok].Count > 0 ? queues[(int)BeeOutcome.Ok]
                : queues[(int)BeeOutcome.Slow].Count > 0 ? queues[(int)BeeOutcome.Slow] : queues[(int)BeeOutcome.Failed];
            var old = q.Dequeue();
            detailCounts[record.Entry]--;
            old.Evicted = true;
            details.Remove(old.Sequence);
            if (old.Indexed) evictedIndexed.Add(old.Sequence);
            DetailTrimmed = true;
        }
        // Evicted bees must not stay referenced by the not-yet-indexed list (it is only emptied when the logs are first read).
        if (unindexed.Count >= unindexedCompactAt)
        {
            unindexed.RemoveAll(d => d.Evicted);
            unindexedCompactAt = Math.Max(2000, unindexed.Count * 2);
        }
    }

    /// <summary>
    /// All kept log lines of the run sorted by time. New bees are merged in at the tail (cheap), lines of trimmed bees are
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
