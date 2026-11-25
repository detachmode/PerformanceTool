using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Services;

/// <summary>Inputs to <see cref="HeadlessRunner.RunAsync"/>.</summary>
public sealed record HeadlessRunRequest
{
    /// <summary>The load plan to validate and execute.</summary>
    public required LoadPlan Plan { get; init; }

    /// <summary>Display name of the preset (or plan) being executed.</summary>
    public required string PresetName { get; init; }

    /// <summary>Optional environment name whose values resolve <c>{{Key}}</c> placeholders.</summary>
    public string? EnvironmentName { get; init; }

    /// <summary>Headless execution, logging, output, and export options.</summary>
    public required HeadlessRunOptions Options { get; init; }

    /// <summary>Optional root folder displayed in the startup banner (used by <c>Beesharp.Cli</c>).</summary>
    public string? Root { get; init; }
    /// <summary>Optional database file path for the "Saved to" line when storage persists runs on disk.</summary>
    public string? SavedToPath { get; init; }
    /// <summary>Optional script compilation problems collected at startup.</summary>
    public IReadOnlyList<ScriptLoadProblem>? ScriptProblems { get; init; }
}

/// <summary>
/// Single shared headless execution engine used by both <c>Beesharp.Host</c> (<c>dotnet run -- run ...</c>)
/// and <c>Beesharp.Cli</c> (<c>beesharp run ...</c>). Handles validation, live preflight/shutdown/failure/log/progress
/// streaming, optional run export, and summary formatting.
/// </summary>
public static class HeadlessRunner
{
    private const int MaxLiveFailures = 50;

    /// <summary>
    /// Validates the plan, scripts, environment, and placeholders, runs the load test to completion (or cancellation),
    /// streams live output, optionally exports results, and writes the final summary.
    /// </summary>
    /// <param name="request">The run request containing the plan, preset name, environment, and options.</param>
    /// <param name="services">Application service provider supplying the scenario catalog, environment store, run manager, storage, and script stores.</param>
    /// <param name="output">Standard output writer for banners, progress, live logs, and summary output.</param>
    /// <param name="error">Error output writer for validation, compilation, export, and file-write errors.</param>
    /// <param name="stop">Cancellation token (typically Ctrl+C) that stops the active run while still allowing the shutdown program to execute.</param>
    /// <returns>One of the <see cref="HeadlessExitCodes"/> exit codes (<c>0</c>, <c>1</c>, or <c>2</c>).</returns>
    public static async Task<int> RunAsync(
        HeadlessRunRequest request,
        IServiceProvider services,
        TextWriter output,
        TextWriter error,
        CancellationToken stop = default)
    {
        var plan = request.Plan;
        var options = request.Options;
        var catalog = services.GetRequiredService<ScenarioCatalog>();
        var envStore = services.GetRequiredService<EnvironmentStore>();
        var runs = services.GetRequiredService<RunManager>();
        var storage = services.GetRequiredService<IRunStorage>();

        // 1. Resolve environment if requested.
        EnvironmentDefinition? env = null;
        if (!string.IsNullOrWhiteSpace(request.EnvironmentName))
        {
            env = await envStore.GetAsync(request.EnvironmentName);
            if (env is null)
            {
                error.WriteLine($"beesharp: Environment '{request.EnvironmentName}' was not found in {envStore.Directory}.");
                return HeadlessExitCodes.CouldNotRun;
            }
        }

        // 2. Validate plan, scripts and placeholders before anything starts.
        foreach (var entry in plan.Scenarios)
            if (catalog.Find(entry.ScenarioId) is { } scenario) entry.Normalize(scenario);

        var planErrors = plan.Validate(catalog);
        planErrors.AddRange(MissingScripts(plan, services));
        if (planErrors.Count > 0)
        {
            if (request.ScriptProblems is { Count: > 0 })
            {
                foreach (var problem in request.ScriptProblems)
                    error.WriteLine($"beesharp: {problem.Kind} {problem.FileName} does not compile: {problem.Message}");
            }
            error.WriteLine($"beesharp: Preset '{request.PresetName}' cannot run:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", planErrors));
            return HeadlessExitCodes.CouldNotRun;
        }

