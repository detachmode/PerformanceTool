namespace Beesharp.Services.TraceStore;

/// <summary>
/// How much and how carefully Beesharp fetches traces (brain 0065). The trace store is shared infrastructure, so everything is capped and rate limited.
/// The defaults are the Proposed design of brain 0065; an environment can override <c>TraceStore.MaxTraces</c> and <c>TraceStore.SettleSeconds</c>.
/// </summary>
public sealed class TraceFetchOptions
{
    /// <summary>Cap on the traces fetched for one run (default 500).</summary>
    public int MaxTraces { get; set; } = 500;

    /// <summary>Cap on the failed bees whose traces are fetched (default 100). Canceled bees (a stopped run) are not fetched.</summary>
    public int MaxFailed { get; set; } = 100;

    /// <summary>How many of the slowest slow calls per endpoint get their trace fetched (default 5).</summary>
    public int SlowestPerEndpoint { get; set; } = 5;

    /// <summary>How many typical calls per endpoint get their trace fetched, as the baseline of "slow vs normal" (default 3).</summary>
    public int TypicalPerEndpoint { get; set; } = 3;

    /// <summary>How many endpoints are tracked for selection (default 200); calls of further endpoints are ignored.</summary>
    public int MaxEndpoints { get; set; } = 200;

    /// <summary>Cap on the spans stored per trace (default 2000); a bigger trace is cut and marked truncated.</summary>
    public int MaxSpansPerTrace { get; set; } = 2000;

    /// <summary>How long a bee must have ended before its trace is asked for: the services' spans arrive late (default 15 s).</summary>
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often the background loop looks for traces to fetch during the run (default 10 s).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Traces fetched per batch (default 20); the loop waits for the next poll between batches.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Minimum time between two requests to the trace store: the request rate limit (default 200 ms, at most 5 requests per second).</summary>
    public TimeSpan RequestInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How long a trace that was not found waits before its one retry (default 15 s).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>A trace not found less than this long after its bee ended is "not arrived yet" rather than missing (default 30 s).</summary>
    public TimeSpan ArrivalWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A trace older than this is probably expired in the store (default 72 hours, Jaeger's usual retention).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(72);

    /// <summary>The longest the final pass after the run may take in total (default 2 minutes); what is still open then is reported as not found.</summary>
    public TimeSpan FinalPassTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A copy with the environment's overrides applied.</summary>
    /// <param name="settings">The trace store settings of the run's environment.</param>
    /// <returns>The options for one run.</returns>
    public TraceFetchOptions With(TraceStoreSettings settings)
    {
        var copy = (TraceFetchOptions)MemberwiseClone();
        if (settings.MaxTraces is { } max) copy.MaxTraces = max;
        if (settings.SettleSeconds is { } settle) copy.SettleDelay = TimeSpan.FromSeconds(settle);
        return copy;
    }
}
