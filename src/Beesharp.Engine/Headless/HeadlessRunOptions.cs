using System.Globalization;

namespace Beesharp.Services;

/// <summary>Shared options for headless <c>run</c> execution in both <c>Beesharp.Host</c> and <c>Beesharp.Cli</c>.</summary>
public sealed record HeadlessRunOptions
{
    /// <summary>Preset name or preset JSON file path. Required in <c>Beesharp.Cli</c>; optional in <c>Beesharp.Host</c> when a default plan is configured.</summary>
    public string? Preset { get; init; }

    /// <summary>Environment name whose <c>{{Key}}</c> values are resolved.</summary>
    public string? Environment { get; init; }

    /// <summary>Overrides the plan's duration in seconds.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>Prints a progress line every this many seconds while running; 0 disables periodic progress lines.</summary>
    public double ProgressSeconds { get; init; } = 5;

    /// <summary>
    /// When non-null, streams live scenario <c>ILogger</c> lines at or above this level to stdout during the run.
    /// Canonical values: <c>Trace</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c>.
    /// </summary>
    public string? LogsLevel { get; init; }

    /// <summary>When true, writes machine-readable summary JSON to stdout and suppresses banner/progress/text chatter on stdout.</summary>
    public bool JsonToStdout { get; init; }

    /// <summary>When set, also writes the summary as JSON to this file path.</summary>
    public string? JsonFile { get; init; }

    /// <summary>When set, exports the finished run to this file or directory path.</summary>
    public string? ExportPath { get; init; }

    /// <summary>Optional export format override (<c>parquet</c>, <c>duckdb</c>, <c>json</c>, <c>csv</c>). Inferred from <see cref="ExportPath"/> when omitted.</summary>
    public string? ExportFormat { get; init; }
}

/// <summary>Result of parsing <c>run</c> CLI options.</summary>
/// <param name="IsHelp">True when a help flag (<c>-h</c>, <c>--help</c>, or <c>-?</c>) was encountered.</param>
/// <param name="Options">Parsed run options when parsing succeeded without help or error.</param>
/// <param name="Root">Optional workspace root directory passed via <c>--root</c>, resolved to a full path.</param>
/// <param name="Storage">Optional normalized storage provider name (<c>duckdb</c> or <c>memory</c>) passed via <c>--storage</c>.</param>
/// <param name="Error">Validation or syntax error message when parsing failed.</param>
public sealed record ParsedHeadlessRunArgs(
    bool IsHelp,
    HeadlessRunOptions? Options = null,
    string? Root = null,
    string? Storage = null,
    string? Error = null);

/// <summary>
/// Shared parser for headless <c>run</c> CLI arguments, used by both <c>Beesharp.Cli</c> and <c>Beesharp.Host</c>.
/// </summary>
public static class HeadlessArgumentParser
{
    /// <summary>Help text printed for <c>Beesharp.Host</c> <c>run --help</c>.</summary>
    public const string HostRunUsage = """
        run - execute a load plan headlessly, stream progress & failures, print summary

        Usage:
          dotnet run -- run [options]

        Options:
          -p, --preset <name>              Preset name to run (defaults to the host's configured default plan).
          -e, --env <name>                 Environment whose {{Key}} values are filled in.
          -d, --duration <seconds>         Run for this long instead of the plan's duration.
          --progress <seconds>             Print a progress line every N seconds (default 5, 0 = none).
          --logs [level]                   Stream live scenario logs (trace|debug|info|warn|error; default info).
          --json [file]                    Print summary as JSON to stdout (suppresses chatter), or write JSON to <file>.
          --export <path>                  Export the finished run to a file or directory.
          --export-format <fmt>            Export format: parquet, duckdb, json, or csv (inferred from --export path when omitted).
          -h, --help                       Show this help text.

        Exit codes:
          0  all bees succeeded
          1  at least one bee failed or was canceled (also: stopped with Ctrl+C, shutdown program failed)
          2  the run could not run: bad arguments, preset or environment not found, unknown {{placeholder}}, preflight failed
        """;

