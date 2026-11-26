namespace Beesharp.Services.TraceStore;

/// <summary>One call of a finished bee, as the selection sees it.</summary>
/// <param name="Endpoint">The normalized endpoint.</param>
/// <param name="DurationMs">The call's duration.</param>
/// <param name="IsSlow">Whether the run's slow rule judged the call slow.</param>
/// <param name="Failed">Whether the call failed.</param>
public sealed record TraceCallObservation(string Endpoint, double DurationMs, bool IsSlow, bool Failed);

/// <summary>A finished bee, as the selection sees it.</summary>
/// <param name="TraceId">The bee's trace ID; a bee without one (tracing off) is ignored.</param>
/// <param name="Sequence">Zero-based bee sequence.</param>
/// <param name="Failed">Whether the bee failed (not: was canceled by a stop).</param>
/// <param name="EndedUtc">When the bee ended.</param>
/// <param name="Calls">The bee's calls.</param>
public sealed record TraceBeeObservation(string? TraceId, int Sequence, bool Failed, DateTimeOffset EndedUtc, IReadOnlyList<TraceCallObservation> Calls);

/// <summary>A trace chosen for fetching and why.</summary>
/// <param name="TraceId">The trace ID.</param>
/// <param name="Sequence">Zero-based bee sequence.</param>
/// <param name="EndedUtc">When the bee ended.</param>
/// <param name="Reasons">Why it was chosen: <c>failed</c>, <c>slowest GET /x</c>, <c>typical GET /x</c>.</param>
public sealed record TraceCandidate(string TraceId, int Sequence, DateTimeOffset EndedUtc, IReadOnlyList<string> Reasons);

/// <summary>
/// Chooses which traces to fetch (brain 0065), as bees finish: all failed bees (capped), the slowest few slow calls per endpoint, and a few typical calls
/// per endpoint (nearest to the median, picked from a bounded random sample) as the baseline for "slow vs normal". Memory stays bounded however long the run is.
/// The order of <see cref="Select"/> is the priority when the per-run cap bites: failed first, then the slowest of every endpoint, then typical.
/// </summary>
public sealed class TraceSelector
{
    private sealed record Entry(string TraceId, int Sequence, DateTimeOffset EndedUtc, double DurationMs);

    private sealed class EndpointState
    {
        public List<Entry> Slowest { get; } = [];
        public List<Entry> Sample { get; } = [];
        public long Seen { get; set; }
    }

    private readonly TraceFetchOptions options;
    private readonly Random random;
    private readonly Lock sync = new();
    private readonly List<Entry> failed = [];
    private readonly Dictionary<string, EndpointState> endpoints = new(StringComparer.Ordinal);
    private readonly int sampleSize;

    /// <summary>Creates a selector.</summary>
    /// <param name="options">The caps and counts.</param>
    /// <param name="seed">Seed of the random sample of typical calls (tests pass a fixed one).</param>
    public TraceSelector(TraceFetchOptions options, int seed = 20261126)
    {
        this.options = options;
        random = new Random(seed);
        sampleSize = Math.Max(20, options.TypicalPerEndpoint * 8);
    }

    /// <summary>Takes a finished bee into account. Cheap: bounded work per call, no I/O.</summary>
    /// <param name="bee">The bee.</param>
    public void Observe(TraceBeeObservation bee)
    {
        if (string.IsNullOrEmpty(bee.TraceId)) return;
        lock (sync)
        {
            if (bee.Failed && failed.Count < options.MaxFailed)
                failed.Add(new Entry(bee.TraceId, bee.Sequence, bee.EndedUtc, 0));

            foreach (var call in bee.Calls)
            {
                if (call.Failed) continue;
                if (!endpoints.TryGetValue(call.Endpoint, out var state))
                {
                    if (endpoints.Count >= options.MaxEndpoints) continue;
                    endpoints[call.Endpoint] = state = new EndpointState();
                }
                var entry = new Entry(bee.TraceId, bee.Sequence, bee.EndedUtc, call.DurationMs);
                if (call.IsSlow)
                {
                    if (options.SlowestPerEndpoint <= 0) continue;
                    var slowest = state.Slowest;
                    if (slowest.Count < options.SlowestPerEndpoint || call.DurationMs > slowest[^1].DurationMs)
                    {
                        if (slowest.Any(e => e.TraceId == bee.TraceId && e.DurationMs >= call.DurationMs)) continue;
                        slowest.Add(entry);
                        slowest.Sort((a, b) => b.DurationMs.CompareTo(a.DurationMs));
                        if (slowest.Count > options.SlowestPerEndpoint) slowest.RemoveAt(slowest.Count - 1);
                    }
                }
                else
                {
                    state.Seen++;
                    if (state.Sample.Count < sampleSize) state.Sample.Add(entry);
                    else if (random.NextInt64(state.Seen) is var j && j < sampleSize) state.Sample[(int)j] = entry;
                }
            }
        }
    }

    /// <summary>The traces to fetch now, in priority order, at most <see cref="TraceFetchOptions.MaxTraces"/>. Changes as the run goes on (a slower call replaces a less slow one).</summary>
    /// <returns>The candidates, each trace once with all its reasons.</returns>
    public IReadOnlyList<TraceCandidate> Select()
    {
        lock (sync)
        {
            var order = new List<string>();
            var byTrace = new Dictionary<string, (Entry Entry, List<string> Reasons)>(StringComparer.Ordinal);
            void Add(Entry entry, string reason)
            {
                if (!byTrace.TryGetValue(entry.TraceId, out var found))
                {
                    byTrace[entry.TraceId] = (entry, [reason]);
                    order.Add(entry.TraceId);
                }
                else if (!found.Reasons.Contains(reason)) found.Reasons.Add(reason);
            }

            foreach (var entry in failed) Add(entry, "failed");

            var names = endpoints.Keys.Order(StringComparer.Ordinal).ToList();
            for (var rank = 0; rank < options.SlowestPerEndpoint; rank++)
                foreach (var name in names)
                    if (rank < endpoints[name].Slowest.Count) Add(endpoints[name].Slowest[rank], "slowest " + name);

            var typical = names.ToDictionary(n => n, n => NearestToMedian(endpoints[n].Sample));
            for (var rank = 0; rank < options.TypicalPerEndpoint; rank++)
                foreach (var name in names)
                    if (rank < typical[name].Count) Add(typical[name][rank], "typical " + name);

            return order.Take(Math.Max(0, options.MaxTraces))
                .Select(id => new TraceCandidate(id, byTrace[id].Entry.Sequence, byTrace[id].Entry.EndedUtc, byTrace[id].Reasons))
                .ToList();
        }
    }

    private List<Entry> NearestToMedian(List<Entry> sample)
    {
        if (sample.Count == 0 || options.TypicalPerEndpoint <= 0) return [];
        var sorted = sample.OrderBy(e => e.DurationMs).ToList();
        var median = sorted[(sorted.Count - 1) / 2].DurationMs;
        return sample.OrderBy(e => Math.Abs(e.DurationMs - median)).ThenBy(e => e.Sequence)
            .DistinctBy(e => e.TraceId).Take(options.TypicalPerEndpoint).ToList();
    }
}
