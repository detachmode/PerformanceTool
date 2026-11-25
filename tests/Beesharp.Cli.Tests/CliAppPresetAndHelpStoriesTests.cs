using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace Beesharp.Cli.Tests;

/// <summary>
/// CI operator stories for <see cref="CliApp"/> and <see cref="CliArguments"/>:
/// top-level help and empty-workspace diagnostics, standalone preset files via relative paths
/// with filename fallback and malformed-file validation, and custom DI scenario registration.
/// </summary>
public sealed class CliAppPresetAndHelpStoriesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beesharp-cli-stories-{Guid.NewGuid():N}");

    public CliAppPresetAndHelpStoriesTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root))
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private sealed class FastPulseScenario : Scenario<EmptyScenarioParameters>
    {
        public override string Id => "fast-pulse";
        public override string Name => "Fast Pulse";
        public override string Description => "Deterministic in-process scenario for CLI workflow stories.";

        protected override Task<ScenarioOutcome> ExecuteAsync(EmptyScenarioParameters p, ScenarioContext ctx, CancellationToken ct)
        {
            ctx.Log("pulse ok");
            ctx.RecordCall("GET /pulse", durationMs: 4, statusCode: 200, bytes: 8);
            return Task.FromResult(new ScenarioOutcome(200, 8));
        }
    }

    private static async Task<(int Exit, string Out, string Err)> RunCliAsync(
        string[] args,
        Action<IServiceCollection>? configureServices = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, output, error, CancellationToken.None, configureServices);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Operator_checks_top_level_help_and_gets_clear_guidance_in_an_empty_workspace()
    {
        // 1. Every top-level help form prints usage to stdout, keeps stderr empty, and exits with 0.
        foreach (var helpArg in new[] { "--help", "-h", "help" })
        {
            var help = await RunCliAsync([helpArg]);
            Assert.Equal(ExitCodes.Ok, help.Exit);
            Assert.Contains("beesharp run --preset", help.Out);
            Assert.Contains("Exit codes: 0", help.Out);
            Assert.Equal("", help.Err);
        }

        // 2. Missing or unknown commands explain what went wrong and point to --help.
        var noCmd = await RunCliAsync([]);
        Assert.Equal(ExitCodes.CouldNotRun, noCmd.Exit);
        Assert.Contains("No command given.", noCmd.Err);
        Assert.Contains("beesharp --help", noCmd.Err);

        var unknownCmd = await RunCliAsync(["deploy"]);
        Assert.Equal(ExitCodes.CouldNotRun, unknownCmd.Exit);
        Assert.Contains("Unknown command 'deploy'.", unknownCmd.Err);

        // 3. Running against a brand-new root folder with no presets tells the operator the presets folder is empty.
        var emptyWorkspace = await RunCliAsync(["run", "--preset", "Smoke", "--root", root, "--storage", "memory", "--progress", "0"]);
        Assert.Equal(ExitCodes.CouldNotRun, emptyWorkspace.Exit);
        Assert.Contains("Preset 'Smoke' was not found", emptyWorkspace.Err);
        Assert.Contains("there are no presets there", emptyWorkspace.Err);
    }

    [Fact]
    public async Task Standalone_preset_files_support_relative_paths_filename_fallback_and_clear_validation_errors()
    {
        var filesDir = Path.Combine(root, "standalone");
        Directory.CreateDirectory(filesDir);

        // 1. Unnamed preset JSON referenced by a relative path falls back to its file stem ("canary-check")
        //    and runs cleanly against a custom scenario wired through configureServices.
        var canaryPath = Path.Combine(filesDir, "canary-check.json");
        await File.WriteAllTextAsync(canaryPath, """
            {
              "name": "   ",
              "savedAt": "2026-11-24T10:00:00Z",
              "plan": {
                "durationSeconds": 1,
                "maxConcurrency": 10,
                "timeoutSeconds": 15,
                "scenarios": [
                  { "scenarioId": "fast-pulse", "label": "Canary Pulse", "ratePerMinute": 300 }
                ]
              }
            }
            """);

        var relativeCanaryPath = Path.GetRelativePath(Directory.GetCurrentDirectory(), canaryPath);
        Assert.False(Path.IsPathRooted(relativeCanaryPath));

        var ok = await RunCliAsync(
            ["run", "--preset", relativeCanaryPath, "--root", root, "--storage", "memory", "--progress", "0", "--json"],
            services => services.AddScenario<FastPulseScenario>());

        Assert.Equal(ExitCodes.Ok, ok.Exit);
        Assert.Equal("", ok.Err);
        var summary = JsonSerializer.Deserialize<JsonElement>(ok.Out);
        Assert.Equal("canary-check", summary.GetProperty("preset").GetString());
        Assert.True(summary.GetProperty("bees").GetInt32() > 0);
        Assert.Equal(0, summary.GetProperty("failedBees").GetInt32());

        // 2. Malformed JSON preset file exits with 2 and explains why it could not be parsed.
        var malformedPath = Path.Combine(filesDir, "broken.json");
        await File.WriteAllTextAsync(malformedPath, """{ "name": "Broken", "plan": """);
        var malformed = await RunCliAsync(["run", "--preset", malformedPath, "--root", root, "--storage", "memory", "--progress", "0"]);
        Assert.Equal(ExitCodes.CouldNotRun, malformed.Exit);
        Assert.Contains("is not valid", malformed.Err);

        // 3. Valid JSON that deserializes to null or omits the plan exits with 2 ("holds no plan").
        var nullPresetPath = Path.Combine(filesDir, "null-preset.json");
        await File.WriteAllTextAsync(nullPresetPath, "null");
        var nullPreset = await RunCliAsync(["run", "--preset", nullPresetPath, "--root", root, "--storage", "memory", "--progress", "0"]);
        Assert.Equal(ExitCodes.CouldNotRun, nullPreset.Exit);
        Assert.Contains("holds no plan", nullPreset.Err);

        var planlessPath = Path.Combine(filesDir, "planless.json");
        await File.WriteAllTextAsync(planlessPath, """{ "name": "Planless", "plan": null }""");
        var planless = await RunCliAsync(["run", "--preset", planlessPath, "--root", root, "--storage", "memory", "--progress", "0"]);
        Assert.Equal(ExitCodes.CouldNotRun, planless.Exit);
        Assert.Contains("holds no plan", planless.Err);

        // 4. A relative path with a separator but no .json extension is still treated as a file path.
        var missingPathNoExt = Path.Combine("standalone", "does-not-exist");
        var missingFile = await RunCliAsync(["run", "--preset", missingPathNoExt, "--root", root, "--storage", "memory", "--progress", "0"]);
        Assert.Equal(ExitCodes.CouldNotRun, missingFile.Exit);
        Assert.Contains("was not found", missingFile.Err);
    }

    [Fact]
    public async Task Operator_runs_case_insensitive_preset_with_duration_override_logs_and_export_options()
    {
        // Seed a 60-second preset in presets/ and override its duration down to 1 second at runtime.
        var presetsDir = Path.Combine(root, "presets");
        Directory.CreateDirectory(presetsDir);
        await File.WriteAllTextAsync(Path.Combine(presetsDir, "nightly-smoke.json"), """
            {
              "name": "Nightly Smoke",
              "savedAt": "2026-11-24T10:00:00Z",
              "plan": {
                "durationSeconds": 60,
                "maxConcurrency": 10,
                "timeoutSeconds": 15,
                "scenarios": [
                  { "scenarioId": "fast-pulse", "label": "Pulse Check", "ratePerMinute": 300 }
                ]
              }
            }
            """);

        var exportPath = Path.Combine(root, "artifacts", "nightly-export.json");
        var parsed = CliArguments.Parse([
            "run", "-p", "NIGHTLY SMOKE", "--root", root, "--storage", "memory",
            "-d", "1", "--progress", "0", "--logs", "info",
            "--export", exportPath, "--export-format", "json",
        ]);
        Assert.Equal(CliCommand.Run, parsed.Command);

        // Verify RunOptions.ToHeadlessOptions faithfully forwards every configured option.
        var headless = parsed.Run!.ToHeadlessOptions();
        Assert.Equal("NIGHTLY SMOKE", headless.Preset);
        Assert.Equal(1, headless.DurationSeconds);
        Assert.Equal(0, headless.ProgressSeconds);
        Assert.Equal("Information", headless.LogsLevel);
        Assert.Equal(exportPath, headless.ExportPath);
        Assert.Equal("json", headless.ExportFormat);

        var outcome = await RunCliAsync(
            [
                "run", "-p", "NIGHTLY SMOKE", "--root", root, "--storage", "memory",
                "-d", "1", "--progress", "0", "--logs", "info",
                "--export", exportPath, "--export-format", "json",
            ],
            services => services.AddScenario<FastPulseScenario>());

        Assert.Equal(ExitCodes.Ok, outcome.Exit);
        Assert.Contains("preset \"Nightly Smoke\"", outcome.Out);
        Assert.Contains("INF [Pulse Check]", outcome.Out);
        Assert.Contains("pulse ok", outcome.Out);
        Assert.Contains("Exported to", outcome.Out);
        Assert.True(File.Exists(exportPath));

        var exported = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(exportPath));
        Assert.True(exported.TryGetProperty("run", out _));
    }
}
