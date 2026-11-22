using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;
using Xunit;

namespace Beesharp.Tests;

/// <summary>The shutdown program and the runtime data of the server runner (brain 0052).</summary>
public sealed class ShutdownProgramTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"perftool-shutdown-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
    }

    private PreflightStore Preflights() => new(Path.Combine(tempDir, "preflight"));
    private ShutdownStore Shutdowns() => new(Path.Combine(tempDir, "shutdown"));

    private static RunManager Manager(IRunStorage storage, PreflightStore? pre = null, ShutdownStore? sd = null) =>
        new(new LoadRunner(new ScenarioCatalog([new BagScenario()])), null!, storage, pre, sd);

    private static LoadPlan Plan(int seconds = 1, int entries = 1, string? pre = null, string? shutdown = null, double rate = 240) => new()
    {
        DurationSeconds = seconds,
        MaxConcurrency = 5,
        TimeoutSeconds = 5,
        PreflightScriptId = pre,
        ShutdownScriptId = shutdown,
        Scenarios = Enumerable.Range(0, entries).Select(i => new ScenarioRunConfig { ScenarioId = "bag-stub", Label = $"Bag {i + 1}", RatePerMinute = rate }).ToList(),
    };

    private static async Task<LiveRunStatusDto> WaitFinishedAsync(RunManager manager)
    {
        var status = manager.GetCurrent();
        for (var i = 0; i < 200 && status.State != "Finished"; i++)
        {
            await Task.Delay(50);
            status = manager.GetCurrent();
        }
        Assert.Equal("Finished", status.State);
        return status;
    }

    private static async Task Compile(ScriptProgramStore store, string id, string code)
    {
        var res = await store.CompileAndSaveAsync(id, id + ".cs", code);
        Assert.True(res.Success, string.Join("; ", res.Diagnostics.Select(d => d.Message)) + res.Error);
    }

    private static RuntimeValue Value(RuntimeBag bag, string key) => bag.Values.Single(v => v.Key == key);

    [Fact]
    public async Task Shutdown_runs_after_a_normal_finish_and_shows_state_and_logs()
    {
        var sd = Shutdowns();
        await Compile(sd, "cleanup", """
            Log("Cleaning up");
            Data["cleanedUp"] = true;
            """);
        await using var storage = new InMemoryRunStorage();
        var manager = Manager(storage, null, sd);

        await manager.StartAsync(Plan(shutdown: "cleanup"), null);
        var status = await WaitFinishedAsync(manager);

        Assert.Equal("Succeeded", status.ShutdownState);
        Assert.Equal("cleanup.cs", status.ShutdownScriptName);
        Assert.Contains(status.ShutdownLogs!, l => l.Contains("Cleaning up"));
        Assert.True(status.Scenarios[0].Finished > 0);
        var written = Value(manager.GetCurrentRuntime()!.Run, "cleanedUp");
        Assert.Equal(DataWriterKind.Shutdown, written.Writer.Kind);
        // The shutdown output is also in the live logs under its own name.
        Assert.Contains(manager.GetLiveLogs(), l => l.BeeName == "Shutdown" && l.Message.Contains("Cleaning up"));
    }

    [Fact]
    public async Task Shutdown_runs_after_the_run_was_stopped()
    {
        var sd = Shutdowns();
        await Compile(sd, "cleanup", """Data["stopped"] = "cleaned";""");
        await using var storage = new InMemoryRunStorage();
        var manager = Manager(storage, null, sd);

        await manager.StartAsync(Plan(seconds: 60, shutdown: "cleanup"), null);
        for (var i = 0; i < 60 && manager.GetCurrent().Scenarios[0].Finished == 0; i++) await Task.Delay(50);
        await manager.StopAsync();
        var status = await WaitFinishedAsync(manager);

        Assert.Equal("Succeeded", status.ShutdownState);
        Assert.True(status.ElapsedSeconds < 30, "the run was stopped early");
        Assert.Equal("cleaned", Value(manager.GetCurrentRuntime()!.Run, "stopped").Value!.GetValue<string>());
    }

    [Fact]
    public async Task Shutdown_runs_after_a_failed_preflight_and_can_read_what_the_preflight_wrote()
    {
        var pre = Preflights();
        var sd = Shutdowns();
        await Compile(pre, "pre", """
            Data["created"] = "tenant-42";
            Fail("not ready");
            """);
        await Compile(sd, "cleanup", """
            Log($"deleting {Data["created"]}");
            Data["deleted"] = Data["created"];
            """);
        await using var storage = new InMemoryRunStorage();
        var manager = Manager(storage, pre, sd);

        await manager.StartAsync(Plan(pre: "pre", shutdown: "cleanup"), null);
        var status = await WaitFinishedAsync(manager);

        Assert.Equal("Failed", status.PreflightState);
        Assert.Equal(0, status.Scenarios[0].Starts);
        Assert.Equal("Succeeded", status.ShutdownState);
        Assert.Contains(status.ShutdownLogs!, l => l.Contains("deleting tenant-42"));
        var run = manager.GetCurrentRuntime()!.Run;
        Assert.Equal(DataWriterKind.Preflight, Value(run, "created").Writer.Kind);
        Assert.Equal(DataWriterKind.Shutdown, Value(run, "deleted").Writer.Kind);
    }

    [Fact]
    public async Task A_failing_shutdown_shows_as_failed_and_leaves_the_scenario_results_alone()
    {
        var sd = Shutdowns();
        await Compile(sd, "broken", """
            Log("about to fail");
            Fail("cannot delete the tenant");
            """);
        await using var storage = new InMemoryRunStorage();
        var manager = Manager(storage, null, sd);

        await manager.StartAsync(Plan(shutdown: "broken"), null);
        var status = await WaitFinishedAsync(manager);

        Assert.Equal("Failed", status.ShutdownState);
        Assert.Contains("cannot delete the tenant", status.ShutdownError);
        Assert.Contains(status.ShutdownLogs!, l => l.Contains("about to fail"));
        Assert.Null(status.Error);
        Assert.True(status.Scenarios[0].Finished > 0);
        Assert.Equal(0, status.Scenarios[0].Failed);
    }

    [Fact]
    public async Task A_run_without_a_shutdown_program_has_no_shutdown_state()
    {
        await using var storage = new InMemoryRunStorage();
        var manager = Manager(storage);
        await manager.StartAsync(Plan(), null);
        var status = await WaitFinishedAsync(manager);
        Assert.Null(status.ShutdownState);
    }

    [Fact]
    public async Task Starting_with_an_unknown_shutdown_program_is_refused()
    {
        await using var storage = new InMemoryRunStorage();
        var manager = Manager(storage, null, Shutdowns());
        await Assert.ThrowsAsync<ArgumentException>(() => manager.StartAsync(Plan(shutdown: "nope"), null));
    }

    [Fact]
    public async Task The_run_bag_is_shared_by_preflight_entries_and_shutdown_while_entry_bags_stay_private()
    {
        var pre = Preflights();
        var sd = Shutdowns();
        await Compile(pre, "pre", """Data["fromPreflight"] = "hello";""");
        await Compile(sd, "cleanup", """
            Data["shutdownSaw"] = Data.TryGetValue("beeSaw", out var v) ? v : "nothing";
            Data["shutdownSawTeardown"] = Data.ContainsKey("teardownKey");
            """);
        await using var storage = new InMemoryRunStorage();
        var manager = Manager(storage, pre, sd);

        await manager.StartAsync(Plan(entries: 2, pre: "pre", shutdown: "cleanup"), null);
        await WaitFinishedAsync(manager);

        var rt = manager.GetCurrentRuntime()!;
        Assert.Equal(2, rt.Entries.Count);

        // Writer tracking on the run bag: the last writer of each key.
        Assert.Equal(DataWriterKind.Preflight, Value(rt.Run, "fromPreflight").Writer.Kind);
        Assert.Equal(DataWriterKind.Startup, Value(rt.Run, "startupKey").Writer.Kind);
        Assert.Equal(DataWriterKind.Bee, Value(rt.Run, "beeSaw").Writer.Kind);
        Assert.Equal("hello", Value(rt.Run, "beeSaw").Value!.GetValue<string>()); // the bee read the preflight's value
        Assert.Equal(DataWriterKind.Teardown, Value(rt.Run, "teardownKey").Writer.Kind);
        Assert.Equal(DataWriterKind.Shutdown, Value(rt.Run, "shutdownSaw").Writer.Kind);
        Assert.Equal("hello", Value(rt.Run, "shutdownSaw").Value!.GetValue<string>());
        Assert.True(Value(rt.Run, "shutdownSawTeardown").Value!.GetValue<bool>()); // teardowns ran before the shutdown program
        Assert.All(rt.Run.Values, v => Assert.True(v.AtSeconds >= 0));

        // Entry bags: each entry has its own, written by its own startup and bees.
        for (var i = 0; i < 2; i++)
        {
            var bag = rt.Entries[i];
            Assert.Equal(i, bag.Entry);
            var startedAt = Value(bag, "entryStartup");
            Assert.Equal(new DataWriter(DataWriterKind.Startup, i), startedAt.Writer);
            var count = Value(bag, "beeCount");
            Assert.Equal(DataWriterKind.Bee, count.Writer.Kind);
            Assert.Equal(i, count.Writer.Entry);
            Assert.True(count.Value!.GetValue<int>() > 0);
            Assert.DoesNotContain(bag.Values, v => v.Key == "fromPreflight"); // run bag keys are not in the entry bag
        }
        Assert.NotEqual(Value(rt.Entries[0], "entryStartup").Value!.GetValue<string>(), Value(rt.Entries[1], "entryStartup").Value!.GetValue<string>());
    }

    [Fact]
    public async Task A_finished_run_keeps_its_final_values_for_a_past_run_in_every_storage()
    {
        var sd = Shutdowns();
        await Compile(sd, "cleanup", """Data["final"] = 42;""");
        var duck = new DuckDbRunStorage(Path.Combine(tempDir, "runs.duckdb"));
        var memory = new InMemoryRunStorage();
        foreach (IRunStorage storage in new IRunStorage[] { memory, duck })
        {
            var manager = Manager(storage, null, sd);
            var started = await manager.StartAsync(Plan(shutdown: "cleanup"), null);
            await WaitFinishedAsync(manager);
            await storage.FlushAsync();

            // By run id, from storage, as the REST API does for a past run.
            var second = Manager(storage, null, sd);
            var past = await second.GetRuntimeAsync(started.RunId);
            Assert.NotNull(past);
            Assert.Equal(42, Value(past!.Run, "final").Value!.GetValue<int>());
            Assert.Equal(DataWriterKind.Shutdown, Value(past.Run, "final").Writer.Kind);
            Assert.Contains(past.Entries[0].Values, v => v.Key == "beeCount");

            // Loading the stored run makes it the active view, with its values and a version for the poller.
            var loaded = await second.LoadStoredRunAsync(started.RunId);
            Assert.NotNull(loaded);
            Assert.True(loaded!.RuntimeVersion > 0);
            Assert.Equal(42, Value(second.GetCurrentRuntime()!.Run, "final").Value!.GetValue<int>());

            await storage.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_unknown_run_has_no_runtime_data()
    {
        await using var storage = new InMemoryRunStorage();
        Assert.Null(await Manager(storage).GetRuntimeAsync("run-nope"));
    }

    [Fact]
    public void Writer_tracking_records_the_last_writer_and_the_run_time()
    {
        var now = 1.5;
        var data = new RunData(1, () => now);
        data.RunBagFor(new DataWriter(DataWriterKind.Preflight))["k"] = 1;
        now = 7.25;
        data.RunBagFor(new DataWriter(DataWriterKind.Bee, 0, 41))["k"] = 2;
        data.EntryBagFor(0, new DataWriter(DataWriterKind.Startup, 0))["own"] = "x";

        var snap = data.Snapshot();
        var k = snap.Run.Values.Single();
        Assert.Equal(2, k.Value!.GetValue<int>());
        Assert.Equal(new DataWriter(DataWriterKind.Bee, 0, 41), k.Writer);
        Assert.Equal(7.25, k.AtSeconds);
        Assert.Equal("x", snap.Entries[0].Values.Single().Value!.GetValue<string>());
        Assert.True(data.Version >= 3);
    }

    [Fact]
    public void The_bag_is_thread_safe()
    {
        var data = new RunData(0);
        Parallel.For(0, 200, i => data.RunBagFor(new DataWriter(DataWriterKind.Bee, 0, i))[$"k{i % 10}"] = i);
        Assert.Equal(10, data.Snapshot().Run.Values.Count);
    }

    private sealed class Cyclic { public Cyclic? Self { get; set; } public override string ToString() => "cyclic-thing"; }

    [Fact]
    public void Values_that_cannot_be_serialised_show_as_their_ToString()
    {
        var data = new RunData(0);
        var bag = data.RunBagFor(new DataWriter(DataWriterKind.Preflight));
        var loop = new Cyclic();
        loop.Self = loop;
        bag["loop"] = loop;
        bag["number"] = 3;
        bag["list"] = new List<int> { 1, 2 };
        bag["nothing"] = null;

        var values = data.Snapshot().Run.Values.ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal("cyclic-thing", values["loop"]!.GetValue<string>());
        Assert.Equal(3, values["number"]!.GetValue<int>());
        Assert.Equal(2, values["list"]!.AsArray().Count);
        Assert.Null(values["nothing"]);
    }

    [Fact]
    public void The_snapshot_survives_a_round_trip_through_the_wire_format()
    {
        var data = new RunData(1, () => 3);
        data.RunBagFor(new DataWriter(DataWriterKind.Shutdown))["done"] = true;
        data.EntryBagFor(0, new DataWriter(DataWriterKind.Bee, 0, 9))["n"] = 5;

        var json = JsonSerializer.Serialize(data.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"shutdown\"", json, StringComparison.OrdinalIgnoreCase);
        var back = JsonSerializer.Deserialize<RuntimeDataSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(new DataWriter(DataWriterKind.Shutdown), back.Run.Values.Single().Writer);
        Assert.True(back.Run.Values.Single().Value!.GetValue<bool>());
        Assert.Equal(new DataWriter(DataWriterKind.Bee, 0, 9), back.Entries[0].Values.Single().Writer);
        Assert.Equal(3, back.Entries[0].Values.Single().AtSeconds);
    }

    private sealed class EmptyParams { }

    /// <summary>Reads and writes both bags so a test can see who wrote what.</summary>
    private sealed class BagScenario : Scenario<EmptyParams>
    {
        public override string Id => "bag-stub";
        public override string Name => "Bag stub";
        public override string Description => "Test scenario";

        protected override Task StartupAsync(EmptyParams parameters, ScenarioContext context, CancellationToken ct)
        {
            context.RunBag["startupKey"] = "set";
            context.State["entryStartup"] = Guid.NewGuid().ToString("N");
            return Task.CompletedTask;
        }

        protected override Task<ScenarioOutcome> ExecuteAsync(EmptyParams parameters, ScenarioContext context, CancellationToken ct)
        {
            context.RunBag["beeSaw"] = context.RunBag.TryGetValue("fromPreflight", out var v) ? v : "nothing";
            var n = context.State.TryGetValue("beeCount", out var c) ? (int)c! : 0;
            context.State["beeCount"] = n + 1;
            return Task.FromResult(new ScenarioOutcome(200, 10, null));
        }

        protected override Task TeardownAsync(EmptyParams parameters, ScenarioContext context, CancellationToken ct)
        {
            context.RunBag["teardownKey"] = "done";
            return Task.CompletedTask;
        }
    }
}
