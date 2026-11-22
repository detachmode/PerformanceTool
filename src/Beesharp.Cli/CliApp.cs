using System.Globalization;
using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;
using Beesharp.Services.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Cli;

/// <summary>
/// <c>beesharp run</c>: loads the preset and environment from the root folder, runs it through the same Engine code as the server
/// (<see cref="RunManager"/>: preflight, scenarios, shutdown program), prints progress and a summary, saves the run and returns the exit code (<see cref="ExitCodes"/>).
/// </summary>
public static class CliApp
{
    private static readonly JsonSerializerOptions PresetJson = new(JsonSerializerDefaults.Web);

    /// <param name="stop">Cancels like the UI's Stop: bees in flight become canceled, the shutdown program still runs.</param>
    /// <param name="configureServices">For tests: extra services (e.g. a stub scenario).</param>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken stop, Action<IServiceCollection>? configureServices = null)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var parsed = CliArguments.Parse(args);
        switch (parsed.Command)
        {
            case CliCommand.Help:
                output.WriteLine(CliArguments.Usage);
                return ExitCodes.Ok;
            case CliCommand.RunHelp:
                output.WriteLine(CliArguments.RunUsage);
                return ExitCodes.Ok;
            case CliCommand.Invalid:
                error.WriteLine($"beesharp: {parsed.Error}");
                error.WriteLine("Run \"beesharp --help\" for usage.");
                return ExitCodes.CouldNotRun;
        }

