using System.Text;
using System.Text.Json;

namespace Beesharp.Services;

/// <summary>Number of bee failures attributed to a single scenario step.</summary>
/// <param name="Step">Name of the step where bees failed.</param>
/// <param name="Count">Number of bees that failed at <paramref name="Step"/>.</param>
public sealed record StepFailureCount(string Step, int Count);

/// <summary>One scenario entry in the summary. <see cref="Failed"/> includes the canceled bees (brain 0053). Times are milliseconds of the bees that did not fail; null when there were none.</summary>
/// <param name="Number">One-based scenario entry index in the plan.</param>
/// <param name="Label">Display label of the scenario entry, falling back to its scenario ID.</param>
/// <param name="Started">Total number of bees started for this entry.</param>
/// <param name="Finished">Total number of bees that finished (succeeded, failed, or canceled).</param>
/// <param name="Failed">Total number of failed bees, including canceled bees.</param>
/// <param name="Canceled">Number of bees canceled when the run was stopped.</param>
/// <param name="Slow">Number of finished bees with at least one slow call.</param>
/// <param name="Dropped">Number of scheduled bees dropped because too many were already in flight.</param>
/// <param name="TypicalMs">Median (50th percentile) duration in milliseconds of successful bees, or <see langword="null"/> if none succeeded.</param>
/// <param name="Slowest5PercentMs">95th percentile duration in milliseconds of successful bees, or <see langword="null"/> if none succeeded.</param>
/// <param name="FailedPerStep">Optional breakdown of failures by step name when step metadata is available.</param>
/// <param name="MinMs">Minimum duration in milliseconds of successful bees, or <see langword="null"/> if none succeeded.</param>
/// <param name="AvgMs">Mean duration in milliseconds of successful bees, or <see langword="null"/> if none succeeded.</param>
/// <param name="MaxMs">Maximum duration in milliseconds of successful bees, or <see langword="null"/> if none succeeded.</param>
public sealed record EntrySummary(
    int Number, string Label, int Started, int Finished, int Failed, int Canceled, int Slow, int Dropped, double? TypicalMs, double? Slowest5PercentMs,
    IReadOnlyList<StepFailureCount>? FailedPerStep = null,
    double? MinMs = null, double? AvgMs = null, double? MaxMs = null);

/// <summary>Aggregated count for a distinct bee error message.</summary>
/// <param name="Error">Error message reported by failing bees.</param>
/// <param name="Count">Number of bees that failed with <paramref name="Error"/>.</param>
public sealed record ErrorCount(string Error, int Count);

/// <summary>Preflight or shutdown program outcome: <c>None</c> (the preset has none), <c>Succeeded</c>, <c>Failed</c> or <c>NotRun</c>.</summary>
/// <param name="State">Outcome state (<c>None</c>, <c>Succeeded</c>, <c>Failed</c>, or <c>NotRun</c>).</param>
/// <param name="Script">Script name or ID, or <see langword="null"/> when none was configured.</param>
/// <param name="Error">Error message if the program failed, or <see langword="null"/> otherwise.</param>
public sealed record ProgramOutcome(string State, string? Script, string? Error);

