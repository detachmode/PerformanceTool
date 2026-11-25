using System.Globalization;
using System.Text.Json;
using Beesharp.Cli.Runs;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;
using Beesharp.Services.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Cli;

internal sealed class CouldNotRunException(string message) : Exception(message);

/// <summary>
/// <c>beesharp run</c>: loads the preset and environment from the root folder, then delegates execution
/// to the shared <see cref="HeadlessRunner"/> in <c>Beesharp.Engine</c>.
/// </summary>
public static class CliApp
{
    private static readonly JsonSerializerOptions PresetJson = new(JsonSerializerDefaults.Web);

    /// <param name="stop">Cancels like the UI's Stop: bees in flight become canceled, the shutdown program still runs.</param>
    /// <param name="configureServices">For tests: extra services (e.g. a stub scenario).</param>
    public static Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken stop, Action<IServiceCollection>? configureServices = null) =>
        RunAsync(args, output, error, stop, configureServices, httpHandler: null, getEnvironmentVariable: null);

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken stop,
        Action<IServiceCollection>? configureServices,
        HttpMessageHandler? httpHandler,
        Func<string, string?>? getEnvironmentVariable)
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
            case CliCommand.RunsHelp:
            case CliCommand.RunsSubcommandHelp:
                output.WriteLine(parsed.HelpText ?? RunsArguments.RunsUsage);
                return ExitCodes.Ok;
            case CliCommand.Invalid:
                error.WriteLine($"beesharp: {parsed.Error}");
                error.WriteLine(args.Length > 0 && string.Equals(args[0], "runs", StringComparison.OrdinalIgnoreCase)
                    ? "Run \"beesharp runs --help\" for usage."
                    : "Run \"beesharp --help\" for usage.");
                return ExitCodes.CouldNotRun;
        }

        try
        {
            if (parsed.Command is CliCommand.RunsList or CliCommand.RunsSummary or CliCommand.RunsErrors or CliCommand.RunsCalls
                or CliCommand.RunsLogs or CliCommand.RunsBee or CliCommand.RunsCompare)
            {
                return await RunsCommandHandler.ExecuteAsync(parsed, output, httpHandler, getEnvironmentVariable, stop);
            }

            return await RunPresetAsync(parsed.Run!, output, error, stop, configureServices);
        }
        catch (CouldNotRunException ex)
        {
            error.WriteLine($"beesharp: {ex.Message}");
            return ExitCodes.CouldNotRun;
        }
    }

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
        var problems = await provider.LoadBeesharpScriptsAsync();

        var presetName = options.Preset;
        var plan = await LoadPresetAsync(options, provider.GetRequiredService<PresetStore>(), ct: default, name => presetName = name);
        if (options.DurationSeconds is { } seconds) plan.DurationSeconds = seconds;

        var savedToPath = options.Storage == "memory" ? null : storageOptions.ResolveDuckDbPath();
        var request = new HeadlessRunRequest
        {
            Plan = plan,
            PresetName = presetName,
            EnvironmentName = options.Environment,
            Options = options.ToHeadlessOptions(),
            Root = options.Root,
            SavedToPath = savedToPath,
            ScriptProblems = problems,
        };

        return await HeadlessRunner.RunAsync(request, provider, output, error, stop);
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
