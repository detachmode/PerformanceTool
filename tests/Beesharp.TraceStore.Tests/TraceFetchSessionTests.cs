using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Storage;
using Beesharp.Services.TraceStore;
using Microsoft.Extensions.Logging;
using static Beesharp.TraceStore.Tests.Bees;

namespace Beesharp.TraceStore.Tests;

/// <summary>The background fetch (brain 0065): batches, the one retry, caps, reasons for missing traces, and a store that is down never hurting anything.</summary>
public class TraceFetchSessionTests
{
    private const string RunId = "run-test";

    private sealed class Harness : IAsyncDisposable
    {
        public InMemoryRunStorage Storage { get; } = new();
        public CapturingLogger Log { get; } = new();
        public FakeTraceStore Store { get; }
        public TraceFetchSession Session { get; }

        public Harness(Func<string, int, TraceLookup> answer, Action<TraceFetchOptions>? configure = null)
        {
            var options = FastOptions();
            configure?.Invoke(options);
            Storage.EnqueueRunStarted(RunId, DateTimeOffset.UtcNow, new LoadPlan { Scenarios = [new ScenarioRunConfig { ScenarioId = "s" }] });
            Store = new FakeTraceStore(answer);
            Session = new TraceFetchSession(RunId, DateTimeOffset.UtcNow.AddMinutes(-1), Store, Storage, options, Log);
        }

        public async Task<IReadOnlyList<TraceFetchDto>> FetchesAsync() => await Storage.GetTraceFetchesAsync(RunId);

        public async ValueTask DisposeAsync() => await Session.DisposeAsync();
    }

    private static Func<string, int, TraceLookup> AlwaysFound => (id, _) => FakeTraceStore.FoundTrace(id);

    private static async Task<bool> WaitAsync(Func<bool> condition, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    [Fact]
    public async Task The_traces_of_selected_bees_are_fetched_during_the_run_and_kept_with_it()
    {
        await using var h = new Harness(AlwaysFound);
        h.Session.Start();
        h.Session.Observe(Bee(0, failed: true));
        h.Session.Observe(Bee(1, calls: [Call("GET /a", 900, slow: true)]));
        h.Session.Observe(Bee(2, calls: [Call("GET /a", 20)]));

        Assert.True(await WaitAsync(() => h.Store.RequestCount == 3), "the background loop fetched the three traces while the run was still going");
        var summary = await h.Session.CompleteAsync();

        Assert.Equal("traces: 3 of 3 found", summary.Line);
        var fetches = await h.FetchesAsync();
        Assert.Equal(3, fetches.Count);
        Assert.All(fetches, f => Assert.Equal((TraceFetchStatus.Found, 2, 1), (f.Status, f.SpanCount, f.Attempts)));
        Assert.Equal(["failed"], fetches[0].SelectedFor);
        Assert.Equal(["slowest GET /a"], fetches[1].SelectedFor);
        Assert.Equal(["typical GET /a"], fetches[2].SelectedFor);
        Assert.Equal(6, (await h.Storage.GetTraceSpansAsync(RunId)).Count);
        Assert.Equal(1, h.Store.AttemptsFor(TraceId(0)));   // found at the first ask: never asked again by the final pass
    }

    [Fact]
    public async Task The_log_tells_what_a_user_wants_to_follow()
    {
        await using var h = new Harness(AlwaysFound);
        h.Session.Start();
        h.Session.Observe(Bee(0, failed: true));
        Assert.True(await WaitAsync(() => h.Store.RequestCount == 1));
        await h.Session.CompleteAsync();

        var info = h.Log.Messages(LogLevel.Information);
        Assert.Contains(info, m => m.StartsWith("Trace store: Fake store at http://fake.test.") && m.Contains("up to 500 traces"));
        Assert.Contains("fetched 1 of 1 traces", info);
        Assert.Contains("traces: 1 of 1 found", info);
        var debug = h.Log.Messages(LogLevel.Debug);
        Assert.Contains(debug, m => m.Contains(TraceId(0)) && m.Contains("B1") && m.Contains("failed") && m.Contains("2 span(s)"));
        Assert.Empty(h.Log.Messages(LogLevel.Warning));
    }

    [Fact]
    public async Task A_trace_is_not_asked_for_before_its_spans_had_time_to_arrive()
    {
        await using var h = new Harness(AlwaysFound, o => o.SettleDelay = TimeSpan.FromMinutes(5));
        h.Session.Start();
        h.Session.Observe(Bee(0, failed: true));

        await Task.Delay(150);   // several polls

        Assert.Equal(0, h.Store.RequestCount);
    }

    [Fact]
    public async Task The_final_pass_waits_for_the_last_bees_spans_then_fetches_the_rest_in_batches_at_the_rate_limit()
    {
        await using var h = new Harness(AlwaysFound, o =>
        {
            o.BatchSize = 2;
            o.RequestInterval = TimeSpan.FromMilliseconds(40);
            o.SettleDelay = TimeSpan.FromMilliseconds(120);
        });
        for (var i = 0; i < 5; i++) h.Session.Observe(Bee(i, failed: true));

        var started = DateTime.UtcNow;
        var summary = await h.Session.CompleteAsync();
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal("traces: 5 of 5 found", summary.Line);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(100), "waited for the settle delay of the last bee");
        Assert.Equal(5, h.Store.RequestCount);
        var ticks = h.Store.Requests.Select(r => r.Ticks).ToList();
        var gaps = ticks.Zip(ticks.Skip(1), (a, b) => System.Diagnostics.Stopwatch.GetElapsedTime(a, b).TotalMilliseconds).ToList();
        Assert.All(gaps, gap => Assert.True(gap >= 30, $"requests are at least ~40 ms apart, was {gap:0} ms"));
        Assert.Equal(["fetched 2 of 2 traces", "fetched 2 of 2 traces", "fetched 1 of 1 traces"], h.Log.Messages(LogLevel.Information).Where(m => m.StartsWith("fetched")).ToArray());
    }

