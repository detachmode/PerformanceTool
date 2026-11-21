using System.Text.Json.Nodes;
using PerformanceTool.Services;

namespace PerformanceTool.Ai;

// The ports (brain 0047/0051): what the assistant needs from the app. The host implements them as thin adapters over its own services,
// the assistant never references the host. All indexes here are 0-based (entry index, bee sequence), like the app's own data;
// the tools translate to the 1-based numbers people see.

/// <summary>The Live plan being edited, as the server holds it.</summary>
public sealed record PlanState(LoadPlan Plan, string PresetName, LoadPlan SavedPlan, bool HasUnsavedChanges, long Version);

public interface IPlanWorkspacePort
{
    PlanState Get();
    /// <summary>Changes a copy of the plan under the workspace's lock; <paramref name="edit"/> returns false when nothing changed.</summary>
    PlanState Update(Func<LoadPlan, bool> edit);
    /// <summary>Adds an entry with default parameters and returns its index.</summary>
    int AddEntry(string scenarioId);
    PlanState RemoveEntry(int index);
    Task<IReadOnlyList<PresetInfo>> ListPresetsAsync(CancellationToken ct);
    Task SavePresetAsync(string name, CancellationToken ct);
    /// <summary>Loads a preset into the plan. False when there is none with that name.</summary>
    Task<bool> LoadPresetAsync(string name, CancellationToken ct);
    Task<bool> DeletePresetAsync(string name, CancellationToken ct);
}

public sealed record PresetInfo(string Name, DateTimeOffset SavedAt, int Entries, int DurationSeconds);

/// <summary>Figures of one scenario entry in a run.</summary>
public sealed record EntryFigures(int Entry, string Label, int Starts, int Finished, int InFlight, int Failed, int SlowBees, int SlowCalls, int Dropped, double BeesPerMinute);

public sealed record RunInfo(
    string RunId,
    string State,
    double ElapsedSeconds,
    DateTimeOffset StartedUtc,
    int TotalBees,
    int FailedBees,
    int SlowBees,
    long TotalLogs,
    string? Error,
    IReadOnlyList<EntryFigures> Entries,
    string? PreflightState = null,
    string? PreflightError = null);

public interface IRunPort
{
    /// <summary>The current run (running or last finished), or null when the server never had one.</summary>
    RunInfo? GetCurrent();
    Task<RunInfo> StartAsync(string? environmentName, CancellationToken ct);
    Task<RunInfo> StopAsync(CancellationToken ct);
}

public enum BeeOutcomeFilter { All, Ok, Slow, Failed }

public sealed record BeeInfo(int Sequence, int Entry, double StartedAt, double DurationMs, int? StatusCode, long Bytes, string? Error, int SlowCalls)
{
    public string Outcome => Error is not null ? "failed" : SlowCalls > 0 ? "slow" : "ok";
}

public sealed record BeeCallInfo(string Name, double OffsetMs, double DurationMs, int? StatusCode, string? Error);
public sealed record BeeLogInfo(double AtSeconds, string Level, string Message, string? Exception, int? CallIndex);
public sealed record BeeDetailInfo(BeeInfo Bee, IReadOnlyList<BeeCallInfo> Calls, IReadOnlyList<BeeLogInfo> Logs);

public sealed record BeePage(long Total, IReadOnlyList<BeeInfo> Items);

/// <summary>Filters for log queries. <see cref="Bee"/> and <see cref="Entry"/> are 0-based.</summary>
public sealed record LogFilter(
    int? Entry = null,
    int? Bee = null,
    bool? FailedBees = null,
    bool? SlowBees = null,
    IReadOnlyList<string>? Levels = null,
    string? Search = null,
    int Offset = 0,
    int Limit = 20);

public sealed record LogLineInfo(long Id, int Sequence, int Entry, double AtSeconds, string Level, string Message, string? Exception);
public sealed record LogPageInfo(long Total, IReadOnlyList<LogLineInfo> Items);

/// <summary>Past and current runs, their bees, calls and logs.</summary>
public interface IRunHistoryPort
{
    /// <summary>Id of the current run, or of the newest stored run; null when there is none.</summary>
    Task<string?> LatestRunIdAsync(CancellationToken ct);
    Task<IReadOnlyList<RunInfo>> ListRunsAsync(int limit, CancellationToken ct);
    Task<RunInfo?> GetRunAsync(string runId, CancellationToken ct);
    /// <summary>Bees of a run, failed first, then slow, then ok, within each group by start time (brain 0028).</summary>
    Task<BeePage> ListBeesAsync(string runId, int? entry, BeeOutcomeFilter outcome, int offset, int limit, CancellationToken ct);
    /// <summary>Bees by sequence (0-based); unknown sequences are left out.</summary>
    Task<IReadOnlyList<BeeInfo>> GetBeesAsync(string runId, IReadOnlyList<int> sequences, CancellationToken ct);
    Task<BeeDetailInfo?> GetBeeAsync(string runId, int sequence, CancellationToken ct);
    Task<LogPageInfo> QueryLogsAsync(string runId, LogFilter filter, CancellationToken ct);
}

/// <summary>An environment as the assistant may see it. Secret-looking values are masked by the adapter's caller, not here.</summary>
public sealed record EnvironmentInfo(string Name, IReadOnlyList<KeyValuePair<string, string>> Variables);

public interface IEnvironmentPort
{
    Task<IReadOnlyList<EnvironmentInfo>> ListAsync(CancellationToken ct);
    Task<EnvironmentInfo?> GetAsync(string name, CancellationToken ct);
    /// <summary>Creates or replaces. Throws <see cref="ArgumentException"/> with the reason when it is not valid.</summary>
    Task SaveAsync(EnvironmentInfo environment, CancellationToken ct);
    Task<bool> DeleteAsync(string name, CancellationToken ct);
}

public sealed record ScriptInfo(string ScriptId, string FileName, string Name, string? ScenarioId, string Description, DateTimeOffset UpdatedUtc, string Code);
public sealed record Diagnostic(string Severity, int Line, int Column, string Message);
public sealed record CompileOutcome(bool Success, ScriptInfo? Script, IReadOnlyList<Diagnostic> Diagnostics, string? Error);
public sealed record TestCallInfo(string Method, string Url, int? StatusCode, double DurationMs, string? Error);
public sealed record ScriptTestOutcome(bool Success, int? StatusCode, double DurationMs, string? Error, IReadOnlyList<TestCallInfo> Calls, IReadOnlyList<string> Logs);
public sealed record PreflightTestOutcome(bool Success, double DurationMs, string? Error, IReadOnlyList<string> Logs);

/// <summary>Custom C# scenario scripts and preflight scripts (brain 0042).</summary>
public interface IScriptPort
{
    IReadOnlyList<ScriptInfo> ListScripts();
    ScriptInfo? GetScript(string id);
    Task<CompileOutcome> UploadScriptAsync(string? id, string? fileName, string code, CancellationToken ct);
    Task<ScriptTestOutcome> TestRunScriptAsync(string id, JsonObject? parameters, string? environmentName, int timeoutSeconds, CancellationToken ct);
    bool DeleteScript(string id);

    IReadOnlyList<ScriptInfo> ListPreflights();
    ScriptInfo? GetPreflight(string id);
    Task<CompileOutcome> UploadPreflightAsync(string? id, string? fileName, string code, CancellationToken ct);
    Task<PreflightTestOutcome> TestRunPreflightAsync(string id, string? environmentName, int timeoutSeconds, CancellationToken ct);
    bool DeletePreflight(string id);
}