        if (request.ScriptProblems is { Count: > 0 })
        {
            foreach (var problem in request.ScriptProblems)
                error.WriteLine($"beesharp: warning: {problem.Kind} {problem.FileName} does not compile and is skipped: {problem.Message}");
        }

        var unknown = new List<string>();
        var values = env?.ToValues() ?? EnvironmentValues.None;
        foreach (var entry in plan.Scenarios) Placeholders.ResolveJson(entry.Parameters, values, unknown);
        if (unknown.Count > 0)
        {
            error.WriteLine(
                $"beesharp: {(env is null ? "No environment was given (--env) and the preset uses" : $"Environment '{env.Name}' has no value for")} " +
                string.Join(", ", unknown.Select(k => "{{" + k + "}}")) + ".");
            return HeadlessExitCodes.CouldNotRun;
        }

        // 3. Startup banner (suppressed when --json writes to stdout).
        var quiet = options.JsonToStdout;
        var minLogRank = options.LogsLevel is not null ? LevelRank(options.LogsLevel) : (int?)null;

        if (!quiet)
        {
            WriteLineLocked(output, $"beesharp run: preset \"{request.PresetName}\"{(env is null ? "" : $", environment {env.Name}")}");
            WriteLineLocked(output, $"  {plan.Scenarios.Count} scenario {(plan.Scenarios.Count == 1 ? "entry" : "entries")}, {RunSummary.Duration(plan.DurationSeconds)}" +
                $"{(string.IsNullOrWhiteSpace(plan.PreflightScriptId) ? "" : $", preflight {plan.PreflightScriptId}")}" +
                $"{(string.IsNullOrWhiteSpace(plan.ShutdownScriptId) ? "" : $", shutdown program {plan.ShutdownScriptId}")}");
            WriteLineLocked(output, request.Root is not null
                ? $"  root {request.Root}, storage {storage.ProviderName}"
                : $"  storage {storage.ProviderName}");
        }

