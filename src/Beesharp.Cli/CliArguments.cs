using Beesharp.Cli.Runs;
using Beesharp.Services;

namespace Beesharp.Cli;

/// <summary>What <c>beesharp run</c> was asked to do.</summary>
public sealed record RunOptions
{
    /// <summary>A preset name, or the path of a preset file.</summary>
    public required string Preset { get; init; }
    public string? Environment { get; init; }
    /// <summary>The folder with <c>presets/</c>, <c>environments/</c>, <c>scripts/</c> and <c>data/</c>. Default: the current directory.</summary>
    public string Root { get; init; } = Directory.GetCurrentDirectory();
    /// <summary><c>duckdb</c> (default, saves the run) or <c>memory</c> (nothing is kept).</summary>
    public string Storage { get; init; } = "duckdb";
    /// <summary>Replaces the preset's duration (seconds).</summary>
    public int? DurationSeconds { get; init; }
    /// <summary>Writes the summary as JSON to this file.</summary>
    public string? JsonFile { get; init; }
    /// <summary>When true, writes machine-readable summary JSON to stdout and suppresses chatter.</summary>
    public bool JsonToStdout { get; init; }
    /// <summary>A progress line every this many seconds; 0 = none.</summary>
    public double ProgressSeconds { get; init; } = 5;
    /// <summary>When set, streams live scenario logs at or above this level (<c>Trace</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c>).</summary>
    public string? LogsLevel { get; init; }
    /// <summary>Exports the finished run to this file or directory path.</summary>
    public string? ExportPath { get; init; }
    /// <summary>Optional export format (<c>parquet</c>, <c>duckdb</c>, <c>json</c>, <c>csv</c>).</summary>
    public string? ExportFormat { get; init; }

    public HeadlessRunOptions ToHeadlessOptions() => new()
    {
        Preset = Preset,
        Environment = Environment,
        DurationSeconds = DurationSeconds,
        ProgressSeconds = ProgressSeconds,
        LogsLevel = LogsLevel,
        JsonToStdout = JsonToStdout,
        JsonFile = JsonFile,
        ExportPath = ExportPath,
        ExportFormat = ExportFormat,
    };
}

public enum CliCommand
{
    Help,
    RunHelp,
    Run,
    RunsHelp,
    RunsSubcommandHelp,
    RunsList,
    RunsSummary,
    RunsErrors,
    RunsCalls,
    RunsLogs,
    RunsBee,
    RunsCompare,
    Invalid,
}

public sealed record ParsedArguments(
    CliCommand Command,
    RunOptions? Run = null,
    string? Error = null,
    RunsOptions? Runs = null,
    string? HelpText = null);

/// <summary>A tiny hand-written parser (Proposed, brain 0054 U3): delegates shared <c>run</c> option parsing to <see cref="HeadlessArgumentParser"/>.</summary>
public static class CliArguments
{
    public const string Usage = """
        beesharp - run presets headlessly and inspect exported run bundles without the UI

        Usage:
          beesharp run --preset <name or file> [--env <name>] [options]
          beesharp runs <subcommand> [options]
          beesharp --help

        Run "beesharp run --help" or "beesharp runs --help" for subcommand options.

        Exit codes: 0 all bees succeeded (or inspection succeeded), 1 a bee failed (or was canceled), 2 invalid arguments or could not run.
        """;

    public const string RunUsage = """
        beesharp run - run a preset, print a summary, save the run

        Usage:
          beesharp run --preset <name or file> [--env <name>] [--root <folder>] [options]

        Options:
          -p, --preset <name|file>  The preset to run: its name, or the path of a preset .json file. Required.
          -e, --env <name>          The environment whose {{Key}} values are filled in. Needed when the preset uses placeholders.
          --root <folder>           Folder with presets/, environments/, scripts/ (preflight/, shutdown/) and data/.
                                    Default: the current directory.
          --storage <kind>          duckdb (default): save the run to <root>/data/beesharp.duckdb, so the server's UI shows it in the history.
                                    memory: save nothing.
          -d, --duration <seconds>  Run for this long instead of the preset's duration.
          --progress <seconds>      Print a progress line this often (default 5, 0 = none).
          --logs [level]            Stream live scenario logs (trace|debug|info|warn|error; default info).
          --json [file]             Print summary as JSON to stdout (suppresses chatter), or also write JSON to <file>.
          --export <path>           Export the finished run to a file or directory.
          --export-format <fmt>     Export format: parquet, duckdb, json, or csv (inferred from --export path when omitted).
          -h, --help                This text.

        The run starts with the preflight, then the scenario entries, then the shutdown program (as in the UI).
        Ctrl+C stops the run like Stop in the UI: bees in flight are canceled, the shutdown program still runs, the run is saved.

        Exit codes:
          0  all bees succeeded
          1  at least one bee failed or was canceled (also: stopped with Ctrl+C, shutdown program failed)
          2  the run could not run: bad arguments, preset or environment not found, a script does not compile,
             an unknown {{placeholder}}, the preflight failed

        Example:
          beesharp run --preset "Black Friday" --env Staging --root ./load-tests
        """;

    public static ParsedArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new(CliCommand.Invalid, Error: "No command given.");
        if (HeadlessArgumentParser.IsHelpFlag(args[0]) || args[0] == "help") return new(CliCommand.Help);
        if (string.Equals(args[0], "runs", StringComparison.OrdinalIgnoreCase)) return RunsArguments.Parse(args);
        if (args[0] != "run") return new(CliCommand.Invalid, Error: $"Unknown command '{args[0]}'.");

        var parsed = HeadlessArgumentParser.ParseRunOptions(args, startIndex: 1, allowRootAndStorage: true, requirePreset: true);
        if (parsed.IsHelp) return new(CliCommand.RunHelp);
        if (parsed.Error is not null) return new(CliCommand.Invalid, Error: parsed.Error);

        var opts = parsed.Options!;
        return new(CliCommand.Run, new RunOptions
        {
            Preset = opts.Preset!,
            Environment = opts.Environment,
            Root = parsed.Root ?? Directory.GetCurrentDirectory(),
            Storage = parsed.Storage ?? "duckdb",
            DurationSeconds = opts.DurationSeconds,
            JsonFile = opts.JsonFile,
            JsonToStdout = opts.JsonToStdout,
            ProgressSeconds = opts.ProgressSeconds,
            LogsLevel = opts.LogsLevel,
            ExportPath = opts.ExportPath,
            ExportFormat = opts.ExportFormat,
        });
    }
}
