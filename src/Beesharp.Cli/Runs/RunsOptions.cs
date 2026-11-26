using System.Globalization;
using Beesharp.Services;

namespace Beesharp.Cli.Runs;

public sealed record RunsListOptions(string? Url, int Limit, bool Json);

public sealed record RunsSummaryOptions(string File, bool Json, string? Url = null);

public sealed record RunsErrorsOptions(string File, bool Json, string? Url = null);

public sealed record RunsCallsOptions(
    string File,
    int? Entry,
    bool SlowOnly,
    bool FailedOnly,
    double? MinMs,
    string? Search,
    bool Json,
    string? Url = null,
    int? Step = null,
    bool ByStep = false,
    string? Scenario = null,
    string? Endpoint = null,
    int? Slowest = null);

public sealed record RunsTimelineOptions(
    string File,
    double BucketSeconds,
    int? Entry,
    string? Scenario,
    bool Json,
    string? Url = null);

public sealed record RunsLogsOptions(
    string File,
    int? Bee,
    int? Entry,
    int? Step,
    IReadOnlyList<string>? Levels,
    string? Search,
    int Offset,
    int Limit,
    bool Json,
    string? Url = null);

public sealed record RunsBeeOptions(string File, int BeeNumber, bool Json, string? Url = null);

public sealed record RunsCompareOptions(string FileA, string FileB, bool Json, string? Url = null);

/// <summary><c>runs trace</c>: the trace of a bee or a trace ID; <paramref name="Call"/> is 1-based.</summary>
public sealed record RunsTraceOptions(string File, string Key, int? Call, int MaxSpans, bool Json, string? Url = null);

/// <summary><c>runs hotspots</c>.</summary>
public sealed record RunsHotspotsOptions(string File, string? Endpoint, bool SlowOnly, int Limit, bool Json, string? Url = null);

public sealed record RunsOptions(
    RunsListOptions? List = null,
    RunsSummaryOptions? Summary = null,
    RunsErrorsOptions? Errors = null,
    RunsCallsOptions? Calls = null,
    RunsLogsOptions? Logs = null,
    RunsBeeOptions? Bee = null,
    RunsCompareOptions? Compare = null,
    RunsTimelineOptions? Timeline = null,
    RunsTraceOptions? Trace = null,
    RunsHotspotsOptions? Hotspots = null);

/// <summary>Parses <c>beesharp runs ...</c> subcommands and prints explicit 1-based help text.</summary>
internal static class RunsArguments
{
    public const string RunsUsage = """
        beesharp runs - inspect exported run bundles (.zip, .duckdb, .json) or live server runs (--url)

        Usage:
          beesharp runs <subcommand> [options]

        Subcommands:
          list [--url <url>]          List stored runs on a running Beesharp server
          summary <target>            Run metadata, bee counts, per-scenario & per-step counts, min/p50/p95/max latencies
          errors <target>             Grouped, untruncated errors with counts, affected entries/steps, and sample bees
          calls <target>              Endpoint latency breakdown and slow/failing call counts
          logs <target>               Filtered log lines
          bee <target> <bee>          Full step visits, HTTP calls, status codes, errors, and logs for one bee
          compare <targetA> <targetB> Latency and error-rate regression diff between two runs
          timeline <target>           Time-bucketed concurrency, req/s, latency, slow and failure timeline
          hotspots <target>           Where the time of slow calls went, per endpoint (from the fetched distributed traces)
          trace <target> <bee|id>     One bee's distributed trace as a flat span list (depth, service, self time, share)

        Targets & Remote Mode:
          Offline mode: <target> is an exported .zip (Parquet bundle), .duckdb, or .json file.
          Remote mode:  Pass --url <url> (or set BEESHARP_URL when <target> is a run ID or alias);
                        <target> can be an exact run ID, "latest", or "prev" (second most recent run).

        Conventions:
          Scenario entries, steps, and bees are 1-based (matching the UI: entry 1, step 1, bee 1 = B1).
          --json is a pure boolean flag on all "runs" commands and prints untruncated camelCase JSON to stdout.

        Run "beesharp runs <subcommand> --help" for subcommand options.

        Exit codes: 0 success, 2 invalid arguments, missing file/server error, corrupt bundle, or run/bee not found,
        3 (runs trace only) the trace is not stored with the run; the message says why.
        """;

