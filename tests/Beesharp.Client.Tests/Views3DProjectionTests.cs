using Beesharp.Client.Data;
using Beesharp.Client.Runs;
using Beesharp.Client.Views3D;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

public sealed class Views3DProjectionTests
{
    private static ScenarioCatalog CreateCatalog() => new(
    [
        new HttpGetScenario(),
        new HttpPostJsonScenario(),
        new CustomRequestScenario(),
        new UserJourneyScenario(),
    ]);

    [Fact]
    public void ChooseBucketCount_adapts_to_run_duration_and_handles_empty()
    {
        Assert.Equal(0, Views3DProjection.ChooseBucketCount(0));
        Assert.Equal(0, Views3DProjection.ChooseBucketCount(-5));
        Assert.InRange(Views3DProjection.ChooseBucketCount(5), 8, 20);
        Assert.InRange(Views3DProjection.ChooseBucketCount(30), 20, 32);
        Assert.Equal(30, Views3DProjection.ChooseBucketCount(1800));
        Assert.Equal(36, Views3DProjection.ChooseBucketCount(10800));
    }

    [Fact]
    public void OutcomeRgb_follows_beesharp_color_semantics()
    {
        Assert.Equal(Views3DProjection.FailedRgb, Views3DProjection.OutcomeRgb(failedCount: 1, slowCount: 2, ratio: 1.0));
        Assert.Equal(Views3DProjection.SlowRgb, Views3DProjection.OutcomeRgb(failedCount: 0, slowCount: 1, ratio: 1.0));
        Assert.Equal(Views3DProjection.SlowRgb, Views3DProjection.OutcomeRgb(failedCount: 0, slowCount: 0, ratio: 2.0));
        Assert.Equal(Views3DProjection.GoodRgb, Views3DProjection.OutcomeRgb(failedCount: 0, slowCount: 0, ratio: 1.0));
        Assert.Equal(Views3DProjection.RunningRgb, Views3DProjection.OutcomeRgb(failedCount: 0, slowCount: 0, ratio: 1.0, inFlightOnlyCount: 3));

        // Graded green-to-amber between 1.0x and 2.0x
        var mid = Views3DProjection.OutcomeRgb(failedCount: 0, slowCount: 0, ratio: 1.5);
        Assert.InRange(mid[0], Views3DProjection.GoodRgb[0] + 1, Views3DProjection.SlowRgb[0] - 1);
    }

    [Fact]
    public void BuildTerrain_handles_empty_idle_snapshot_gracefully()
    {
        var plan = new LoadPlan();
        var empty = new RunSnapshot(FakeRunState.Idle, 0, plan, [], [], new RunStore(0));

        var terrain = Views3DProjection.BuildTerrain(empty, TerrainGrouping.Scenarios);
        Assert.False(terrain.HasData);
        Assert.Empty(terrain.Lanes);
        Assert.Empty(terrain.Grid);

        var surface = Views3DProjection.BuildSurface(empty);
        Assert.False(surface.HasData);
        Assert.Equal(7, surface.Bands.Count);
    }

    [Fact]
    public void BuildTerrain_by_scenario_orders_failed_and_slow_lanes_first_with_real_fake_data()
    {
        using var source = new FakeRunSource(CreateCatalog());
        var snap = source.Current;

        var terrain = Views3DProjection.BuildTerrain(snap, TerrainGrouping.Scenarios);

        Assert.True(terrain.HasData);
        Assert.Equal(snap.Count, terrain.Lanes.Count);
        Assert.Equal(30, terrain.TimeBuckets);
        Assert.Equal(7, terrain.TimeTicks.Count);

        // Failed lanes precede non-failed lanes.
        var seenHealthy = false;
        foreach (var lane in terrain.Lanes)
        {
            if (lane.FailedBees == 0 && lane.SlowBees == 0) seenHealthy = true;
            else if (lane.FailedBees > 0) Assert.False(seenHealthy);
        }

        // At least one cell should capture a failure and a sample bee sequence.
        var failedCell = terrain.Grid.SelectMany(r => r).FirstOrDefault(c => c.FailedCount > 0);
        Assert.NotNull(failedCell);
        Assert.NotNull(failedCell!.SampleError);
        Assert.NotNull(failedCell.SampleBeeSequence);
        Assert.Equal(Views3DProjection.FailedRgb, failedCell.ColorRgb);

        var payload = Views3DProjection.ToJsPayload(terrain);
        Assert.Equal("terrain", payload.Kind);
        Assert.NotEmpty(payload.Cells);
        Assert.All(payload.Cells, c => Assert.InRange(c.Height, 0.2, 7.5));
    }