        // 4. Start run.
        LiveRunStatusDto status;
        try
        {
            status = await runs.StartAsync(plan, env?.Name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            error.WriteLine($"beesharp: {ex.Message}");
            return HeadlessExitCodes.CouldNotRun;
        }

        var activeRunId = status.RunId;
        var stopped = false;
        using var stopRegistration = stop.Register(() =>
        {
            stopped = true;
            if (!quiet) WriteLineLocked(output, "Stopping: bees in flight are canceled, the shutdown program still runs...");
            _ = runs.StopAsync();
        });

        var finished = runs.WaitUntilFinishedAsync();
        var preflightShown = 0;
        var shutdownShown = 0;
        var nextBeeIndex = 0;
        var failuresPrinted = 0;
        var failuresSuppressed = false;
        long lastLogId = 0;
        var announced = new HashSet<string>();

        void Report(LiveRunStatusDto s)
        {
            if (quiet) return;

            if (s.PreflightState is not null && announced.Add("preflight"))
                WriteLineLocked(output, $"Preflight {s.PreflightScriptName}...");
            preflightShown = WriteNew(output, "  preflight> ", s.PreflightLogs, preflightShown);

            if (s.State is "Running" or "Shutdown" or "Finished" && s.PreflightState != "Failed" && s.PreflightState != "Running" && announced.Add("running"))
                WriteLineLocked(output, "Running scenarios...");

            // Stream immediate failure lines for newly completed failed/canceled bees.
            foreach (var bee in s.NewBees)
            {
                if (bee.Error is null) continue;
                if (minLogRank is null && failuresPrinted >= MaxLiveFailures)
                {
                    if (!failuresSuppressed)
                    {
                        failuresSuppressed = true;
                        WriteLineLocked(output, "  ... further failures suppressed, see summary");
                    }
                    continue;
                }
                failuresPrinted++;
                var entryLabel = EntryLabel(s.Plan, bee.Entry);
                var stepSuffix = StepSuffix(s.Steps, bee.Entry, bee.FailedStep);
                WriteLineLocked(output, $"  FAIL [{entryLabel}] bee #{bee.Sequence + 1}{stepSuffix} ({RunSummary.Ms(bee.DurationMs)}): {RunSummary.Shorten(bee.Error)}");
            }

            // Stream matching live scenario logs when --logs is enabled.
            if (minLogRank is { } rank)
            {
                var logs = runs.GetLiveLogs(afterId: lastLogId, limit: 500);
                foreach (var log in logs)
                {
                    lastLogId = log.Id;
                    // Preflight and shutdown logs are already printed via preflight> / shutdown> prefixes.
                    if (log.Sequence < 0 && log.Entry < 0) continue;
                    if (LevelRank(log.Level) < rank) continue;
                    var entryLabel = EntryLabel(s.Plan, log.Entry);
                    var stepSuffix = log.Step > 0 ? StepSuffix(s.Steps, log.Entry, log.Step) : "";
                    var exSuffix = string.IsNullOrWhiteSpace(log.Exception) ? "" : $" ({RunSummary.Shorten(log.Exception)})";
                    WriteLineLocked(output, $"  [{RunSummary.FormatClock(log.AtSeconds)}] {ShortLevel(log.Level)} [{entryLabel}] {log.BeeName}{stepSuffix}: {log.Message}{exSuffix}");
                }
            }

            if (s.ShutdownState is "Running" or "Succeeded" or "Failed" && announced.Add("shutdown"))
                WriteLineLocked(output, $"Shutdown program {s.ShutdownScriptName}...");
            shutdownShown = WriteNew(output, "  shutdown> ", s.ShutdownLogs, shutdownShown);
        }

        var lastProgress = DateTime.UtcNow;
        while (!finished.IsCompleted)
        {
            await Task.WhenAny(finished, Task.Delay(100));
            status = runs.GetCurrent(afterIndex: nextBeeIndex, forRunId: activeRunId);
            nextBeeIndex = status.NextBeeIndex;
            Report(status);

            if (!quiet && options.ProgressSeconds > 0 && status.State == "Running" && (DateTime.UtcNow - lastProgress).TotalSeconds >= options.ProgressSeconds)
            {
                lastProgress = DateTime.UtcNow;
                var done = status.Scenarios.Sum(s => s.Finished);
                var failed = status.Scenarios.Sum(s => s.Failed);
                var ok = Math.Max(0, done - failed);
                var dropped = status.Scenarios.Sum(s => s.Dropped);
                var flying = status.Scenarios.Sum(s => s.InFlight);
                var droppedPart = dropped > 0 ? $", {dropped} dropped" : "";
                WriteLineLocked(output, $"  [{RunSummary.FormatClock(status.ElapsedSeconds)} / {RunSummary.FormatClock(plan.DurationSeconds)}] {done} bees finished, {ok} ok, {failed} failed{droppedPart}, {flying} flying");
            }
        }
        await finished;

        // 5. Final drain of bees, logs, preflight/shutdown output.
        status = runs.GetCurrent(afterIndex: nextBeeIndex, forRunId: activeRunId);
        nextBeeIndex = status.NextBeeIndex;
        Report(status);

        // 6. Optional --export after storage has been flushed by WaitUntilFinishedAsync.
        string? exportedTo = null;
        long? exportedBytes = null;
        if (options.ExportPath is not null)
        {
            var format = options.ExportFormat ?? HeadlessArgumentParser.InferExportFormat(options.ExportPath, storage.ProviderName);
            try
            {
                var exported = await storage.ExportRunAsync(status.RunId, format, CancellationToken.None);
                if (exported is null)
                {
                    error.WriteLine($"beesharp: could not export run '{status.RunId}': run was not stored.");
                }
                else
                {
                    var destPath = HeadlessArgumentParser.IsDirectoryPath(options.ExportPath)
                        ? Path.Combine(options.ExportPath, exported.FileName)
                        : options.ExportPath;
                    var fullDest = Path.GetFullPath(destPath);
                    if (Path.GetDirectoryName(fullDest) is { Length: > 0 } dir)
                        Directory.CreateDirectory(dir);
                    await File.WriteAllBytesAsync(fullDest, exported.Content);
                    exportedTo = destPath;
                    exportedBytes = exported.Content.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error.WriteLine($"beesharp: could not export to {options.ExportPath}: {ex.Message}");
            }
        }

        // 7. Build summary from stored run (full list of bees).
        var stored = await storage.GetRunAsync(status.RunId);
        var bees = stored?.Bees.Select(b => new LiveBeeDto(b.Sequence, b.Entry, b.StartedAt, b.DurationMs, b.StatusCode, b.Bytes, b.Error, b.SlowCalls, b.FailedStep, b.FailedRep)).ToList() ?? [];
        var savedTo = stored is null || string.Equals(storage.ProviderName, "Memory", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(request.SavedToPath)
            ? null
            : $"{request.SavedToPath}  (run {status.RunId})";

        var summary = RunSummary.Build(request.PresetName, env?.Name, status, bees, stopped, savedTo, exportedTo: exportedTo, exportedBytes: exportedBytes);

        if (options.JsonToStdout)
        {
            WriteLineLocked(output, summary.ToJson());
        }
        else
        {
            lock (output) output.Write(summary.ToText());
        }

        if (options.JsonFile is not null)
        {
            try
            {
                var fullJson = Path.GetFullPath(options.JsonFile);
                if (Path.GetDirectoryName(fullJson) is { Length: > 0 } dir)
                    Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(fullJson, summary.ToJson());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"beesharp: could not write {options.JsonFile}: {ex.Message}");
            }
        }

        return summary.ExitCode;
    }

    private static List<string> MissingScripts(LoadPlan plan, IServiceProvider provider)
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(plan.PreflightScriptId) && provider.GetService<PreflightStore>()?.Get(plan.PreflightScriptId.Trim()) is null)
            errors.Add($"Preflight script '{plan.PreflightScriptId}' was not found.");
        if (!string.IsNullOrWhiteSpace(plan.ShutdownScriptId) && provider.GetService<ShutdownStore>()?.Get(plan.ShutdownScriptId.Trim()) is null)
            errors.Add($"Shutdown script '{plan.ShutdownScriptId}' was not found.");
        return errors;
    }

    private static int WriteNew(TextWriter output, string prefix, IReadOnlyList<string>? lines, int shown)
    {
        if (lines is null) return shown;
        for (; shown < lines.Count; shown++) WriteLineLocked(output, prefix + lines[shown]);
        return shown;
    }

    private static void WriteLineLocked(TextWriter output, string line)
    {
        lock (output) output.WriteLine(line);
    }

    private static string EntryLabel(LoadPlan plan, int entry)
    {
        if (entry < 0 || entry >= plan.Scenarios.Count) return $"#{entry + 1}";
        var cfg = plan.Scenarios[entry];
        return string.IsNullOrWhiteSpace(cfg.Label) ? cfg.ScenarioId : cfg.Label;
    }

    private static string StepSuffix(IReadOnlyList<IReadOnlyList<StepInfo>>? steps, int entry, int stepIndex)
    {
        if (steps is null || entry < 0 || entry >= steps.Count || stepIndex < 0 || stepIndex >= steps[entry].Count)
            return "";
        return $" at {steps[entry][stepIndex].Name}";
    }

    private static int LevelRank(string level) => level.ToLowerInvariant() switch
    {
        "trace" or "trc" => 0,
        "debug" or "dbg" => 1,
        "information" or "info" or "inf" => 2,
        "warning" or "warn" or "wrn" => 3,
        "error" or "err" or "critical" => 4,
        _ => 2,
    };

    private static string ShortLevel(string level) => level.ToLowerInvariant() switch
    {
        "trace" => "TRC",
        "debug" => "DBG",
        "information" => "INF",
        "warning" => "WRN",
        "error" => "ERR",
        "critical" => "CRT",
        _ => "INF",
    };
}