/// <summary>Everything the end of a run says (also the JSON written by <c>--json</c>). It uses the tool's words: bees, scenario entries, preflight, shutdown program (docs/context.md).</summary>
/// <param name="Preset">Name of the executed preset.</param>
/// <param name="Environment">Environment name used for placeholder resolution, or <see langword="null"/> if none was specified.</param>
/// <param name="RunId">Unique identifier assigned to the run.</param>
/// <param name="ElapsedSeconds">Total elapsed run duration in seconds.</param>
/// <param name="Stopped">Whether the run was stopped early (for example via Ctrl+C).</param>
/// <param name="RunError">Top-level run error message if the run failed unexpectedly, or <see langword="null"/>.</param>
/// <param name="Bees">Total finished bees across all scenario entries.</param>
/// <param name="FailedBees">Total failed bees (including canceled bees) across all scenario entries.</param>
/// <param name="CanceledBees">Total canceled bees across all scenario entries.</param>
/// <param name="SlowBees">Total slow bees across all scenario entries.</param>
/// <param name="DroppedBees">Total dropped bees across all scenario entries.</param>
/// <param name="Entries">Per-entry summaries in plan order.</param>
/// <param name="TopErrors">Most frequent error messages across all failed bees.</param>
/// <param name="Preflight">Outcome of the preflight script.</param>
/// <param name="Shutdown">Outcome of the shutdown program.</param>
/// <param name="StorageProvider">Name of the active storage provider (for example <c>DuckDb</c> or <c>Memory</c>).</param>
/// <param name="SavedTo">Human-readable description of where the run was saved, or <see langword="null"/> if not persisted.</param>
/// <param name="ExitCode">Process exit code determined by <see cref="ExitCodeFor"/>.</param>
/// <param name="ExportedTo">Destination path if the run was exported, or <see langword="null"/>.</param>
/// <param name="ExportedBytes">Byte size of the exported artifact, or <see langword="null"/> if not exported.</param>
public sealed record RunSummary(
    string Preset,
    string? Environment,
    string RunId,
    double ElapsedSeconds,
    bool Stopped,
    string? RunError,
    int Bees,
    int FailedBees,
    int CanceledBees,
    int SlowBees,
    int DroppedBees,
    IReadOnlyList<EntrySummary> Entries,
    IReadOnlyList<ErrorCount> TopErrors,
    ProgramOutcome Preflight,
    ProgramOutcome Shutdown,
    string StorageProvider,
    string? SavedTo,
    int ExitCode,
    string? ExportedTo = null,
    long? ExportedBytes = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Serializes this summary as indented JSON using web defaults.</summary>
    /// <returns>The JSON representation of this <see cref="RunSummary"/>.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>The exit code for a finished run (brain 0054): 2 could not run, 1 a bee failed or canceled / stopped / shutdown program failed, else 0.</summary>
    /// <param name="runError">Top-level run error, if any.</param>
    /// <param name="preflightState">Preflight outcome state.</param>
    /// <param name="stopped">Whether the run was stopped early.</param>
    /// <param name="failedBees">Total count of failed (including canceled) bees.</param>
    /// <param name="shutdownState">Shutdown program outcome state.</param>
    /// <returns>One of the <see cref="HeadlessExitCodes"/> constants.</returns>
    public static int ExitCodeFor(string? runError, string? preflightState, bool stopped, int failedBees, string? shutdownState)
    {
        if (stopped) return HeadlessExitCodes.BeesFailed; // a stopped run is not "could not run", even when it was stopped during the preflight
        if (preflightState == "Failed" || runError is not null) return HeadlessExitCodes.CouldNotRun;
        if (failedBees > 0 || shutdownState == "Failed") return HeadlessExitCodes.BeesFailed;
        return HeadlessExitCodes.Ok;
    }

    /// <summary>Builds the summary from the final status and all bees of a run.</summary>
    /// <param name="preset">Preset name executed.</param>
    /// <param name="environment">Optional environment name used for the run.</param>
    /// <param name="status">Final live run status snapshot.</param>
    /// <param name="bees">All recorded bees for the run.</param>
    /// <param name="stopped">Whether the run was stopped via cancellation.</param>
    /// <param name="savedTo">Optional display string describing where the run was saved.</param>
    /// <param name="topErrors">Maximum number of top distinct errors to include.</param>
    /// <param name="exportedTo">Optional destination path of an exported run archive/file.</param>
    /// <param name="exportedBytes">Optional byte length of the exported artifact.</param>
    /// <returns>A populated <see cref="RunSummary"/> instance.</returns>
    public static RunSummary Build(
        string preset, string? environment, LiveRunStatusDto status, IReadOnlyList<LiveBeeDto> bees, bool stopped, string? savedTo, int topErrors = 5,
        string? exportedTo = null, long? exportedBytes = null)
    {
        var entries = new List<EntrySummary>();
        for (var i = 0; i < status.Plan.Scenarios.Count; i++)
        {
            var view = i < status.Scenarios.Count ? status.Scenarios[i] : new LiveScenarioViewDto(0, 0, 0, 0, 0, 0, 0);
            var entryBees = bees.Where(b => b.Entry == i).ToList();
            var times = entryBees.Where(b => b.Error is null).Select(b => b.DurationMs).Order().ToArray();
            var config = status.Plan.Scenarios[i];
            IReadOnlyList<StepFailureCount>? failedSteps = null;
            if (status.Steps is not null && i < status.Steps.Count && status.Steps[i].Count > 0 && view.FailedPerStep is { Count: > 0 })
            {
                var stepList = status.Steps[i];
                var counts = new List<StepFailureCount>();
                for (var s = 0; s < Math.Min(stepList.Count, view.FailedPerStep.Count); s++)
                    if (view.FailedPerStep[s] > 0)
                        counts.Add(new StepFailureCount(stepList[s].Name, view.FailedPerStep[s]));
                if (counts.Count > 0) failedSteps = counts;
            }
            entries.Add(new EntrySummary(
                i + 1,
                string.IsNullOrWhiteSpace(config.Label) ? config.ScenarioId : config.Label,
                view.Starts, view.Finished, view.Failed,
                entryBees.Count(b => ExecutionResult.IsCanceledError(b.Error)),
                view.SlowBees, view.Dropped,
                times.Length == 0 ? null : Percentile(times, 0.5),
                times.Length == 0 ? null : Percentile(times, 0.95),
                failedSteps,
                times.Length == 0 ? null : times[0],
                times.Length == 0 ? null : times.Average(),
                times.Length == 0 ? null : times[^1]));
        }

        var errors = bees.Where(b => b.Error is not null)
            .GroupBy(b => b.Error!)
            .Select(g => new ErrorCount(g.Key, g.Count()))
            .OrderByDescending(e => e.Count).ThenBy(e => e.Error, StringComparer.Ordinal)
            .Take(topErrors).ToList();

        var failed = entries.Sum(e => e.Failed);
        var shutdown = Outcome(status.ShutdownState, status.ShutdownScriptName, status.ShutdownError);
        return new RunSummary(
            preset, environment, status.RunId, status.ElapsedSeconds, stopped, status.Error,
            entries.Sum(e => e.Finished), failed, entries.Sum(e => e.Canceled), entries.Sum(e => e.Slow), entries.Sum(e => e.Dropped),
            entries, errors,
            Outcome(status.PreflightState, status.PreflightScriptName, status.PreflightError),
            shutdown,
            status.StorageProvider, savedTo,
            ExitCodeFor(status.Error, status.PreflightState, stopped, failed, status.ShutdownState),
            exportedTo, exportedBytes);
    }

    private static ProgramOutcome Outcome(string? state, string? script, string? error) =>
        new(state ?? "None", script, error);

    /// <summary>Nearest-rank percentile of a sorted array.</summary>
    /// <param name="sorted">Ascending-sorted array of numeric values (must be non-empty).</param>
    /// <param name="p">Percentile fraction in the range <c>[0, 1]</c> (for example <c>0.5</c> or <c>0.95</c>).</param>
    /// <returns>The value at the nearest-rank percentile.</returns>
    public static double Percentile(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    /// <summary>Plain text for terminals and CI logs: no colour codes, no cursor tricks.</summary>
    /// <returns>Multi-line human-readable summary text.</returns>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Summary");
        sb.AppendLine($"  Run         {RunId}{(Stopped ? "  (stopped with Ctrl+C)" : "")}");
        sb.AppendLine($"  Preset      {Preset}{(Environment is null ? "" : $"   Environment {Environment}")}");
        sb.AppendLine($"  Run time    {Duration(ElapsedSeconds)}");
        sb.AppendLine($"  Preflight   {Describe(Preflight)}");
        sb.AppendLine($"  Shutdown    {Describe(Shutdown)}");
        if (RunError is not null) sb.AppendLine($"  Run error   {RunError}");
        sb.AppendLine();

        var label = Math.Max(5, Entries.Max(e => (int?)Label(e).Length) ?? 5);
        sb.AppendLine($"  {"Entry".PadRight(label)}  {"finished",8}  {"failed",6}  {"slow",5}  {"min",8}  {"typical",9}  {"avg",8}  {"slowest 5%",10}  {"max",8}");
        foreach (var e in Entries)
        {
            sb.AppendLine($"  {Label(e).PadRight(label)}  {e.Finished,8}  {e.Failed,6}  {e.Slow,5}  {Ms(e.MinMs),8}  {Ms(e.TypicalMs),9}  {Ms(e.AvgMs),8}  {Ms(e.Slowest5PercentMs),10}  {Ms(e.MaxMs),8}" +
                (e.Dropped > 0 ? $"   ({e.Dropped} dropped: too many in flight)" : ""));
            if (e.FailedPerStep is { Count: > 0 })
                sb.AppendLine($"    failed at {string.Join(", ", e.FailedPerStep.Select(f => $"{f.Step}: {f.Count}"))}");
        }
        sb.AppendLine($"  {"All".PadRight(label)}  {Bees,8}  {FailedBees,6}  {SlowBees,5}");
        if (CanceledBees > 0) sb.AppendLine($"  {CanceledBees} of the failed bees were canceled when the run stopped.");

        if (TopErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Most common errors");
            foreach (var e in TopErrors)
                sb.AppendLine($"    {e.Count,6} x  {Shorten(e.Error)}");
        }

        sb.AppendLine();
        sb.AppendLine(SavedTo is not null ? $"  Saved to    {SavedTo}" : $"  Not saved   ({(StorageProvider == "Memory" ? "--storage memory keeps nothing" : "the run never started")})");
        if (ExportedTo is not null)
            sb.AppendLine($"  Exported to {ExportedTo}{(ExportedBytes is { } bytes ? $" ({FormatBytes(bytes)})" : "")}");
        if (Bees == 0 && RunError is null) sb.AppendLine("  No bee finished. Check the plan's duration and rates.");
        sb.AppendLine();
        sb.AppendLine(ExitCode switch
        {
            HeadlessExitCodes.Ok => $"Result: all {Bees} bees succeeded (exit code 0).",
            HeadlessExitCodes.BeesFailed when FailedBees > 0 => $"Result: {FailedBees} of {Bees} bees failed (exit code 1).",
            HeadlessExitCodes.BeesFailed when Stopped => "Result: the run was stopped (exit code 1).",
            HeadlessExitCodes.BeesFailed => "Result: the shutdown program failed (exit code 1).",
            _ => "Result: the run could not run (exit code 2).",
        });
        return sb.ToString();
    }

    private static string Label(EntrySummary e) => $"#{e.Number} {e.Label}";

    private static string Describe(ProgramOutcome o) => o.State switch
    {
        "None" => "none in this preset",
        "Succeeded" => $"succeeded ({o.Script})",
        "Failed" => $"failed ({o.Script}): {o.Error}",
        _ => $"{o.State.ToLowerInvariant()} ({o.Script})",
    };

    /// <summary>Formats a duration in seconds as either <c>0.0 s</c> or <c>M min S s</c>.</summary>
    /// <param name="seconds">Duration in seconds.</param>
    /// <returns>Human-readable duration string.</returns>
    public static string Duration(double seconds) =>
        seconds < 60 ? $"{seconds:0.0} s" : $"{(int)(seconds / 60)} min {(int)(seconds % 60)} s";

    /// <summary>Formats an elapsed time in seconds as <c>MM:SS</c> or <c>HH:MM:SS</c>.</summary>
    /// <param name="seconds">Elapsed seconds (negative values are clamped to zero).</param>
    /// <returns>Clock-formatted timestamp string.</returns>
    public static string FormatClock(double seconds)
    {
        var total = Math.Max(0, (int)Math.Floor(seconds));
        var h = total / 3600;
        var m = (total % 3600) / 60;
        var s = total % 60;
        return h > 0 ? $"{h:00}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    /// <summary>Formats a duration in milliseconds as <c>-</c>, whole milliseconds (<c>ms</c>), or seconds (<c>s</c>).</summary>
    /// <param name="ms">Duration in milliseconds, or <see langword="null"/> when unavailable.</param>
    /// <returns>Formatted duration string.</returns>
    public static string Ms(double? ms) => ms is null ? "-" : ms.Value >= 1000 ? $"{ms.Value / 1000:0.00} s" : $"{ms.Value:0} ms";

    /// <summary>Formats a byte count into <c>B</c>, <c>KB</c>, or <c>MB</c>.</summary>
    /// <param name="bytes">Size in bytes.</param>
    /// <returns>Human-readable byte size string.</returns>
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB",
    };

    /// <summary>Collapses line endings and truncates an error message to at most <paramref name="maxLength"/> characters.</summary>
    /// <param name="error">Error text to shorten.</param>
    /// <param name="maxLength">Maximum length of the returned string, including ellipsis when truncated.</param>
    /// <returns>Single-line shortened error string.</returns>
    public static string Shorten(string error, int maxLength = 110)
    {
        var line = error.ReplaceLineEndings(" ").Trim();
        return line.Length <= maxLength ? line : line[..(maxLength - 3)] + "...";
    }
}
