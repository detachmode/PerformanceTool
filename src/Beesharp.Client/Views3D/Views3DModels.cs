using Beesharp.Client.Runs;

namespace Beesharp.Client.Views3D;

/// <summary>Which 3D visualization is active on the 3D Views page.</summary>
public enum Views3DKind
{
    Terrain,
    Surface,
    RunHistory,
    ErrorCascade,
    SwarmMatrix,
}

/// <summary>How error signature lanes are ordered in the 4. Error Cascade view.</summary>
public enum ErrorCascadeSort
{
    FirstSeen,
    MostFrequent,
}

/// <summary>Which bar segments are shown in the 5. Swarm: Dead or Alive matrix view.</summary>
public enum SwarmDisplayMode
{
    Stacked,
    ActiveOnly,
    FailedOnly,
}

/// <summary>How depth lanes are grouped in the Terrain view.</summary>
public enum TerrainGrouping
{
    Scenarios,
    Steps,
}

/// <summary>Camera angle presets for the 3D scene.</summary>
public enum CameraPreset
{
    Iso,
    Top,
    Front,
    Side,
}

/// <summary>An axis tick along the X (run time) axis.</summary>
public sealed record TimeAxisTick(double Fraction, string Label);

/// <summary>One depth lane in the Scenario &amp; Step Terrain view.</summary>
public sealed record TerrainLane(
    int Index,
    int Entry,
    int StepIndex,
    string Name,
    string Meta,
    double TypicalMs,
    int TotalBees,
    int FailedBees,
    int SlowBees);

/// <summary>One (lane × time bucket) cell in the Scenario &amp; Step Terrain view.</summary>
public sealed record TerrainCell(
    int LaneIndex,
    int Entry,
    int? StepIndex,
    int BucketIndex,
    double FromSec,
    double ToSec,
    string TimeLabel,
    double AvgDurationMs,
    double MaxDurationMs,
    double TypicalMs,
    double Ratio,
    int BeeCount,
    int SlowCount,
    int FailedCount,
    int CanceledCount,
    int InFlightCount,
    string? SampleError,
    int? SampleBeeSequence,
    int[] ColorRgb);

/// <summary>Complete projected data for Graph 1: Scenario &amp; Step Terrain.</summary>
public sealed record TerrainScene(
    TerrainGrouping Grouping,
    int? ScenarioEntry,
    IReadOnlyList<TerrainLane> Lanes,
    IReadOnlyList<IReadOnlyList<TerrainCell>> Grid,
    int TimeBuckets,
    double RunDurationSec,
    double MaxDurationMs,
    IReadOnlyList<TimeAxisTick> TimeTicks,
    string? Note = null,
    int MissingDetailCount = 0)
{
    public bool HasData => TimeBuckets > 0 && Lanes.Count > 0 && Grid.Any(r => r.Any(c => c.BeeCount > 0 || c.InFlightCount > 0));
}

/// <summary>One logarithmic/adaptive duration band in the Latency Distribution Surface view.</summary>
public sealed record LatencyBand(
    int Index,
    string Label,
    double MinMs,
    double MaxMs,
    bool IsSlow);

/// <summary>One (latency band × time bucket) cell in the Latency Distribution Surface view.</summary>
public sealed record SurfaceCell(
    int BandIndex,
    string BandLabel,
    double BandMinMs,
    double BandMaxMs,
    int BucketIndex,
    double FromSec,
    double ToSec,
    string TimeLabel,
    int Count,
    int SlowCount,
    int FailedCount,
    double AvgDurationMs,
    double MaxDurationMs,
    string? SampleError,
    int? ScenarioEntryFilter,
    int? DominantEntry,
    int? SampleBeeSequence,
    int[] ColorRgb);

/// <summary>Complete projected data for Graph 3: Latency Distribution Surface.</summary>
public sealed record SurfaceScene(
    int? ScenarioEntryFilter,
    IReadOnlyList<LatencyBand> Bands,
    IReadOnlyList<IReadOnlyList<SurfaceCell>> Grid,
    int TimeBuckets,
    double RunDurationSec,
    int MaxCount,
    IReadOnlyList<TimeAxisTick> TimeTicks,
    double TypicalMs)
{
    public bool HasData => TimeBuckets > 0 && Bands.Count > 0 && MaxCount > 0;
}

