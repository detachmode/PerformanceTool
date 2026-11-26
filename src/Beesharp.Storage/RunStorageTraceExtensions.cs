using Beesharp.Services.Scenarios;

namespace Beesharp.Services.Storage;

/// <summary>
/// The trace queries of a run (brain 0065) written once on top of <see cref="IRunStorage"/>'s plain reads, so every storage provider answers
/// <c>runs trace</c> and <c>runs hotspots</c> through the same <see cref="TraceAnalytics"/> engine as the CLI's bundle readers.
/// </summary>
public static class RunStorageTraceExtensions
{
    /// <summary>One bee's trace (or the part below one call) as a flat, bounded span list.</summary>
    /// <param name="storage">The run storage.</param>
    /// <param name="runId">The run.</param>
    /// <param name="query">The bee or trace ID, optional call and span bound.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The view (with <see cref="TraceViewDto.Found"/> false and the reason when the trace is not stored), or <see langword="null"/> when the run, bee or trace ID is unknown.</returns>
    /// <exception cref="ArgumentException">The key is neither a bee number nor a trace ID, or the call does not exist in the bee.</exception>
    public static async Task<TraceViewDto?> GetTraceViewAsync(this IRunStorage storage, string runId, TraceViewQuery query, CancellationToken ct = default)
    {
        var key = TraceAnalytics.ParseKey(query.Key)
            ?? throw new ArgumentException($"'{query.Key}' is neither a bee (5 or B5) nor a trace ID (32 hex characters).");
        if (await storage.GetRunAsync(runId, ct) is null) return null;

        var fetches = await storage.GetTraceFetchesAsync(runId, ct);
        var sequence = key.Sequence;
        if (sequence is null)
        {
            sequence = fetches.FirstOrDefault(f => f.TraceId == key.TraceId)?.Sequence;
            if (sequence is null)
                sequence = (await storage.GetCallFactsAsync(runId, ct)).Where(c => c.TraceId == key.TraceId).Select(c => (int?)c.Sequence).FirstOrDefault();
            if (sequence is null) return null;
        }

        var bee = await storage.GetBeeDetailAsync(runId, sequence.Value, ct);
        if (bee is null) return null;
        var info = new TraceAnalytics.BeeTraceInfo(
            bee.Sequence, bee.TraceId ?? bee.Calls.Select(c => c.TraceId).FirstOrDefault(t => t is not null), bee.DurationMs,
            bee.Calls.Select((c, i) => new TraceAnalytics.CallTraceInfo(
                i + 1, c.SpanId, c.DurationMs, string.IsNullOrWhiteSpace(c.Endpoint) ? EndpointNormalizer.NormalizeCallName(c.Name) : c.Endpoint)).ToList());

        IReadOnlyList<TraceSpanDto> spans = info.TraceId is null ? [] : await storage.GetTraceSpansAsync(runId, [info.TraceId], ct);
        return TraceAnalytics.ComposeView(runId, info, query.Call, query.MaxSpans, fetches, spans);
    }

    /// <summary>Where the time of the slow calls went, per endpoint, for a run.</summary>
    /// <param name="storage">The run storage.</param>
    /// <param name="runId">The run.</param>
    /// <param name="query">Endpoint filter and limits.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The hotspots, or <see langword="null"/> when the run is unknown.</returns>
    public static async Task<HotspotsResultDto?> GetHotspotsAsync(this IRunStorage storage, string runId, HotspotsQuery query, CancellationToken ct = default)
    {
        if (await storage.GetRunAsync(runId, ct) is null) return null;
        var fetches = await storage.GetTraceFetchesAsync(runId, ct);
        if (fetches.Count == 0) return TraceAnalytics.BuildHotspots(runId, [], [], fetches, query);
        var spans = await storage.GetTraceSpansAsync(runId, null, ct);
        var calls = await storage.GetCallFactsAsync(runId, ct);
        return TraceAnalytics.BuildHotspots(runId, calls, spans, fetches, query);
    }
}
