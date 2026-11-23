using System.Text;
using System.Text.Json;
using Beesharp.Services;

namespace Beesharp.Cli;

public sealed record StepFailureCount(string Step, int Count);

/// <summary>One scenario entry in the summary. <see cref="Failed"/> includes the canceled bees (brain 0053). Times are milliseconds of the bees that did not fail; null when there were none.</summary>
public sealed record EntrySummary(
    int Number, string Label, int Started, int Finished, int Failed, int Canceled, int Slow, int Dropped, double? TypicalMs, double? Slowest5PercentMs,
    IReadOnlyList<StepFailureCount>? FailedPerStep = null);

public sealed record ErrorCount(string Error, int Count);

/// <summary>Preflight or shutdown program outcome: <c>None</c> (the preset has none), <c>Succeeded</c>, <c>Failed</c> or <c>NotRun</c>.</summary>
public sealed record ProgramOutcome(string State, string? Script, string? Error);

/// <summary>Everything the end of a run says (also the JSON written by <c>--json</c>). It uses the tool's words: bees, scenario entries, preflight, shutdown program (docs/context.md).</summary>
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
    int ExitCode)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>The exit code for a finished run (brain 0054): 2 could not run, 1 a bee failed or canceled / stopped / shutdown program failed, else 0.</summary>
    public static int ExitCodeFor(string? runError, string? preflightState, bool stopped, int failedBees, string? shutdownState)
    {
        if (stopped) return ExitCodes.BeesFailed; // a stopped run is not "could not run", even when it was stopped during the preflight
        if (preflightState == "Failed" || runError is not null) return ExitCodes.CouldNotRun;
        if (failedBees > 0 || shutdownState == "Failed") return ExitCodes.BeesFailed;
        return ExitCodes.Ok;
    }

    /// <summary>Builds the summary from the final status and all bees of a run.</summary>
    public static RunSummary Build(
        string preset, string? environment, LiveRunStatusDto status, IReadOnlyList<LiveBeeDto> bees, bool stopped, string? savedTo, int topErrors = 5)
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
                failedSteps));
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
            ExitCodeFor(status.Error, status.PreflightState, stopped, failed, status.ShutdownState));
    }

    private static ProgramOutcome Outcome(string? state, string? script, string? error) =>
        new(state ?? "None", script, error);

    /// <summary>Nearest-rank percentile of a sorted array.</summary>
    public static double Percentile(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    /// <summary>Plain text for terminals and CI logs: no colour codes, no cursor tricks.</summary>
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
        sb.AppendLine($"  {"Entry".PadRight(label)}  {"finished",8}  {"failed",6}  {"slow",5}  {"typical",9}  {"slowest 5%",10}");
        foreach (var e in Entries)
        {
            sb.AppendLine($"  {Label(e).PadRight(label)}  {e.Finished,8}  {e.Failed,6}  {e.Slow,5}  {Ms(e.TypicalMs),9}  {Ms(e.Slowest5PercentMs),10}" +
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
        if (Bees == 0 && RunError is null) sb.AppendLine("  No bee finished. Check the plan's duration and rates.");
        sb.AppendLine();
        sb.AppendLine(ExitCode switch
        {
            ExitCodes.Ok => $"Result: all {Bees} bees succeeded (exit code 0).",
            ExitCodes.BeesFailed when FailedBees > 0 => $"Result: {FailedBees} of {Bees} bees failed (exit code 1).",
            ExitCodes.BeesFailed when Stopped => "Result: the run was stopped (exit code 1).",
            ExitCodes.BeesFailed => "Result: the shutdown program failed (exit code 1).",
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

    internal static string Duration(double seconds) =>
        seconds < 60 ? $"{seconds:0.0} s" : $"{(int)(seconds / 60)} min {(int)(seconds % 60)} s";

    internal static string Ms(double? ms) => ms is null ? "-" : ms.Value >= 1000 ? $"{ms.Value / 1000:0.00} s" : $"{ms.Value:0} ms";

    private static string Shorten(string error)
    {
        var line = error.ReplaceLineEndings(" ").Trim();
        return line.Length <= 110 ? line : line[..107] + "...";
    }
}