    public const string ListUsage = """
        beesharp runs list - list stored runs on a running Beesharp server

        Usage:
          beesharp runs list [--url <url>] [--limit <n>] [--json]

        Options:
          --url <url>   Beesharp server URL (defaults to BEESHARP_URL environment variable).
          --limit <n>   Maximum runs to fetch (1 to 500, default 20).
          --json        Print untruncated JSON to stdout.
          -h, --help    Show this help text.
        """;

    public const string SummaryUsage = """
        beesharp runs summary - summarize an exported run bundle or remote run

        Usage:
          beesharp runs summary <file|runId|latest|prev> [--url <url>] [--json]

        Options:
          --url <url>   Query a running Beesharp server (also uses BEESHARP_URL if target is not a local file).
          --json        Print untruncated JSON to stdout.
          -h, --help    Show this help text.
        """;

    public const string ErrorsUsage = """
        beesharp runs errors - group and count untruncated errors in a run bundle or remote run

        Usage:
          beesharp runs errors <file|runId|latest|prev> [--url <url>] [--json]

        Options:
          --url <url>   Query a running Beesharp server (also uses BEESHARP_URL if target is not a local file).
          --json        Print untruncated JSON to stdout.
          -h, --help    Show this help text.
        """;

    public const string CallsUsage = """
        beesharp runs calls - endpoint latency breakdown and slow/failing call counts

        Usage:
          beesharp runs calls <file|runId|latest|prev> [options]

        Options:
          --url <url>      Query a running Beesharp server (also uses BEESHARP_URL if target is not a local file).
          --entry <n>      Filter to one scenario entry (1-based, e.g. 1).
          --scenario <id>  Filter to scenario entry/entries matching this scenario ID or display label.
          --step <n>       Filter to one scenario step (1-based, e.g. 1).
          --by-step        Split endpoint latency and slow counts by scenario step (useful when multiple steps share a URL).
          --slowest <n>    List the top <n> slowest individual calls (1 to 1000) with bee ID, step, duration, status, and headers.
          --endpoint <ep>  Filter to endpoints containing <ep> (works with summary or --slowest).
          --slow           Only endpoints/calls marked slow.
          --failed         Only endpoints/calls with failures.
          --min-ms <ms>    Minimum duration in milliseconds.
          --search <q>     Filter endpoints/calls whose normalized name, URL, host, or step name contains <q>.
          --json           Print untruncated JSON to stdout.
          -h, --help       Show this help text.
        """;

    public const string LogsUsage = """
        beesharp runs logs - query filtered log lines from a run bundle or remote run

        Usage:
          beesharp runs logs <file|runId|latest|prev> [options]

        Options:
          --url <url>              Query a running Beesharp server (also uses BEESHARP_URL if target is not a local file).
          --bee <n|Bn>             Filter to one bee (1-based: 1 or B1 = first bee).
          --entry <n|kind>         Filter to one scenario entry (1-based, e.g. 1) or preflight|shutdown|host.
          --step <n>               Filter to one scenario step (1-based, e.g. 1).
          --level <lvl[,lvl...]>   Filter by level(s): trace, debug, info, warn, error.
          --search <q>             Case-insensitive substring match in message, exception, or bee name.
          --offset <n>             Zero-based row offset (default 0).
          --limit <n>              Maximum log lines to return (default 200, max 10000).
          --json                   Print untruncated JSON to stdout.
          -h, --help               Show this help text.
        """;

    public const string BeeUsage = """
        beesharp runs bee - inspect one bee's step visits, calls, errors, and logs

        Usage:
          beesharp runs bee <file|runId|latest|prev> <bee> [--url <url>] [--json]

        Arguments:
          <target>      Exported run bundle (.zip, .duckdb, or .json) or run ID / latest / prev with --url.
          <bee>         1-based bee number (1 or B1 = first bee).

        Options:
          --url <url>   Query a running Beesharp server (also uses BEESHARP_URL if target is not a local file).
          --json        Print untruncated JSON to stdout.
          -h, --help    Show this help text.
        """;