    [Fact]
    public async Task A_trace_that_has_not_arrived_is_asked_once_more_and_found_then()
    {
        await using var h = new Harness((id, attempt) => attempt == 1 ? TraceLookup.Missing : FakeTraceStore.FoundTrace(id));
        h.Session.Observe(Bee(0, failed: true));

        var summary = await h.Session.CompleteAsync();

        Assert.Equal("traces: 1 of 1 found", summary.Line);
        var fetch = Assert.Single(await h.FetchesAsync());
        Assert.Equal((TraceFetchStatus.Found, 2), (fetch.Status, fetch.Attempts));
        Assert.Contains(h.Log.Messages(LogLevel.Information), m => m.StartsWith("fetched 0 of 1 traces (1 not arrived yet, retry in"));
        Assert.Contains("fetched 1 of 1 traces", h.Log.Messages(LogLevel.Information));
    }

    [Fact]
    public async Task A_trace_that_never_comes_is_asked_twice_at_most_and_recorded_with_the_likely_reason()
    {
        await using var h = new Harness((_, _) => TraceLookup.Missing, o =>
        {
            o.ArrivalWindow = TimeSpan.FromHours(1);
            o.Retention = TimeSpan.FromDays(3);
        });
        var now = DateTimeOffset.UtcNow;
        h.Session.Observe(Bee(0, now, failed: true));                                    // ended just now: the spans are probably still on their way
        h.Session.Observe(Bee(1, now - TimeSpan.FromDays(5), failed: true));             // older than the store keeps traces

        var summary = await h.Session.CompleteAsync();

        var fetches = await h.FetchesAsync();
        Assert.All(fetches, f => Assert.Equal((TraceFetchStatus.NotFound, 2, 0), (f.Status, f.Attempts, f.SpanCount)));
        Assert.Equal(TraceFetchReasons.NotArrived, fetches[0].Reason);
        Assert.Equal(TraceFetchReasons.Expired, fetches[1].Reason);
        Assert.Equal(2, h.Store.AttemptsFor(TraceId(0)));   // the one retry, not more
        Assert.Equal(2, h.Store.AttemptsFor(TraceId(1)));
        Assert.Equal("traces: 0 of 2 found, 2 not found: 1 expired, 1 not arrived", summary.Line);
    }

    [Fact]
    public async Task A_trace_that_is_old_enough_and_still_missing_is_not_in_the_store()
    {
        await using var h = new Harness((_, _) => TraceLookup.Missing, o => o.ArrivalWindow = TimeSpan.Zero);
        h.Session.Observe(Bee(0, failed: true));

        var summary = await h.Session.CompleteAsync();

        Assert.Equal(TraceFetchReasons.NotInStore, (await h.FetchesAsync())[0].Reason);
        Assert.Contains("not in the store (sampled out or not instrumented)", summary.Line);
    }

