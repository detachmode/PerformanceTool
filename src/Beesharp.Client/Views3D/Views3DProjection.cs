using Beesharp.Client.Chart;
using Beesharp.Client.Data;
using Beesharp.Client.Runs;
using Beesharp.Client.Shared;
using Beesharp.Services;

namespace Beesharp.Client.Views3D;

/// <summary>
/// Pure C# projection of <see cref="RunSnapshot"/> and <see cref="RunStore"/> into the two 3D views:
/// Graph 1 (Scenario &amp; Step Terrain) and Graph 3 (Latency Distribution Surface).
/// Keeps colour semantics aligned with <see cref="SlowCallRule"/> and <c>app.css</c>.
/// </summary>
public static class Views3DProjection
{
    public static readonly int[] GoodRgb = [52, 211, 153];     // #34d399
    public static readonly int[] SlowRgb = [251, 191, 36];     // #fbbf24
    public static readonly int[] FailedRgb = [240, 82, 82];    // #f05252
    public static readonly int[] CanceledRgb = [248, 128, 86]; // #f88056
    public static readonly int[] RunningRgb = [91, 155, 240];  // #5b9bf0

    public const int MinBucketCount = 8;
    public const int MaxBucketCount = 120;
    public const int DefaultHistoryMaxBars = 64;
    public const int MaxErrorCascadeLanes = 8;
    private const int MaxSwarmDetailedBees = 250;

    private static readonly double[] NiceLadderMs =
    [
        10, 25, 50, 75, 100, 150, 250, 400, 600,
        1000, 1500, 2500, 4000, 6000, 10000, 15000, 25000, 40000, 60000, 100000
    ];

    private static readonly double[] DefaultCutoffsMs = [25, 60, 120, 250, 500, 1000];

    /// <summary>Adaptive bucket count (roughly 20–36 for real runs; gracefully handles short runs).</summary>
    public static int ChooseBucketCount(double spanSeconds)
    {
        if (!(spanSeconds > 0)) return 0;
        if (spanSeconds <= 20) return Math.Clamp((int)Math.Ceiling(spanSeconds), 8, 20);
        if (spanSeconds <= 40) return Math.Clamp((int)Math.Round(spanSeconds), 20, 32);
        if (spanSeconds <= 180) return 24;
        if (spanSeconds <= 2400) return 30;
        return 36;
    }

    /// <summary>Clamps a manual grid bucket/bar override to [<see cref="MinBucketCount"/>, <see cref="MaxBucketCount"/>].</summary>
    public static int ClampBucketCount(int count) => Math.Clamp(count, MinBucketCount, MaxBucketCount);

    private static int ResolveBucketCount(double spanSeconds, int? bucketCountOverride)
    {
        if (!(spanSeconds > 0)) return 0;
        return bucketCountOverride is { } v ? ClampBucketCount(v) : ChooseBucketCount(spanSeconds);
    }

    /// <summary>Coral red for failed bees, shifting to a slightly brighter red-amber tint when canceled bees are present.</summary>
    public static int[] ErrorCascadeRgb(int count, int canceledCount)
    {
        if (count <= 0 || canceledCount <= 0) return FailedRgb;
        if (canceledCount >= count) return CanceledRgb;
        var t = canceledCount / (double)count;
        return
        [
            (int)Math.Round(FailedRgb[0] + (CanceledRgb[0] - FailedRgb[0]) * t),
            (int)Math.Round(FailedRgb[1] + (CanceledRgb[1] - FailedRgb[1]) * t),
            (int)Math.Round(FailedRgb[2] + (CanceledRgb[2] - FailedRgb[2]) * t),
        ];
    }

    /// <summary>Beesharp's exact outcome colour: red if failed, amber if slow or ratio &gt;= 2×, else graded green-to-amber via <see cref="SlowCallRule.Step"/>.</summary>
    public static int[] OutcomeRgb(int failedCount, int slowCount, double ratio, int inFlightOnlyCount = 0)
    {
        if (failedCount > 0) return FailedRgb;
        if (slowCount > 0 || ratio >= SlowCallRule.Factor) return SlowRgb;
        if (inFlightOnlyCount > 0) return RunningRgb;
        return StepRgb(SlowCallRule.Step(ratio));
    }

    /// <summary>Graded green-to-amber RGB for a <see cref="SlowCallRule.Step"/> value (<c>0..MaxStep</c>).</summary>
    public static int[] StepRgb(int step)
    {
        if (step <= 0) return GoodRgb;
        if (step >= SlowCallRule.MaxStep) return SlowRgb;
        var t = step / (double)SlowCallRule.MaxStep;
        return
        [
            (int)Math.Round(GoodRgb[0] + (SlowRgb[0] - GoodRgb[0]) * t),
            (int)Math.Round(GoodRgb[1] + (SlowRgb[1] - GoodRgb[1]) * t),
            (int)Math.Round(GoodRgb[2] + (SlowRgb[2] - GoodRgb[2]) * t),
        ];
    }

    /// <summary>Exact <see cref="BeeBar"/> outcome colour: red if failed, amber if slow, blue if running-only, else graded via <see cref="BeeBar.Step"/>.</summary>
    public static int[] BeeBarRgb(in BeeBar bar)
    {
        if (bar.Failed > 0) return FailedRgb;
        if (bar.Slow > 0) return SlowRgb;
        if (bar.Count == 0 && bar.Running > 0) return RunningRgb;
        return StepRgb(bar.Step);
    }