    public const string CompareUsage = """
        beesharp runs compare - compare latency and error rates between two runs

        Usage:
          beesharp runs compare <fileA|runA|prev> <fileB|runB|latest> [--url <url>] [--json]

        Arguments:
          <targetA>     Baseline run bundle (.zip, .duckdb, .json) or run ID / prev / latest with --url.
          <targetB>     Candidate run bundle (.zip, .duckdb, .json) or run ID / latest / prev with --url.

        Options:
          --url <url>   Query a running Beesharp server (also uses BEESHARP_URL if targets are not local files).
          --json        Print untruncated JSON to stdout.
          -h, --help    Show this help text.
        """;

    public const string TimelineUsage = """
        beesharp runs timeline - time-bucketed concurrency, req/s, latency, slow and failure timeline

        Usage:
          beesharp runs timeline <file|runId|latest|prev> [options]

        Options:
          --url <url>         Query a running Beesharp server (also uses BEESHARP_URL if target is not a local file).
          --bucket <seconds>  Bucket width in seconds (default 5, e.g. 1, 5, 10, 30).
          --entry <n>         Filter to one scenario entry (1-based, e.g. 1).
          --scenario <id>     Filter to scenario entry/entries matching this scenario ID or display label.
          --json              Print untruncated JSON to stdout.
          -h, --help          Show this help text.
        """;

    public const string TraceUsage = """
        beesharp runs trace - one bee's distributed trace as a flat, bounded span list

        Usage:
          beesharp runs trace <file|runId|latest|prev> <bee|traceId> [--call <n>] [--max-spans <n>] [--url <url>] [--json]

        Arguments:
          <target>      Exported run bundle (.zip, .duckdb, or .json) or run ID / latest / prev with --url.
          <bee|traceId> A bee (1-based: 5 or B5) or a trace ID (32 hex characters, as printed by "runs calls --slowest" and "runs bee").

        Options:
          --url <url>      Query a running Beesharp server (also uses BEESHARP_URL if target is not a local file).
          --call <n>       Only the spans below call <n> of the bee (1-based, as numbered by "runs bee").
          --max-spans <n>  Show at most <n> spans (1 to 5000, default 50); over that the spans with the largest self time are kept.
          --json           Print untruncated JSON to stdout.
          -h, --help       Show this help text.

        Each line: depth, service, span name, duration, self time (the span's own time, without its children) and its share of the bee's (or call's) duration,
        error flag, HTTP status and key attributes such as db.query.text. Needs the trace to be fetched into the run (a TraceStore.* environment).
        Exit codes: 0 shown, 2 invalid arguments or run/bee not found, 3 the trace is not stored with the run (the message gives the likely reason).
        """;

    public const string HotspotsUsage = """
        beesharp runs hotspots - where the time of slow calls went, per endpoint

        Usage:
          beesharp runs hotspots <file|runId|latest|prev> [--endpoint <ep>] [--slow] [--limit <n>] [--url <url>] [--json]

        Options:
          --url <url>      Query a running Beesharp server (also uses BEESHARP_URL if target is not a local file).
          --endpoint <ep>  Only endpoints containing <ep>.
          --slow           Only endpoints that have slow calls with a fetched trace.
          --limit <n>      Show at most <n> endpoints (1 to 100, default 10); each shows its top 5 hotspots.
          --json           Print untruncated JSON to stdout.
          -h, --help       Show this help text.

        For each endpoint's slow calls: the span (service + name) with the largest self time per call, how many slow calls it explains, its typical self time and share of
        the call, example trace IDs for "runs trace", and the same span's time in the endpoint's typical calls as the baseline. Needs traces fetched into the run.
        """;

    public static ParsedArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
            return Invalid("Missing subcommand for 'runs'.");

        var sub = args[1];
        if (HeadlessArgumentParser.IsHelpFlag(sub) || string.Equals(sub, "help", StringComparison.OrdinalIgnoreCase))
            return new(CliCommand.RunsHelp, HelpText: RunsUsage);