    [Fact]
    public void BuildTerrain_by_scenario_includes_in_flight_bees_with_running_color()
    {
        var plan = new LoadPlan { DurationSeconds = 30 };
        plan.Scenarios.Add(new ScenarioRunConfig { ScenarioId = "http-get", Label = "Live API", RatePerMinute = 60 });
        var store = new RunStore(1);
        var views = new[] { new ScenarioView(0, 0, 0, 0, 2, 2) };
        var running = new IReadOnlyList<RunningBee>[]
        {
            [
                new RunningBee(0, 0, 1.0, []),
                new RunningBee(1, 0, 1.2, []),
            ]
        };
        var snap = new RunSnapshot(FakeRunState.Running, 4.0, plan, ["Live API"], views, store, running);

        var terrain = Views3DProjection.BuildTerrain(snap, TerrainGrouping.Scenarios);
        Assert.True(terrain.HasData);
        var activeCell = terrain.Grid[0].FirstOrDefault(c => c.InFlightCount > 0);
        Assert.NotNull(activeCell);
        Assert.Equal(0, activeCell!.BeeCount);
        Assert.Equal(2, activeCell.InFlightCount);
        Assert.Equal(Views3DProjection.RunningRgb, activeCell.ColorRgb);
    }

    [Fact]
    public void BuildTerrain_by_step_aggregates_step_durations_and_handles_missing_details()
    {
        using var source = new FakeRunSource(CreateCatalog());
        var snap = source.Current;

        var stepScene = Views3DProjection.BuildTerrain(snap, TerrainGrouping.Steps, scenarioEntry: 0);
        Assert.True(stepScene.HasData);
        Assert.Equal(0, stepScene.ScenarioEntry);
        Assert.Equal(snap.StepsOf(0).Count, stepScene.Lanes.Count);
        Assert.Contains(stepScene.Lanes, l => l.FailedBees > 0);

        // Synthetic run with placeholder detail (like Live mode before detail fetch)
        var plan = new LoadPlan { DurationSeconds = 20 };
        plan.Scenarios.Add(new ScenarioRunConfig { ScenarioId = "user-journey", Label = "Checkout", RatePerMinute = 30 });
        var store = new RunStore(1);
        var errId = store.Errors.Intern("HTTP 503");
        var bee = new BeeRecord(0, 0, 2.0, 420, 0, errId, store.Errors, FailedStep: 1);
        var placeholder = new BeeDetail(0, 0, [new FakeCall(0, "Loading…", 0, 420, 503, errId, false, 0)], () => []);
        store.Complete(bee, placeholder);
        var steps = new[] { new ScenarioSteps(["Auth", "Pay"]) };
        var liveLike = new RunSnapshot(FakeRunState.Finished, 10.0, plan, ["Checkout"], [new ScenarioView(1, 1, 0, 0, 0, 1)], store, Steps: steps);

        var missingStepScene = Views3DProjection.BuildTerrain(liveLike, TerrainGrouping.Steps, scenarioEntry: 0);
        Assert.Equal(1, missingStepScene.MissingDetailCount);
        Assert.NotNull(missingStepScene.Note);
        Assert.Equal(1, missingStepScene.Lanes[1].FailedBees);
    }

    [Fact]
    public void BuildSurface_adapts_bands_and_filters_by_scenario()
    {
        using var source = new FakeRunSource(CreateCatalog());
        var snap = source.Current;

        var all = Views3DProjection.BuildSurface(snap, entryFilter: null);
        Assert.True(all.HasData);
        Assert.Equal(7, all.Bands.Count);
        for (var i = 1; i < all.Bands.Count - 1; i++)
            Assert.True(all.Bands[i].MaxMs > all.Bands[i].MinMs);

        var onlyEntry0 = Views3DProjection.BuildSurface(snap, entryFilter: 0);
        Assert.True(onlyEntry0.HasData);
        Assert.Equal(0, onlyEntry0.ScenarioEntryFilter);

        var totalAll = all.Grid.SelectMany(r => r).Sum(c => c.Count);
        var totalEntry0 = onlyEntry0.Grid.SelectMany(r => r).Sum(c => c.Count);
        Assert.True(totalAll > totalEntry0);
        Assert.Equal(snap.Scenarios[0].Finished, totalEntry0);

        var payload = Views3DProjection.ToJsPayload(onlyEntry0);
        Assert.Equal("surface", payload.Kind);
        Assert.NotEmpty(payload.Cells);
    }