    /// <summary>Returns whether <paramref name="arg"/> is a recognized help flag (<c>-h</c>, <c>--help</c>, or <c>-?</c>).</summary>
    /// <param name="arg">The command-line token to check.</param>
    /// <returns><see langword="true"/> if <paramref name="arg"/> is a help flag; otherwise <see langword="false"/>.</returns>
    public static bool IsHelpFlag(string arg) => arg is "-h" or "--help" or "-?";

    /// <summary>Parses arguments starting at <paramref name="startIndex"/> (typically 1, right after <c>run</c>).</summary>
    /// <param name="args">Full list of command-line arguments.</param>
    /// <param name="startIndex">Zero-based index in <paramref name="args"/> where <c>run</c> options begin.</param>
    /// <param name="allowRootAndStorage">Whether <c>--root</c> and <c>--storage</c> options are accepted.</param>
    /// <param name="requirePreset">Whether <c>--preset</c> is mandatory.</param>
    /// <returns>A <see cref="ParsedHeadlessRunArgs"/> describing help, parsed options, or a parse error.</returns>
    public static ParsedHeadlessRunArgs ParseRunOptions(
        IReadOnlyList<string> args,
        int startIndex,
        bool allowRootAndStorage,
        bool requirePreset)
    {
        string? preset = null, env = null, root = null, storage = null, jsonFile = null, logsLevel = null, exportPath = null, exportFormat = null;
        int? duration = null;
        double? progress = null;
        var jsonToStdout = false;

        for (var i = startIndex; i < args.Count; i++)
        {
            var arg = args[i];
            if (IsHelpFlag(arg)) return new(IsHelp: true);

            if (!arg.StartsWith('-'))
                return Fail($"Unexpected argument '{arg}'.");

            string name = arg;
            string? inlineValue = null;
            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                name = arg[..eq];
                inlineValue = arg[(eq + 1)..];
            }

            // Options with optional values: --logs [level], --json [file]
            if (name == "--logs")
            {
                if (inlineValue is not null)
                {
                    var normalized = NormalizeLogLevel(inlineValue);
                    if (normalized is null) return Fail($"--logs level must be trace, debug, info, warn, or error, not '{inlineValue}'.");
                    logsLevel = normalized;
                }
                else if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                {
                    var next = args[++i];
                    var normalized = NormalizeLogLevel(next);
                    if (normalized is null) return Fail($"--logs level must be trace, debug, info, warn, or error, not '{next}'.");
                    logsLevel = normalized;
                }
                else
                {
                    logsLevel = "Information";
                }
                continue;
            }

            if (name == "--json")
            {
                if (inlineValue is not null)
                {
                    if (string.IsNullOrWhiteSpace(inlineValue)) return Fail("Option --json needs a file path when '=' is used.");
                    jsonFile = inlineValue;
                    jsonToStdout = false;
                }
                else if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                {
                    jsonFile = args[++i];
                    jsonToStdout = false;
                }
                else
                {
                    jsonToStdout = true;
                    jsonFile = null;
                }
                continue;
            }

            // Options requiring a value
            var canonical = name switch
            {
                "-p" => "--preset",
                "-e" => "--env",
                "-d" => "--duration",
                _ => name,
            };

            var knownValueOption = canonical is "--preset" or "--env" or "--duration" or "--progress" or "--export" or "--export-format"
                || (allowRootAndStorage && canonical is "--root" or "--storage");

            if (!knownValueOption)
                return Fail($"Unknown option '{name}'.");

            string value;
            if (inlineValue is not null)
                value = inlineValue;
            else if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                value = args[++i];
            else
                return Fail($"Option {name} needs a value.");

            switch (canonical)
            {
                case "--preset": preset = value; break;
                case "--env": env = value; break;
                case "--root": root = value; break;
                case "--storage":
                    storage = value.ToLowerInvariant();
                    if (storage is not ("duckdb" or "memory"))
                        return Fail($"--storage must be duckdb or memory, not '{value}'.");
                    break;
                case "--duration":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) || d < 1)
                        return Fail("--duration must be a whole number of seconds, 1 or more.");
                    duration = d;
                    break;
                case "--progress":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || p < 0 || double.IsNaN(p) || double.IsInfinity(p))
                        return Fail("--progress must be a number of seconds, 0 or more.");
                    progress = p;
                    break;
                case "--export":
                    if (string.IsNullOrWhiteSpace(value)) return Fail("Option --export needs a path.");
                    exportPath = value;
                    break;
                case "--export-format":
                    exportFormat = value.Trim().ToLowerInvariant();
                    if (exportFormat is not ("parquet" or "duckdb" or "json" or "csv"))
                        return Fail($"--export-format must be parquet, duckdb, json, or csv, not '{value}'.");
                    break;
            }
        }

        if (requirePreset && string.IsNullOrWhiteSpace(preset))
            return Fail("--preset is required.");

        return new(
            IsHelp: false,
            Options: new HeadlessRunOptions
            {
                Preset = string.IsNullOrWhiteSpace(preset) ? null : preset.Trim(),
                Environment = string.IsNullOrWhiteSpace(env) ? null : env.Trim(),
                DurationSeconds = duration,
                ProgressSeconds = progress ?? 5,
                LogsLevel = logsLevel,
                JsonToStdout = jsonToStdout,
                JsonFile = string.IsNullOrWhiteSpace(jsonFile) ? null : jsonFile,
                ExportPath = string.IsNullOrWhiteSpace(exportPath) ? null : exportPath,
                ExportFormat = exportFormat,
            },
            Root: string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root),
            Storage: storage);

        static ParsedHeadlessRunArgs Fail(string error) => new(IsHelp: false, Error: error);
    }

    /// <summary>Normalizes a CLI log level token (such as <c>info</c> or <c>wrn</c>) to its canonical level name.</summary>
    /// <param name="raw">Raw log level string from the command line.</param>
    /// <returns>The canonical level name (<c>Trace</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>, or <c>Error</c>), or <see langword="null"/> if unrecognized.</returns>
    public static string? NormalizeLogLevel(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "trace" or "trc" => "Trace",
        "debug" or "dbg" => "Debug",
        "info" or "inf" or "information" => "Information",
        "warn" or "wrn" or "warning" => "Warning",
        "error" or "err" => "Error",
        _ => null,
    };

    /// <summary>Returns whether <paramref name="path"/> represents a directory (ends with a directory separator or already exists as a directory).</summary>
    /// <param name="path">Destination path to inspect.</param>
    /// <returns><see langword="true"/> if <paramref name="path"/> is treated as a directory; otherwise <see langword="false"/>.</returns>
    public static bool IsDirectoryPath(string path) =>
        path.EndsWith('/') || path.EndsWith('\\') || Directory.Exists(path);

    /// <summary>Infers the export format from <paramref name="exportPath"/> and <paramref name="storageProvider"/> when <c>--export-format</c> is omitted.</summary>
    /// <param name="exportPath">Target export file or directory path.</param>
    /// <param name="storageProvider">Active storage provider name (for example <c>DuckDb</c> or <c>Memory</c>).</param>
    /// <returns>The inferred format string (<c>parquet</c>, <c>duckdb</c>, <c>json</c>, or <c>csv</c>).</returns>
    public static string InferExportFormat(string exportPath, string storageProvider)
    {
        var defaultFormat = string.Equals(storageProvider, "DuckDb", StringComparison.OrdinalIgnoreCase) ? "parquet" : "json";
        if (IsDirectoryPath(exportPath)) return defaultFormat;

        var lower = exportPath.Trim().ToLowerInvariant();
        if (lower.EndsWith(".csv.zip", StringComparison.Ordinal) || lower.EndsWith(".csv", StringComparison.Ordinal)) return "csv";
        if (lower.EndsWith(".duckdb", StringComparison.Ordinal)) return "duckdb";
        if (lower.EndsWith(".json", StringComparison.Ordinal)) return "json";
        if (lower.EndsWith(".parquet", StringComparison.Ordinal) || lower.EndsWith(".zip", StringComparison.Ordinal)) return "parquet";
        return defaultFormat;
    }
}