/// <summary>One run's scenario data fed into <see cref="Views3DProjection.BuildRunHistory"/>.</summary>
public sealed record HistoryRunInput(
    string RunId,
    string Label,
    DateTimeOffset StartedUtc,
    string State,
    double ElapsedSeconds,
    int Entry,
    bool IsCurrent,
    bool SetupMatches,
    string SetupBadge,
    IReadOnlyList<BeeRecord> Bees,
    int FinishedCount,
    IReadOnlyList<RunningBee>? RunningBees = null,
    int? TotalBeesOverride = null,
    int? FailedBeesOverride = null,
    int? SlowBeesOverride = null);

/// <summary>A matching previous run discovered in Live or Fake mode that can be toggled in the sidebar.</summary>
public sealed record HistoryRunCandidate(
    string RunId,
    string Label,
    DateTimeOffset StartedUtc,
    string State,
    double ElapsedSeconds,
    int Entry,
    bool SetupMatches,
    string SetupBadge,
    int TotalBees,
    int FailedBees,
    int SlowBees);

/// <summary>One run lane along the Z axis in the 3. Run History view.</summary>
public sealed record HistoryLane(
    int Index,
    string RunId,
    string Name,
    string ShortName,
    string Meta,
    DateTimeOffset StartedUtc,
    string State,
    double ElapsedSeconds,
    int Entry,
    bool IsCurrent,
    bool IsStopped,
    bool SetupMatches,
    string SetupBadge,
    double TypicalMs,
    int TotalBees,
    int FailedBees,
    int SlowBees,
    double EndFraction,
    string? EndLabel);

/// <summary>One (run lane × time slot) bar in the 3. Run History view, matching the 2D <c>BeeBars</c> bar model.</summary>
public sealed record HistoryCell(
    int LaneIndex,
    string RunId,
    int Entry,
    int BucketIndex,
    double FromSec,
    double ToSec,
    double StartSec,
    string TimeLabel,
    bool IsSingleBee,
    int? BeeSequence,
    int BeeCount,
    int SlowCount,
    int FailedCount,
    int CanceledCount,
    int RunningCount,
    double MaxDurationMs,
    double Ratio,
    int Step,
    string? SampleError,
    int[] ColorRgb);

/// <summary>Complete projected data for Tab 3: Run History comparison across runs for one scenario.</summary>
public sealed record RunHistoryScene(
    int ScenarioEntry,
    string ScenarioLabel,
    string ScenarioSetup,
    IReadOnlyList<HistoryLane> Lanes,
    IReadOnlyList<IReadOnlyList<HistoryCell>> Grid,
    int TimeBuckets,
    double BucketWidthSec,
    double MaxDurationSec,
    double MaxDurationMs,
    IReadOnlyList<TimeAxisTick> TimeTicks,
    string? Note = null)
{
    public bool HasData => TimeBuckets > 0 && Lanes.Count > 0 && Grid.Any(r => r.Any(c => c.BeeCount > 0 || c.RunningCount > 0));
}

/// <summary>One distinct error signature lane along the Z axis in the 4. Error Cascade view.</summary>
public sealed record ErrorCascadeLane(
    int Index,
    string Signature,
    string DisplayLabel,
    string Meta,
    double FirstSeenSec,
    double LastSeenSec,
    int TotalCount,
    int CanceledCount,
    IReadOnlyList<int> AffectedEntries,
    IReadOnlyList<string> AffectedEntryLabels,
    int? DominantEntry,
    int? SampleBeeSequence,
    int? SampleStep);

/// <summary>One (error signature × time bucket) cell in the 4. Error Cascade view.</summary>
public sealed record ErrorCascadeCell(
    int LaneIndex,
    string Signature,
    string DisplayLabel,
    int BucketIndex,
    double FromSec,
    double ToSec,
    string TimeLabel,
    int Count,
    int CanceledCount,
    double FirstOccurrenceSec,
    int LaneTotalCount,
    IReadOnlyList<int> AffectedEntries,
    IReadOnlyList<string> AffectedEntryLabels,
    int? SampleBeeSequence,
    int? SampleEntry,
    int? SampleStep,
    int[] ColorRgb);

