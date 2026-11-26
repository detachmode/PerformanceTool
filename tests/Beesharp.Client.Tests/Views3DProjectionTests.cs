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

    [Fact]
    public void BucketCountOverride_applies_and_clamps_across_all_four_views()
    {
        using var source = new FakeRunSource(CreateCatalog());
        var snap = source.Current;
        var histEntry = Views3DProjection.ResolveHistoryScenarioEntry(snap, null);
        var prevRuns = Views3DProjection.SynthesizeFakeHistoryRuns(snap, histEntry);

        // Adaptive defaults when null
        Assert.Equal(30, Views3DProjection.BuildTerrain(snap, TerrainGrouping.Scenarios, bucketCountOverride: null).TimeBuckets);
        Assert.Equal(30, Views3DProjection.BuildSurface(snap, bucketCountOverride: null).TimeBuckets);

        // Manual overrides
        Assert.Equal(64, Views3DProjection.BuildTerrain(snap, TerrainGrouping.Scenarios, bucketCountOverride: 64).TimeBuckets);
        Assert.Equal(48, Views3DProjection.BuildSurface(snap, bucketCountOverride: 48).TimeBuckets);
        Assert.Equal(52, Views3DProjection.BuildErrorCascade(snap, ErrorCascadeSort.FirstSeen, bucketCountOverride: 52).TimeBuckets);

        // Clamping to [8, 120]
        Assert.Equal(8, Views3DProjection.BuildTerrain(snap, TerrainGrouping.Scenarios, bucketCountOverride: 2).TimeBuckets);
        Assert.Equal(120, Views3DProjection.BuildSurface(snap, bucketCountOverride: 500).TimeBuckets);
        Assert.Equal(8, Views3DProjection.BuildErrorCascade(snap, ErrorCascadeSort.FirstSeen, bucketCountOverride: 1).TimeBuckets);
        Assert.Equal(120, Views3DProjection.BuildErrorCascade(snap, ErrorCascadeSort.FirstSeen, bucketCountOverride: 999).TimeBuckets);

        // RunHistory honors maxBarsOverride (clamped) via BeeBars.GridWidth
        var histCoarse = Views3DProjection.BuildRunHistory(snap, histEntry, prevRuns, maxBarsOverride: 4); // clamped to 8
        var histFine = Views3DProjection.BuildRunHistory(snap, histEntry, prevRuns, maxBarsOverride: 500); // clamped to 120
        Assert.True(histCoarse.HasData);
        Assert.True(histFine.HasData);
        Assert.InRange(histCoarse.TimeBuckets, 1, 8);
        Assert.InRange(histFine.TimeBuckets, histCoarse.TimeBuckets + 1, 120);
    }

    [Fact]
    public void BuildErrorCascade_handles_empty_and_zero_error_runs_gracefully()
    {
        var emptyPlan = new LoadPlan();
        var emptySnap = new RunSnapshot(FakeRunState.Idle, 0, emptyPlan, [], [], new RunStore(0));
        var emptyScene = Views3DProjection.BuildErrorCascade(emptySnap, ErrorCascadeSort.FirstSeen);
        Assert.False(emptyScene.HasData);
        Assert.Empty(emptyScene.Lanes);
        Assert.Empty(emptyScene.Grid);

        // Run with only healthy bees -> 0 errors
        var plan = new LoadPlan { DurationSeconds = 20 };
        plan.Scenarios.Add(new ScenarioRunConfig { ScenarioId = "http-get", Label = "Healthy", RatePerMinute = 30 });
        var store = new RunStore(1);
        store.Complete(new BeeRecord(0, 0, 1.0, 90, 0, ErrorDictionary.None, store.Errors), EmptyDetail(0));
        store.Complete(new BeeRecord(1, 0, 5.0, 110, 0, ErrorDictionary.None, store.Errors), EmptyDetail(1));
        var okSnap = new RunSnapshot(FakeRunState.Finished, 20.0, plan, ["Healthy"], [new ScenarioView(2, 0, 0, 0, 0, 2)], store);

        var okScene = Views3DProjection.BuildErrorCascade(okSnap, ErrorCascadeSort.FirstSeen);
        Assert.False(okScene.HasData);
        Assert.Equal(0, okScene.TotalErrors);
        Assert.Equal(0, okScene.DistinctErrorCount);
        Assert.Empty(okScene.Lanes);
        Assert.Empty(okScene.Grid);
    }

    [Fact]
    public void BuildErrorCascade_orders_by_first_seen_vs_most_frequent_filters_and_preserves_metadata()
    {
        var plan = new LoadPlan { DurationSeconds = 60 };
        plan.Scenarios.Add(new ScenarioRunConfig { ScenarioId = "http-get", Label = "Auth", RatePerMinute = 60 });
        plan.Scenarios.Add(new ScenarioRunConfig { ScenarioId = "http-post", Label = "Checkout", RatePerMinute = 60 });

        var store = new RunStore(2);
        var dbErr = store.Errors.Intern("DB connection pool exhausted\n   at Pool.Acquire()");
        var longErr = store.Errors.Intern("HTTP 502 Bad Gateway upstream checkout timeout exceeded limit");
        var cancelErr = store.Errors.Intern(ExecutionResult.CanceledError);
        var blankErr = store.Errors.Intern("   ");

        // Entry 0: 1x dbErr at 2.0s (step 1), 1x blankErr at 12.0s
        store.Complete(new BeeRecord(10, 0, 2.0, 400, 0, dbErr, store.Errors, FailedStep: 1), EmptyDetail(10));
        store.Complete(new BeeRecord(11, 0, 12.0, 300, 0, blankErr, store.Errors), EmptyDetail(11));

        // Entry 1: 3x longErr starting at 5.0s (step 2), plus 1x cancelErr at 58.0s
        store.Complete(new BeeRecord(20, 1, 5.0, 800, 0, longErr, store.Errors, FailedStep: 2), EmptyDetail(20));
        store.Complete(new BeeRecord(21, 1, 6.0, 810, 0, longErr, store.Errors, FailedStep: 2), EmptyDetail(21));
        store.Complete(new BeeRecord(22, 1, 7.0, 820, 0, longErr, store.Errors, FailedStep: 2), EmptyDetail(22));
        store.Complete(new BeeRecord(23, 1, 58.0, 150, 0, cancelErr, store.Errors), EmptyDetail(23));

        var snap = new RunSnapshot(
            FakeRunState.Finished,
            60.0,
            plan,
            ["Auth", "Checkout"],
            [new ScenarioView(2, 2, 0, 0, 0, 2), new ScenarioView(4, 4, 0, 0, 0, 4)],
            store);

        // FirstSeen: dbErr (2s) -> longErr (5s) -> blankErr (12s) -> cancelErr (58s)
        var byFirst = Views3DProjection.BuildErrorCascade(snap, ErrorCascadeSort.FirstSeen, entryFilter: null, bucketCountOverride: 12);
        Assert.True(byFirst.HasData);
        Assert.Equal(6, byFirst.TotalErrors);
        Assert.Equal(4, byFirst.DistinctErrorCount);
        Assert.Equal(12, byFirst.TimeBuckets);
        Assert.Equal(4, byFirst.Lanes.Count);
        Assert.StartsWith("DB connection pool exhausted", byFirst.Lanes[0].Signature);
        Assert.Equal(2.0, byFirst.Lanes[0].FirstSeenSec);
        Assert.Equal(1, byFirst.Lanes[0].SampleStep);
        Assert.StartsWith("HTTP 502 Bad Gateway", byFirst.Lanes[1].Signature);
        Assert.Equal(3, byFirst.Lanes[1].TotalCount);
        Assert.Equal("(No error message)", byFirst.Lanes[2].Signature);
        Assert.Equal(ExecutionResult.CanceledError, byFirst.Lanes[3].Signature);

        // DisplayLabel truncated cleanly to <= 34 chars while full Signature stays intact
        Assert.True(byFirst.Lanes[0].DisplayLabel.Length <= 34);
        Assert.DoesNotContain('\n', byFirst.Lanes[0].DisplayLabel);
        Assert.True(byFirst.Lanes[1].DisplayLabel.Length <= 34);
        Assert.EndsWith("…", byFirst.Lanes[1].DisplayLabel);
        Assert.Equal("HTTP 502 Bad Gateway upstream checkout timeout exceeded limit", byFirst.Lanes[1].Signature);

        // MostFrequent: longErr (3) first, then the 1-count errors ordered by FirstSeen
        var byFreq = Views3DProjection.BuildErrorCascade(snap, ErrorCascadeSort.MostFrequent, entryFilter: null, bucketCountOverride: 12);
        Assert.Equal("HTTP 502 Bad Gateway upstream checkout timeout exceeded limit", byFreq.Lanes[0].Signature);
        Assert.Equal(3, byFreq.Lanes[0].TotalCount);

        // Cell metadata and color semantics (regular FailedRgb vs CanceledRgb)
        var longCell = byFirst.Grid[1].First(c => c.Count > 0);
        Assert.Equal(3, longCell.Count); // 5s, 6s, 7s in bucket [5s..10s) wait: 5/5=1 -> bucket 1 covers 5..10s!
        Assert.Equal(5.0, longCell.FirstOccurrenceSec);
        Assert.Equal([1], longCell.AffectedEntries);
        Assert.Equal(20, longCell.SampleBeeSequence);
        Assert.Equal(1, longCell.SampleEntry);
        Assert.Equal(2, longCell.SampleStep);
        Assert.Equal(Views3DProjection.FailedRgb, longCell.ColorRgb);

        var cancelCell = byFirst.Grid[3].First(c => c.Count > 0);
        Assert.Equal(1, cancelCell.CanceledCount);
        Assert.Equal(Views3DProjection.CanceledRgb, cancelCell.ColorRgb);

        // Scenario entry filter isolates entry 0 only
        var onlyEntry0 = Views3DProjection.BuildErrorCascade(snap, ErrorCascadeSort.FirstSeen, entryFilter: 0, bucketCountOverride: 10);
        Assert.Equal(0, onlyEntry0.ScenarioEntryFilter);
        Assert.Equal(2, onlyEntry0.TotalErrors);
        Assert.Equal(2, onlyEntry0.Lanes.Count);

        // JS payload keeps full error message in TooltipError
        var payload = Views3DProjection.ToJsPayload(byFirst);
        Assert.Equal("errors", payload.Kind);
        Assert.Contains(payload.Cells, c => c.TooltipError == "HTTP 502 Bad Gateway upstream checkout timeout exceeded limit");
    }

    [Fact]
    public void BuildErrorCascade_caps_at_top_8_distinct_signatures()
    {
        var plan = new LoadPlan { DurationSeconds = 40 };
        plan.Scenarios.Add(new ScenarioRunConfig { ScenarioId = "http-get", Label = "API", RatePerMinute = 60 });
        var store = new RunStore(1);

        for (var i = 0; i < 11; i++)
        {
            var errId = store.Errors.Intern($"Error kind #{i + 1}");
            store.Complete(new BeeRecord(i, 0, i + 1.0, 200, 0, errId, store.Errors), EmptyDetail(i));
        }

        var snap = new RunSnapshot(FakeRunState.Finished, 40.0, plan, ["API"], [new ScenarioView(11, 11, 0, 0, 0, 11)], store);
        var scene = Views3DProjection.BuildErrorCascade(snap, ErrorCascadeSort.FirstSeen);

        Assert.True(scene.HasData);
        Assert.Equal(11, scene.TotalErrors);
        Assert.Equal(11, scene.DistinctErrorCount);
        Assert.Equal(Views3DProjection.MaxErrorCascadeLanes, scene.Lanes.Count);
        Assert.Equal(Views3DProjection.MaxErrorCascadeLanes, scene.Grid.Count);
    }

    [Fact]
    public void BuildSwarmMatrix_projects_active_failed_empty_pads_modes_and_step_transitions()
    {
        var emptySnap = new RunSnapshot(FakeRunState.Idle, 0, new LoadPlan(), [], [], new RunStore(0));
        var emptySwarm = Views3DProjection.BuildSwarmMatrix(emptySnap);
        Assert.False(emptySwarm.HasData);
        Assert.Empty(emptySwarm.Lanes);

        var plan = new LoadPlan { DurationSeconds = 60 };
        plan.Scenarios.Add(new ScenarioRunConfig { ScenarioId = "user-journey", Label = "Checkout", RatePerMinute = 60 });
        plan.Scenarios.Add(new ScenarioRunConfig { ScenarioId = "http-get", Label = "Ping", RatePerMinute = 30 });

        var steps = new[]
        {
            new ScenarioSteps(["Auth", "Cart", "Pay", "Confirm"]),
            ScenarioSteps.None, // no declared steps -> gets 1 default "Run" step + 3 trailing empty pads
        };

        var store = new RunStore(2);
        var errPay = store.Errors.Intern("HTTP 504 Payment timeout");
        var errPing = store.Errors.Intern("HTTP 500 Ping failed");

        // Bee with step visits demonstrating: 0->1 (next), 1->2 (next), 2->2 (repeat), 2->1 (loop), 1->3 (skip)
        var visits = new List<RecordedStepVisit>
        {
            new(0, 0),
            new(1, 0),
            new(2, 0),
            new(2, 1),
            new(1, 1),
            new(3, 0),
        };
        var slowCalls = new List<FakeCall>
        {
            new(0, "POST /pay", 0, 500, 200, ErrorDictionary.None, false, 0, Slow: true, TypicalMs: 100, Step: 2, Rep: 0),
            new(1, "POST /pay", 550, 480, 504, errPay, false, 0, Slow: false, TypicalMs: 100, Step: 2, Rep: 1),
        };
        store.Complete(
            new BeeRecord(0, 0, 2.0, 1200, 1, errPay, store.Errors, FailedStep: 2, FailedRep: 1),
            new BeeDetail(0, 0, slowCalls, () => [], visits));

        // Ping failure (entry 1 without declared steps -> lands on step 0 "Run")
        store.Complete(
            new BeeRecord(1, 1, 3.0, 200, 0, errPing, store.Errors, FailedStep: -1),
            new BeeDetail(1, 1, [new FakeCall(0, "GET /ping", 0, 200, 500, errPing, false, 0)], () => []));

        var running = new IReadOnlyList<RunningBee>[]
        {
            [
                new RunningBee(2, 0, 8.0, [], CurrentStep: 2),
                new RunningBee(3, 0, 8.5, [], CurrentStep: 2),
                new RunningBee(4, 0, 9.0, [], CurrentStep: 0),
            ],
            [
                new RunningBee(5, 1, 9.2, [], CurrentStep: -1),
            ],
        };

        var snap = new RunSnapshot(
            FakeRunState.Running,
            10.0,
            plan,
            ["Checkout", "Ping"],
            [new ScenarioView(1, 1, 0, 1, 3, 4), new ScenarioView(1, 1, 0, 0, 1, 2)],
            store,
            running,
            Steps: steps);

        var stacked = Views3DProjection.BuildSwarmMatrix(snap, SwarmDisplayMode.Stacked);
        Assert.True(stacked.HasData);
        Assert.True(stacked.IsRunning);
        Assert.Equal(4, stacked.MaxSteps);
        Assert.Equal(2, stacked.Lanes.Count);

        // Lane 0 (Checkout): 4 declared steps, Lane 1 (Ping): 1 step ("Run") + 3 trailing empty pads
        Assert.Equal(4, stacked.Lanes[0].DeclaredSteps);
        Assert.Equal(1, stacked.Lanes[1].DeclaredSteps);
        Assert.False(stacked.Grid[1][0].IsEmptyPad);
        Assert.Equal("Run", stacked.Grid[1][0].StepName);
        Assert.Equal(1, stacked.Grid[1][0].ActiveCount);
        Assert.Equal(1, stacked.Grid[1][0].FailedCount);
        Assert.True(stacked.Grid[1][1].IsEmptyPad);
        Assert.True(stacked.Grid[1][2].IsEmptyPad);
        Assert.True(stacked.Grid[1][3].IsEmptyPad);

        // Checkout Step 3 (index 2): 2 active bees, 1 failed bee, slow ratio >= 2 -> amber active color, 1 repeat, 1 loop
        var payCell = stacked.Grid[0][2];
        Assert.Equal(2, payCell.ActiveCount);
        Assert.Equal(1, payCell.FailedCount);
        Assert.Equal(1, payCell.SelfRepeatCount);
        Assert.Equal(1, payCell.BackLoopCount);
        Assert.Equal(Views3DProjection.SlowRgb, payCell.ActiveColorRgb);
        Assert.Equal("HTTP 504 Payment timeout", payCell.SampleError);

        // Transition arcs: next, repeat, loop, skip
        Assert.Contains(stacked.Arcs, a => a.ScenarioIndex == 0 && a.FromStep == 0 && a.ToStep == 1 && a.Kind == "next");
        Assert.Contains(stacked.Arcs, a => a.ScenarioIndex == 0 && a.FromStep == 2 && a.ToStep == 2 && a.Kind == "repeat");
        Assert.Contains(stacked.Arcs, a => a.ScenarioIndex == 0 && a.FromStep == 2 && a.ToStep == 1 && a.Kind == "loop");
        Assert.Contains(stacked.Arcs, a => a.ScenarioIndex == 0 && a.FromStep == 1 && a.ToStep == 3 && a.Kind == "skip");
        Assert.Equal("1 → 2 → 3 → 3 → 2 → 4", stacked.Lanes[0].SamplePath);

        // Stacked JS payload has primary (active) + secondary (failed) on Pay cell and IsEmptyPad on trailing Ping cells
        var jsStacked = Views3DProjection.ToJsPayload(stacked);
        Assert.Equal("swarm", jsStacked.Kind);
        Assert.True(jsStacked.IsRunning);
        var jsPay = jsStacked.Cells.First(c => c.LaneIndex == 0 && c.BucketIndex == 2);
        Assert.True(jsPay.Height > 0);
        Assert.True(jsPay.SecondaryHeight > 0);
        Assert.Equal(Views3DProjection.FailedRgb, jsPay.SecondaryColor);
        Assert.Equal(3, jsStacked.Cells.Count(c => c.LaneIndex == 1 && c.IsEmptyPad));

        // ActiveOnly vs FailedOnly modes
        var jsActiveOnly = Views3DProjection.ToJsPayload(Views3DProjection.BuildSwarmMatrix(snap, SwarmDisplayMode.ActiveOnly));
        var jsPayActive = jsActiveOnly.Cells.First(c => c.LaneIndex == 0 && c.BucketIndex == 2);
        Assert.True(jsPayActive.Height > 0);
        Assert.Equal(0, jsPayActive.SecondaryHeight);
        Assert.Equal(Views3DProjection.SlowRgb, jsPayActive.Color);

        var jsFailedOnly = Views3DProjection.ToJsPayload(Views3DProjection.BuildSwarmMatrix(snap, SwarmDisplayMode.FailedOnly));
        var jsPayFailed = jsFailedOnly.Cells.First(c => c.LaneIndex == 0 && c.BucketIndex == 2);
        Assert.True(jsPayFailed.Height > 0);
        Assert.Equal(0, jsPayFailed.SecondaryHeight);
        Assert.Equal(Views3DProjection.FailedRgb, jsPayFailed.Color);
        var jsAuthFailed = jsFailedOnly.Cells.First(c => c.LaneIndex == 0 && c.BucketIndex == 0);
        Assert.Equal(0, jsAuthFailed.Height);
    }

    [Fact]
    public void BuildCallsMatrix_projects_shared_exclusive_and_tagged_endpoints_empty_pads_and_metrics()
    {
        var emptySnap = new RunSnapshot(FakeRunState.Idle, 0, new LoadPlan(), [], [], new RunStore(0));
        var emptyCalls = Views3DProjection.BuildCallsMatrix(emptySnap);
        Assert.False(emptyCalls.HasData);
        Assert.Empty(emptyCalls.Lanes);
        Assert.Empty(emptyCalls.Columns);

        var plan = new LoadPlan { DurationSeconds = 60 };
        plan.Scenarios.Add(new ScenarioRunConfig { ScenarioId = "demo-shop", Label = "Checkout", RatePerMinute = 60 });
        plan.Scenarios.Add(new ScenarioRunConfig { ScenarioId = "demo-shop", Label = "Express VIP", RatePerMinute = 30 });

        var store = new RunStore(2);
        var errPay = store.Errors.Intern("HTTP 504 Payment timeout");

        // Entry 0 (Checkout): calls GET /api/products, tagged POST /api/inventory/reserve [Checkout], and named Order · Standard (slow + failed)
        var checkoutCalls = new List<FakeCall>
        {
            new(0, "GET /api/products?q=honey", 0, 40, 200, ErrorDictionary.None, false, 0, Slow: false, TypicalMs: 40, Errors: store.Errors, Endpoint: "GET /api/products"),
            new(1, "POST /api/inventory/reserve [Checkout]", 50, 90, 200, ErrorDictionary.None, false, 0, Slow: false, TypicalMs: 85, Errors: store.Errors, Endpoint: "POST /api/inventory/reserve [Checkout]"),
            new(2, "Order · Standard", 150, 420, 200, ErrorDictionary.None, false, 0, Slow: true, TypicalMs: 120, Errors: store.Errors, Endpoint: "Order · Standard"),
        };
        var checkoutCalls2 = new List<FakeCall>
        {
            new(0, "GET /api/products?q=tea", 0, 44, 200, ErrorDictionary.None, false, 0, Slow: false, TypicalMs: 40, Errors: store.Errors, Endpoint: "GET /api/products"),
            new(1, "POST /api/inventory/reserve [Checkout]", 50, 80, 200, ErrorDictionary.None, false, 0, Slow: false, TypicalMs: 85, Errors: store.Errors, Endpoint: "POST /api/inventory/reserve [Checkout]"),
            new(2, "Order · Standard", 150, 680, 504, errPay, false, 0, Slow: false, TypicalMs: 120, Errors: store.Errors, Endpoint: "Order · Standard"),
        };
        store.Complete(new BeeRecord(0, 0, 1.0, 550, 1, ErrorDictionary.None, store.Errors), new BeeDetail(0, 0, checkoutCalls, () => []));
        store.Complete(new BeeRecord(1, 0, 2.0, 804, 0, errPay, store.Errors), new BeeDetail(1, 0, checkoutCalls2, () => []));

        // Entry 1 (Express VIP): shares GET /api/products, has its own tagged POST /api/inventory/reserve [ExpressVip] and Order · VIP Express (fast)
        var vipCalls = new List<FakeCall>
        {
            new(0, "GET /api/products?q=honey", 0, 35, 200, ErrorDictionary.None, false, 0, Slow: false, TypicalMs: 40, Errors: store.Errors, Endpoint: "GET /api/products"),
            new(1, "POST /api/inventory/reserve [ExpressVip]", 40, 105, 200, ErrorDictionary.None, false, 0, Slow: false, TypicalMs: 110, Errors: store.Errors, Endpoint: "POST /api/inventory/reserve [ExpressVip]"),
            new(2, "Order · VIP Express", 170, 110, 200, ErrorDictionary.None, false, 0, Slow: false, TypicalMs: 100, Errors: store.Errors, Endpoint: "Order · VIP Express"),
        };
        store.Complete(new BeeRecord(2, 1, 1.5, 265, 0, ErrorDictionary.None, store.Errors), new BeeDetail(2, 1, vipCalls, () => []));

        var snap = new RunSnapshot(
            FakeRunState.Finished,
            10.0,
            plan,
            ["Checkout", "Express VIP"],
            [new ScenarioView(2, 1, 1, 1, 0, 2), new ScenarioView(1, 0, 0, 0, 0, 1)],
            store);

        var p95Scene = Views3DProjection.BuildCallsMatrix(snap, CallsMatrixMetric.P95);
        Assert.True(p95Scene.HasData);
        Assert.Equal(2, p95Scene.Lanes.Count);
        Assert.Equal(5, p95Scene.Columns.Count); // 5 distinct endpoints (tagged endpoints stay separate!)
        Assert.Equal(9, p95Scene.TotalCalls);

        // Ordered by worst p95 descending: "Order · Standard" (680ms) must be first!
        Assert.Equal("Order · Standard", p95Scene.Columns[0].Endpoint);
        Assert.Contains(p95Scene.Columns, c => c.Endpoint == "POST /api/inventory/reserve [Checkout]");
        Assert.Contains(p95Scene.Columns, c => c.Endpoint == "POST /api/inventory/reserve [ExpressVip]");

        // Shared GET /api/products has non-empty cells in BOTH scenarios
        var sharedColIdx = p95Scene.Columns.ToList().FindIndex(c => c.Endpoint == "GET /api/products");
        Assert.False(p95Scene.Grid[0][sharedColIdx].IsEmptyPad);
        Assert.False(p95Scene.Grid[1][sharedColIdx].IsEmptyPad);
        Assert.Equal(2, p95Scene.Grid[0][sharedColIdx].CallCount);
        Assert.Equal(1, p95Scene.Grid[1][sharedColIdx].CallCount);

        // Exclusive tagged endpoint: Checkout has empty pad for [ExpressVip], Express VIP has Bar
        var vipReserveIdx = p95Scene.Columns.ToList().FindIndex(c => c.Endpoint == "POST /api/inventory/reserve [ExpressVip]");
        Assert.True(p95Scene.Grid[0][vipReserveIdx].IsEmptyPad);
        Assert.False(p95Scene.Grid[1][vipReserveIdx].IsEmptyPad);
        Assert.Equal(Views3DProjection.GoodRgb, p95Scene.Grid[1][vipReserveIdx].ColorRgb);

        // Order · Standard failed on Checkout -> red color, sample error & bee sequence captured, empty pad on VIP
        var stdOrderCell = p95Scene.Grid[0][0];
        Assert.False(stdOrderCell.IsEmptyPad);
        Assert.Equal(1, stdOrderCell.FailedCount);
        Assert.Equal(1, stdOrderCell.SlowCount);
        Assert.Equal("HTTP 504 Payment timeout", stdOrderCell.SampleError);
        Assert.Equal(1, stdOrderCell.SampleBeeSequence);
        Assert.Equal(Views3DProjection.FailedRgb, stdOrderCell.ColorRgb);
        Assert.True(p95Scene.Grid[1][0].IsEmptyPad);

        // Metric scaling switches height between P95, Avg, and Max
        var avgScene = Views3DProjection.BuildCallsMatrix(snap, CallsMatrixMetric.Avg);
        var maxScene = Views3DProjection.BuildCallsMatrix(snap, CallsMatrixMetric.Max);
        Assert.Equal(420, avgScene.Grid[0][0].AvgMs);
        Assert.Equal(680, maxScene.Grid[0][0].MaxMs);

        var jsP95 = Views3DProjection.ToJsPayload(p95Scene);
        Assert.Equal("calls", jsP95.Kind);
        Assert.Equal(5, jsP95.TimeBuckets);
        Assert.Contains(jsP95.Cells, c => c.IsEmptyPad);
        var jsStdOrder = jsP95.Cells.First(c => c.LaneIndex == 0 && c.BucketIndex == 0);
        Assert.False(jsStdOrder.IsEmptyPad);
        Assert.True(jsStdOrder.AlertBadge);
        Assert.Equal(Views3DProjection.FailedRgb, jsStdOrder.Color);
    }

    private static BeeDetail EmptyDetail(int seq) => new(seq, 0, [], () => []);
}