    [Fact]
    public void BuildRunHistory_handles_differing_durations_stopped_run_and_max_ms_height()
    {
        var plan = new LoadPlan { DurationSeconds = 60 };
        plan.Scenarios.Add(new ScenarioRunConfig { ScenarioId = "http-get", Label = "GET /items", RatePerMinute = 60 });

        // Current run: 60s, slot 0 has 1 bee (120ms), slot 10 (at 10s) has 2 bees (100ms + 900ms longest!)
        var curStore = new RunStore(1);
        curStore.Complete(new BeeRecord(0, 0, 0.2, 120, 0, ErrorDictionary.None, curStore.Errors), EmptyDetail(0));
        curStore.Complete(new BeeRecord(1, 0, 10.1, 100, 0, ErrorDictionary.None, curStore.Errors), EmptyDetail(1));
        curStore.Complete(new BeeRecord(2, 0, 10.4, 900, 1, ErrorDictionary.None, curStore.Errors), EmptyDetail(2));
        var curSnap = new RunSnapshot(FakeRunState.Finished, 60.0, plan, ["GET /items"], [new ScenarioView(3, 0, 1, 1, 0, 3)], curStore);

        // Previous stopped run: stopped early at 20s with a canceled bee at 19.5s and a 450ms bee at 0.3s
        var prevErrs = new ErrorDictionary();
        var cancelId = prevErrs.Intern(ExecutionResult.CanceledError);
        var prevBees = new List<BeeRecord>
        {
            new(10, 0, 0.3, 450, 0, ErrorDictionary.None, prevErrs),
            new(11, 0, 19.5, 500, 0, cancelId, prevErrs),
        };
        var prevRun = new HistoryRunInput(
            "run-stopped",
            "Run stopped",
            DateTimeOffset.UtcNow.AddHours(-1),
            "Stopped",
            20.0,
            0,
            IsCurrent: false,
            SetupMatches: true,
            SetupBadge: "Same setup",
            Bees: prevBees,
            FinishedCount: prevBees.Count);

        var scene = Views3DProjection.BuildRunHistory(curSnap, 0, [prevRun]);
        Assert.True(scene.HasData);
        Assert.Equal(2, scene.Lanes.Count);
        Assert.Equal(60.0, scene.MaxDurationSec);

        // Lane 0 = Current (full duration), Lane 1 = Stopped @ 0:20
        var laneCur = scene.Lanes[0];
        var lanePrev = scene.Lanes[1];
        Assert.True(laneCur.IsCurrent);
        Assert.Equal(1.0, laneCur.EndFraction, precision: 3);
        Assert.True(lanePrev.IsStopped);
        Assert.InRange(lanePrev.EndFraction, 0.30, 0.36);
        Assert.StartsWith("Stopped @", lanePrev.EndLabel);

        // Verify single bee vs group metadata and MaxMs height semantics
        var curSingle = scene.Grid[0].First(c => c.BeeCount == 1);
        Assert.True(curSingle.IsSingleBee);
        Assert.Equal(0, curSingle.BeeSequence);
        Assert.Equal(120, curSingle.MaxDurationMs);

        var curGroup = scene.Grid[0].First(c => c.BeeCount == 2);
        Assert.False(curGroup.IsSingleBee);
        Assert.Equal(900, curGroup.MaxDurationMs);
        Assert.Equal(1, curGroup.SlowCount);
        Assert.Equal(Views3DProjection.SlowRgb, curGroup.ColorRgb);

        // Stopped run's canceled bee has FailedRgb
        var canceledCell = scene.Grid[1].First(c => c.FailedCount > 0);
        Assert.Equal(1, canceledCell.CanceledCount);
        Assert.Equal(Views3DProjection.FailedRgb, canceledCell.ColorRgb);

        // JS payload reflects MaxMs heights and single vs group tooltips
        var payload = Views3DProjection.ToJsPayload(scene);
        Assert.Equal("history", payload.Kind);
        var jsSingle = payload.Cells.First(c => c.LaneIndex == 0 && c.BucketIndex == curSingle.BucketIndex);
        var jsGroup = payload.Cells.First(c => c.LaneIndex == 0 && c.BucketIndex == curGroup.BucketIndex);
        Assert.True(jsGroup.Height > jsSingle.Height);
        Assert.Contains(jsSingle.TooltipRows, r => r.Label == "Bee" && r.Value.Contains("Bee #1"));
        Assert.Contains(jsGroup.TooltipRows, r => r.Label == "Group" && r.Value.Contains("2 bees") && r.Value.Contains("longest"));
    }