/// <summary>Complete projected data for Tab 4: Error Cascade (Time × Distinct Error Signature × Count).</summary>
public sealed record ErrorCascadeScene(
    ErrorCascadeSort Sort,
    int? ScenarioEntryFilter,
    IReadOnlyList<ErrorCascadeLane> Lanes,
    IReadOnlyList<IReadOnlyList<ErrorCascadeCell>> Grid,
    int TimeBuckets,
    double RunDurationSec,
    int MaxCount,
    int TotalErrors,
    int DistinctErrorCount,
    IReadOnlyList<TimeAxisTick> TimeTicks)
{
    public bool HasData => TimeBuckets > 0 && Lanes.Count > 0 && MaxCount > 0;
}

/// <summary>One step-to-step transition edge observed across bees in a scenario.</summary>
public sealed record SwarmTransitionArc(
    int ScenarioIndex,
    int FromStep,
    int ToStep,
    int Count,
    string Kind);

/// <summary>One scenario lane along the Z axis in the 5. Swarm: Dead or Alive matrix view.</summary>
public sealed record SwarmLane(
    int Index,
    int Entry,
    string Name,
    string Meta,
    int DeclaredSteps,
    int ActiveBees,
    int FailedBees,
    int SlowBees,
    string SamplePath);

/// <summary>One (scenario × step) cell in the 5. Swarm: Dead or Alive matrix view.</summary>
public sealed record SwarmStepCell(
    int ScenarioIndex,
    int Entry,
    int StepIndex,
    int TotalSteps,
    string ScenarioName,
    string StepName,
    bool IsEmptyPad,
    int ActiveCount,
    int FailedCount,
    int SlowCount,
    int SelfRepeatCount,
    int BackLoopCount,
    double AvgDurationMs,
    double TypicalMs,
    double Ratio,
    string? SampleError,
    int? SampleBeeSequence,
    int[] ActiveColorRgb);

/// <summary>Complete projected data for Tab 5: Swarm: Dead or Alive (Scenario × Step × Active/Failed).</summary>
public sealed record SwarmScene(
    SwarmDisplayMode Mode,
    int MaxSteps,
    int MaxActive,
    int MaxFailed,
    int MaxTotal,
    IReadOnlyList<string> StepHeaders,
    IReadOnlyList<SwarmLane> Lanes,
    IReadOnlyList<IReadOnlyList<SwarmStepCell>> Grid,
    IReadOnlyList<SwarmTransitionArc> Arcs,
    bool IsRunning)
{
    public bool HasData => Lanes.Count > 0 && MaxSteps > 0;
}

/// <summary>Key/value row rendered inside the JS hover tooltip.</summary>
public sealed record JsTooltipRow(string Label, string Value);

/// <summary>One 3D column sent to <c>views-3d.js</c>.</summary>
public sealed record JsSceneCell(
    int LaneIndex,
    int BucketIndex,
    double Height,
    int[] Color,
    string TooltipTitle,
    IReadOnlyList<JsTooltipRow> TooltipRows,
    string? TooltipError,
    double SecondaryHeight = 0,
    int[]? SecondaryColor = null,
    bool IsEmptyPad = false,
    string? StepName = null,
    string? BadgeText = null,
    bool AlertBadge = false);

/// <summary>One depth lane label sent to <c>views-3d.js</c>.</summary>
public sealed record JsSceneLane(
    int Index,
    string Name,
    bool Highlight,
    double? EndFraction = null,
    string? EndLabel = null,
    bool IsStopped = false,
    int? DeclaredSteps = null);

/// <summary>One step-to-step jump/flow arc sent to <c>views-3d.js</c> for Swarm mode.</summary>
public sealed record JsSceneArc(
    int ScenarioIndex,
    int FromStep,
    int ToStep,
    int Count,
    string Kind);

/// <summary>Serialised scene payload consumed by <c>views-3d.js</c>.</summary>
public sealed record JsScenePayload(
    string Kind,
    double XSpan,
    double ZSpan,
    int TimeBuckets,
    IReadOnlyList<JsSceneLane> Lanes,
    IReadOnlyList<TimeAxisTick> TimeTicks,
    IReadOnlyList<JsSceneCell> Cells,
    IReadOnlyList<JsSceneArc>? Arcs = null,
    IReadOnlyList<string>? StepHeaders = null,
    bool IsRunning = false);
