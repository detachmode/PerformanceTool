using System.Diagnostics;

namespace Beesharp.Services.TraceStore;

/// <summary>
/// The background fetch of one run's traces (brain 0065). Bees are reported with <see cref="Observe"/> as they finish; a loop asks the trace store for
/// the selected traces in batches once their spans have had time to arrive, saves them with the run, and <see cref="CompleteAsync"/> does the final pass after the run:
/// the last traces, one retry for what is missing, and a reason for every trace that never came. Never throws into the run, never blocks it: <see cref="Observe"/>
/// does bounded in-memory work; every request, wait and write happens on the session's own task. At most two requests per trace, a rate limit between requests,
/// caps per run and per trace. A trace store that is down is reported once (and again when it answers) and costs nothing but the traces.
/// </summary>
public sealed class TraceFetchSession : IAsyncDisposable
{
    private enum FetchState { Pending, Found, NotFound, Error }

    private sealed class Tracked(TraceCandidate candidate)
    {
        public TraceCandidate Candidate { get; set; } = candidate;
        public FetchState State { get; set; } = FetchState.Pending;
        public string? Reason { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset? LastAttemptUtc { get; set; }
        public int SpanCount { get; set; }
        public bool Truncated { get; set; }
        public bool IsFailedBee => Candidate.Reasons.Contains("failed");
    }

    private readonly string runId;
    private readonly DateTimeOffset runStartedUtc;
    private readonly ITraceStore store;
    private readonly IRunStorage storage;
    private readonly TraceFetchOptions options;
    private readonly ILogger log;
    private readonly TraceSelector selector;
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, Tracked> tracked = new(StringComparer.Ordinal);
    private readonly Lock sync = new();
    private Task? loop;
    private long lastRequestTicks;
    private TraceLookupOutcome? storeProblem;
    private bool storageProblemLogged;
    private bool completed;

    /// <summary>Creates the session of a run.</summary>
    /// <param name="runId">The run the traces belong to.</param>
    /// <param name="runStartedUtc">When the run started (the lower bound of the time window asked of the store).</param>
    /// <param name="store">The trace store.</param>
    /// <param name="storage">Where the fetched traces are kept.</param>
    /// <param name="options">Caps, delays and rate limit.</param>
    /// <param name="log">Logger with the <c>Beesharp.Traces</c> category (its lines land in the run's host logs).</param>
    /// <param name="selector">The selection; a default one for <paramref name="options"/> when <see langword="null"/>.</param>
    public TraceFetchSession(string runId, DateTimeOffset runStartedUtc, ITraceStore store, IRunStorage storage, TraceFetchOptions options, ILogger log, TraceSelector? selector = null)
    {
        this.runId = runId;
        this.runStartedUtc = runStartedUtc;
        this.store = store;
        this.storage = storage;
        this.options = options;
        this.log = log;
        this.selector = selector ?? new TraceSelector(options);
    }

    /// <summary>The logger category of everything the fetch says; it ends up in the run's host logs (<c>beesharp runs logs &lt;run&gt; --entry host</c>).</summary>
    public const string LogCategory = "Beesharp.Traces";

    /// <summary>Reports a finished bee. Cheap and safe to call from the run's result callback.</summary>
    /// <param name="bee">The bee and its calls.</param>
    public void Observe(TraceBeeObservation bee)
    {
        try { selector.Observe(bee); }
        catch (Exception ex) { log.LogDebug(ex, "Could not take bee {Bee} into account for trace fetching", bee.Sequence + 1); }
    }