    /// <summary>Returns the first scenario entry index that has declared steps (preferring one with failures or slow bees), or -1.</summary>
    public static int ResolveStepScenarioEntry(RunSnapshot snapshot, int? requestedEntry)
    {
        if (requestedEntry is { } req && req >= 0 && req < snapshot.Count && snapshot.StepsOf(req).Any)
            return req;

        var best = -1;
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (!snapshot.StepsOf(i).Any) continue;
            if (best < 0) { best = i; continue; }
            var cur = snapshot.Scenarios[i];
            var prev = snapshot.Scenarios[best];
            if (cur.Failed > prev.Failed || (cur.Failed == prev.Failed && cur.SlowBees > prev.SlowBees))
                best = i;
        }
        return best;
    }

    /// <summary>Projects Graph 1: Scenario &amp; Step Terrain.</summary>
    public static TerrainScene BuildTerrain(
        RunSnapshot snapshot,
        TerrainGrouping grouping,
        int? scenarioEntry = null,
        int? bucketCountOverride = null)
    {
        if (snapshot.Count == 0)
            return new TerrainScene(grouping, null, [], [], 0, 0, 0, []);

        var spanSec = ComputeRunSpanSeconds(snapshot, grouping == TerrainGrouping.Steps ? scenarioEntry : null);
        var buckets = ResolveBucketCount(spanSec, bucketCountOverride);
        if (buckets == 0)
            return new TerrainScene(grouping, scenarioEntry, [], [], 0, 0, 0, []);

        var ticks = BuildTimeTicks(spanSec);
        return grouping == TerrainGrouping.Steps
            ? BuildStepTerrain(snapshot, scenarioEntry, spanSec, buckets, ticks)
            : BuildScenarioTerrain(snapshot, spanSec, buckets, ticks);
    }

    /// <summary>Projects Graph 2: Latency Distribution Surface.</summary>
    public static SurfaceScene BuildSurface(
        RunSnapshot snapshot,
        int? entryFilter = null,
        int? bucketCountOverride = null)
    {
        if (snapshot.Count == 0)
            return new SurfaceScene(entryFilter, BuildLatencyBands([], 0), [], 0, 0, 0, [], 0);

        if (entryFilter is { } ef && (ef < 0 || ef >= snapshot.Count))
            entryFilter = null;

        var spanSec = ComputeRunSpanSeconds(snapshot, entryFilter);
        var buckets = ResolveBucketCount(spanSec, bucketCountOverride);
        if (buckets == 0)
            return new SurfaceScene(entryFilter, BuildLatencyBands([], 0), [], 0, 0, 0, [], 0);

        var startEntry = entryFilter ?? 0;
        var endEntry = entryFilter ?? snapshot.Count - 1;

        var durations = new List<double>();
        for (var e = startEntry; e <= endEntry; e++)
        {
            var bees = snapshot.Store.Bees(e);
            var count = FinishedCount(snapshot, e, bees.Count);
            for (var i = 0; i < count; i++)
                durations.Add(bees[i].DurationMs);
        }

        var typicalMs = Median(durations);
        var bands = BuildLatencyBands(durations, typicalMs);
        var bucketWidth = spanSec / buckets;

        var acc = new SurfaceAccumulator[bands.Count, buckets];
        for (var b = 0; b < bands.Count; b++)
            for (var t = 0; t < buckets; t++)
                acc[b, t] = new SurfaceAccumulator();

        for (var e = startEntry; e <= endEntry; e++)
        {
            var bees = snapshot.Store.Bees(e);
            var count = FinishedCount(snapshot, e, bees.Count);
            for (var i = 0; i < count; i++)
            {
                var bee = bees[i];
                var bIdx = FindBandIndex(bands, bee.DurationMs);
                var tIdx = BucketIndex(bee.StartedAt, bucketWidth, buckets);
                acc[bIdx, tIdx].Add(bee);
            }
        }

        var grid = new List<IReadOnlyList<SurfaceCell>>(bands.Count);
        var maxCount = 0;
        for (var b = 0; b < bands.Count; b++)
        {
            var band = bands[b];
            var row = new List<SurfaceCell>(buckets);
            for (var t = 0; t < buckets; t++)
            {
                var a = acc[b, t];
                var fromSec = t * bucketWidth;
                var toSec = (t + 1) * bucketWidth;
                var timeLabel = $"{Format.Time(fromSec)}–{Format.Time(toSec)}";
                if (a.Count > maxCount) maxCount = a.Count;

                var avgMs = a.Count > 0 ? a.DurationSum / a.Count : 0;
                var ratio = Math.Max(a.MaxNonFailedRatio, typicalMs > 0 && a.Count > 0 ? SlowCallRule.Ratio(avgMs, typicalMs) : 1.0);
                var color = a.Count > 0 ? OutcomeRgb(a.FailedCount, a.SlowCount, ratio) : GoodRgb;

                row.Add(new SurfaceCell(
                    b,
                    band.Label,
                    band.MinMs,
                    band.MaxMs,
                    t,
                    fromSec,
                    toSec,
                    timeLabel,
                    a.Count,
                    a.SlowCount,
                    a.FailedCount,
                    avgMs,
                    a.MaxDurationMs,
                    a.SampleError,
                    entryFilter,
                    a.DominantEntry(),
                    a.SampleBeeSequence,
                    color));
            }
            grid.Add(row);
        }

        return new SurfaceScene(entryFilter, bands, grid, buckets, spanSec, maxCount, BuildTimeTicks(spanSec), typicalMs);
    }

    /// <summary>Returns the scenario entry index to use for 3. Run History (preferring most failures, then most bees, else 0), or -1.</summary>
    public static int ResolveHistoryScenarioEntry(RunSnapshot snapshot, int? requestedEntry)
    {
        if (requestedEntry is { } req && req >= 0 && req < snapshot.Count)
            return req;
        if (snapshot.Count == 0) return -1;

        var best = 0;
        for (var i = 1; i < snapshot.Count; i++)
        {
            var cur = i < snapshot.Scenarios.Length ? snapshot.Scenarios[i] : new ScenarioView(0, 0, 0, 0, 0, 0);
            var prev = best < snapshot.Scenarios.Length ? snapshot.Scenarios[best] : new ScenarioView(0, 0, 0, 0, 0, 0);
            if (cur.Failed > prev.Failed || (cur.Failed == prev.Failed && cur.Finished > prev.Finished))
                best = i;
        }
        return best;
    }

    /// <summary>Finds a scenario entry in a stored run summary matching <paramref name="targetCfg"/> / <paramref name="targetLabel"/>.</summary>
    public static (RemoteEntrySummary Entry, bool SetupMatches, string SetupBadge)? MatchHistoryEntry(
        ScenarioRunConfig targetCfg,
        string targetLabel,
        RemoteRunSummary candidateSummary,
        int? preferredEntryIndex = null)
    {
        if (candidateSummary.Entries.Count == 0) return null;

        RemoteEntrySummary? best = null;
        var bestSetupMatches = false;

        foreach (var e in candidateSummary.Entries)
        {
            if (!ScenarioIdentityMatches(targetCfg, targetLabel, e)) continue;
            var setupOk = SetupMatches(targetCfg, e);
            if (best is null
                || (setupOk && !bestSetupMatches)
                || (setupOk == bestSetupMatches && preferredEntryIndex is { } pref && e.Entry == pref && best.Entry != pref))
            {
                best = e;
                bestSetupMatches = setupOk;
            }
        }

        if (best is null) return null;
        var badge = bestSetupMatches
            ? "Same setup"
            : best.Pattern == SpawningPattern.Bursts && best.BeesPerBurst is > 0 && best.BurstsPerMinute is > 0
                ? $"{best.BeesPerBurst} × {Format.Rate(best.BurstsPerMinute.Value)}/min"
                : $"{Format.Rate(best.BeesPerMinute)}/min";
        return (best, bestSetupMatches, badge);
    }

    /// <summary>Converts compact stored bees from <c>/api/runs/{runId}/bees</c> into sorted <see cref="BeeRecord"/>s for <see cref="BeeBars"/>.</summary>
    public static (IReadOnlyList<BeeRecord> Bees, ErrorDictionary Errors) ToBeeRecords(
        IReadOnlyList<RemoteBeeSummary> items,
        int entry,
        double fallbackTypicalMs = 0)
    {
        var errors = new ErrorDictionary();
        if (items.Count == 0) return ([], errors);

        var ordered = items.OrderBy(b => b.StartedAt).ThenBy(b => b.Sequence).ToList();
        var okDurations = new List<double>(ordered.Count);
        foreach (var b in ordered)
        {
            if (b.Error is null && b.SlowCalls == 0)
                okDurations.Add(b.DurationMs);
        }

        var typicalMs = okDurations.Count >= SlowCallRule.MinSamples
            ? Median(okDurations)
            : (fallbackTypicalMs > 0 ? fallbackTypicalMs : Median(okDurations));

        var records = new List<BeeRecord>(ordered.Count);
        foreach (var b in ordered)
        {
            var errId = b.Error is null ? ErrorDictionary.None : errors.Intern(b.Error);
            byte ratioCode = 0;
            if (b.Error is null && b.SlowCalls == 0 && typicalMs > 0)
            {
                var ratio = Math.Min(SlowCallRule.Factor - 0.01, SlowCallRule.Ratio(b.DurationMs, typicalMs));
                ratioCode = SlowCallRule.Encode(ratio);
            }
            records.Add(new BeeRecord(b.Sequence, entry, b.StartedAt, b.DurationMs, b.SlowCalls, errId, errors, ratioCode, b.FailedStep, b.FailedRep));
        }

        return (records, errors);
    }

    /// <summary>Synthesizes 3 deterministic previous runs for Fake mode derived from the selected scenario's current bees.</summary>
    public static IReadOnlyList<HistoryRunInput> SynthesizeFakeHistoryRuns(RunSnapshot snapshot, int entry)
    {
        if (entry < 0 || entry >= snapshot.Count) return [];

        var srcBees = snapshot.Store.Bees(entry);
        var finished = FinishedCount(snapshot, entry, srcBees.Count);
        if (finished <= 0) return [];

        var ordered = new List<BeeRecord>(finished);
        var okDurations = new List<double>(finished);
        var maxStarted = 0.0;
        for (var i = 0; i < finished; i++)
        {
            var b = srcBees[i];
            ordered.Add(b);
            if (b.StartedAt > maxStarted) maxStarted = b.StartedAt;
            if (!b.Failed && b.SlowCalls == 0) okDurations.Add(b.DurationMs);
        }
        ordered.Sort((a, b) =>
        {
            var c = a.StartedAt.CompareTo(b.StartedAt);
            return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
        });

        var spanSec = Math.Max(1.0, Math.Max(snapshot.Elapsed, maxStarted));
        var baseTypicalMs = Math.Max(20.0, Median(okDurations));
        var anchorTime = new DateTimeOffset(2026, 1, 15, 14, 0, 0, TimeSpan.Zero);

        // 1) Faster run (-12% faster, full duration, fewer slow/failed bees)
        var fastErrors = new ErrorDictionary();
        var fastBees = new List<BeeRecord>(finished);
        var fastSlow = 0;
        var fastFailed = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var b = ordered[i];
            var jitter = 0.85 + ((DeterministicHash(entry, 1, i) % 11) * 0.006); // 0.85..0.91
            var dur = Math.Max(15.0, Math.Round(b.DurationMs * jitter, 1));
            var keepFail = b.Failed && (DeterministicHash(entry, 1, i + 17) % 2 == 0);
            var errId = keepFail ? fastErrors.Intern(b.Error ?? "Request failed") : ErrorDictionary.None;
            var slowCalls = !keepFail && b.SlowCalls > 0 && (DeterministicHash(entry, 1, i + 31) % 3 == 0) ? b.SlowCalls : 0;
            if (keepFail) fastFailed++;
            else if (slowCalls > 0) fastSlow++;
            var ratio = !keepFail && slowCalls == 0
                ? Math.Min(SlowCallRule.Factor - 0.01, SlowCallRule.Ratio(dur, baseTypicalMs))
                : 1.0;
            fastBees.Add(new BeeRecord(b.Sequence, entry, b.StartedAt, dur, slowCalls, errId, fastErrors,
                keepFail || slowCalls > 0 ? (byte)0 : SlowCallRule.Encode(ratio),
                keepFail ? b.FailedStep : -1, keepFail ? b.FailedRep : -1));
        }

        // 2) Manually stopped early run (~47% of duration, with canceled bees at cutoff)
        var stopSec = Math.Max(1.0, Math.Round(spanSec * 0.47, 1));
        var cancelWindowStart = Math.Max(0, stopSec - Math.Max(1.0, stopSec * 0.03));
        var stopErrors = new ErrorDictionary();
        var stopBees = new List<BeeRecord>();
        var stopSlow = 0;
        var stopFailed = 0;
        var canceledAdded = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var b = ordered[i];
            if (b.StartedAt >= stopSec) break;
            var inCancelTail = b.StartedAt >= cancelWindowStart && canceledAdded < 3 && (b.StartedAt + b.DurationMs / 1000.0 >= stopSec || i >= ordered.Count - 1 || ordered[i + 1].StartedAt >= stopSec);
            if (inCancelTail)
            {
                canceledAdded++;
                stopFailed++;
                var dur = Math.Clamp((stopSec - b.StartedAt) * 1000.0, 45.0, Math.Max(45.0, b.DurationMs));
                var errId = stopErrors.Intern(ExecutionResult.CanceledError);
                stopBees.Add(new BeeRecord(b.Sequence, entry, b.StartedAt, dur, 0, errId, stopErrors, 0, b.FailedStep, b.FailedRep));
                continue;
            }
            var errId2 = b.Failed ? stopErrors.Intern(b.Error ?? "Request failed") : ErrorDictionary.None;
            if (b.Failed) stopFailed++;
            else if (b.SlowCalls > 0) stopSlow++;
            stopBees.Add(new BeeRecord(b.Sequence, entry, b.StartedAt, b.DurationMs, b.SlowCalls, errId2, stopErrors, b.RatioCode, b.FailedStep, b.FailedRep));
        }
        if (stopBees.Count == 0 && ordered.Count > 0)
        {
            var first = ordered[0];
            var errId = stopErrors.Intern(ExecutionResult.CanceledError);
            stopFailed = 1;
            stopBees.Add(new BeeRecord(first.Sequence, entry, Math.Min(first.StartedAt, stopSec * 0.5), 120, 0, errId, stopErrors));
        }

        // 3) Baseline run (+12% slower with a mid-run latency bump)
        var baseErrors = new ErrorDictionary();
        var baseBees = new List<BeeRecord>(finished);
        var baseSlow = 0;
        var baseFailed = 0;
        var bumpFrom = spanSec * 0.55;
        var bumpTo = spanSec * 0.68;
        for (var i = 0; i < ordered.Count; i++)
        {
            var b = ordered[i];
            var inBump = b.StartedAt >= bumpFrom && b.StartedAt <= bumpTo;
            var scale = inBump ? 1.48 : 1.12;
            var dur = Math.Round(b.DurationMs * scale, 1);
            var extraFail = inBump && !b.Failed && (DeterministicHash(entry, 3, i) % 19 == 0);
            var failed = b.Failed || extraFail;
            var errId = failed ? baseErrors.Intern(b.Error ?? "HTTP 504 Gateway Timeout") : ErrorDictionary.None;
            var slowCalls = !failed && (b.SlowCalls > 0 || dur > baseTypicalMs * SlowCallRule.Factor) ? Math.Max(1, b.SlowCalls) : 0;
            if (failed) baseFailed++;
            else if (slowCalls > 0) baseSlow++;
            var ratio = !failed && slowCalls == 0
                ? Math.Min(SlowCallRule.Factor - 0.01, SlowCallRule.Ratio(dur, baseTypicalMs))
                : 1.0;
            baseBees.Add(new BeeRecord(b.Sequence, entry, b.StartedAt, dur, slowCalls, errId, baseErrors,
                failed || slowCalls > 0 ? (byte)0 : SlowCallRule.Encode(ratio),
                failed ? b.FailedStep : -1, failed ? b.FailedRep : -1));
        }

        return
        [
            new HistoryRunInput($"fake-prev-1-e{entry}", "Prev #1 · -12% faster", anchorTime.AddHours(-2), "Finished", spanSec, entry, false, true, "Same setup", fastBees, fastBees.Count, null, fastBees.Count, fastFailed, fastSlow),
            new HistoryRunInput($"fake-prev-2-e{entry}", $"Prev #2 · Stopped @ {Format.Time(stopSec)}", anchorTime.AddHours(-5), "Stopped", stopSec, entry, false, true, "Same setup", stopBees, stopBees.Count, null, stopBees.Count, stopFailed, stopSlow),
            new HistoryRunInput($"fake-prev-3-e{entry}", "Prev #3 · Baseline", anchorTime.AddDays(-1), "Finished", spanSec, entry, false, true, "Same setup", baseBees, baseBees.Count, null, baseBees.Count, baseFailed, baseSlow),
        ];
    }

    /// <summary>
    /// Projects Tab 3: Run History comparison for one scenario across the active run and up to 5 previous runs,
    /// using <see cref="BeeBars"/>'s shared grid bucketing and <see cref="BeeBar.MaxMs"/> height semantics per lane.
    /// </summary>
    public static RunHistoryScene BuildRunHistory(
        RunSnapshot currentSnapshot,
        int entry,
        IReadOnlyList<HistoryRunInput> previousRuns,
        string? currentRunId = null,
        DateTimeOffset? currentStartedUtc = null,
        int? maxBarsOverride = null)
    {
        if (currentSnapshot.Count == 0 || entry < 0 || entry >= currentSnapshot.Count)
            return new RunHistoryScene(-1, "", "", [], [], 0, 0, 0, 0, [], "No scenario selected.");

        var maxBars = maxBarsOverride is { } ov ? ClampBucketCount(ov) : DefaultHistoryMaxBars;

        var cfg = currentSnapshot.Plan.Scenarios[entry];
        var scnLabel = entry < currentSnapshot.Labels.Count && !string.IsNullOrWhiteSpace(currentSnapshot.Labels[entry])
            ? currentSnapshot.Labels[entry]
            : cfg.ScenarioId;
        var setupText = Format.Shape(cfg) is { Length: > 0 } shape
            ? $"{shape} · {Format.Rate(cfg.EffectiveRatePerMinute())}/min"
            : $"{Format.Rate(cfg.EffectiveRatePerMinute())}/min";

        var currentBees = currentSnapshot.Store.Bees(entry);
        var currentFinished = FinishedCount(currentSnapshot, entry, currentBees.Count);
        var currentRunning = currentSnapshot.BeesInFlight is not null && entry < currentSnapshot.BeesInFlight.Length
            ? currentSnapshot.BeesInFlight[entry]
            : null;
        var currentView = entry < currentSnapshot.Scenarios.Length ? currentSnapshot.Scenarios[entry] : new ScenarioView(0, 0, 0, 0, 0, 0);
        var currentState = currentSnapshot.State switch
        {
            FakeRunState.Running => "Running",
            FakeRunState.Paused => "Paused",
            FakeRunState.Finished => "Finished",
            _ => "Idle",
        };

        var currentInput = new HistoryRunInput(
            currentRunId ?? "current",
            "#1 · Current",
            currentStartedUtc ?? DateTimeOffset.UtcNow,
            currentState,
            currentSnapshot.Elapsed,
            entry,
            IsCurrent: true,
            SetupMatches: true,
            SetupBadge: "Same setup",
            Bees: currentBees,
            FinishedCount: currentFinished,
            RunningBees: currentRunning,
            TotalBeesOverride: Math.Max(currentFinished, currentView.Finished),
            FailedBeesOverride: currentView.Failed,
            SlowBeesOverride: currentView.SlowBees);

        var runs = new List<HistoryRunInput>(1 + Math.Min(5, previousRuns.Count)) { currentInput };
        foreach (var prev in previousRuns)
        {
            if (runs.Count >= 6) break;
            runs.Add(prev);
        }

        // Compute shared X span [0, maxDurationSec] across all compared runs.
        var maxDurationSec = 0.0;
        var maxBeeStarted = 0.0;
        var anyBee = false;
        var laneDurations = new double[runs.Count];

        for (var i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            var laneMaxStart = 0.0;
            var count = Math.Min(r.FinishedCount, r.Bees.Count);
            for (var j = 0; j < count; j++)
            {
                anyBee = true;
                var st = r.Bees[j].StartedAt;
                if (st > laneMaxStart) laneMaxStart = st;
            }
            if (r.RunningBees is { Count: > 0 })
            {
                anyBee = true;
                foreach (var rb in r.RunningBees)
                    if (rb.StartedAt > laneMaxStart) laneMaxStart = rb.StartedAt;
            }
            laneDurations[i] = Math.Max(r.ElapsedSeconds, laneMaxStart);
            if (laneDurations[i] > maxDurationSec) maxDurationSec = laneDurations[i];
            if (laneMaxStart > maxBeeStarted) maxBeeStarted = laneMaxStart;
        }

        if (anyBee && maxDurationSec < 1.0) maxDurationSec = 1.0;
        if (!(maxDurationSec > 0))
            return new RunHistoryScene(entry, scnLabel, setupText, [], [], 0, 0, 0, 0, []);

        var bucketWidth = BeeBars.GridWidth(maxDurationSec, maxBars);
        var maxSlotIdx = BeeBars.SlotIndex(Math.Max(0, maxDurationSec - 1e-6), bucketWidth);
        if (anyBee)
            maxSlotIdx = Math.Max(maxSlotIdx, BeeBars.SlotIndex(maxBeeStarted, bucketWidth));
        var timeBuckets = (int)(maxSlotIdx + 1);

        var lanes = new List<HistoryLane>(runs.Count);
        var grid = new List<IReadOnlyList<HistoryCell>>(runs.Count);
        var maxDurationMs = 0.0;

        for (var l = 0; l < runs.Count; l++)
        {
            var r = runs[l];
            var count = Math.Min(r.FinishedCount, r.Bees.Count);
            var okDurations = new List<double>(count);
            var failedTotal = 0;
            var slowTotal = 0;
            var canceledTotal = 0;

            var slotSampleErr = new string?[timeBuckets];
            var slotCanceled = new int[timeBuckets];
            var slotSampleSeq = new int?[timeBuckets];
            var slotSampleScore = new double[timeBuckets];
            Array.Fill(slotSampleScore, -1.0);

            var sorted = new List<BeeRecord>(count);
            for (var i = 0; i < count; i++)
            {
                var b = r.Bees[i];
                sorted.Add(b);
                if (b.Failed)
                {
                    failedTotal++;
                    if (b.Canceled) canceledTotal++;
                }
                else if (b.Outcome == BeeOutcome.Slow)
                {
                    slowTotal++;
                }
                else
                {
                    okDurations.Add(b.DurationMs);
                }

                var k = (int)Math.Clamp(BeeBars.SlotIndex(b.StartedAt, bucketWidth), 0, timeBuckets - 1);
                if (b.Failed)
                {
                    if (b.Canceled) slotCanceled[k]++;
                    slotSampleErr[k] ??= b.Error;
                    if (slotSampleScore[k] < 1_000_000)
                    {
                        slotSampleScore[k] = 1_000_000;
                        slotSampleSeq[k] = b.Sequence;
                    }
                }
                else
                {
                    var score = (b.Outcome == BeeOutcome.Slow ? 10_000 : 0) + b.DurationMs;
                    if (score > slotSampleScore[k])
                    {
                        slotSampleScore[k] = score;
                        slotSampleSeq[k] = b.Sequence;
                    }
                }
            }

            var bars = BeeBars.BuildGrid(sorted, sorted.Count, BeeShow.All, bucketWidth, 0, maxSlotIdx, 0, maxDurationSec, out _);
            if (r.RunningBees is { Count: > 0 })
                bars = BeeBars.WithRunning(bars, r.RunningBees, bucketWidth, r.ElapsedSeconds, gridFirst: 0);

            var row = new List<HistoryCell>(timeBuckets);
            for (var t = 0; t < timeBuckets; t++)
            {
                var bar = t < bars.Count ? bars[t] : default;
                var fromSec = BeeBars.Slot(t, bucketWidth);
                var toSec = BeeBars.Slot(t + 1, bucketWidth);
                var timeLabel = $"{Format.Time(fromSec)}–{Format.Time(toSec)}";
                var active = bar.Total > 0;
                var isSingleBee = active && bar.Sequence >= 0 && (bar.Count == 1 || bar.IsRunningBee);
                var maxMs = active ? bar.MaxMs : 0;
                if (active && maxMs > maxDurationMs) maxDurationMs = maxMs;

                var color = active ? BeeBarRgb(bar) : GoodRgb;
                var beeSeq = isSingleBee ? bar.Sequence : slotSampleSeq[t];

                row.Add(new HistoryCell(
                    l,
                    r.RunId,
                    r.Entry,
                    t,
                    fromSec,
                    toSec,
                    bar.BucketStart,
                    timeLabel,
                    isSingleBee,
                    beeSeq,
                    bar.Count,
                    bar.Slow,
                    bar.Failed,
                    slotCanceled[t],
                    bar.Running,
                    maxMs,
                    Math.Round(bar.Ratio, 2),
                    bar.Step,
                    slotSampleErr[t],
                    color));
            }

            var totalBees = Math.Max(count, r.TotalBeesOverride ?? count);
            var finalFailed = Math.Max(failedTotal, r.FailedBeesOverride ?? failedTotal);
            var finalSlow = Math.Max(slowTotal, r.SlowBeesOverride ?? slowTotal);
            var isStopped = string.Equals(r.State, "Stopped", StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.State, "Canceled", StringComparison.OrdinalIgnoreCase)
                || canceledTotal > 0;
            var laneElapsed = laneDurations[l];
            var endFraction = Math.Clamp(laneElapsed / maxDurationSec, 0.0, 1.0);
            var endLabel = isStopped
                ? $"Stopped @ {Format.Time(laneElapsed)}"
                : (endFraction < 0.98 && !string.Equals(r.State, "Running", StringComparison.OrdinalIgnoreCase)
                    ? $"Ended @ {Format.Time(laneElapsed)}"
                    : null);

            var shortName = r.IsCurrent
                ? (isStopped ? $"#1 · Current (Stopped @ {Format.Time(laneElapsed)})" : $"#1 · Current ({Format.Time(laneElapsed)})")
                : (isStopped && !r.Label.Contains("Stopped", StringComparison.OrdinalIgnoreCase)
                    ? $"{r.Label} · Stopped @ {Format.Time(laneElapsed)}"
                    : $"{r.Label} · {Format.Time(laneElapsed)}");
            var meta = isStopped
                ? $"Stopped @ {Format.Time(laneElapsed)}"
                : $"{Format.Num(totalBees)} bees · {Format.Time(laneElapsed)}";

            lanes.Add(new HistoryLane(
                l,
                r.RunId,
                r.Label,
                shortName,
                meta,
                r.StartedUtc,
                r.State,
                laneElapsed,
                r.Entry,
                r.IsCurrent,
                isStopped,
                r.SetupMatches,
                r.SetupBadge,
                Median(okDurations),
                totalBees,
                finalFailed,
                finalSlow,
                endFraction,
                endLabel));
            grid.Add(row);
        }

        return new RunHistoryScene(
            entry,
            scnLabel,
            setupText,
            lanes,
            grid,
            timeBuckets,
            bucketWidth,
            maxDurationSec,
            maxDurationMs,
            BuildTimeTicks(maxDurationSec));
    }

    /// <summary>Converts a <see cref="RunHistoryScene"/> into the compact JS payload for <c>views-3d.js</c>.</summary>
    public static JsScenePayload ToJsPayload(RunHistoryScene scene)
    {
        var lanes = scene.Lanes
            .Select(l => new JsSceneLane(
                l.Index,
                l.ShortName,
                l.IsCurrent || l.FailedBees > 0,
                l.EndFraction,
                l.EndLabel,
                l.IsStopped))
            .ToList();

        var cells = new List<JsSceneCell>();
        var scaleMs = Math.Max(150.0, scene.MaxDurationMs);

        foreach (var row in scene.Grid)
        {
            foreach (var c in row)
            {
                if (c.BeeCount == 0 && c.RunningCount == 0) continue;

                // Bar height = bar.MaxMs (duration of the longest bee in this bar/group).
                var h = Math.Clamp(0.28 + (c.MaxDurationMs / scaleMs) * 6.8, 0.28, 7.2);
                var lane = scene.Lanes[c.LaneIndex];

                List<JsTooltipRow> ttRows;
                string ttTitle;
                if (c.IsSingleBee && c.BeeSequence is { } seq)
                {
                    ttTitle = $"{lane.Name} · Bee #{seq + 1}";
                    var outcome = c.FailedCount > 0
                        ? (c.CanceledCount > 0 ? "Canceled" : "Failed")
                        : c.SlowCount > 0
                            ? "Slow"
                            : c.RunningCount > 0 && c.BeeCount == 0
                                ? "In flight"
                                : "Ok";
                    ttRows =
                    [
                        new("Bee", $"Bee #{seq + 1} · {Format.Ms(c.MaxDurationMs)}"),
                        new("Started", Format.Time(c.StartSec)),
                        new("Status", $"{outcome} · {lane.State}"),
                    ];
                }
                else
                {
                    var total = c.BeeCount + c.RunningCount;
                    ttTitle = $"{lane.Name} · {c.TimeLabel}";
                    ttRows =
                    [
                        new("Group", $"{Format.Num(total)} bees · longest {Format.Ms(c.MaxDurationMs)}"),
                        new("Breakdown", $"{c.FailedCount} failed · {c.SlowCount} slow" + (c.RunningCount > 0 ? $" · {c.RunningCount} active" : "")),
                        new("Run status", lane.EndLabel ?? $"{lane.State} · {Format.Time(lane.ElapsedSeconds)}"),
                    ];
                }

                cells.Add(new JsSceneCell(
                    c.LaneIndex,
                    c.BucketIndex,
                    Math.Round(h, 3),
                    c.ColorRgb,
                    ttTitle,
                    ttRows,
                    c.SampleError));
            }
        }

        var zSpan = Math.Clamp(scene.Lanes.Count * 2.2, 8.0, 14.0);
        return new JsScenePayload("history", 22, zSpan, scene.TimeBuckets, lanes, scene.TimeTicks, cells);
    }

    private static bool ScenarioIdentityMatches(ScenarioRunConfig targetCfg, string targetLabel, RemoteEntrySummary candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.ScenarioId))
            return string.Equals(candidate.ScenarioId.Trim(), targetCfg.ScenarioId.Trim(), StringComparison.OrdinalIgnoreCase);

        var candLabel = candidate.Label?.Trim() ?? "";
        if (candLabel.Length == 0) return false;
        return string.Equals(candLabel, targetLabel.Trim(), StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(targetCfg.Label) && string.Equals(candLabel, targetCfg.Label.Trim(), StringComparison.OrdinalIgnoreCase))
            || string.Equals(candLabel, targetCfg.ScenarioId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SetupMatches(ScenarioRunConfig targetCfg, RemoteEntrySummary candidate)
    {
        if (candidate.Pattern is { } pat)
        {
            if (pat != targetCfg.Pattern) return false;
            return pat == SpawningPattern.Bursts
                ? candidate.BeesPerBurst == targetCfg.BeesPerBurst && Math.Abs((candidate.BurstsPerMinute ?? 0) - targetCfg.BurstsPerMinute) < 0.05
                : Math.Abs((candidate.RatePerMinute ?? 0) - targetCfg.RatePerMinute) < 0.05;
        }
        return Math.Abs(candidate.BeesPerMinute - targetCfg.EffectiveRatePerMinute()) < 0.05;
    }

    private static uint DeterministicHash(int a, int b, int c)
    {
        unchecked
        {
            var h = (uint)(a * 374761393 + b * 668265263 + c * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }

    /// <summary>Converts a <see cref="TerrainScene"/> into the compact JS payload for <c>views-3d.js</c>.</summary>
    public static JsScenePayload ToJsPayload(TerrainScene scene)
    {
        var lanes = scene.Lanes
            .Select(l => new JsSceneLane(l.Index, l.Name, l.FailedBees > 0 || l.SlowBees > 0))
            .ToList();

        var cells = new List<JsSceneCell>();
        var scaleMs = Math.Max(150.0, scene.MaxDurationMs);

        foreach (var row in scene.Grid)
        {
            foreach (var c in row)
            {
                if (c.BeeCount == 0 && c.InFlightCount == 0) continue;

                var effectiveMs = c.BeeCount > 0 ? c.AvgDurationMs : c.MaxDurationMs;
                var h = Math.Clamp(0.28 + (effectiveMs / scaleMs) * 6.8, 0.28, 7.2);
                var laneName = scene.Lanes[c.LaneIndex].Name;
                var ttRows = new List<JsTooltipRow>(3)
                {
                    new(c.BeeCount > 0 ? "Avg duration" : "In flight",
                        c.BeeCount > 0
                            ? $"{Format.Ms(c.AvgDurationMs)}{(Format.OverTypical(c.Ratio) ? $" ({Format.Times(c.Ratio)})" : "")}"
                            : $"{c.InFlightCount} active"),
                    new("Max duration", Format.Ms(c.MaxDurationMs)),
                    new("Bees", $"{Format.Num(c.BeeCount)} total · {c.SlowCount} slow · {c.FailedCount} failed")
                };

                cells.Add(new JsSceneCell(
                    c.LaneIndex,
                    c.BucketIndex,
                    Math.Round(h, 3),
                    c.ColorRgb,
                    $"{laneName} · {c.TimeLabel}",
                    ttRows,
                    c.SampleError));
            }
        }

        return new JsScenePayload("terrain", 22, 10, scene.TimeBuckets, lanes, scene.TimeTicks, cells);
    }

    /// <summary>Converts a <see cref="SurfaceScene"/> into the compact JS payload for <c>views-3d.js</c>.</summary>
    public static JsScenePayload ToJsPayload(SurfaceScene scene)
    {
        var lanes = scene.Bands
            .Select(b => new JsSceneLane(b.Index, b.Label, b.IsSlow))
            .ToList();

        var cells = new List<JsSceneCell>();
        var scaleCount = Math.Max(1.0, scene.MaxCount);

        foreach (var row in scene.Grid)
        {
            foreach (var c in row)
            {
                if (c.Count == 0) continue;
                var h = Math.Clamp(0.22 + (c.Count / scaleCount) * 6.8, 0.22, 7.2);
                var ttRows = new List<JsTooltipRow>(3)
                {
                    new("Bees in band", Format.Num(c.Count)),
                    new("Avg / Max", $"{Format.Ms(c.AvgDurationMs)} / {Format.Ms(c.MaxDurationMs)}"),
                    new("Slow / Failed", $"{c.SlowCount} slow · {c.FailedCount} failed")
                };

                cells.Add(new JsSceneCell(
                    c.BandIndex,
                    c.BucketIndex,
                    Math.Round(h, 3),
                    c.ColorRgb,
                    $"Band {c.BandLabel} · {c.TimeLabel}",
                    ttRows,
                    c.SampleError));
            }
        }

        return new JsScenePayload("surface", 22, 12, scene.TimeBuckets, lanes, scene.TimeTicks, cells);
    }

    /// <summary>
    /// Projects Tab 4: Error Cascade (<c>Time × Distinct Error Signature × Count</c>), scanning failed/canceled bees
    /// across all scenarios (or <paramref name="entryFilter"/>) and grouping up to top 8 distinct error signatures.
    /// </summary>
    public static ErrorCascadeScene BuildErrorCascade(
        RunSnapshot snapshot,
        ErrorCascadeSort sort = ErrorCascadeSort.FirstSeen,
        int? entryFilter = null,
        int? bucketCountOverride = null)
    {
        if (snapshot.Count == 0)
            return new ErrorCascadeScene(sort, entryFilter, [], [], 0, 0, 0, 0, 0, []);

        if (entryFilter is { } ef && (ef < 0 || ef >= snapshot.Count))
            entryFilter = null;

        var spanSec = ComputeRunSpanSeconds(snapshot, entryFilter);
        var buckets = ResolveBucketCount(spanSec, bucketCountOverride);
        if (buckets == 0)
            return new ErrorCascadeScene(sort, entryFilter, [], [], 0, 0, 0, 0, 0, []);

        var ticks = BuildTimeTicks(spanSec);
        var startEntry = entryFilter ?? 0;
        var endEntry = entryFilter ?? snapshot.Count - 1;

        var groupsBySig = new Dictionary<string, ErrorSignatureGroup>(StringComparer.Ordinal);
        var totalErrors = 0;

        for (var e = startEntry; e <= endEntry; e++)
        {
            var bees = snapshot.Store.Bees(e);
            var count = FinishedCount(snapshot, e, bees.Count);
            for (var i = 0; i < count; i++)
            {
                var bee = bees[i];
                if (!bee.Failed) continue;

                totalErrors++;
                var raw = snapshot.Store.Errors.Resolve(bee.ErrorId) ?? bee.Error;
                var sig = string.IsNullOrWhiteSpace(raw) ? "(No error message)" : raw.Trim();

                if (!groupsBySig.TryGetValue(sig, out var grp))
                {
                    grp = new ErrorSignatureGroup(sig);
                    groupsBySig[sig] = grp;
                }
                grp.Add(bee);
            }
        }

        if (groupsBySig.Count == 0)
            return new ErrorCascadeScene(sort, entryFilter, [], [], buckets, spanSec, 0, 0, 0, ticks);

        var orderedGroups = sort == ErrorCascadeSort.MostFrequent
            ? groupsBySig.Values
                .OrderByDescending(g => g.TotalCount)
                .ThenBy(g => g.FirstSeenSec)
                .ThenBy(g => g.Signature, StringComparer.Ordinal)
            : groupsBySig.Values
                .OrderBy(g => g.FirstSeenSec)
                .ThenByDescending(g => g.TotalCount)
                .ThenBy(g => g.Signature, StringComparer.Ordinal);

        var selectedGroups = orderedGroups.Take(MaxErrorCascadeLanes).ToList();
        var bucketWidth = spanSec / buckets;
        var lanes = new List<ErrorCascadeLane>(selectedGroups.Count);
        var grid = new List<IReadOnlyList<ErrorCascadeCell>>(selectedGroups.Count);
        var maxCount = 0;

        for (var l = 0; l < selectedGroups.Count; l++)
        {
            var g = selectedGroups[l];
            var displayLabel = FormatErrorSignature(g.Signature, 34);
            var laneEntries = g.EntryCounts.Keys.OrderBy(x => x).ToList();
            var laneEntryLabels = laneEntries.Select(idx => ScenarioDisplayName(snapshot, idx)).ToList();
            var dominantEntry = g.DominantEntry();

            lanes.Add(new ErrorCascadeLane(
                l,
                g.Signature,
                displayLabel,
                $"@ {Format.Time(g.FirstSeenSec)} · {Format.Num(g.TotalCount)}",
                g.FirstSeenSec,
                g.LastSeenSec,
                g.TotalCount,
                g.CanceledCount,
                laneEntries,
                laneEntryLabels,
                dominantEntry,
                g.SampleBeeSequence,
                g.SampleStep));

            var acc = new ErrorCellAccumulator[buckets];
            for (var t = 0; t < buckets; t++) acc[t] = new ErrorCellAccumulator();

            foreach (var bee in g.Bees)
            {
                var tIdx = BucketIndex(bee.StartedAt, bucketWidth, buckets);
                acc[tIdx].Add(bee);
            }

            var row = new List<ErrorCascadeCell>(buckets);
            for (var t = 0; t < buckets; t++)
            {
                var a = acc[t];
                var fromSec = t * bucketWidth;
                var toSec = (t + 1) * bucketWidth;
                var timeLabel = $"{Format.Time(fromSec)}–{Format.Time(toSec)}";
                if (a.Count > maxCount) maxCount = a.Count;

                var cellEntries = a.Entries.OrderBy(x => x).ToList();
                var cellEntryLabels = cellEntries.Select(idx => ScenarioDisplayName(snapshot, idx)).ToList();
                var color = ErrorCascadeRgb(a.Count, a.CanceledCount);

                row.Add(new ErrorCascadeCell(
                    l,
                    g.Signature,
                    displayLabel,
                    t,
                    fromSec,
                    toSec,
                    timeLabel,
                    a.Count,
                    a.CanceledCount,
                    a.Count > 0 ? a.FirstOccurrenceSec : 0,
                    g.TotalCount,
                    cellEntries,
                    cellEntryLabels,
                    a.SampleBeeSequence,
                    a.SampleEntry,
                    a.SampleStep,
                    color));
            }
            grid.Add(row);
        }

        return new ErrorCascadeScene(
            sort,
            entryFilter,
            lanes,
            grid,
            buckets,
            spanSec,
            maxCount,
            totalErrors,
            groupsBySig.Count,
            ticks);
    }

    /// <summary>Converts an <see cref="ErrorCascadeScene"/> into the compact JS payload for <c>views-3d.js</c>.</summary>
    public static JsScenePayload ToJsPayload(ErrorCascadeScene scene)
    {
        var lanes = scene.Lanes
            .Select(l => new JsSceneLane(l.Index, l.DisplayLabel, l.Index == 0))
            .ToList();

        var cells = new List<JsSceneCell>();
        var scaleCount = Math.Max(1.0, scene.MaxCount);

        foreach (var row in scene.Grid)
        {
            foreach (var c in row)
            {
                if (c.Count == 0) continue;
                var h = Math.Clamp(0.24 + (c.Count / scaleCount) * 6.8, 0.24, 7.2);
                var lane = scene.Lanes[c.LaneIndex];
                var scenariosText = c.AffectedEntryLabels.Count > 0
                    ? string.Join(", ", c.AffectedEntryLabels)
                    : "–";
                var countText = c.CanceledCount > 0
                    ? $"{Format.Num(c.Count)} ({Format.Num(c.CanceledCount)} canceled) / {Format.Num(c.LaneTotalCount)} total"
                    : $"{Format.Num(c.Count)} in bucket / {Format.Num(c.LaneTotalCount)} total";

                var ttRows = new List<JsTooltipRow>(3)
                {
                    new("Occurrences", countText),
                    new("First seen", $"{Format.Time(lane.FirstSeenSec)} (bucket {Format.Time(c.FirstOccurrenceSec)})"),
                    new("Scenarios", scenariosText),
                };

                cells.Add(new JsSceneCell(
                    c.LaneIndex,
                    c.BucketIndex,
                    Math.Round(h, 3),
                    c.ColorRgb,
                    $"{lane.DisplayLabel} · {c.TimeLabel}",
                    ttRows,
                    c.Signature));
            }
        }

        var zSpan = Math.Clamp(scene.Lanes.Count * 1.6, 8.0, 14.0);
        return new JsScenePayload("errors", 22, zSpan, scene.TimeBuckets, lanes, scene.TimeTicks, cells);
    }

    /// <summary>Normalizes whitespace/newlines and truncates an error signature cleanly for lane labels.</summary>
    public static string FormatErrorSignature(string signature, int maxLength = 34)
    {
        if (string.IsNullOrWhiteSpace(signature)) return "(No error message)";
        var normalized = string.Join(' ', signature.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0) return "(No error message)";
        if (normalized.Length <= maxLength) return normalized;
        return maxLength <= 1 ? normalized[..maxLength] : normalized[..(maxLength - 1)].TrimEnd() + "…";
    }

    /// <summary>Active bar colour for Swarm matrix: blue below 1.25×, smoothly lerping toward amber at 2× typical.</summary>
    public static int[] SwarmActiveColorRgb(double ratio)
    {
        if (ratio >= SlowCallRule.Factor) return SlowRgb;
        if (ratio <= 1.25) return RunningRgb;
        var t = Math.Clamp((ratio - 1.25) / (SlowCallRule.Factor - 1.25), 0.0, 1.0);
        return
        [
            (int)Math.Round(RunningRgb[0] + (SlowRgb[0] - RunningRgb[0]) * t),
            (int)Math.Round(RunningRgb[1] + (SlowRgb[1] - RunningRgb[1]) * t),
            (int)Math.Round(RunningRgb[2] + (SlowRgb[2] - RunningRgb[2]) * t),
        ];
    }

    /// <summary>
    /// Projects Tab 5: Swarm: Dead or Alive (<c>Scenario × Declared Step × Active/Failed</c>) with step-to-step
    /// transition arcs (<c>next</c>, <c>skip</c>, <c>repeat</c>, <c>loop</c>) and sample bee paths.
    /// </summary>
    public static SwarmScene BuildSwarmMatrix(RunSnapshot snapshot, SwarmDisplayMode mode = SwarmDisplayMode.Stacked)
    {
        if (snapshot.Count == 0)
            return new SwarmScene(mode, 0, 0, 0, 0, [], [], [], [], false);

        var stepCounts = new int[snapshot.Count];
        var maxSteps = 0;
        for (var e = 0; e < snapshot.Count; e++)
        {
            var steps = snapshot.StepsOf(e);
            stepCounts[e] = steps.Any ? steps.Count : 1;
            if (stepCounts[e] > maxSteps) maxSteps = stepCounts[e];
        }

        var stepHeaders = new List<string>(maxSteps);
        for (var s = 0; s < maxSteps; s++) stepHeaders.Add($"Step {s + 1}");

        var lanes = new List<SwarmLane>(snapshot.Count);
        var grid = new List<IReadOnlyList<SwarmStepCell>>(snapshot.Count);
        var arcs = new List<SwarmTransitionArc>();
        var maxActive = 0;
        var maxFailed = 0;
        var maxTotal = 0;

        for (var e = 0; e < snapshot.Count; e++)
        {
            var steps = snapshot.StepsOf(e);
            var hasDeclaredSteps = steps.Any;
            var stepCount = stepCounts[e];
            var scnName = ScenarioDisplayName(snapshot, e);

            var activeCounts = new int[stepCount];
            var failedCounts = new int[stepCount];
            var slowCounts = new int[stepCount];
            var repeatCounts = new int[stepCount];
            var loopCounts = new int[stepCount];
            var durSums = new double[stepCount];
            var durCounts = new int[stepCount];
            var typicalSamples = new List<double>[stepCount];
            var maxNonFailedRatio = new double[stepCount];
            var sampleError = new string?[stepCount];
            var sampleBeeSeq = new int?[stepCount];

            for (var s = 0; s < stepCount; s++)
            {
                typicalSamples[s] = [];
                maxNonFailedRatio[s] = 1.0;
            }

            var arcCounts = new Dictionary<(int From, int To, string Kind), int>();
            List<int>? bestPath = null;
            var bestPathHasJump = false;

            void RecordPath(List<int> path)
            {
                if (path.Count == 0) return;
                var hasJump = false;
                for (var k = 0; k < path.Count - 1; k++)
                {
                    var from = path[k];
                    var to = path[k + 1];
                    var kind = to == from ? "repeat" : to < from ? "loop" : to > from + 1 ? "skip" : "next";
                    if (kind != "next") hasJump = true;
                    if (kind == "repeat") repeatCounts[from]++;
                    else if (kind == "loop") loopCounts[from]++;
                    var key = (from, to, kind);
                    arcCounts.TryGetValue(key, out var cnt);
                    arcCounts[key] = cnt + 1;
                }

                if (bestPath is null
                    || (hasJump && !bestPathHasJump)
                    || (hasJump == bestPathHasJump && path.Count >= bestPath.Count))
                {
                    bestPath = path;
                    bestPathHasJump = hasJump;
                }
            }

            // 1. Live in-flight bees per step
            if (snapshot.BeesInFlight is { } bif && e < bif.Length && bif[e].Count > 0)
            {
                foreach (var rb in bif[e])
                {
                    var elapsedMs = Math.Max(0, (snapshot.Elapsed - rb.StartedAt) * 1000.0);
                    var curStep = hasDeclaredSteps ? Math.Clamp(StepLogic.CurrentStep(rb, elapsedMs), 0, stepCount - 1) : 0;
                    activeCounts[curStep]++;
                    sampleBeeSeq[curStep] ??= rb.Sequence;

                    var hadStepCall = false;
                    foreach (var c in rb.Calls)
                    {
                        if (c.Skipped || c.OffsetMs > elapsedMs) continue;
                        var cs = hasDeclaredSteps ? c.Step : 0;
                        if (cs < 0 || cs >= stepCount) continue;
                        if (cs == curStep) hadStepCall = true;
                        durSums[cs] += c.DurationMs;
                        durCounts[cs]++;
                        if (c.TypicalMs is { } tm && tm > 0) typicalSamples[cs].Add(tm);
                        if (c.Slow) slowCounts[cs]++;
                        var r = SlowCallRule.Ratio(c.DurationMs, c.TypicalMs, c.SlowThresholdMs);
                        if (r > maxNonFailedRatio[cs]) maxNonFailedRatio[cs] = r;
                    }
                    if (!hadStepCall && elapsedMs > 0)
                    {
                        durSums[curStep] += elapsedMs;
                        durCounts[curStep]++;
                    }

                    RecordPath(ExtractStepPath(rb.StepVisits, rb.Calls, hasDeclaredSteps, stepCount, elapsedMs));
                }
            }
            else if (e < snapshot.Scenarios.Length && snapshot.Scenarios[e].InFlight > 0)
            {
                activeCounts[0] = snapshot.Scenarios[e].InFlight;
            }

            // 2. Finished & failed bees per step
            var bees = snapshot.Store.Bees(e);
            var finished = FinishedCount(snapshot, e, bees.Count);
            for (var i = 0; i < finished; i++)
            {
                var bee = bees[i];
                if (!bee.Failed) continue;
                var fStep = hasDeclaredSteps
                    ? (bee.FailedStep >= 0 && bee.FailedStep < stepCount ? bee.FailedStep : -1)
                    : 0;
                if (fStep < 0) continue;
                failedCounts[fStep]++;
                sampleError[fStep] ??= snapshot.Store.Errors.Resolve(bee.ErrorId) ?? bee.Error;
                sampleBeeSeq[fStep] = bee.Sequence;
            }

            if (hasDeclaredSteps && finished > 0)
            {
                var failedPer = snapshot.Store.FailedPerStep(e);
                for (var s = 0; s < stepCount && s < failedPer.Count; s++)
                {
                    if (failedPer[s] > failedCounts[s])
                        failedCounts[s] = failedPer[s];
                }
            }

            var detailStart = Math.Max(0, finished - MaxSwarmDetailedBees);
            for (var i = detailStart; i < finished; i++)
            {
                var bee = bees[i];
                var detail = snapshot.Store.Detail(bee.Sequence);
                if (detail is null || IsPlaceholderDetail(detail))
                {
                    if (!hasDeclaredSteps)
                    {
                        durSums[0] += bee.DurationMs;
                        durCounts[0]++;
                        if (bee.Outcome == BeeOutcome.Slow) slowCounts[0]++;
                        if (!bee.Failed && bee.Ratio > maxNonFailedRatio[0]) maxNonFailedRatio[0] = bee.Ratio;
                    }
                    continue;
                }

                foreach (var c in detail.Calls)
                {
                    if (c.Skipped) continue;
                    var cs = hasDeclaredSteps ? c.Step : 0;
                    if (cs < 0 || cs >= stepCount) continue;
                    durSums[cs] += c.DurationMs;
                    durCounts[cs]++;
                    if (c.TypicalMs is { } tm && tm > 0) typicalSamples[cs].Add(tm);
                    if (c.Failed)
                    {
                        sampleError[cs] ??= c.Error;
                    }
                    else
                    {
                        if (c.Slow) slowCounts[cs]++;
                        var r = SlowCallRule.Ratio(c.DurationMs, c.TypicalMs, c.SlowThresholdMs);
                        if (r > maxNonFailedRatio[cs]) maxNonFailedRatio[cs] = r;
                    }
                }

                RecordPath(ExtractStepPath(detail.StepVisits, detail.Calls, hasDeclaredSteps, stepCount, double.PositiveInfinity));
            }

            foreach (var ((fromStep, toStep, kind), count) in arcCounts
                .OrderBy(kv => kv.Key.From)
                .ThenBy(kv => kv.Key.To)
                .ThenBy(kv => kv.Key.Kind, StringComparer.Ordinal))
            {
                arcs.Add(new SwarmTransitionArc(e, fromStep, toStep, count, kind));
            }

            var row = new List<SwarmStepCell>(maxSteps);
            for (var s = 0; s < maxSteps; s++)
            {
                if (s >= stepCount)
                {
                    row.Add(new SwarmStepCell(
                        e,
                        e,
                        s,
                        stepCount,
                        scnName,
                        "",
                        IsEmptyPad: true,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        1.0,
                        null,
                        null,
                        [40, 37, 34]));
                    continue;
                }

                var stepName = hasDeclaredSteps ? steps.Name(s) : "Run";
                if (string.IsNullOrWhiteSpace(stepName)) stepName = $"Step {s + 1}";

                var avgMs = durCounts[s] > 0 ? durSums[s] / durCounts[s] : 0;
                var typicalMs = typicalSamples[s].Count > 0 ? Median(typicalSamples[s]) : avgMs;
                var ratio = Math.Max(maxNonFailedRatio[s], typicalMs > 0 && avgMs > 0 ? SlowCallRule.Ratio(avgMs, typicalMs) : 1.0);
                var activeColor = SwarmActiveColorRgb(ratio);

                var act = activeCounts[s];
                var fail = failedCounts[s];
                if (act > maxActive) maxActive = act;
                if (fail > maxFailed) maxFailed = fail;
                if (act + fail > maxTotal) maxTotal = act + fail;

                row.Add(new SwarmStepCell(
                    e,
                    e,
                    s,
                    stepCount,
                    scnName,
                    stepName,
                    IsEmptyPad: false,
                    act,
                    fail,
                    slowCounts[s],
                    repeatCounts[s],
                    loopCounts[s],
                    avgMs,
                    typicalMs,
                    Math.Round(ratio, 2),
                    sampleError[s],
                    sampleBeeSeq[s],
                    activeColor));
            }

            var laneActive = activeCounts.Sum();
            var laneFailed = failedCounts.Sum();
            var laneSlow = slowCounts.Sum();
            var samplePath = bestPath is { Count: > 0 }
                ? string.Join(" → ", bestPath.Select(st => (st + 1).ToString()))
                : string.Join(" → ", Enumerable.Range(1, stepCount));

            lanes.Add(new SwarmLane(
                e,
                e,
                scnName,
                $"{Format.Num(laneActive)} act · {Format.Num(laneFailed)} fail",
                stepCount,
                laneActive,
                laneFailed,
                laneSlow,
                samplePath));
            grid.Add(row);
        }

        return new SwarmScene(
            mode,
            maxSteps,
            maxActive,
            maxFailed,
            maxTotal,
            stepHeaders,
            lanes,
            grid,
            arcs,
            snapshot.Running);
    }

    /// <summary>Converts a <see cref="SwarmScene"/> into the compact JS payload for <c>views-3d.js</c>.</summary>
    public static JsScenePayload ToJsPayload(SwarmScene scene)
    {
        var lanes = scene.Lanes
            .Select(l => new JsSceneLane(l.Index, l.Name, l.FailedBees > 0 || l.ActiveBees > 0, DeclaredSteps: l.DeclaredSteps))
            .ToList();

        var scaleCount = Math.Max(4.0, scene.Mode switch
        {
            SwarmDisplayMode.ActiveOnly => scene.MaxActive,
            SwarmDisplayMode.FailedOnly => scene.MaxFailed,
            _ => scene.MaxTotal,
        });

        var cells = new List<JsSceneCell>();
        foreach (var row in scene.Grid)
        {
            foreach (var c in row)
            {
                if (c.IsEmptyPad)
                {
                    cells.Add(new JsSceneCell(
                        c.ScenarioIndex,
                        c.StepIndex,
                        0,
                        [40, 37, 34],
                        "",
                        [],
                        null,
                        IsEmptyPad: true));
                    continue;
                }

                var activeH = c.ActiveCount > 0 ? Math.Clamp(0.24 + (c.ActiveCount / scaleCount) * 5.6, 0.24, 6.0) : 0;
                var failedH = c.FailedCount > 0 ? Math.Clamp(0.24 + (c.FailedCount / scaleCount) * 4.4, 0.24, 4.6) : 0;

                double primaryH = 0;
                int[] primaryColor = [40, 37, 34];
                double secondaryH = 0;
                int[]? secondaryColor = null;

                switch (scene.Mode)
                {
                    case SwarmDisplayMode.ActiveOnly:
                        if (activeH > 0)
                        {
                            primaryH = activeH;
                            primaryColor = c.ActiveColorRgb;
                        }
                        break;
                    case SwarmDisplayMode.FailedOnly:
                        if (failedH > 0)
                        {
                            primaryH = failedH;
                            primaryColor = FailedRgb;
                        }
                        break;
                    default:
                        if (activeH > 0 && failedH > 0)
                        {
                            primaryH = activeH;
                            primaryColor = c.ActiveColorRgb;
                            secondaryH = failedH;
                            secondaryColor = FailedRgb;
                        }
                        else if (activeH > 0)
                        {
                            primaryH = activeH;
                            primaryColor = c.ActiveColorRgb;
                        }
                        else if (failedH > 0)
                        {
                            primaryH = failedH;
                            primaryColor = FailedRgb;
                        }
                        break;
                }

                var showFailed = scene.Mode != SwarmDisplayMode.ActiveOnly;
                var showActive = scene.Mode != SwarmDisplayMode.FailedOnly;
                var alertBadge = showFailed && c.FailedCount > 0;
                string? badgeText = null;
                if (alertBadge || (showActive && c.ActiveCount > 0))
                {
                    badgeText = scene.Mode switch
                    {
                        SwarmDisplayMode.FailedOnly => $"{c.StepName} ({c.FailedCount} fail)",
                        SwarmDisplayMode.ActiveOnly => $"{c.StepName} ({c.ActiveCount} act)",
                        _ => $"{c.StepName} ({c.ActiveCount}a · {c.FailedCount}f)",
                    };
                }

                var ttRows = new List<JsTooltipRow>(4)
                {
                    new("Active in-flight", $"{Format.Num(c.ActiveCount)} bees"),
                    new("Failed (step)", $"{Format.Num(c.FailedCount)} bees"),
                    new("Slow / Avg", c.AvgDurationMs > 0
                        ? $"{Format.Num(c.SlowCount)} slow · {Format.Ms(c.AvgDurationMs)} ({Format.Times(c.Ratio)})"
                        : $"{Format.Num(c.SlowCount)} slow"),
                    new("Repeats / Loops", $"{Format.Num(c.SelfRepeatCount)} rep · {Format.Num(c.BackLoopCount)} loop"),
                };

                cells.Add(new JsSceneCell(
                    c.ScenarioIndex,
                    c.StepIndex,
                    Math.Round(primaryH, 3),
                    primaryColor,
                    $"{c.ScenarioName} · {c.StepName} (Step {c.StepIndex + 1}/{c.TotalSteps})",
                    ttRows,
                    c.SampleError,
                    Math.Round(secondaryH, 3),
                    secondaryColor,
                    IsEmptyPad: false,
                    StepName: c.StepName,
                    BadgeText: badgeText,
                    AlertBadge: alertBadge));
            }
        }

        var jsArcs = scene.Arcs
            .Select(a => new JsSceneArc(a.ScenarioIndex, a.FromStep, a.ToStep, a.Count, a.Kind))
            .ToList();

        var xSpan = Math.Clamp(scene.MaxSteps * 3.6, 12.0, 24.0);
        var zSpan = Math.Clamp(scene.Lanes.Count * 2.6, 8.0, 15.0);
        return new JsScenePayload("swarm", xSpan, zSpan, scene.MaxSteps, lanes, [], cells, jsArcs, scene.StepHeaders, scene.IsRunning);
    }

    private static List<int> ExtractStepPath(
        IReadOnlyList<RecordedStepVisit>? stepVisits,
        IReadOnlyList<FakeCall> calls,
        bool hasDeclaredSteps,
        int stepCount,
        double maxOffsetMs)
    {
        var path = new List<int>();
        if (stepVisits is { Count: > 0 })
        {
            foreach (var v in stepVisits)
            {
                var st = hasDeclaredSteps ? v.Step : 0;
                if (st >= 0 && st < stepCount) path.Add(st);
            }
            return path;
        }

        var lastStep = -1;
        var lastRep = -1;
        foreach (var c in calls)
        {
            if (c.Skipped || c.OffsetMs > maxOffsetMs) continue;
            var st = hasDeclaredSteps ? c.Step : 0;
            if (st < 0 || st >= stepCount) continue;
            if (st == lastStep && c.Rep == lastRep) continue;
            path.Add(st);
            lastStep = st;
            lastRep = c.Rep;
        }
        return path;
    }

    /// <summary>Builds 7 adaptive logarithmic latency bands around the run's duration distribution.</summary>
    public static IReadOnlyList<LatencyBand> BuildLatencyBands(IReadOnlyList<double> durations, double typicalMs)
    {
        double[] cutoffs;
        if (durations.Count < 4 || !(typicalMs > 0))
        {
            cutoffs = DefaultCutoffsMs;
        }
        else
        {
            var sorted = durations.ToArray();
            Array.Sort(sorted);
            var p10 = sorted[Math.Clamp((int)(sorted.Length * 0.10), 0, sorted.Length - 1)];
            var p95 = sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1)];
            var lo = Math.Max(10, Math.Min(Math.Max(10, p10 * 1.1), typicalMs * 0.35));
            var hi = Math.Max(lo * 8, Math.Max(p95 * 1.05, typicalMs * SlowCallRule.Factor * 1.25));

            if (typicalMs <= 250 && p95 <= 2500)
            {
                cutoffs = DefaultCutoffsMs;
            }
            else
            {
                var chosen = new int[6];
                var prevIdx = -1;
                for (var i = 0; i < 6; i++)
                {
                    var target = lo * Math.Pow(hi / lo, i / 5.0);
                    var idx = NearestLadderIndex(target, minIdx: prevIdx + 1, maxIdx: NiceLadderMs.Length - (6 - i));
                    chosen[i] = idx;
                    prevIdx = idx;
                }
                cutoffs = chosen.Select(idx => NiceLadderMs[idx]).ToArray();
            }
        }

        var bands = new List<LatencyBand>(7);
        var slowThreshold = typicalMs > 0 ? typicalMs * SlowCallRule.Factor : 500;

        bands.Add(new LatencyBand(0, $"≤ {Format.Ms(cutoffs[0])}", 0, cutoffs[0], cutoffs[0] * 0.5 >= slowThreshold));
        for (var i = 1; i < 6; i++)
        {
            var min = cutoffs[i - 1];
            var max = cutoffs[i];
            bands.Add(new LatencyBand(i, Format.Ms(max), min, max, min >= slowThreshold));
        }
        bands.Add(new LatencyBand(6, $"{Format.Ms(cutoffs[5])}+", cutoffs[5], double.PositiveInfinity, cutoffs[5] >= slowThreshold || typicalMs <= 0));
        return bands;
    }

    private static TerrainScene BuildScenarioTerrain(RunSnapshot snapshot, double spanSec, int buckets, IReadOnlyList<TimeAxisTick> ticks)
    {
        var entries = new List<(int Entry, string Label, string Meta, double TypicalMs, int Total, int Failed, int Slow)>(snapshot.Count);
        for (var e = 0; e < snapshot.Count; e++)
        {
            var bees = snapshot.Store.Bees(e);
            var count = FinishedCount(snapshot, e, bees.Count);
            var durs = new List<double>(count);
            var failed = 0;
            var slow = 0;
            for (var i = 0; i < count; i++)
            {
                var b = bees[i];
                durs.Add(b.DurationMs);
                if (b.Failed) failed++;
                else if (b.Outcome == BeeOutcome.Slow) slow++;
            }

            // Prefer snapshot counters if larger (e.g. Live mode summary counters)
            if (e < snapshot.Scenarios.Length)
            {
                failed = Math.Max(failed, snapshot.Scenarios[e].Failed);
                slow = Math.Max(slow, snapshot.Scenarios[e].SlowBees);
            }

            var label = e < snapshot.Labels.Count ? snapshot.Labels[e] : $"Scenario {e + 1}";
            var cfg = e < snapshot.Plan.Scenarios.Count ? snapshot.Plan.Scenarios[e] : null;
            var meta = cfg is null ? "" : Format.Shape(cfg) is { Length: > 0 } shape ? shape : $"{Format.Rate(cfg.EffectiveRatePerMinute())}/min";
            entries.Add((e, $"{e + 1}. {label}", meta, Median(durs), count, failed, slow));
        }

        // Order failed first, then slow, then entry index so problematic lanes sit closest to the camera.
        entries.Sort((a, b) =>
        {
            var af = a.Failed > 0 ? 1 : 0;
            var bf = b.Failed > 0 ? 1 : 0;
            var c = bf.CompareTo(af);
            if (c != 0) return c;
            c = b.Failed.CompareTo(a.Failed);
            if (c != 0) return c;
            c = b.Slow.CompareTo(a.Slow);
            if (c != 0) return c;
            return a.Entry.CompareTo(b.Entry);
        });

        var bucketWidth = spanSec / buckets;
        var lanes = new List<TerrainLane>(entries.Count);
        var grid = new List<IReadOnlyList<TerrainCell>>(entries.Count);
        var maxDurationMs = 0.0;

        for (var l = 0; l < entries.Count; l++)
        {
            var info = entries[l];
            lanes.Add(new TerrainLane(l, info.Entry, -1, info.Label, info.Meta, info.TypicalMs, info.Total, info.Failed, info.Slow));

            var acc = new TerrainAccumulator[buckets];
            for (var t = 0; t < buckets; t++) acc[t] = new TerrainAccumulator();

            var bees = snapshot.Store.Bees(info.Entry);
            var count = FinishedCount(snapshot, info.Entry, bees.Count);
            for (var i = 0; i < count; i++)
            {
                var bee = bees[i];
                var tIdx = BucketIndex(bee.StartedAt, bucketWidth, buckets);
                acc[tIdx].AddFinished(bee);
            }

            if (snapshot.BeesInFlight is not null && info.Entry < snapshot.BeesInFlight.Length)
            {
                foreach (var r in snapshot.BeesInFlight[info.Entry])
                {
                    var tIdx = BucketIndex(r.StartedAt, bucketWidth, buckets);
                    var inFlightMs = Math.Max(0, (snapshot.Elapsed - r.StartedAt) * 1000.0);
                    acc[tIdx].AddInFlight(inFlightMs);
                }
            }

            var row = new List<TerrainCell>(buckets);
            for (var t = 0; t < buckets; t++)
            {
                var a = acc[t];
                var fromSec = t * bucketWidth;
                var toSec = (t + 1) * bucketWidth;
                var timeLabel = $"{Format.Time(fromSec)}–{Format.Time(toSec)}";
                var avgMs = a.BeeCount > 0 ? a.DurationSum / a.BeeCount : 0;
                var maxMs = Math.Max(a.MaxDurationMs, a.MaxInFlightMs);
                if (avgMs > maxDurationMs) maxDurationMs = avgMs;
                if (a.BeeCount == 0 && maxMs > maxDurationMs) maxDurationMs = maxMs;

                var ratio = a.MaxNonFailedRatio > 1.0
                    ? a.MaxNonFailedRatio
                    : (info.TypicalMs > 0 && a.BeeCount > 0 ? SlowCallRule.Ratio(avgMs, info.TypicalMs) : 1.0);
                var color = OutcomeRgb(a.FailedCount, a.SlowCount, ratio, a.BeeCount == 0 ? a.InFlightCount : 0);

                row.Add(new TerrainCell(
                    l,
                    info.Entry,
                    null,
                    t,
                    fromSec,
                    toSec,
                    timeLabel,
                    avgMs,
                    maxMs,
                    info.TypicalMs,
                    Math.Round(ratio, 2),
                    a.BeeCount,
                    a.SlowCount,
                    a.FailedCount,
                    a.CanceledCount,
                    a.InFlightCount,
                    a.SampleError,
                    a.SampleBeeSequence,
                    color));
            }
            grid.Add(row);
        }

        return new TerrainScene(TerrainGrouping.Scenarios, null, lanes, grid, buckets, spanSec, maxDurationMs, ticks);
    }

    private static TerrainScene BuildStepTerrain(RunSnapshot snapshot, int? scenarioEntry, double spanSec, int buckets, IReadOnlyList<TimeAxisTick> ticks)
    {
        var entry = ResolveStepScenarioEntry(snapshot, scenarioEntry);
        if (entry < 0)
            return new TerrainScene(TerrainGrouping.Steps, null, [], [], buckets, spanSec, 0, ticks, "No scenarios in this run declare steps.");

        var steps = snapshot.StepsOf(entry);
        var stepCount = steps.Count;
        var bucketWidth = spanSec / buckets;
        var acc = new TerrainAccumulator[stepCount, buckets];
        var stepDurations = new List<double>[stepCount];
        var stepTotal = new int[stepCount];
        var stepFailed = new int[stepCount];
        var stepSlow = new int[stepCount];

        for (var s = 0; s < stepCount; s++)
        {
            stepDurations[s] = [];
            for (var t = 0; t < buckets; t++)
                acc[s, t] = new TerrainAccumulator();
        }

        var bees = snapshot.Store.Bees(entry);
        var count = FinishedCount(snapshot, entry, bees.Count);
        var missingDetailCount = 0;

        for (var i = 0; i < count; i++)
        {
            var bee = bees[i];
            var detail = snapshot.Store.Detail(bee.Sequence);
            if (detail is null || IsPlaceholderDetail(detail))
            {
                missingDetailCount++;
                if (bee.Failed && bee.FailedStep >= 0 && bee.FailedStep < stepCount)
                {
                    var tIdx = BucketIndex(bee.StartedAt, bucketWidth, buckets);
                    acc[bee.FailedStep, tIdx].AddStepSample(0, true, bee.Canceled, false, 1.0, bee.Error, bee.Sequence);
                    stepTotal[bee.FailedStep]++;
                    stepFailed[bee.FailedStep]++;
                }
                continue;
            }

            // Aggregate calls per step within this bee.
            var hasStepCalls = new bool[stepCount];
            var minOffset = new double[stepCount];
            var maxEnd = new double[stepCount];
            var hasFail = new bool[stepCount];
            var hasSlow = new bool[stepCount];
            var maxRatio = new double[stepCount];
            var stepErr = new string?[stepCount];
            Array.Fill(minOffset, double.MaxValue);
            Array.Fill(maxRatio, 1.0);

            foreach (var c in detail.Calls)
            {
                if (c.Skipped || c.Step < 0 || c.Step >= stepCount) continue;
                var s = c.Step;
                hasStepCalls[s] = true;
                if (c.OffsetMs < minOffset[s]) minOffset[s] = c.OffsetMs;
                var end = c.OffsetMs + c.DurationMs;
                if (end > maxEnd[s]) maxEnd[s] = end;
                if (c.Failed)
                {
                    hasFail[s] = true;
                    stepErr[s] ??= c.Error;
                }
                else
                {
                    if (c.Slow) hasSlow[s] = true;
                    var r = SlowCallRule.Ratio(c.DurationMs, c.TypicalMs, c.SlowThresholdMs);
                    if (r > maxRatio[s]) maxRatio[s] = r;
                }
            }

            for (var s = 0; s < stepCount; s++)
            {
                if (!hasStepCalls[s])
                {
                    if (bee.Failed && bee.FailedStep == s)
                    {
                        var tIdx = BucketIndex(bee.StartedAt, bucketWidth, buckets);
                        acc[s, tIdx].AddStepSample(0, true, bee.Canceled, false, 1.0, bee.Error, bee.Sequence);
                        stepTotal[s]++;
                        stepFailed[s]++;
                    }
                    continue;
                }

                var durMs = Math.Max(0, maxEnd[s] - minOffset[s]);
                var atSec = Math.Clamp(bee.StartedAt + minOffset[s] / 1000.0, 0, spanSec);
                var tIdx2 = BucketIndex(atSec, bucketWidth, buckets);
                var failed = hasFail[s] || (bee.Failed && bee.FailedStep == s);
                var canceled = failed && (bee.Canceled || ExecutionResult.IsCanceledError(stepErr[s]));
                var err = stepErr[s] ?? (failed ? bee.Error : null);

                acc[s, tIdx2].AddStepSample(durMs, failed, canceled, !failed && hasSlow[s], maxRatio[s], err, bee.Sequence);
                stepDurations[s].Add(durMs);
                stepTotal[s]++;
                if (failed) stepFailed[s]++;
                else if (hasSlow[s]) stepSlow[s]++;
            }
        }

        if (snapshot.BeesInFlight is not null && entry < snapshot.BeesInFlight.Length)
        {
            foreach (var r in snapshot.BeesInFlight[entry])
            {
                var elapsedMs = Math.Max(0, (snapshot.Elapsed - r.StartedAt) * 1000.0);
                var s = StepLogic.CurrentStep(r, elapsedMs);
                if (s >= 0 && s < stepCount)
                {
                    var tIdx = BucketIndex(r.StartedAt, bucketWidth, buckets);
                    acc[s, tIdx].AddInFlight(elapsedMs);
                }
            }
        }

        var lanes = new List<TerrainLane>(stepCount);
        var grid = new List<IReadOnlyList<TerrainCell>>(stepCount);
        var maxDurationMs = 0.0;

        for (var s = 0; s < stepCount; s++)
        {
            var typical = Median(stepDurations[s]);
            var meta = stepFailed[s] > 0 ? $"{stepFailed[s]} failed"
                : stepSlow[s] > 0 ? $"{stepSlow[s]} slow"
                : typical > 0 ? $"~{Format.Ms(typical)}"
                : "no samples";
            lanes.Add(new TerrainLane(s, entry, s, $"Step {s + 1} · {steps.Name(s)}", meta, typical, stepTotal[s], stepFailed[s], stepSlow[s]));

            var row = new List<TerrainCell>(buckets);
            for (var t = 0; t < buckets; t++)
            {
                var a = acc[s, t];
                var fromSec = t * bucketWidth;
                var toSec = (t + 1) * bucketWidth;
                var timeLabel = $"{Format.Time(fromSec)}–{Format.Time(toSec)}";
                var avgMs = a.BeeCount > 0 ? a.DurationSum / a.BeeCount : 0;
                var maxMs = Math.Max(a.MaxDurationMs, a.MaxInFlightMs);
                if (avgMs > maxDurationMs) maxDurationMs = avgMs;
                if (a.BeeCount == 0 && maxMs > maxDurationMs) maxDurationMs = maxMs;

                var ratio = a.MaxNonFailedRatio > 1.0
                    ? a.MaxNonFailedRatio
                    : (typical > 0 && a.BeeCount > 0 ? SlowCallRule.Ratio(avgMs, typical) : 1.0);
                var color = OutcomeRgb(a.FailedCount, a.SlowCount, ratio, a.BeeCount == 0 ? a.InFlightCount : 0);

                row.Add(new TerrainCell(
                    s,
                    entry,
                    s,
                    t,
                    fromSec,
                    toSec,
                    timeLabel,
                    avgMs,
                    maxMs,
                    typical,
                    Math.Round(ratio, 2),
                    a.BeeCount,
                    a.SlowCount,
                    a.FailedCount,
                    a.CanceledCount,
                    a.InFlightCount,
                    a.SampleError,
                    a.SampleBeeSequence,
                    color));
            }
            grid.Add(row);
        }

        string? note = null;
        if (missingDetailCount > 0 && count > 0)
            note = $"Step call detail available for {Format.Num(count - missingDetailCount)} of {Format.Num(count)} finished bees.";

        return new TerrainScene(TerrainGrouping.Steps, entry, lanes, grid, buckets, spanSec, maxDurationMs, ticks, note, missingDetailCount);
    }

    internal static bool IsPlaceholderDetail(BeeDetail detail) =>
        detail.Calls.Count == 1 && string.Equals(detail.Calls[0].Name, "Loading…", StringComparison.Ordinal) && detail.StepVisits is null;

    private static double ComputeRunSpanSeconds(RunSnapshot snapshot, int? entryFilter)
    {
        var maxStarted = 0.0;
        var anyBee = false;
        var startEntry = entryFilter is { } ef && ef >= 0 && ef < snapshot.Count ? ef : 0;
        var endEntry = entryFilter is { } ef2 && ef2 >= 0 && ef2 < snapshot.Count ? ef2 : snapshot.Count - 1;

        for (var e = startEntry; e <= endEntry; e++)
        {
            var bees = snapshot.Store.Bees(e);
            var count = FinishedCount(snapshot, e, bees.Count);
            for (var i = 0; i < count; i++)
            {
                anyBee = true;
                if (bees[i].StartedAt > maxStarted) maxStarted = bees[i].StartedAt;
            }
            if (snapshot.BeesInFlight is not null && e < snapshot.BeesInFlight.Length && snapshot.BeesInFlight[e].Count > 0)
            {
                anyBee = true;
                foreach (var r in snapshot.BeesInFlight[e])
                    if (r.StartedAt > maxStarted) maxStarted = r.StartedAt;
            }
        }

        if (!anyBee && snapshot.State == FakeRunState.Idle && snapshot.Elapsed <= 0)
            return 0;

        var span = Math.Max(snapshot.Elapsed, maxStarted);
        return anyBee ? Math.Max(1.0, span) : span;
    }

    private static int FinishedCount(RunSnapshot snapshot, int entry, int storeCount) =>
        entry >= 0 && entry < snapshot.Scenarios.Length ? Math.Min(storeCount, snapshot.Scenarios[entry].Finished) : storeCount;

    private static int BucketIndex(double startedAt, double bucketWidth, int buckets) =>
        Math.Clamp((int)Math.Floor(Math.Max(0, startedAt) / bucketWidth), 0, buckets - 1);

    private static int FindBandIndex(IReadOnlyList<LatencyBand> bands, double durationMs)
    {
        for (var i = 0; i < bands.Count - 1; i++)
            if (durationMs < bands[i].MaxMs) return i;
        return bands.Count - 1;
    }

    private static int NearestLadderIndex(double targetMs, int minIdx, int maxIdx)
    {
        var bestIdx = minIdx;
        var bestDist = double.MaxValue;
        for (var i = minIdx; i <= maxIdx; i++)
        {
            var dist = Math.Abs(Math.Log(NiceLadderMs[i]) - Math.Log(targetMs));
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    private static IReadOnlyList<TimeAxisTick> BuildTimeTicks(double spanSec)
    {
        const int segments = 6;
        var ticks = new List<TimeAxisTick>(segments + 1);
        for (var i = 0; i <= segments; i++)
        {
            var frac = i / (double)segments;
            ticks.Add(new TimeAxisTick(frac, Format.Time(frac * spanSec)));
        }
        return ticks;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var copy = values.ToArray();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    private sealed class TerrainAccumulator
    {
        public int BeeCount;
        public int SlowCount;
        public int FailedCount;
        public int CanceledCount;
        public int InFlightCount;
        public double DurationSum;
        public double MaxDurationMs;
        public double MaxInFlightMs;
        public double MaxNonFailedRatio = 1.0;
        public string? SampleError;
        public int? SampleBeeSequence;
        private double sampleScore = -1;

        public void AddFinished(in BeeRecord bee)
        {
            BeeCount++;
            DurationSum += bee.DurationMs;
            if (bee.DurationMs > MaxDurationMs) MaxDurationMs = bee.DurationMs;
            if (bee.Failed)
            {
                FailedCount++;
                if (bee.Canceled) CanceledCount++;
                SampleError ??= bee.Error;
                if (sampleScore < 1_000_000)
                {
                    sampleScore = 1_000_000;
                    SampleBeeSequence = bee.Sequence;
                }
            }
            else
            {
                if (bee.Outcome == BeeOutcome.Slow) SlowCount++;
                var r = bee.Ratio;
                if (r > MaxNonFailedRatio) MaxNonFailedRatio = r;
                var score = (bee.Outcome == BeeOutcome.Slow ? 10_000 : 0) + bee.DurationMs;
                if (score > sampleScore)
                {
                    sampleScore = score;
                    SampleBeeSequence = bee.Sequence;
                }
            }
        }

        public void AddStepSample(double durationMs, bool failed, bool canceled, bool slow, double ratio, string? error, int sequence)
        {
            BeeCount++;
            DurationSum += durationMs;
            if (durationMs > MaxDurationMs) MaxDurationMs = durationMs;
            if (failed)
            {
                FailedCount++;
                if (canceled) CanceledCount++;
                SampleError ??= error;
                if (sampleScore < 1_000_000)
                {
                    sampleScore = 1_000_000;
                    SampleBeeSequence = sequence;
                }
            }
            else
            {
                if (slow) SlowCount++;
                if (ratio > MaxNonFailedRatio) MaxNonFailedRatio = ratio;
                var score = (slow ? 10_000 : 0) + durationMs;
                if (score > sampleScore)
                {
                    sampleScore = score;
                    SampleBeeSequence = sequence;
                }
            }
        }

        public void AddInFlight(double elapsedMs)
        {
            InFlightCount++;
            if (elapsedMs > MaxInFlightMs) MaxInFlightMs = elapsedMs;
        }
    }

    private sealed class SurfaceAccumulator
    {
        public int Count;
        public int SlowCount;
        public int FailedCount;
        public double DurationSum;
        public double MaxDurationMs;
        public double MaxNonFailedRatio = 1.0;
        public string? SampleError;
        public int? SampleBeeSequence;
        private double sampleScore = -1;
        private readonly Dictionary<int, (int Total, int Failed)> byEntry = [];

        public void Add(in BeeRecord bee)
        {
            Count++;
            DurationSum += bee.DurationMs;
            if (bee.DurationMs > MaxDurationMs) MaxDurationMs = bee.DurationMs;

            byEntry.TryGetValue(bee.Entry, out var cur);
            byEntry[bee.Entry] = (cur.Total + 1, cur.Failed + (bee.Failed ? 1 : 0));

            if (bee.Failed)
            {
                FailedCount++;
                SampleError ??= bee.Error;
                if (sampleScore < 1_000_000)
                {
                    sampleScore = 1_000_000;
                    SampleBeeSequence = bee.Sequence;
                }
            }
            else
            {
                if (bee.Outcome == BeeOutcome.Slow) SlowCount++;
                var r = bee.Ratio;
                if (r > MaxNonFailedRatio) MaxNonFailedRatio = r;
                var score = (bee.Outcome == BeeOutcome.Slow ? 10_000 : 0) + bee.DurationMs;
                if (score > sampleScore)
                {
                    sampleScore = score;
                    SampleBeeSequence = bee.Sequence;
                }
            }
        }

        public int? DominantEntry()
        {
            if (byEntry.Count == 0) return null;
            int? best = null;
            (int Total, int Failed) bestStats = (-1, -1);
            foreach (var (entry, stats) in byEntry)
            {
                if (best is null || stats.Failed > bestStats.Failed || (stats.Failed == bestStats.Failed && stats.Total > bestStats.Total))
                {
                    best = entry;
                    bestStats = stats;
                }
            }
            return best;
        }
    }

    private static string ScenarioDisplayName(RunSnapshot snapshot, int entry)
    {
        var label = entry >= 0 && entry < snapshot.Labels.Count && !string.IsNullOrWhiteSpace(snapshot.Labels[entry])
            ? snapshot.Labels[entry]
            : entry >= 0 && entry < snapshot.Plan.Scenarios.Count
                ? snapshot.Plan.Scenarios[entry].ScenarioId
                : $"Scenario {entry + 1}";
        return $"{entry + 1}. {label}";
    }

    private sealed class ErrorSignatureGroup(string signature)
    {
        public string Signature { get; } = signature;
        public double FirstSeenSec { get; private set; } = double.MaxValue;
        public double LastSeenSec { get; private set; } = double.MinValue;
        public int TotalCount { get; private set; }
        public int CanceledCount { get; private set; }
        public int? SampleBeeSequence { get; private set; }
        public int? SampleStep { get; private set; }
        public Dictionary<int, int> EntryCounts { get; } = [];
        public List<BeeRecord> Bees { get; } = [];

        public void Add(in BeeRecord bee)
        {
            Bees.Add(bee);
            TotalCount++;
            if (bee.Canceled) CanceledCount++;
            EntryCounts.TryGetValue(bee.Entry, out var c);
            EntryCounts[bee.Entry] = c + 1;

            if (bee.StartedAt < FirstSeenSec
                || (bee.StartedAt == FirstSeenSec && (SampleBeeSequence is null || bee.Sequence < SampleBeeSequence.Value)))
            {
                FirstSeenSec = bee.StartedAt;
                SampleBeeSequence = bee.Sequence;
            }
            if (bee.StartedAt > LastSeenSec) LastSeenSec = bee.StartedAt;
            if (SampleStep is null && bee.FailedStep >= 0)
                SampleStep = bee.FailedStep;
        }

        public int? DominantEntry()
        {
            if (EntryCounts.Count == 0) return null;
            int? best = null;
            var bestCount = -1;
            foreach (var (entry, cnt) in EntryCounts)
            {
                if (best is null || cnt > bestCount || (cnt == bestCount && entry < best.Value))
                {
                    best = entry;
                    bestCount = cnt;
                }
            }
            return best;
        }
    }

    private sealed class ErrorCellAccumulator
    {
        public int Count;
        public int CanceledCount;
        public double FirstOccurrenceSec = double.MaxValue;
        public int? SampleBeeSequence;
        public int? SampleEntry;
        public int? SampleStep;
        public HashSet<int> Entries { get; } = [];

        public void Add(in BeeRecord bee)
        {
            Count++;
            if (bee.Canceled) CanceledCount++;
            Entries.Add(bee.Entry);

            if (bee.StartedAt < FirstOccurrenceSec
                || (bee.StartedAt == FirstOccurrenceSec && (SampleBeeSequence is null || bee.Sequence < SampleBeeSequence.Value)))
            {
                FirstOccurrenceSec = bee.StartedAt;
                SampleBeeSequence = bee.Sequence;
                SampleEntry = bee.Entry;
                if (bee.FailedStep >= 0) SampleStep = bee.FailedStep;
            }
            else if (SampleStep is null && bee.FailedStep >= 0)
            {
                SampleStep = bee.FailedStep;
            }
        }
    }
}
