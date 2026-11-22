using System.Text.Json.Nodes;
using Beesharp.Services.Runtime;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;
using Beesharp.Client.Data;
using Beesharp.Client.Runs;

namespace Beesharp.Client.Tests;

/// <summary>Runtime data of the fake run (brain 0052): run bag, entry bags, token refresh, final values of a finished run.</summary>
public class RuntimeDataTests
{
    private static ScenarioCatalog Catalog() =>
        new([new HttpGetScenario(), new HttpPostJsonScenario(), new CustomRequestScenario(), new UserJourneyScenario()]);

    [Fact]
    public void Preflight_fills_the_run_bag_and_startups_fill_the_entry_bags()
    {
        var data = new RuntimeData(3);
        new FakeRuntimeScript(data).Begin(3);
        var snap = data.Snapshot();

        Assert.Contains(snap.Run.Values, v => v.Key == "authToken" && v.Writer.Kind == DataWriterKind.Preflight);
        Assert.Contains(snap.Run.Values, v => v.Key == "tenantId");
        Assert.Equal(6, Assert.IsType<JsonArray>(snap.Run.Values.Single(v => v.Key == "seedUserIds").Value).Count);
        Assert.All(snap.Entries, e => Assert.Contains(e.Values, v => v.Key == "pool" && v.Writer == new DataWriter(DataWriterKind.Startup, e.Entry)));
    }

    [Fact]
    public void Token_is_refreshed_by_a_bee_of_the_first_entry_every_30_seconds_only()
    {
        var data = new RuntimeData(2);
        var script = new FakeRuntimeScript(data);
        script.Begin(2);
        var first = data.Get(-1, "authToken")!.GetValue<string>();

        script.BeeFinished(0, 0, 10, false, 1);
        script.BeeFinished(1, 1, 31, false, 1); // another entry never refreshes
        Assert.Equal(first, data.Get(-1, "authToken")!.GetValue<string>());

        script.BeeFinished(0, 2, 31, false, 2);
        var second = data.Get(-1, "authToken")!.GetValue<string>();
        Assert.NotEqual(first, second);
        Assert.Equal(1, data.Get(-1, "refreshCount")!.GetValue<int>());
        script.BeeFinished(0, 3, 40, false, 3); // not due again yet
        Assert.Equal(second, data.Get(-1, "authToken")!.GetValue<string>());
        Assert.Equal(new DataWriter(DataWriterKind.Bee, 0, 2), data.Snapshot().Run.Values.Single(v => v.Key == "authToken").Writer);
    }

    [Fact]
    public void Cart_ids_stay_bounded_and_a_failed_bee_writes_only_the_counter()
    {
        var data = new RuntimeData(1);
        var script = new FakeRuntimeScript(data);
        script.Begin(1);
        for (var i = 0; i < 20; i++) script.BeeFinished(0, i, i, false, i + 1);
        Assert.Equal(5, Assert.IsType<JsonArray>(data.Get(0, "cartIds")).Count);

        var before = data.Get(0, "cartIds");
        script.BeeFinished(0, 20, 21, true, 21);
        Assert.Same(before, data.Get(0, "cartIds"));
        Assert.Equal(21, data.Get(0, "completed")!.GetValue<int>());
    }

    [Fact]
    public void Seeded_finished_run_has_final_values()
    {
        var source = new FakeRunSource(Catalog());
        var s = source.Current;
        Assert.Equal(FakeRunState.Finished, s.State);
        var rt = Assert.IsType<RuntimeDataSnapshot>(s.Runtime);

        Assert.Contains(rt.Run.Values, v => v.Key == "authToken");
        Assert.True(rt.Run.Values.Single(v => v.Key == "refreshCount").Value!.GetValue<int>() >= 50); // 30 minutes, every 30 s
        Assert.Equal(s.Count, rt.Entries.Count);
        for (var i = 0; i < s.Count; i++)
        {
            Assert.Equal(s.Scenarios[i].Finished, rt.Entries[i].Values.Single(v => v.Key == "completed").Value!.GetValue<int>());
            var cleaned = rt.Entries[i].Values.Single(v => v.Key == "cleanedUp");
            Assert.Equal(DataWriterKind.Teardown, cleaned.Writer.Kind);
            Assert.Equal(i, cleaned.Writer.Entry);
        }
    }

    [Fact]
    public void A_started_run_has_the_preflight_values_at_once_and_keeps_them_after_stop()
    {
        var source = new FakeRunSource(Catalog());
        source.Start();
        Assert.Contains(source.Current.Runtime!.Run.Values, v => v.Key == "tenantId");
        Assert.DoesNotContain(source.Current.Runtime!.Entries[0].Values, v => v.Key == "cleanedUp"); // teardown comes at the end
        source.Stop();
        Assert.Contains(source.Current.Runtime!.Run.Values, v => v.Key == "tenantId");
        Assert.Contains(source.Current.Runtime!.Entries[0].Values, v => v.Key == "cleanedUp");
        source.Dispose();
    }

    [Fact]
    public void The_fake_shutdown_program_runs_at_the_end_of_a_finished_and_of_a_stopped_run()
    {
        var source = new FakeRunSource(Catalog());
        var seeded = source.Current;
        Assert.Equal("Succeeded", seeded.Shutdown!.State);
        Assert.Equal("cleanup.cs", seeded.Shutdown.Name);
        Assert.Contains(seeded.Shutdown.Logs, l => l.Contains("Deleting"));
        Assert.Equal(DataWriterKind.Shutdown, seeded.Runtime!.Run.Values.Single(v => v.Key == "cleanedUp").Writer.Kind);
        Assert.Equal("Succeeded", seeded.Preflight!.State);

        source.Start();
        Assert.Null(source.Current.Shutdown); // not yet: it runs at the end
        source.Stop();
        Assert.Equal("Succeeded", source.Current.Shutdown!.State);
        Assert.Contains(source.Current.Runtime!.Run.Values, v => v.Key == "deletedCarts" && v.Writer.Kind == DataWriterKind.Shutdown);
        source.Dispose();
    }
}