    [Fact]
    public void SynthesizeFakeHistoryRuns_creates_three_deterministic_runs_including_stopped_run()
    {
        using var source = new FakeRunSource(CreateCatalog());
        var snap = source.Current;
        var entry = Views3DProjection.ResolveHistoryScenarioEntry(snap, null);
        Assert.InRange(entry, 0, snap.Count - 1);

        var synthA = Views3DProjection.SynthesizeFakeHistoryRuns(snap, entry);
        var synthB = Views3DProjection.SynthesizeFakeHistoryRuns(snap, entry);

        Assert.Equal(3, synthA.Count);
        Assert.Equal(synthA.Select(r => r.RunId), synthB.Select(r => r.RunId));
        Assert.Equal(synthA.Select(r => r.Bees.Count), synthB.Select(r => r.Bees.Count));
        Assert.Contains(synthA, r => r.State == "Stopped" && r.ElapsedSeconds < snap.Elapsed);

        var scene = Views3DProjection.BuildRunHistory(snap, entry, synthA);
        Assert.True(scene.HasData);
        Assert.Equal(4, scene.Lanes.Count);
        Assert.Contains(scene.Lanes, l => l.IsStopped && l.EndFraction < 0.6);
    }

    [Fact]
    public void MatchHistoryEntry_matches_scenario_setup_and_converts_remote_bees()
    {
        var targetCfg = new ScenarioRunConfig
        {
            ScenarioId = "user-journey",
            Label = "Checkout",
            Pattern = SpawningPattern.Linear,
            RatePerMinute = 60,
        };

        var summarySame = new RemoteRunSummary(
            "run-1", "Stopped", 45.0, DateTimeOffset.UtcNow.AddMinutes(-20), 40, 2, 1, 100, null,
            [
                new RemoteEntrySummary(0, "Other", 10, 10, 0, 0, 0, 0, 0, 30),
                new RemoteEntrySummary(1, "Checkout", 40, 40, 0, 2, 1, 1, 0, 60),
            ]);
        var matchSame = Views3DProjection.MatchHistoryEntry(targetCfg, "Checkout", summarySame, preferredEntryIndex: 0);
        Assert.NotNull(matchSame);
        Assert.Equal(1, matchSame!.Value.Entry.Entry);
        Assert.True(matchSame.Value.SetupMatches);

        var summaryDiffRate = new RemoteRunSummary(
            "run-2", "Finished", 120.0, DateTimeOffset.UtcNow.AddMinutes(-40), 200, 0, 0, 200, null,
            [new RemoteEntrySummary(0, "Checkout", 200, 200, 0, 0, 0, 0, 0, 120)]);
        var matchDiff = Views3DProjection.MatchHistoryEntry(targetCfg, "Checkout", summaryDiffRate, preferredEntryIndex: 0);
        Assert.NotNull(matchDiff);
        Assert.False(matchDiff!.Value.SetupMatches);
        Assert.Equal("120/min", matchDiff.Value.SetupBadge);

        var (records, _) = Views3DProjection.ToBeeRecords(
            [
                new RemoteBeeSummary(5, 1, 8.0, 250, 200, 500, null, 0),
                new RemoteBeeSummary(2, 1, 2.0, 900, 500, 200, "HTTP 500", 0),
            ],
            entry: 1);
        Assert.Equal(2, records.Count);
        Assert.Equal(2.0, records[0].StartedAt);
        Assert.True(records[0].Failed);
        Assert.False(records[1].Failed);
    }

    private static BeeDetail EmptyDetail(int seq) => new(seq, 0, [], () => []);
}
