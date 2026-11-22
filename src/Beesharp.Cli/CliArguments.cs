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
    /// <summary>A progress line every this many seconds; 0 = none.</summary>
    public double ProgressSeconds { get; init; } = 5;
}

public enum CliCommand { Help, RunHelp, Run, Invalid }

public sealed record ParsedArguments(CliCommand Command, RunOptions? Run = null, string? Error = null);

/// <summary>A tiny hand-written parser (Proposed, brain 0054 U3): the tool has one command and a handful of options, so no parser library and no prerelease dependency.</summary>
public static class CliArguments
{
    public const string Usage = """
        beesharp - run a preset without the UI (for CI pipelines)

        Usage:
          beesharp run --preset <name or file> [--env <name>] [options]
          beesharp --help

        Run "beesharp run --help" for the options.

        Exit codes: 0 all bees succeeded, 1 a bee failed (or was canceled), 2 the run could not run.
        """;

    public const string RunUsage = """
        beesharp run - run a preset, print a summary, save the run

        Usage:
          beesharp run --preset <name or file> [--env <name>] [--root <folder>] [options]

        Options:
          --preset <name|file>   The preset to run: its name, or the path of a preset .json file. Required.
          --env <name>           The environment whose {{Key}} values are filled in. Needed when the preset uses placeholders.
          --root <folder>        Folder with presets/, environments/, scripts/ (preflight/, shutdown/) and data/.
                                 Default: the current directory.
          --storage <kind>       duckdb (default): save the run to <root>/data/beesharp.duckdb, so the server's UI shows it in the history.
                                 memory: save nothing.
          --duration <seconds>   Run for this long instead of the preset's duration.
          --json <file>          Also write the summary as JSON to this file.
          --progress <seconds>   Print a progress line this often (default 5, 0 = none).
          -h, --help             This text.

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
        if (IsHelp(args[0]) || args[0] == "help") return new(CliCommand.Help);
        if (args[0] != "run") return new(CliCommand.Invalid, Error: $"Unknown command '{args[0]}'.");

        string? preset = null, env = null, root = null, storage = null, json = null;
        int? duration = null;
        double? progress = null;

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (IsHelp(arg)) return new(CliCommand.RunHelp);
            if (!arg.StartsWith("--", StringComparison.Ordinal)) return Fail($"Unexpected argument '{arg}'.");

            string name = arg, value;
            var eq = arg.IndexOf('=');
            if (eq > 0) { name = arg[..eq]; value = arg[(eq + 1)..]; }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];
            else return Fail($"Option {name} needs a value.");

            switch (name)
            {
                case "--preset": preset = value; break;
                case "--env": env = value; break;
                case "--root": root = value; break;
                case "--json": json = value; break;
                case "--storage":
                    storage = value.ToLowerInvariant();
                    if (storage is not ("duckdb" or "memory")) return Fail($"--storage must be duckdb or memory, not '{value}'.");
                    break;
                case "--duration":
                    if (!int.TryParse(value, out var d) || d < 1) return Fail("--duration must be a whole number of seconds, 1 or more.");
                    duration = d;
                    break;
                case "--progress":
                    if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var p) || p < 0) return Fail("--progress must be a number of seconds, 0 or more.");
                    progress = p;
                    break;
                default: return Fail($"Unknown option '{name}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(preset)) return Fail("--preset is required.");
        return new(CliCommand.Run, new RunOptions
        {
            Preset = preset.Trim(),
            Environment = string.IsNullOrWhiteSpace(env) ? null : env.Trim(),
            Root = string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : Path.GetFullPath(root),
            Storage = storage ?? "duckdb",
            DurationSeconds = duration,
            JsonFile = string.IsNullOrWhiteSpace(json) ? null : json,
            ProgressSeconds = progress ?? 5,
        });

        static ParsedArguments Fail(string error) => new(CliCommand.Invalid, Error: error);
    }

    private static bool IsHelp(string arg) => arg is "-h" or "--help" or "-?";
}