        try
        {
            return await RunPresetAsync(parsed.Run!, output, error, stop, configureServices);
        }
        catch (CouldNotRunException ex)
        {
            error.WriteLine($"beesharp: {ex.Message}");
            return ExitCodes.CouldNotRun;
        }
    }

    private sealed class CouldNotRunException(string message) : Exception(message);

    private static async Task<int> RunPresetAsync(
        RunOptions options, TextWriter output, TextWriter error, CancellationToken stop, Action<IServiceCollection>? configureServices)
    {
        if (!Directory.Exists(options.Root)) throw new CouldNotRunException($"Root folder '{options.Root}' does not exist.");

        var services = new ServiceCollection();
        services.AddBeesharpEngine(new BeesharpDirectories { Root = options.Root });
        var storageOptions = new StorageOptions
        {
            Provider = options.Storage == "memory" ? StorageOptions.Memory : StorageOptions.DuckDb,
            Root = options.Root,
        };
        services.AddBeesharpStorage(storageOptions);
        services.AddScenario<HttpGetScenario>();
        services.AddScenario<HttpPostJsonScenario>();
        services.AddScenario<CustomRequestScenario>();
        services.AddScenario<UserJourneyScenario>();
        configureServices?.Invoke(services);

        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ScenarioCatalog>();
        var problems = await provider.LoadBeesharpScriptsAsync();

        // 1. The preset and the environment (exit 2 when missing).
        var presetName = options.Preset;
        var plan = await LoadPresetAsync(options, provider.GetRequiredService<PresetStore>(), ct: default, name => presetName = name);
        if (options.DurationSeconds is { } seconds) plan.DurationSeconds = seconds;

        EnvironmentDefinition? env = null;
        if (options.Environment is not null)
        {
            env = await provider.GetRequiredService<EnvironmentStore>().GetAsync(options.Environment)
                ?? throw new CouldNotRunException($"Environment '{options.Environment}' was not found in {provider.GetRequiredService<EnvironmentStore>().Directory}.");
        }

        // 2. Check everything that can be checked before anything starts: scripts, plan, placeholders.
        foreach (var entry in plan.Scenarios)
            if (catalog.Find(entry.ScenarioId) is { } scenario) entry.Normalize(scenario);
        var planErrors = plan.Validate(catalog);
        var missingScripts = MissingScripts(plan, provider);
        planErrors.AddRange(missingScripts);
        if (planErrors.Count > 0)
        {
            foreach (var problem in problems)
                error.WriteLine($"beesharp: {problem.Kind} {problem.FileName} does not compile: {problem.Message}");
            throw new CouldNotRunException($"Preset '{presetName}' cannot run:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", planErrors));
        }
        foreach (var problem in problems)
            error.WriteLine($"beesharp: warning: {problem.Kind} {problem.FileName} does not compile and is skipped: {problem.Message}");

        var unknown = new List<string>();
        var values = env?.ToValues() ?? EnvironmentValues.None;
        foreach (var entry in plan.Scenarios) Placeholders.ResolveJson(entry.Parameters, values, unknown);
        if (unknown.Count > 0)
            throw new CouldNotRunException(
                $"{(env is null ? "No environment was given (--env) and the preset uses" : $"Environment '{env.Name}' has no value for")} " +
                string.Join(", ", unknown.Select(k => "{{" + k + "}}")) + ".");

        // 3. Run.
        var runs = provider.GetRequiredService<RunManager>();
        var storage = provider.GetRequiredService<IRunStorage>();
        output.WriteLine($"beesharp run: preset \"{presetName}\"{(env is null ? "" : $", environment {env.Name}")}");
        output.WriteLine($"  {plan.Scenarios.Count} scenario {(plan.Scenarios.Count == 1 ? "entry" : "entries")}, {RunSummary.Duration(plan.DurationSeconds)}" +
            $"{(string.IsNullOrWhiteSpace(plan.PreflightScriptId) ? "" : $", preflight {plan.PreflightScriptId}")}" +
            $"{(string.IsNullOrWhiteSpace(plan.ShutdownScriptId) ? "" : $", shutdown program {plan.ShutdownScriptId}")}");
        output.WriteLine($"  root {options.Root}, storage {storage.ProviderName}");

        LiveRunStatusDto status;
        try
        {
            status = await runs.StartAsync(plan, env?.Name);
        }
        catch (ArgumentException ex)
        {
            throw new CouldNotRunException(ex.Message);
        }

        var stopped = false;
        using var stopRegistration = stop.Register(() =>
        {
            stopped = true;
            lock (output) output.WriteLine("Stopping: bees in flight are canceled, the shutdown program still runs...");
            _ = runs.StopAsync();
        });

        var finished = runs.WaitUntilFinishedAsync();
        // Messages in the order things happen, even when a step (a short preflight) was over between two polls.
        var preflightShown = 0;
        var shutdownShown = 0;
        var announced = new HashSet<string>();
        void Report(LiveRunStatusDto s)
        {
            if (s.PreflightState is not null && announced.Add("preflight")) output.WriteLine($"Preflight {s.PreflightScriptName}...");
            preflightShown = WriteNew(output, "  preflight> ", s.PreflightLogs, preflightShown);
            if (s.State is "Running" or "Shutdown" or "Finished" && s.PreflightState != "Failed" && s.PreflightState != "Running" && announced.Add("running"))
                output.WriteLine("Running scenarios...");
            if (s.ShutdownState is "Running" or "Succeeded" or "Failed" && announced.Add("shutdown")) output.WriteLine($"Shutdown program {s.ShutdownScriptName}...");
            shutdownShown = WriteNew(output, "  shutdown> ", s.ShutdownLogs, shutdownShown);
        }

        var lastProgress = DateTime.UtcNow;
        while (!finished.IsCompleted)
        {
            await Task.WhenAny(finished, Task.Delay(100));
            status = runs.GetCurrent(afterSeq: int.MaxValue); // counters only, no bees
            Report(status);
            if (options.ProgressSeconds > 0 && status.State == "Running" && (DateTime.UtcNow - lastProgress).TotalSeconds >= options.ProgressSeconds)
            {
                lastProgress = DateTime.UtcNow;
                var done = status.Scenarios.Sum(s => s.Finished);
                var failed = status.Scenarios.Sum(s => s.Failed);
                var flying = status.Scenarios.Sum(s => s.InFlight);
                output.WriteLine($"  {RunSummary.Duration(status.ElapsedSeconds),9}  {done} bees finished, {failed} failed, {flying} flying");
            }
        }
        await finished;

        // 4. Summary.
        status = runs.GetCurrent(afterSeq: int.MaxValue);
        Report(status);
        // The stored run is the full list of bees (the live status lists them in completion order, which is no good for paging by sequence).
        var stored = await storage.GetRunAsync(status.RunId);
        var bees = stored?.Bees.Select(b => new LiveBeeDto(b.Sequence, b.Entry, b.StartedAt, b.DurationMs, b.StatusCode, b.Bytes, b.Error, b.SlowCalls)).ToList() ?? [];
        var savedTo = stored is null || options.Storage == "memory" ? null : $"{storageOptions.ResolveDuckDbPath()}  (run {status.RunId})";
        var summary = RunSummary.Build(presetName, env?.Name, status, bees, stopped, savedTo);
        output.Write(summary.ToText());

        if (options.JsonFile is not null)
        {
            try { await File.WriteAllTextAsync(options.JsonFile, summary.ToJson()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"beesharp: could not write {options.JsonFile}: {ex.Message}");
            }
        }
        return summary.ExitCode;
    }

    private static int WriteNew(TextWriter output, string prefix, IReadOnlyList<string>? lines, int shown)
    {
        if (lines is null) return shown;
        for (; shown < lines.Count; shown++) output.WriteLine(prefix + lines[shown]);
        return shown;
    }

    private static List<string> MissingScripts(LoadPlan plan, IServiceProvider provider)
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(plan.PreflightScriptId) && provider.GetRequiredService<PreflightStore>().Get(plan.PreflightScriptId.Trim()) is null)
            errors.Add($"Preflight script '{plan.PreflightScriptId}' was not found.");
        if (!string.IsNullOrWhiteSpace(plan.ShutdownScriptId) && provider.GetRequiredService<ShutdownStore>().Get(plan.ShutdownScriptId.Trim()) is null)
            errors.Add($"Shutdown script '{plan.ShutdownScriptId}' was not found.");
        return errors;
    }

    /// <summary>A preset by file path (when it looks like one and exists) or by name (case-insensitive) from the presets folder.</summary>
    private static async Task<LoadPlan> LoadPresetAsync(RunOptions options, PresetStore store, CancellationToken ct, Action<string> setName)
    {
        var value = options.Preset;
        var looksLikeFile = value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || value.Contains('/') || value.Contains('\\');
        if (looksLikeFile)
        {
            var path = Path.IsPathRooted(value) ? value : Path.GetFullPath(value);
            if (!File.Exists(path)) throw new CouldNotRunException($"Preset file '{value}' was not found.");
            try
            {
                var preset = JsonSerializer.Deserialize<Preset>(await File.ReadAllTextAsync(path, ct), PresetJson);
                if (preset?.Plan is null) throw new CouldNotRunException($"Preset file '{value}' holds no plan.");
                setName(string.IsNullOrWhiteSpace(preset.Name) ? Path.GetFileNameWithoutExtension(path) : preset.Name);
                return preset.Plan;
            }
            catch (JsonException ex)
            {
                throw new CouldNotRunException($"Preset file '{value}' is not valid: {ex.Message}");
            }
        }

        var all = await store.ListAsync(ct);
        var found = all.FirstOrDefault(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            var names = all.Count == 0 ? "there are no presets there" : "available: " + string.Join(", ", all.Select(p => $"\"{p.Name}\""));
            throw new CouldNotRunException($"Preset '{value}' was not found in {store.Directory} ({names}).");
        }
        setName(found.Name);
        return found.Plan;
    }
}