    [Fact]
    public async Task The_cap_per_run_and_the_cap_per_trace_hold()
    {
        await using var h = new Harness((id, _) => FakeTraceStore.FoundTrace(id, spans: 5), o =>
        {
            o.MaxTraces = 3;
            o.MaxFailed = 3;
            o.MaxSpansPerTrace = 2;
        });
        for (var i = 0; i < 10; i++) h.Session.Observe(Bee(i, failed: true));

        var summary = await h.Session.CompleteAsync();

        Assert.Equal(3, h.Store.RequestCount);
        Assert.Equal("traces: 3 of 3 found", summary.Line);
        var fetches = await h.FetchesAsync();
        Assert.All(fetches, f => Assert.Equal((2, true), (f.SpanCount, f.Truncated)));
        Assert.Equal(6, (await h.Storage.GetTraceSpansAsync(RunId)).Count);
    }

    [Fact]
    public async Task A_store_that_is_down_costs_nothing_but_the_traces_and_is_reported_once_not_per_request()
    {
        await using var h = new Harness((_, _) => TraceLookup.Unreachable("Jaeger at http://j:16686 could not be reached: Connection refused"));
        h.Session.Start();
        for (var i = 0; i < 40; i++) h.Session.Observe(Bee(i, failed: true));   // observing is fine while the store is down

        Assert.True(await WaitAsync(() => h.Store.RequestCount >= 3), "the loop keeps probing the store");
        var summary = await h.Session.CompleteAsync();

        var warnings = h.Log.Messages(LogLevel.Warning);
        var warning = Assert.Single(warnings);   // one line for the problem, however many rounds and requests
        Assert.Contains("could not be reached", warning);
        Assert.Contains("the run is not affected", warning);
        Assert.True(h.Store.RequestCount < 40, $"a store that is down gets a probe per round, not a request per trace (got {h.Store.RequestCount})");
        var fetches = await h.FetchesAsync();
        Assert.Equal(40, fetches.Count);
        Assert.All(fetches, f => Assert.Equal((TraceFetchStatus.Error, TraceFetchReasons.StoreUnreachable), (f.Status, f.Reason)));
        Assert.Equal("traces: 0 of 40 found, 40 not found: 40 store unreachable", summary.Line);
    }

    [Fact]
    public async Task A_store_that_answers_with_an_error_is_reported_with_its_own_reason()
    {
        await using var h = new Harness((_, _) => TraceLookup.Failed("Jaeger at http://j refused the request (HTTP 401); check TraceStore.Token"));
        h.Session.Observe(Bee(0, failed: true));

        var summary = await h.Session.CompleteAsync();

        Assert.Contains("TraceStore.Token", Assert.Single(h.Log.Messages(LogLevel.Warning)));
        Assert.Equal(TraceFetchReasons.StoreError, (await h.FetchesAsync())[0].Reason);
        Assert.Contains("store error", summary.Line);
    }

    [Fact]
    public async Task A_store_that_comes_back_is_reported_once_and_its_traces_are_fetched()
    {
        var up = false;
        await using var h = new Harness((id, _) => up ? FakeTraceStore.FoundTrace(id) : TraceLookup.Unreachable("Fake store at http://fake.test could not be reached"));
        h.Session.Start();
        for (var i = 0; i < 3; i++) h.Session.Observe(Bee(i, failed: true));
        Assert.True(await WaitAsync(() => h.Store.RequestCount >= 2));

        up = true;
        Assert.True(await WaitAsync(() => h.Log.Messages(LogLevel.Information).Any(m => m.Contains("answers again"))));
        var summary = await h.Session.CompleteAsync();

        Assert.Single(h.Log.Messages(LogLevel.Warning));
        Assert.Single(h.Log.Messages(LogLevel.Information), m => m.Contains("answers again"));
        Assert.Equal("traces: 3 of 3 found", summary.Line);
    }

    [Fact]
    public async Task A_stopped_run_does_not_wait_for_late_spans_or_for_the_retry()
    {
        await using var h = new Harness((_, _) => TraceLookup.Missing, o =>
        {
            o.SettleDelay = TimeSpan.FromMinutes(5);
            o.RetryDelay = TimeSpan.FromMinutes(5);
        });
        h.Session.Observe(Bee(0, failed: true));

        var started = DateTime.UtcNow;
        var summary = await h.Session.CompleteAsync(stopped: true);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(1, h.Store.AttemptsFor(TraceId(0)));
        Assert.Equal(1, summary.NotFound);
    }

