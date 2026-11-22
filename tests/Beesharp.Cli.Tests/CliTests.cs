using System.Text.Json;
using Beesharp.Cli;
using Beesharp.Services.Storage;
using Xunit;

namespace Beesharp.Cli.Tests;

/// <summary>The command-line tool (brain 0054, U3): argument parsing, exit codes, placeholders, summary text and the Ctrl+C path. Real script scenarios in a temp root, in-memory storage.</summary>
public sealed class CliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beesharp-cli-{Guid.NewGuid():N}");

    public CliTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "scripts", "preflight"));
        Directory.CreateDirectory(Path.Combine(root, "scripts", "shutdown"));
        Directory.CreateDirectory(Path.Combine(root, "presets"));
        Directory.CreateDirectory(Path.Combine(root, "environments"));
        File.WriteAllText(Path.Combine(root, "scripts", "quick-work.cs"), """
            public record QuickParams(
                [property: Range(0, 100)] int FailurePercent = 0,
                [property: Range(1, 60000)] int DelayMs = 20,
                string Greeting = "hi"
            );

            Scenario.Define<QuickParams>("Quick work", async (p, ctx) =>
            {
                ctx.Log($"{p.Greeting} from quick work");
                await Task.Delay(p.DelayMs, ctx.CancellationToken);
                if (p.FailurePercent > 0 && Random.Shared.Next(100) < p.FailurePercent)
                    throw new InvalidOperationException("quick work failed on purpose");
            }, id: "quick-work");
            """);
        File.WriteAllText(Path.Combine(root, "scripts", "preflight", "warmup.cs"), "Log(\"Warming up\");\nData[\"tenant\"] = \"demo\";\n");
        File.WriteAllText(Path.Combine(root, "scripts", "preflight", "broken-check.cs"), "Fail(\"target is not ready\");\n");
        File.WriteAllText(Path.Combine(root, "scripts", "shutdown", "cleanup.cs"), "Log(\"Cleaning up tenant \" + (Data.TryGetValue(\"tenant\", out var t) ? t : \"none\"));\n");
        File.WriteAllText(Path.Combine(root, "scripts", "shutdown", "failing-cleanup.cs"), "Fail(\"cleanup failed\");\n");
        File.WriteAllText(Path.Combine(root, "environments", "staging.json"),
            """{ "name": "Staging", "variables": [ { "key": "Greeting", "value": "hello staging" } ] }""");
        Preset("Passing", failurePercent: 0);
        Preset("Failing bees", failurePercent: 100);
        Preset("Bad preflight", preflight: "broken-check");
        Preset("Missing preflight", preflight: "nope");
        Preset("Failing shutdown", shutdown: "failing-cleanup");
        Preset("Unknown scenario", scenario: "does-not-exist");
        Preset("Long", delayMs: 30_000, seconds: 30, rate: 600);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private void Preset(string name, int failurePercent = 0, string? preflight = "warmup", string? shutdown = "cleanup", string scenario = "quick-work",
        int delayMs = 20, int seconds = 1, int rate = 600)
    {
        string Id(string? id) => id is null ? "null" : $"\"{id}\"";
        File.WriteAllText(Path.Combine(root, "presets", name.Replace(' ', '-').ToLowerInvariant() + ".json"), $$$"""
            { "name": "{{{name}}}", "savedAt": "2026-11-22T10:00:00Z",
              "plan": { "durationSeconds": {{{seconds}}}, "maxConcurrency": 50, "timeoutSeconds": 60,
                "preflightScriptId": {{{Id(preflight)}}}, "shutdownScriptId": {{{Id(shutdown)}}},
                "scenarios": [ { "scenarioId": "{{{scenario}}}", "label": "Quick work", "ratePerMinute": {{{rate}}},
                  "parameters": { "FailurePercent": {{{failurePercent}}}, "DelayMs": {{{delayMs}}}, "Greeting": "{{Greeting}}" } } ] } }
            """);
    }

    private sealed record Outcome(int Exit, string Out, string Err);

    private async Task<Outcome> Cli(string[] args, CancellationToken stop = default, TextWriter? output = null)
    {
        var o = output ?? new StringWriter();
        var e = new StringWriter();
        var exit = await CliApp.RunAsync(args, o, e, stop);
        return new Outcome(exit, o.ToString()!, e.ToString());
    }

    private Task<Outcome> Run(string preset, string? env = "Staging", params string[] more)
    {
        var args = new List<string> { "run", "--preset", preset, "--root", root, "--storage", "memory", "--progress", "0" };
        if (env is not null) args.AddRange(["--env", env]);
        args.AddRange(more);
        return Cli([.. args]);
    }

    // ---- arguments ----

    [Fact]
    public void Run_arguments_are_parsed_with_values_and_defaults()
    {
        var p = CliArguments.Parse(["run", "--preset", "Black Friday", "--env", "Staging", "--root", root, "--duration", "7", "--json", "out.json", "--storage", "Memory", "--progress", "0.5"]);
        Assert.Equal(CliCommand.Run, p.Command);
        var o = p.Run!;
        Assert.Equal("Black Friday", o.Preset);
        Assert.Equal("Staging", o.Environment);
        Assert.Equal(root, o.Root);
        Assert.Equal(7, o.DurationSeconds);
        Assert.Equal("out.json", o.JsonFile);
        Assert.Equal("memory", o.Storage);
        Assert.Equal(0.5, o.ProgressSeconds);

        var min = CliArguments.Parse(["run", "--preset=x"]).Run!;
        Assert.Equal("x", min.Preset);
        Assert.Null(min.Environment);
        Assert.Equal("duckdb", min.Storage);
        Assert.Equal(Directory.GetCurrentDirectory(), min.Root);
        Assert.Equal(5, min.ProgressSeconds);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Help_is_recognised(string arg) => Assert.Equal(CliCommand.Help, CliArguments.Parse([arg]).Command);

    [Fact]
    public void Run_help_is_recognised() => Assert.Equal(CliCommand.RunHelp, CliArguments.Parse(["run", "--help"]).Command);

    [Theory]
    [InlineData]
    [InlineData("walk")]
    [InlineData("run")]
    [InlineData("run", "--preset")]
    [InlineData("run", "--preset", "x", "--nope", "1")]
    [InlineData("run", "--preset", "x", "--storage", "sqlite")]
    [InlineData("run", "--preset", "x", "--duration", "0")]
    [InlineData("run", "--preset", "x", "--duration", "soon")]
    [InlineData("run", "--preset", "x", "stray")]
    public void Bad_arguments_are_invalid(params string[] args)
    {
        var p = CliArguments.Parse(args);
        Assert.Equal(CliCommand.Invalid, p.Command);
        Assert.False(string.IsNullOrWhiteSpace(p.Error));
    }

    [Fact]
    public async Task Bad_arguments_exit_with_2_and_name_the_problem()
    {
        var r = await Cli(["run", "--env", "Staging"]);
        Assert.Equal(ExitCodes.CouldNotRun, r.Exit);
        Assert.Contains("--preset is required", r.Err);
    }

    [Fact]
    public async Task Help_prints_usage_and_exits_with_0()
    {
        var r = await Cli(["run", "--help"]);
        Assert.Equal(0, r.Exit);
        Assert.Contains("--preset", r.Out);
        Assert.Contains("Exit codes", r.Out);
    }

    // ---- exit codes ----

    [Fact]
    public async Task All_bees_succeed_gives_exit_0_and_a_summary()
    {
        var json = Path.Combine(root, "summary.json");
        var r = await Run("Passing", "Staging", "--json", json);

        Assert.Equal(ExitCodes.Ok, r.Exit);
        Assert.Contains("Summary", r.Out);
        Assert.Contains("Preflight   succeeded (warmup.cs)", r.Out);
        Assert.Contains("Shutdown    succeeded (cleanup.cs)", r.Out);
        Assert.Contains("#1 Quick work", r.Out);
        Assert.Contains("slowest 5%", r.Out);
        Assert.Contains("Result: all ", r.Out);
        Assert.Contains("shutdown> ", r.Out);
        Assert.Contains("Cleaning up tenant demo", r.Out); // the preflight's run bag reached the shutdown program
        Assert.DoesNotContain('\u001b', r.Out); // plain text, no colour codes

        var summary = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(json));
        Assert.Equal(0, summary.GetProperty("exitCode").GetInt32());
        Assert.True(summary.GetProperty("bees").GetInt32() > 0);
        Assert.Equal(0, summary.GetProperty("failedBees").GetInt32());
    }

    [Fact]
    public async Task A_failing_bee_gives_exit_1_and_lists_the_error_with_its_count()
    {
        var r = await Run("Failing bees");

        Assert.Equal(ExitCodes.BeesFailed, r.Exit);
        Assert.Contains("Most common errors", r.Out);
        Assert.Contains("x  quick work failed on purpose", r.Out);
        Assert.Contains(" bees failed (exit code 1)", r.Out);
    }

    [Fact]
    public async Task Missing_preset_gives_exit_2()
    {
        var r = await Run("Does not exist");
        Assert.Equal(ExitCodes.CouldNotRun, r.Exit);
        Assert.Contains("Preset 'Does not exist' was not found", r.Err);
        Assert.Contains("\"Passing\"", r.Err); // names what exists
    }

    [Fact]
    public async Task Missing_preset_file_gives_exit_2()
    {
        var r = await Run(Path.Combine(root, "presets", "nope.json"));
        Assert.Equal(ExitCodes.CouldNotRun, r.Exit);
        Assert.Contains("was not found", r.Err);
    }

    [Fact]
    public async Task A_preset_file_path_works()
    {
        var r = await Run(Path.Combine(root, "presets", "passing.json"));
        Assert.Equal(ExitCodes.Ok, r.Exit);
        Assert.Contains("preset \"Passing\"", r.Out);
    }

    [Fact]
    public async Task Missing_environment_gives_exit_2()
    {
        var r = await Run("Passing", "Nowhere");
        Assert.Equal(ExitCodes.CouldNotRun, r.Exit);
        Assert.Contains("Environment 'Nowhere' was not found", r.Err);
    }

    [Fact]
    public async Task Placeholder_without_an_environment_refuses_to_start()
    {
        var r = await Run("Passing", env: null);
        Assert.Equal(ExitCodes.CouldNotRun, r.Exit);
        Assert.Contains("{{Greeting}}", r.Err);
        Assert.DoesNotContain("Preflight", r.Out); // nothing started
    }

    [Fact]
    public async Task Placeholder_the_environment_does_not_have_refuses_to_start()
    {
        File.WriteAllText(Path.Combine(root, "environments", "empty.json"), """{ "name": "Empty", "variables": [ { "key": "Other", "value": "x" } ] }""");
        var r = await Run("Passing", "Empty");
        Assert.Equal(ExitCodes.CouldNotRun, r.Exit);
        Assert.Contains("Environment 'Empty' has no value for {{Greeting}}", r.Err);
    }

    [Fact]
    public async Task Failed_preflight_gives_exit_2_but_the_shutdown_program_still_runs()
    {
        var r = await Run("Bad preflight");
        Assert.Equal(ExitCodes.CouldNotRun, r.Exit);
        Assert.Contains("Preflight   failed (broken-check.cs): target is not ready", r.Out);
        Assert.Contains("Shutdown    succeeded", r.Out);
        Assert.Contains("the run could not run", r.Out);
    }

    [Fact]
    public async Task Unknown_preflight_or_scenario_gives_exit_2()
    {
        var pre = await Run("Missing preflight");
        Assert.Equal(ExitCodes.CouldNotRun, pre.Exit);
        Assert.Contains("Preflight script 'nope' was not found", pre.Err);

        var scenario = await Run("Unknown scenario");
        Assert.Equal(ExitCodes.CouldNotRun, scenario.Exit);
        Assert.Contains("unknown scenario 'does-not-exist'", scenario.Err);
    }

    [Fact]
    public async Task A_script_that_does_not_compile_gives_exit_2_when_the_preset_needs_it()
    {
        File.Delete(Path.Combine(root, "scripts", "quick-work.cs"));
        File.WriteAllText(Path.Combine(root, "scripts", "quick-work.cs"), "this is not C#;");
        var r = await Run("Passing");
        Assert.Equal(ExitCodes.CouldNotRun, r.Exit);
        Assert.Contains("quick-work.cs does not compile", r.Err);
        Assert.Contains("unknown scenario 'quick-work'", r.Err);
    }

    [Fact]
    public async Task A_broken_script_the_preset_does_not_need_is_only_a_warning()
    {
        File.WriteAllText(Path.Combine(root, "scripts", "unrelated.cs"), "this is not C#;");
        var r = await Run("Passing");
        Assert.Equal(ExitCodes.Ok, r.Exit);
        Assert.Contains("unrelated.cs does not compile and is skipped", r.Err);
    }

    [Fact]
    public async Task Missing_root_folder_gives_exit_2()
    {
        var r = await Cli(["run", "--preset", "Passing", "--root", Path.Combine(root, "nope")]);
        Assert.Equal(ExitCodes.CouldNotRun, r.Exit);
        Assert.Contains("does not exist", r.Err);
    }

    [Fact]
    public async Task Failing_shutdown_program_gives_exit_1_and_is_named()
    {
        var r = await Run("Failing shutdown");
        Assert.Equal(ExitCodes.BeesFailed, r.Exit);
        Assert.Contains("Shutdown    failed (failing-cleanup.cs): cleanup failed", r.Out);
        Assert.Contains("the shutdown program failed", r.Out);
    }

    [Fact]
    public async Task Duration_overrides_the_preset_and_progress_lines_are_printed()
    {
        var r = await Run("Passing", "Staging", "--duration", "2", "--progress", "0.3");
        Assert.Equal(0, r.Exit);
        Assert.Contains("2.0 s", r.Out);
        Assert.Contains("bees finished,", r.Out);
        Assert.Contains("flying", r.Out);
    }

    // ---- Ctrl+C ----

    /// <summary>Raises the stop as soon as a progress line says bees are flying: so the stop happens with bees in flight, without guessing at timing.</summary>
    private sealed class StopWhenFlying(CancellationTokenSource stop) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, @", [1-9]\d* flying")) stop.Cancel();
        }
    }

    [Fact]
    public async Task Ctrl_C_cancels_the_bees_in_flight_runs_the_shutdown_program_and_exits_non_zero()
    {
        using var stop = new CancellationTokenSource();
        var json = Path.Combine(root, "stopped.json");
        var r = await Cli(
            ["run", "--preset", "Long", "--env", "Staging", "--root", root, "--storage", "memory", "--progress", "0.2", "--json", json],
            stop.Token, new StopWhenFlying(stop));

        Assert.Equal(ExitCodes.BeesFailed, r.Exit);
        Assert.Contains("Stopping:", r.Out);
        Assert.Contains("stopped with Ctrl+C", r.Out);
        Assert.Contains("Canceled: run stopped", r.Out);
        Assert.Contains("were canceled when the run stopped", r.Out);
        Assert.Contains("Cleaning up tenant demo", r.Out); // the shutdown program still ran
        Assert.Contains("Shutdown    succeeded", r.Out);

        var summary = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(json));
        Assert.True(summary.GetProperty("stopped").GetBoolean());
        Assert.True(summary.GetProperty("canceledBees").GetInt32() > 0);
        Assert.True(summary.GetProperty("failedBees").GetInt32() >= summary.GetProperty("canceledBees").GetInt32());
    }

    // ---- saving ----

    [Fact]
    public async Task The_run_is_saved_to_the_duckdb_file_under_the_root()
    {
        var r = await Cli(["run", "--preset", "Passing", "--env", "Staging", "--root", root, "--progress", "0"]);
        Assert.Equal(ExitCodes.Ok, r.Exit);

        var path = Path.Combine(root, "data", "beesharp.duckdb");
        Assert.Contains(path, r.Out);
        Assert.True(File.Exists(path));
        await using var storage = new DuckDbRunStorage(path);
        var run = Assert.Single(await storage.ListRunsAsync(10));
        Assert.Equal("Finished", run.State);
        Assert.True(run.TotalBees > 0);
        Assert.Contains(run.RunId, r.Out);
    }

    [Fact]
    public async Task Memory_storage_keeps_nothing()
    {
        var r = await Run("Passing");
        Assert.Contains("Not saved", r.Out);
        Assert.False(File.Exists(Path.Combine(root, "data", "beesharp.duckdb")));
    }

    // ---- summary ----

    [Fact]
    public void Exit_code_rules()
    {
        Assert.Equal(0, RunSummary.ExitCodeFor(null, "Succeeded", false, 0, "Succeeded"));
        Assert.Equal(1, RunSummary.ExitCodeFor(null, "Succeeded", false, 1, "Succeeded"));
        Assert.Equal(1, RunSummary.ExitCodeFor(null, "Succeeded", true, 0, "Succeeded"));
        Assert.Equal(1, RunSummary.ExitCodeFor(null, null, false, 0, "Failed"));
        Assert.Equal(2, RunSummary.ExitCodeFor(null, "Failed", false, 0, null));
        Assert.Equal(2, RunSummary.ExitCodeFor("boom", null, false, 0, null));
        Assert.Equal(1, RunSummary.ExitCodeFor("canceled", "Failed", true, 0, null)); // stopped wins over a preflight cut short
    }

    [Fact]
    public void Percentile_is_nearest_rank()
    {
        double[] sorted = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        Assert.Equal(50, RunSummary.Percentile(sorted, 0.5));
        Assert.Equal(100, RunSummary.Percentile(sorted, 0.95));
        Assert.Equal(7, RunSummary.Percentile([7], 0.95));
    }
}
