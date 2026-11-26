using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;
using Beesharp.Services.TraceStore;

namespace Beesharp.Engine.Tests;

/// <summary>The background trace fetch inside a run (brain 0065): traces land in the run, the log says what happens, and the run itself never notices the trace store.</summary>
public sealed class TraceFetchEngineTests
{
    public sealed class P
    {
        public int Dummy { get; set; }
    }

    /// <summary>Each bee makes one call; every 5th takes much longer, bee 2 fails after its call.</summary>
    private sealed class CallScenario : Scenario<P>
    {
        public override string Id => "calls";
        public override string Name => "Calls";
        public override string Description => "Test";

        protected override async Task<ScenarioOutcome> ExecuteAsync(P p, ScenarioContext ctx, CancellationToken ct)
        {
            await ctx.MeasureCallAsync("GET /products/1", () => Task.Delay(Random.Shared.Next(0, 100) < 25 ? 60 : 4, ct));
            return new ScenarioOutcome(200, 0);
        }
    }

    private sealed class FailingScenario : Scenario<P>
    {
        public override string Id => "calls";
        public override string Name => "Calls";
        public override string Description => "Test";

        protected override async Task<ScenarioOutcome> ExecuteAsync(P p, ScenarioContext ctx, CancellationToken ct)
        {
            await ctx.MeasureCallAsync("GET /products/1", () => Task.Delay(4, ct));
            throw new InvalidOperationException("payment declined");
        }
    }

    private sealed class FakeStore(Func<string, TraceLookup> answer) : ITraceStore
    {
        public int Requests;
        public string Kind => "fake";
        public string Description => "Fake store at http://trace.test";
        public Task<TraceLookup> GetTraceAsync(string traceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(answer(traceId));
        }
    }

    private sealed class FakeFactory(ITraceStore store) : ITraceStoreFactory
    {
        public int Created;
        public ITraceStore Create(TraceStoreSettings settings) { Created++; return store; }
    }

    private static TraceFetchOptions Fast() => new()
    {
        SettleDelay = TimeSpan.Zero,
        PollInterval = TimeSpan.FromMilliseconds(20),
        RetryDelay = TimeSpan.FromMilliseconds(30),
        RequestInterval = TimeSpan.FromMilliseconds(1),
        TypicalMinSample = 3,
    };

    private static TraceLookup Found(string id) =>
        new(TraceLookupOutcome.Found, [new TraceSpanDto(id, "00000000000000a1", null, "beesharp", "bee", "internal", 1_000_000_000, 5), new TraceSpanDto(id, "00000000000000a2", "00000000000000a1", "shop-api", "GET /products/{id}", "server", 1_001_000_000, 3)]);

    private static async Task<(RunManager Manager, InMemoryRunStorage Storage, string RunId, double ElapsedSeconds)> RunAsync(
        ITraceStoreFactory factory, string? environment, Scenario<P>? scenario = null)
    {
        scenario ??= new CallScenario();
        var storage = new InMemoryRunStorage();
        var envs = new EnvironmentStore(Path.Combine(Path.GetTempPath(), $"beesharp-env-tf-{Guid.NewGuid():N}"));
        await envs.SaveAsync(new EnvironmentDefinition { Name = "Dev", Variables = [new("TraceStore.Url", "http://trace.test")] });
        await envs.SaveAsync(new EnvironmentDefinition { Name = "Plain", Variables = [new("BaseUrl", "http://shop")] });
        var manager = new RunManager(new LoadRunner(new ScenarioCatalog([scenario])), envs, storage, traces: new TraceFetching(factory, Fast()));
        var entry = ScenarioRunConfig.CreateFor(scenario);
        entry.RatePerMinute = 600;
        var status = await manager.StartAsync(new LoadPlan { DurationSeconds = 1, MaxConcurrency = 20, TimeoutSeconds = 10, Scenarios = [entry] }, environment);
        await manager.WaitUntilFinishedAsync();
        var done = manager.GetCurrent();
        return (manager, storage, status.RunId, done.ElapsedSeconds);
    }