        return sub.ToLowerInvariant() switch
        {
            "list" => ParseList(args),
            "summary" => ParseSummary(args),
            "errors" => ParseErrors(args),
            "calls" => ParseCalls(args),
            "logs" => ParseLogs(args),
            "bee" => ParseBee(args),
            "compare" => ParseCompare(args),
            "timeline" => ParseTimeline(args),
            "trace" => ParseTrace(args),
            "hotspots" => ParseHotspots(args),
            _ => Invalid($"Unknown runs subcommand '{sub}'."),
        };
    }

    private static ParsedArguments ParseList(IReadOnlyList<string> args)
    {
        var scan = Scan(args, startIndex: 2, ListUsage, valueOptions: ["--url", "--limit"]);
        if (scan.HelpText is not null) return new(CliCommand.RunsSubcommandHelp, HelpText: scan.HelpText);
        if (scan.Error is not null) return Invalid(scan.Error);
        if (scan.Positionals.Count > 0) return Invalid($"Unexpected argument '{scan.Positionals[0]}'.");
        if (!TryExtractUrl(scan, out var url, out var urlErr)) return Invalid(urlErr!);

        var limit = 20;
        if (scan.TryGetLast("--limit", out var limitRaw))
        {
            if (!int.TryParse(limitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) || l < 1 || l > 500)
                return Invalid($"--limit must be a whole number between 1 and 500, not '{limitRaw}'.");
            limit = l;
        }

        return new(CliCommand.RunsList, Runs: new RunsOptions(List: new RunsListOptions(url, limit, scan.Json)));
    }

    private static ParsedArguments ParseSummary(IReadOnlyList<string> args)
    {
        var scan = Scan(args, startIndex: 2, SummaryUsage, valueOptions: ["--url"]);
        if (scan.HelpText is not null) return new(CliCommand.RunsSubcommandHelp, HelpText: scan.HelpText);
        if (scan.Error is not null) return Invalid(scan.Error);
        if (scan.Positionals.Count == 0) return Invalid("Missing <file> argument for 'runs summary'.");
        if (scan.Positionals.Count > 1) return Invalid($"Unexpected argument '{scan.Positionals[1]}'.");
        if (!TryExtractUrl(scan, out var url, out var urlErr)) return Invalid(urlErr!);
        return new(CliCommand.RunsSummary, Runs: new RunsOptions(Summary: new RunsSummaryOptions(scan.Positionals[0], scan.Json, url)));
    }

    private static ParsedArguments ParseErrors(IReadOnlyList<string> args)
    {
        var scan = Scan(args, startIndex: 2, ErrorsUsage, valueOptions: ["--url"]);
        if (scan.HelpText is not null) return new(CliCommand.RunsSubcommandHelp, HelpText: scan.HelpText);
        if (scan.Error is not null) return Invalid(scan.Error);
        if (scan.Positionals.Count == 0) return Invalid("Missing <file> argument for 'runs errors'.");
        if (scan.Positionals.Count > 1) return Invalid($"Unexpected argument '{scan.Positionals[1]}'.");
        if (!TryExtractUrl(scan, out var url, out var urlErr)) return Invalid(urlErr!);
        return new(CliCommand.RunsErrors, Runs: new RunsOptions(Errors: new RunsErrorsOptions(scan.Positionals[0], scan.Json, url)));
    }

    private static ParsedArguments ParseCalls(IReadOnlyList<string> args)
    {
        var scan = Scan(
            args,
            startIndex: 2,
            CallsUsage,
            valueOptions: ["--url", "--entry", "--scenario", "--step", "--slowest", "--endpoint", "--min-ms", "--search"],
            boolFlags: ["--slow", "--failed", "--by-step"]);
        if (scan.HelpText is not null) return new(CliCommand.RunsSubcommandHelp, HelpText: scan.HelpText);
        if (scan.Error is not null) return Invalid(scan.Error);
        if (scan.Positionals.Count == 0) return Invalid("Missing <file> argument for 'runs calls'.");
        if (scan.Positionals.Count > 1) return Invalid($"Unexpected argument '{scan.Positionals[1]}'.");
        if (!TryExtractUrl(scan, out var url, out var urlErr)) return Invalid(urlErr!);

        int? entry = null;
        if (scan.TryGetLast("--entry", out var entryRaw))
        {
            if (!TryParseOneBasedInt(entryRaw, out var entryNum))
                return Invalid($"--entry must be a whole number of 1 or more (1-based), not '{entryRaw}'.");
            entry = entryNum - 1;
        }

        int? step = null;
        if (scan.TryGetLast("--step", out var stepRaw))
        {
            if (!TryParseOneBasedInt(stepRaw, out var stepNum))
                return Invalid($"--step must be a whole number of 1 or more (1-based), not '{stepRaw}'.");
            step = stepNum - 1;
        }

        int? slowest = null;
        if (scan.TryGetLast("--slowest", out var slowestRaw))
        {
            if (!int.TryParse(slowestRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > 1000)
                return Invalid($"--slowest must be a whole number between 1 and 1000, not '{slowestRaw}'.");
            slowest = n;
        }

        double? minMs = null;
        if (scan.TryGetLast("--min-ms", out var minRaw))
        {
            if (!double.TryParse(minRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms < 0 || double.IsNaN(ms) || double.IsInfinity(ms))
                return Invalid($"--min-ms must be a non-negative number, not '{minRaw}'.");
            minMs = ms;
        }

        scan.TryGetLast("--search", out var search);
        scan.TryGetLast("--scenario", out var scenario);
        scan.TryGetLast("--endpoint", out var endpoint);

        return new(CliCommand.RunsCalls, Runs: new RunsOptions(Calls: new RunsCallsOptions(
            scan.Positionals[0],
            entry,
            scan.HasFlag("--slow"),
            scan.HasFlag("--failed"),
            minMs,
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            scan.Json,
            url,
            step,
            scan.HasFlag("--by-step"),
            string.IsNullOrWhiteSpace(scenario) ? null : scenario.Trim(),
            string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim(),
            slowest)));
    }

    private static ParsedArguments ParseLogs(IReadOnlyList<string> args)
    {
        var scan = Scan(
            args,
            startIndex: 2,
            LogsUsage,
            valueOptions: ["--url", "--bee", "--entry", "--step", "--level", "--search", "--offset", "--limit"]);
        if (scan.HelpText is not null) return new(CliCommand.RunsSubcommandHelp, HelpText: scan.HelpText);
        if (scan.Error is not null) return Invalid(scan.Error);
        if (scan.Positionals.Count == 0) return Invalid("Missing <file> argument for 'runs logs'.");
        if (scan.Positionals.Count > 1) return Invalid($"Unexpected argument '{scan.Positionals[1]}'.");
        if (!TryExtractUrl(scan, out var url, out var urlErr)) return Invalid(urlErr!);

        int? bee = null;
        if (scan.TryGetLast("--bee", out var beeRaw))
        {
            if (!TryParseBeeNumber(beeRaw, out var beeNum))
                return Invalid($"--bee must be a 1-based bee number (e.g. 1 or B1), not '{beeRaw}'.");
            bee = beeNum - 1;
        }

        int? entry = null;
        if (scan.TryGetLast("--entry", out var entryRaw))
        {
            var lowered = entryRaw.Trim().ToLowerInvariant();
            if (lowered == "preflight") entry = ProgramLogEntry.Preflight;
            else if (lowered == "shutdown") entry = ProgramLogEntry.Shutdown;
            else if (lowered == "host") entry = ProgramLogEntry.Host;
            else if (TryParseOneBasedInt(entryRaw, out var entryNum)) entry = entryNum - 1;
            else return Invalid($"--entry must be a 1-based entry number (1 or more) or preflight|shutdown|host, not '{entryRaw}'.");
        }

        int? step = null;
        if (scan.TryGetLast("--step", out var stepRaw))
        {
            if (!TryParseOneBasedInt(stepRaw, out var stepNum))
                return Invalid($"--step must be a whole number of 1 or more (1-based), not '{stepRaw}'.");
            step = stepNum - 1;
        }

        IReadOnlyList<string>? levels = null;
        if (scan.TryGetAll("--level", out var levelValues))
        {
            var parsedLevels = new List<string>();
            foreach (var item in levelValues)
            {
                foreach (var part in item.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var normalized = NormalizeLogLevel(part);
                    if (normalized is null)
                        return Invalid($"--level must be trace, debug, info, warn, or error, not '{part}'.");
                    if (!parsedLevels.Contains(normalized, StringComparer.Ordinal))
                        parsedLevels.Add(normalized);
                }
            }
            if (parsedLevels.Count == 0)
                return Invalid("Option --level needs at least one log level.");
            levels = parsedLevels;
        }

        var offset = 0;
        if (scan.TryGetLast("--offset", out var offsetRaw))
        {
            if (!int.TryParse(offsetRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) || o < 0)
                return Invalid($"--offset must be a whole number of 0 or more, not '{offsetRaw}'.");
            offset = o;
        }

        var limit = 200;
        if (scan.TryGetLast("--limit", out var limitRaw))
        {
            if (!int.TryParse(limitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) || l < 1 || l > 10_000)
                return Invalid($"--limit must be a whole number between 1 and 10000, not '{limitRaw}'.");
            limit = l;
        }

        scan.TryGetLast("--search", out var search);

        return new(CliCommand.RunsLogs, Runs: new RunsOptions(Logs: new RunsLogsOptions(
            scan.Positionals[0],
            bee,
            entry,
            step,
            levels,
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            offset,
            limit,
            scan.Json,
            url)));
    }

    private static ParsedArguments ParseBee(IReadOnlyList<string> args)
    {
        var scan = Scan(args, startIndex: 2, BeeUsage, valueOptions: ["--url"]);
        if (scan.HelpText is not null) return new(CliCommand.RunsSubcommandHelp, HelpText: scan.HelpText);
        if (scan.Error is not null) return Invalid(scan.Error);
        if (scan.Positionals.Count == 0) return Invalid("Missing <file> argument for 'runs bee'.");
        if (scan.Positionals.Count == 1) return Invalid("Missing <bee> argument for 'runs bee'.");
        if (scan.Positionals.Count > 2) return Invalid($"Unexpected argument '{scan.Positionals[2]}'.");
        if (!TryExtractUrl(scan, out var url, out var urlErr)) return Invalid(urlErr!);

        if (!TryParseBeeNumber(scan.Positionals[1], out var beeNumber))
            return Invalid($"Bee number must be 1 or more (e.g. 1 or B1), not '{scan.Positionals[1]}'.");

        return new(CliCommand.RunsBee, Runs: new RunsOptions(Bee: new RunsBeeOptions(scan.Positionals[0], beeNumber, scan.Json, url)));
    }

    private static ParsedArguments ParseCompare(IReadOnlyList<string> args)
    {
        var scan = Scan(args, startIndex: 2, CompareUsage, valueOptions: ["--url"]);
        if (scan.HelpText is not null) return new(CliCommand.RunsSubcommandHelp, HelpText: scan.HelpText);
        if (scan.Error is not null) return Invalid(scan.Error);
        if (scan.Positionals.Count == 0) return Invalid("Missing <fileA> argument for 'runs compare'.");
        if (scan.Positionals.Count == 1) return Invalid("Missing <fileB> argument for 'runs compare'.");
        if (scan.Positionals.Count > 2) return Invalid($"Unexpected argument '{scan.Positionals[2]}'.");
        if (!TryExtractUrl(scan, out var url, out var urlErr)) return Invalid(urlErr!);
        return new(CliCommand.RunsCompare, Runs: new RunsOptions(Compare: new RunsCompareOptions(scan.Positionals[0], scan.Positionals[1], scan.Json, url)));
    }

    private static ParsedArguments ParseTimeline(IReadOnlyList<string> args)
    {
        var scan = Scan(args, startIndex: 2, TimelineUsage, valueOptions: ["--url", "--bucket", "--entry", "--scenario"]);
        if (scan.HelpText is not null) return new(CliCommand.RunsSubcommandHelp, HelpText: scan.HelpText);
        if (scan.Error is not null) return Invalid(scan.Error);
        if (scan.Positionals.Count == 0) return Invalid("Missing <file> argument for 'runs timeline'.");
        if (scan.Positionals.Count > 1) return Invalid($"Unexpected argument '{scan.Positionals[1]}'.");
        if (!TryExtractUrl(scan, out var url, out var urlErr)) return Invalid(urlErr!);

        var bucketSeconds = 5.0;
        if (scan.TryGetLast("--bucket", out var bucketRaw))
        {
            if (!double.TryParse(bucketRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                || b <= 0 || b > 3600 || double.IsNaN(b) || double.IsInfinity(b))
                return Invalid($"--bucket must be a positive number of seconds (up to 3600), not '{bucketRaw}'.");
            bucketSeconds = b;
        }

        int? entry = null;
        if (scan.TryGetLast("--entry", out var entryRaw))
        {
            if (!TryParseOneBasedInt(entryRaw, out var entryNum))
                return Invalid($"--entry must be a whole number of 1 or more (1-based), not '{entryRaw}'.");
            entry = entryNum - 1;
        }

        scan.TryGetLast("--scenario", out var scenario);

        return new(CliCommand.RunsTimeline, Runs: new RunsOptions(Timeline: new RunsTimelineOptions(
            scan.Positionals[0],
            bucketSeconds,
            entry,
            string.IsNullOrWhiteSpace(scenario) ? null : scenario.Trim(),
            scan.Json,
            url)));
    }

    private static ParsedArguments ParseTrace(IReadOnlyList<string> args)
    {
        var scan = Scan(args, startIndex: 2, TraceUsage, valueOptions: ["--url", "--call", "--max-spans"]);
        if (scan.HelpText is not null) return new(CliCommand.RunsSubcommandHelp, HelpText: scan.HelpText);
        if (scan.Error is not null) return Invalid(scan.Error);
        if (scan.Positionals.Count == 0) return Invalid("Missing <file> argument for 'runs trace'.");
        if (scan.Positionals.Count == 1) return Invalid("Missing <bee|traceId> argument for 'runs trace'.");
        if (scan.Positionals.Count > 2) return Invalid($"Unexpected argument '{scan.Positionals[2]}'.");
        if (!TryExtractUrl(scan, out var url, out var urlErr)) return Invalid(urlErr!);
        if (TraceAnalytics.ParseKey(scan.Positionals[1]) is null)
            return Invalid($"'{scan.Positionals[1]}' is neither a bee (1-based: 5 or B5) nor a trace ID (32 hex characters).");

        int? call = null;
        if (scan.TryGetLast("--call", out var callRaw))
        {
            if (!TryParseOneBasedInt(callRaw, out var callNum))
                return Invalid($"--call must be a whole number of 1 or more (1-based), not '{callRaw}'.");
            call = callNum;
        }

        var maxSpans = 50;
        if (scan.TryGetLast("--max-spans", out var maxRaw))
        {
            if (!int.TryParse(maxRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > 5000)
                return Invalid($"--max-spans must be a whole number between 1 and 5000, not '{maxRaw}'.");
            maxSpans = n;
        }

        return new(CliCommand.RunsTrace, Runs: new RunsOptions(Trace: new RunsTraceOptions(scan.Positionals[0], scan.Positionals[1].Trim(), call, maxSpans, scan.Json, url)));
    }

    private static ParsedArguments ParseHotspots(IReadOnlyList<string> args)
    {
        var scan = Scan(args, startIndex: 2, HotspotsUsage, valueOptions: ["--url", "--endpoint", "--limit"], boolFlags: ["--slow"]);
        if (scan.HelpText is not null) return new(CliCommand.RunsSubcommandHelp, HelpText: scan.HelpText);
        if (scan.Error is not null) return Invalid(scan.Error);
        if (scan.Positionals.Count == 0) return Invalid("Missing <file> argument for 'runs hotspots'.");
        if (scan.Positionals.Count > 1) return Invalid($"Unexpected argument '{scan.Positionals[1]}'.");
        if (!TryExtractUrl(scan, out var url, out var urlErr)) return Invalid(urlErr!);

        var limit = 10;
        if (scan.TryGetLast("--limit", out var limitRaw))
        {
            if (!int.TryParse(limitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > 100)
                return Invalid($"--limit must be a whole number between 1 and 100, not '{limitRaw}'.");
            limit = n;
        }

        scan.TryGetLast("--endpoint", out var endpoint);
        return new(CliCommand.RunsHotspots, Runs: new RunsOptions(Hotspots: new RunsHotspotsOptions(
            scan.Positionals[0], string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim(), scan.HasFlag("--slow"), limit, scan.Json, url)));
    }

    private static bool TryExtractUrl(ScanResult scan, out string? url, out string? error)
    {
        url = null;
        error = null;
        if (!scan.TryGetLast("--url", out var raw))
            return true;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Option --url needs a URL.";
            return false;
        }
        url = raw.Trim();
        return true;
    }

    internal static bool TryParseOneBasedInt(string raw, out int value) =>
        int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 1;

    internal static bool TryParseBeeNumber(string raw, out int beeNumber)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('B') || trimmed.StartsWith('b'))
            trimmed = trimmed[1..];
        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out beeNumber) && beeNumber >= 1;
    }

    private static string? NormalizeLogLevel(string raw) =>
        HeadlessArgumentParser.NormalizeLogLevel(raw)
        ?? (raw.Trim().ToLowerInvariant() is "critical" or "ftl" ? "Error" : null);

    private static ParsedArguments Invalid(string message) => new(CliCommand.Invalid, Error: message);

    private sealed record ScanResult(
        string? HelpText,
        string? Error,
        bool Json,
        List<string> Positionals,
        HashSet<string> Flags,
        Dictionary<string, List<string>> Values)
    {
        public bool HasFlag(string name) => Flags.Contains(name);

        public bool TryGetLast(string name, out string value)
        {
            if (Values.TryGetValue(name, out var list) && list.Count > 0)
            {
                value = list[^1];
                return true;
            }
            value = "";
            return false;
        }

        public bool TryGetAll(string name, out List<string> values)
        {
            if (Values.TryGetValue(name, out var list) && list.Count > 0)
            {
                values = list;
                return true;
            }
            values = [];
            return false;
        }
    }

    private static ScanResult Scan(
        IReadOnlyList<string> args,
        int startIndex,
        string usage,
        string[] valueOptions,
        string[]? boolFlags = null)
    {
        var valueSet = new HashSet<string>(valueOptions, StringComparer.Ordinal);
        var boolSet = new HashSet<string>(boolFlags ?? [], StringComparer.Ordinal);
        var positionals = new List<string>();
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var json = false;
        var endOfOptions = false;

        for (var i = startIndex; i < args.Count; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && HeadlessArgumentParser.IsHelpFlag(arg))
                return new(usage, null, false, positionals, flags, values);

            if (endOfOptions || !arg.StartsWith('-') || arg == "-")
            {
                positionals.Add(arg);
                continue;
            }

            string name = arg;
            string? inline = null;
            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                name = arg[..eq];
                inline = arg[(eq + 1)..];
            }

            if (name == "--json")
            {
                if (inline is not null)
                    return new(null, "Option --json is a boolean flag and does not take a value.", false, positionals, flags, values);
                json = true;
                continue;
            }

            if (boolSet.Contains(name))
            {
                if (inline is not null)
                    return new(null, $"Option {name} is a boolean flag and does not take a value.", false, positionals, flags, values);
                flags.Add(name);
                continue;
            }

            if (valueSet.Contains(name))
            {
                string val;
                if (inline is not null)
                    val = inline;
                else if (i + 1 < args.Count && args[i + 1] != "--" && !args[i + 1].StartsWith('-'))
                    val = args[++i];
                else
                    return new(null, $"Option {name} needs a value.", false, positionals, flags, values);

                if (!values.TryGetValue(name, out var list))
                    values[name] = list = [];
                list.Add(val);
                continue;
            }

            return new(null, $"Unknown option '{name}'.", false, positionals, flags, values);
        }

        return new(null, null, json, positionals, flags, values);
    }
}