    /// <summary>Starts the background loop.</summary>
    public void Start()
    {
        log.LogInformation(
            "Trace store: {Store}. Fetching in the background up to {Max} traces: failed bees, the {Slowest} slowest and {Typical} typical calls per endpoint (first request {Settle:0} s after a bee ends)",
            store.Description, options.MaxTraces, options.SlowestPerEndpoint, options.TypicalPerEndpoint, options.SettleDelay.TotalSeconds);
        loop = Task.Run(() => LoopAsync(stop.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(options.PollInterval, ct);
                try { await RoundAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log.LogDebug(ex, "A trace fetch round failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One look at the selection: fetches what is due, one batch.</summary>
    private async Task RoundAsync(CancellationToken ct)
    {
        Refresh();
        var now = DateTimeOffset.UtcNow;
        List<Tracked> due;
        lock (sync)
            due = tracked.Values
                .Where(t => t.State == FetchState.Pending && t.Attempts < 2
                    && t.Candidate.EndedUtc + options.SettleDelay <= now
                    && (t.LastAttemptUtc is null || now - t.LastAttemptUtc >= options.RetryDelay))
                .OrderBy(t => t.Attempts)
                .Take(storeProblem is null ? options.BatchSize : 1)   // a store that is down gets one probe per round, not a batch
                .ToList();
        if (due.Count > 0) await FetchAsync(due, ct);
    }

    /// <summary>Adds the selector's current choice to what is tracked, within the caps (failed bees keep room of their own).</summary>
    private void Refresh()
    {
        var chosen = selector.Select();
        lock (sync)
        {
            var failedBudget = Math.Min(options.MaxFailed, options.MaxTraces / 2);
            var otherBudget = options.MaxTraces - failedBudget;
            var failedCount = tracked.Values.Count(t => t.IsFailedBee);
            var otherCount = tracked.Count - failedCount;
            foreach (var candidate in chosen)
            {
                if (tracked.TryGetValue(candidate.TraceId, out var existing))
                {
                    existing.Candidate = existing.Candidate with { Reasons = candidate.Reasons };
                    continue;
                }
                var isFailed = candidate.Reasons.Contains("failed");
                if (isFailed ? failedCount >= failedBudget : otherCount >= otherBudget) continue;
                tracked[candidate.TraceId] = new Tracked(candidate);
                if (isFailed) failedCount++; else otherCount++;
            }
        }
    }

    private async Task FetchAsync(List<Tracked> batch, CancellationToken ct)
    {
        var save = new List<StoredTrace>();
        int found = 0, retryLater = 0, missing = 0, asked = 0;
        foreach (var t in batch)
        {
            ct.ThrowIfCancellationRequested();
            await RateLimitAsync(ct);
            var lookup = await store.GetTraceAsync(t.Candidate.TraceId, runStartedUtc - TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10), ct);
            var now = DateTimeOffset.UtcNow;

            if (lookup.Outcome is TraceLookupOutcome.Unreachable or TraceLookupOutcome.Error)
            {
                NoteStoreProblem(lookup);
                break;   // the rest waits: no point asking a store that just failed
            }
            NoteStoreOk();
            asked++;
            t.Attempts++;
            t.LastAttemptUtc = now;

            if (lookup.Outcome == TraceLookupOutcome.Found)
            {
                var spans = lookup.Spans;
                t.Truncated = spans.Count > options.MaxSpansPerTrace;
                if (t.Truncated) spans = spans.OrderBy(s => s.StartUnixNanos).Take(options.MaxSpansPerTrace).ToList();
                t.State = FetchState.Found;
                t.SpanCount = spans.Count;
                t.Reason = null;
                found++;
                save.Add(new StoredTrace(ToDto(t), spans));
                log.LogDebug("Trace {TraceId} (B{Bee}, {Reasons}): {Spans} span(s) from {Services} service(s)", t.Candidate.TraceId, t.Candidate.Sequence + 1,
                    string.Join("; ", t.Candidate.Reasons), spans.Count, spans.Select(s => s.Service).Distinct().Count());
            }
            else if (t.Attempts >= 2)
            {
                // The second ask (the one retry) did not find it either: the trace is not there.
                save.Add(Finish(t, FetchState.NotFound, NotFoundReason(t, now)));
                missing++;
                log.LogDebug("Trace {TraceId} (B{Bee}, {Reasons}) not found, giving up after {Attempt} attempts", t.Candidate.TraceId, t.Candidate.Sequence + 1, string.Join("; ", t.Candidate.Reasons), t.Attempts);
            }
            else
            {
                retryLater++;
                log.LogDebug("Trace {TraceId} (B{Bee}, {Reasons}) not found yet, retry in {Retry:0} s", t.Candidate.TraceId, t.Candidate.Sequence + 1,
                    string.Join("; ", t.Candidate.Reasons), options.RetryDelay.TotalSeconds);
            }
        }

        await SaveAsync(save);
        if (asked == 0) return;
        var line = $"fetched {found} of {asked} traces";
        if (retryLater > 0) line += $" ({retryLater} not arrived yet, retry in {options.RetryDelay.TotalSeconds:0} s)";
        else if (missing > 0) line += $" ({missing} not found)";
        log.LogInformation("{Batch}", line);
    }

    private StoredTrace Finish(Tracked t, FetchState state, string reason)
    {
        t.State = state;
        t.Reason = reason;
        t.SpanCount = 0;
        return new StoredTrace(ToDto(t), []);
    }

    private string NotFoundReason(Tracked t, DateTimeOffset now)
    {
        var last = t.LastAttemptUtc ?? now;
        if (now - t.Candidate.EndedUtc > options.Retention) return TraceFetchReasons.Expired;
        if (last - t.Candidate.EndedUtc < options.ArrivalWindow) return TraceFetchReasons.NotArrived;
        return TraceFetchReasons.NotInStore;
    }

    private TraceFetchDto ToDto(Tracked t) => new(
        t.Candidate.TraceId, t.Candidate.Sequence,
        t.State switch { FetchState.Found => TraceFetchStatus.Found, FetchState.NotFound => TraceFetchStatus.NotFound, FetchState.Error => TraceFetchStatus.Error, _ => TraceFetchStatus.NotFound },
        t.State == FetchState.Found ? null : t.Reason ?? TraceFetchReasons.NotArrived,
        t.SpanCount, t.Attempts, t.Candidate.Reasons, t.LastAttemptUtc, t.Truncated);

    private async Task RateLimitAsync(CancellationToken ct)
    {
        var elapsed = Stopwatch.GetElapsedTime(lastRequestTicks);
        if (lastRequestTicks != 0 && elapsed < options.RequestInterval)
            await Task.Delay(options.RequestInterval - elapsed, ct);
        lastRequestTicks = Stopwatch.GetTimestamp();
    }

    private void NoteStoreProblem(TraceLookup lookup)
    {
        if (storeProblem == lookup.Outcome) return;   // once per problem, not per request
        storeProblem = lookup.Outcome;
        log.LogWarning("{Problem}. Traces are asked for again later; the run is not affected", lookup.Message ?? $"{store.Description} failed");
    }

    private void NoteStoreOk()
    {
        if (storeProblem is null) return;
        storeProblem = null;
        log.LogInformation("{Store} answers again", store.Description);
    }

    private async Task SaveAsync(List<StoredTrace> traces)
    {
        if (traces.Count == 0) return;
        try { await storage.SaveTracesAsync(runId, traces); }
        catch (Exception ex)
        {
            if (storageProblemLogged) return;
            storageProblemLogged = true;
            log.LogWarning("Could not store fetched traces with the run: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// The final pass after the run: waits until the last bees' spans had time to arrive, fetches what is left, retries what was not found once, then records every
    /// trace that never came with its likely reason. Bounded by <see cref="TraceFetchOptions.FinalPassTimeout"/>; never throws.
    /// </summary>
    /// <param name="stopped">The run was stopped: no waiting for late spans or for the retry, one immediate attempt per trace.</param>
    /// <param name="ct">Cancellation token; a canceled pass records the traces it did not get with the reasons known so far.</param>
    /// <returns>The "x of y found" figures (also logged as the last line).</returns>
    public async Task<TraceFetchSummaryDto> CompleteAsync(bool stopped = false, CancellationToken ct = default)
    {
        if (completed) return Summary();
        completed = true;
        await stop.CancelAsync();
        if (loop is not null) { try { await loop; } catch (Exception) { } }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.FinalPassTimeout);
        try
        {
            Refresh();
            if (!stopped) await WaitUntilSettledAsync(timeout.Token);
            await FinalRoundAsync(retry: false, timeout.Token);
            if (!stopped && storeProblem is null && Pending(attempts: 1).Count > 0)
            {
                await WaitForRetryAsync(timeout.Token);
                await FinalRoundAsync(retry: true, timeout.Token);
            }
        }
        catch (OperationCanceledException) { log.LogInformation("The final trace fetch was cut short"); }
        catch (Exception ex) { log.LogWarning("The final trace fetch failed: {Message}", ex.Message); }

        // Whatever is still open did not come.
        var finish = new List<StoredTrace>();
        var now = DateTimeOffset.UtcNow;
        lock (sync)
            foreach (var t in tracked.Values.Where(t => t.State == FetchState.Pending))
                finish.Add(storeProblem is { } problem && t.Attempts == 0
                    ? Finish(t, FetchState.Error, problem == TraceLookupOutcome.Unreachable ? TraceFetchReasons.StoreUnreachable : TraceFetchReasons.StoreError)
                    : Finish(t, FetchState.NotFound, NotFoundReason(t, now)));
        await SaveAsync(finish);

        var summary = Summary();
        if (summary.Selected == 0) log.LogInformation("Traces: none were selected (no bee with a trace failed or made a call)");
        else log.LogInformation("{Summary}", summary.Line);
        return summary;
    }

    private List<Tracked> Pending(int attempts)
    {
        lock (sync) return tracked.Values.Where(t => t.State == FetchState.Pending && t.Attempts == attempts).ToList();
    }

    private async Task WaitUntilSettledAsync(CancellationToken ct)
    {
        DateTimeOffset latest;
        lock (sync)
        {
            var pending = tracked.Values.Where(t => t.State == FetchState.Pending).ToList();
            if (pending.Count == 0) return;
            latest = pending.Max(t => t.Candidate.EndedUtc);
        }
        var wait = latest + options.SettleDelay - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            log.LogInformation("Waiting {Seconds:0} s for the last spans to arrive in the trace store", wait.TotalSeconds);
            await Task.Delay(wait, ct);
        }
    }

    private async Task WaitForRetryAsync(CancellationToken ct)
    {
        var last = Pending(attempts: 1).Max(t => t.LastAttemptUtc) ?? DateTimeOffset.UtcNow;
        var wait = last + options.RetryDelay - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            log.LogInformation("{Missing} trace(s) not found yet; one retry in {Seconds:0} s", Pending(attempts: 1).Count, wait.TotalSeconds);
            await Task.Delay(wait, ct);
        }
    }

    private async Task FinalRoundAsync(bool retry, CancellationToken ct)
    {
        var todo = Pending(attempts: retry ? 1 : 0);
        for (var i = 0; i < todo.Count && storeProblem is null; i += options.BatchSize)
            await FetchAsync(todo.Skip(i).Take(options.BatchSize).ToList(), ct);
    }

    private TraceFetchSummaryDto Summary()
    {
        lock (sync) return TraceFetchSummaryDto.From(tracked.Values.Where(t => t.State != FetchState.Pending).Select(ToDto).OrderBy(f => f.Sequence).ToList());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        if (loop is not null) { try { await loop; } catch (Exception) { } }
        (store as IDisposable)?.Dispose();
        stop.Dispose();
    }
}