    [Fact]
    public async Task The_final_pass_is_bounded_in_time()
    {
        await using var h = new Harness(AlwaysFound, o =>
        {
            o.SettleDelay = TimeSpan.FromMinutes(5);
            o.FinalPassTimeout = TimeSpan.FromMilliseconds(200);
        });
        h.Session.Observe(Bee(0, failed: true));

        var started = DateTime.UtcNow;
        var summary = await h.Session.CompleteAsync();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(1, summary.NotFound);   // not fetched in the time given: recorded as missing, with a reason
        Assert.Contains("cut short", h.Log.Messages(LogLevel.Information).Last(m => m.Contains("cut short")));
    }

    [Fact]
    public async Task Nothing_selected_is_said_so_and_asks_nothing()
    {
        await using var h = new Harness(AlwaysFound);
        h.Session.Observe(Bee(0));

        var summary = await h.Session.CompleteAsync();

        Assert.Equal(0, h.Store.RequestCount);
        Assert.Equal(0, summary.Selected);
        Assert.Contains(h.Log.Messages(LogLevel.Information), m => m.StartsWith("Traces: none were selected"));
    }

    [Fact]
    public async Task Disposing_releases_the_store()
    {
        var h = new Harness(AlwaysFound);
        h.Session.Start();
        await h.DisposeAsync();
        Assert.True(h.Store.Disposed);
    }

    // ---- nothing happens without configuration ----

    private sealed class CountingFactory : ITraceStoreFactory
    {
        public int Created { get; private set; }
        public ITraceStore Create(TraceStoreSettings settings) { Created++; return new FakeTraceStore(AlwaysFound); }
    }

    [Fact]
    public async Task Without_a_trace_store_in_the_environment_nothing_is_fetched_and_the_log_says_so()
    {
        var factory = new CountingFactory();
        var fetching = new TraceFetching(factory, Bees.FastOptions());
        var storage = new InMemoryRunStorage();
        var log = new CapturingLogger();

        foreach (var env in new EnvironmentValues?[] { null, EnvironmentValues.None, new EnvironmentValues("Dev", new Dictionary<string, string> { ["BaseUrl"] = "http://shop" }) })
            Assert.Null(fetching.Begin(RunId, DateTimeOffset.UtcNow, env, storage, log));

        Assert.Equal(0, factory.Created);
        Assert.Equal(3, log.Messages(LogLevel.Information).Count(m => m.Contains("no trace store is configured") && m.Contains("TraceStore.Kind") && m.Contains("TraceStore.Url")));
        Assert.Empty(await storage.GetTraceFetchesAsync(RunId));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_wrong_trace_store_setting_is_a_warning_and_nothing_is_fetched()
    {
        var factory = new CountingFactory();
        var fetching = new TraceFetching(factory, Bees.FastOptions());
        var log = new CapturingLogger();
        var env = new EnvironmentValues("Dev", new Dictionary<string, string> { [TraceStoreSettings.KindKey] = "elastic", [TraceStoreSettings.UrlKey] = "http://es" });

        Assert.Null(fetching.Begin(RunId, DateTimeOffset.UtcNow, env, new InMemoryRunStorage(), log));

        Assert.Equal(0, factory.Created);
        Assert.Contains("not supported", Assert.Single(log.Messages(LogLevel.Warning)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task With_a_trace_store_in_the_environment_a_session_starts_and_the_log_names_the_store()
    {
        var factory = new CountingFactory();
        var fetching = new TraceFetching(factory, Bees.FastOptions());
        var log = new CapturingLogger();
        var env = new EnvironmentValues("Dev", new Dictionary<string, string> { [TraceStoreSettings.UrlKey] = "http://localhost:16686", [TraceStoreSettings.MaxTracesKey] = "7" });

        await using var session = fetching.Begin(RunId, DateTimeOffset.UtcNow, env, new InMemoryRunStorage(), log);

        Assert.NotNull(session);
        Assert.Equal(1, factory.Created);
        Assert.Contains("up to 7 traces", Assert.Single(log.Messages(LogLevel.Information)));   // the environment's cap
    }
}