    private static async Task<IReadOnlyList<string>> HostLogAsync(IRunStorage storage, string runId, string? level = null)
    {
        var page = await storage.QueryLogsAsync(runId, new LogQuery(Entry: ProgramLogEntry.Host, Levels: level is null ? null : [level], Limit: 1000));
        return page.Items.Select(l => $"{l.Level}: {l.Message}").ToList();
    }

    [Fact]
    public async Task A_run_whose_environment_names_a_trace_store_fetches_traces_into_the_run_and_logs_it_in_the_hosts_logs()
    {
        var store = new FakeStore(Found);
        var factory = new FakeFactory(store);

        var (manager, storage, runId, _) = await RunAsync(factory, "Dev");

        var status = manager.GetCurrent();
        Assert.Equal("Finished", status.State);
        Assert.Null(status.Error);
        Assert.Equal(1, factory.Created);
        var fetches = await storage.GetTraceFetchesAsync(runId);
        Assert.NotEmpty(fetches);
        Assert.All(fetches, f => Assert.Equal(TraceFetchStatus.Found, f.Status));
        Assert.Equal(fetches.Count * 2, (await storage.GetTraceSpansAsync(runId)).Count);
        foreach (var f in fetches)   // each fetched trace belongs to a bee of this run
            Assert.Equal(f.TraceId, (await storage.GetBeeDetailAsync(runId, f.Sequence))!.TraceId);

        var log = await HostLogAsync(storage, runId);
        Assert.Contains(log, l => l.StartsWith("Information: [Traces] Trace store: Fake store at http://trace.test."));
        Assert.Contains(log, l => l.StartsWith("Information: [Traces] fetched "));
        Assert.Contains($"Information: [Traces] traces: {fetches.Count} of {fetches.Count} found", log);
        Assert.Contains(log, l => l.StartsWith("Debug: [Traces] Trace "));   // per-trace details
        Assert.DoesNotContain(log, l => l.StartsWith("Warning"));
    }

    [Fact]
    public async Task A_trace_store_that_is_down_does_not_fail_or_slow_the_run_and_is_one_warning_in_the_log()
    {
        var store = new FakeStore(_ => TraceLookup.Unreachable("Fake store at http://trace.test could not be reached: Connection refused"));

        var started = DateTime.UtcNow;
        var (manager, storage, runId, elapsed) = await RunAsync(new FakeFactory(store), "Dev");

        var status = manager.GetCurrent();
        Assert.Equal("Finished", status.State);
        Assert.Null(status.Error);
        Assert.Equal(10, status.Scenarios[0].Finished);
        Assert.Equal(0, status.Scenarios[0].Failed);
        Assert.True(elapsed < 2.5, $"the run took {elapsed:0.0} s, the trace store made no difference");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));

        var warnings = await HostLogAsync(storage, runId, "Warning");
        Assert.Single(warnings);
        Assert.Contains("could not be reached", warnings[0]);
        Assert.All(await storage.GetTraceFetchesAsync(runId), f => Assert.Equal(TraceFetchReasons.StoreUnreachable, f.Reason));
        Assert.True(store.Requests <= elapsed / 0.02 + 15, $"a store that is down is probed once per poll, not once per trace (got {store.Requests})");
    }

    [Fact]
    public async Task Without_a_trace_store_nothing_is_fetched_and_the_log_says_why()
    {
        var store = new FakeStore(Found);
        var factory = new FakeFactory(store);

        foreach (var environment in new string?[] { null, "Plain" })
        {
            var (_, storage, runId, _) = await RunAsync(factory, environment);
            Assert.Empty(await storage.GetTraceFetchesAsync(runId));
            Assert.Contains(await HostLogAsync(storage, runId), l => l.StartsWith("Information: [Traces] Traces are not fetched: no trace store is configured"));
        }

        Assert.Equal(0, factory.Created);
        Assert.Equal(0, store.Requests);
    }

    [Fact]
    public async Task A_failed_bee_gets_its_trace_fetched_and_is_marked_so()
    {
        var store = new FakeStore(Found);

        var (_, storage, runId, _) = await RunAsync(new FakeFactory(store), "Dev", new FailingScenario());

        var fetches = await storage.GetTraceFetchesAsync(runId);
        Assert.Equal(10, fetches.Count(f => f.SelectedFor.Contains("failed")));
    }
}
