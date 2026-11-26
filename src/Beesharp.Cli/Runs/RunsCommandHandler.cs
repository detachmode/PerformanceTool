namespace Beesharp.Cli.Runs;

/// <summary>Executes offline (<c>.zip</c>, <c>.duckdb</c>, <c>.json</c>) and remote (<c>--url</c> / <c>BEESHARP_URL</c>) <c>beesharp runs ...</c> subcommands.</summary>
internal static class RunsCommandHandler
{
    public static async Task<int> ExecuteAsync(
        ParsedArguments parsed,
        TextWriter output,
        HttpMessageHandler? httpHandler = null,
        Func<string, string?>? getEnvironmentVariable = null,
        CancellationToken ct = default)
    {
        var getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        switch (parsed.Command)
        {
            case CliCommand.RunsList:
            {
                var opts = parsed.Runs!.List!;
                var url = ResolveRemoteUrl(opts.Url, getEnv)
                    ?? throw new CouldNotRunException("runs list requires --url <url> or BEESHARP_URL.");
                using var client = new RemoteRunClient(url, httpHandler);
                var report = await client.ListRunsAsync(opts.Limit, ct);
                WriteResult(output, opts.Json, report, report.ToText());
                return ExitCodes.Ok;
            }
            case CliCommand.RunsSummary:
            {
                var opts = parsed.Runs!.Summary!;
                var url = ResolveRemoteUrl(opts.Url, getEnv, opts.File);
                if (url is not null)
                {
                    using var client = new RemoteRunClient(url, httpHandler);
                    var runId = await client.ResolveRunIdAsync(opts.File, ct);
                    using var bundle = await client.OpenParquetBundleAsync(runId, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees | BundleTables.Calls, ct);
                    var report = bundle.GetSummary();
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                else
                {
                    using var bundle = RunBundleReader.Open(opts.File, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees | BundleTables.Calls);
                    var report = bundle.GetSummary();
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                return ExitCodes.Ok;
            }
            case CliCommand.RunsErrors:
            {
                var opts = parsed.Runs!.Errors!;
                var url = ResolveRemoteUrl(opts.Url, getEnv, opts.File);
                if (url is not null)
                {
                    using var client = new RemoteRunClient(url, httpHandler);
                    var runId = await client.ResolveRunIdAsync(opts.File, ct);
                    using var bundle = await client.OpenParquetBundleAsync(runId, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees, ct);
                    var report = bundle.GetErrors();
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                else
                {
                    using var bundle = RunBundleReader.Open(opts.File, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees);
                    var report = bundle.GetErrors();
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                return ExitCodes.Ok;
            }
            case CliCommand.RunsCalls:
            {
                var opts = parsed.Runs!.Calls!;
                var url = ResolveRemoteUrl(opts.Url, getEnv, opts.File);
                if (opts.Slowest is > 0)
                {
                    if (url is not null)
                    {
                        using var client = new RemoteRunClient(url, httpHandler);
                        var runId = await client.ResolveRunIdAsync(opts.File, ct);
                        var report = await client.GetSlowestCallsAsync(runId, opts, ct);
                        WriteResult(output, opts.Json, report, report.ToText());
                    }
                    else
                    {
                        using var bundle = RunBundleReader.Open(opts.File, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees | BundleTables.Calls);
                        var report = bundle.GetSlowestCalls(opts);
                        WriteResult(output, opts.Json, report, report.ToText());
                    }
                }
                else if (url is not null)
                {
                    using var client = new RemoteRunClient(url, httpHandler);
                    var runId = await client.ResolveRunIdAsync(opts.File, ct);
                    var report = await client.GetCallsAsync(runId, opts, ct);
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                else
                {
                    using var bundle = RunBundleReader.Open(opts.File, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees | BundleTables.Calls);
                    var report = bundle.GetCalls(opts);
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                return ExitCodes.Ok;
            }
            case CliCommand.RunsLogs:
            {
                var opts = parsed.Runs!.Logs!;
                var url = ResolveRemoteUrl(opts.Url, getEnv, opts.File);
                if (url is not null)
                {
                    using var client = new RemoteRunClient(url, httpHandler);
                    var runId = await client.ResolveRunIdAsync(opts.File, ct);
                    var report = await client.QueryLogsAsync(runId, opts, ct);
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                else
                {
                    using var bundle = RunBundleReader.Open(opts.File, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Logs);
                    var report = bundle.QueryLogs(opts);
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                return ExitCodes.Ok;
            }
            case CliCommand.RunsBee:
            {
                var opts = parsed.Runs!.Bee!;
                var url = ResolveRemoteUrl(opts.Url, getEnv, opts.File);
                if (url is not null)
                {
                    using var client = new RemoteRunClient(url, httpHandler);
                    var runId = await client.ResolveRunIdAsync(opts.File, ct);
                    var report = await client.GetBeeAsync(runId, opts.BeeNumber, ct);
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                else
                {
                    using var bundle = RunBundleReader.Open(opts.File, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees | BundleTables.Calls | BundleTables.Logs);
                    var report = bundle.GetBee(opts.BeeNumber);
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                return ExitCodes.Ok;
            }
            case CliCommand.RunsCompare:
            {
                var opts = parsed.Runs!.Compare!;
                var url = ResolveRemoteUrl(opts.Url, getEnv, opts.FileA, opts.FileB);
                if (url is not null)
                {
                    using var client = new RemoteRunClient(url, httpHandler);
                    var (runA, runB) = await client.ResolveCompareRunIdsAsync(opts.FileA, opts.FileB, ct);
                    using var bundleA = await client.OpenParquetBundleAsync(runA, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees, ct);
                    using var bundleB = await client.OpenParquetBundleAsync(runB, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees, ct);
                    var report = RunsComparer.Compare(bundleA.GetSummary(), bundleB.GetSummary());
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                else
                {
                    using var bundleA = RunBundleReader.Open(opts.FileA, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees);
                    using var bundleB = RunBundleReader.Open(opts.FileB, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees);
                    var report = RunsComparer.Compare(bundleA.GetSummary(), bundleB.GetSummary());
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                return ExitCodes.Ok;
            }
            case CliCommand.RunsTimeline:
            {
                var opts = parsed.Runs!.Timeline!;
                var url = ResolveRemoteUrl(opts.Url, getEnv, opts.File);
                if (url is not null)
                {
                    using var client = new RemoteRunClient(url, httpHandler);
                    var runId = await client.ResolveRunIdAsync(opts.File, ct);
                    using var bundle = await client.OpenParquetBundleAsync(runId, BundleTables.Runs | BundleTables.Bees | BundleTables.Calls, ct);
                    var report = bundle.GetTimeline(opts);
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                else
                {
                    using var bundle = RunBundleReader.Open(opts.File, BundleTables.Runs | BundleTables.Bees | BundleTables.Calls);
                    var report = bundle.GetTimeline(opts);
                    WriteResult(output, opts.Json, report, report.ToText());
                }
                return ExitCodes.Ok;
            }
            case CliCommand.RunsTrace:
            {
                var opts = parsed.Runs!.Trace!;
                var url = ResolveRemoteUrl(opts.Url, getEnv, opts.File);
                RunTraceReport report;
                if (url is not null)
                {
                    using var client = new RemoteRunClient(url, httpHandler);
                    var runId = await client.ResolveRunIdAsync(opts.File, ct);
                    report = await client.GetTraceAsync(runId, opts, ct);
                }
                else
                {
                    using var bundle = RunBundleReader.Open(opts.File, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees | BundleTables.Calls | BundleTables.Traces);
                    report = bundle.GetTrace(opts);
                }
                WriteResult(output, opts.Json, report, report.ToText());
                return report.ExitCode;
            }
            case CliCommand.RunsHotspots:
            {
                var opts = parsed.Runs!.Hotspots!;
                var url = ResolveRemoteUrl(opts.Url, getEnv, opts.File);
                HotspotsResultDto report;
                if (url is not null)
                {
                    using var client = new RemoteRunClient(url, httpHandler);
                    var runId = await client.ResolveRunIdAsync(opts.File, ct);
                    report = await client.GetHotspotsAsync(runId, opts, ct);
                }
                else
                {
                    using var bundle = RunBundleReader.Open(opts.File, BundleTables.Runs | BundleTables.RunSteps | BundleTables.Bees | BundleTables.Calls | BundleTables.Traces);
                    report = bundle.GetHotspots(opts);
                }
                WriteResult(output, opts.Json, report, HotspotsText.ToText(report));
                return ExitCodes.Ok;
            }
            default:
                throw new InvalidOperationException($"Unsupported runs command {parsed.Command}.");
        }
    }

    private static string? ResolveRemoteUrl(
        string? explicitUrl,
        Func<string, string?> getEnv,
        params string[] targets)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return explicitUrl.Trim();

        var envUrl = getEnv("BEESHARP_URL");
        if (string.IsNullOrWhiteSpace(envUrl))
            return null;

        if (targets.Length == 0)
            return envUrl.Trim();

        foreach (var t in targets)
        {
            if (File.Exists(Path.GetFullPath(t)) || LooksLikeBundlePath(t))
                return null;
        }

        return envUrl.Trim();
    }

    private static bool LooksLikeBundlePath(string target)
    {
        if (target.Contains('/') || target.Contains('\\'))
            return true;
        var ext = Path.GetExtension(target).ToLowerInvariant();
        return ext is ".zip" or ".duckdb" or ".json";
    }

    private static void WriteResult<T>(TextWriter output, bool json, T report, string text)
    {
        if (json)
            output.WriteLine(RunsJson.Serialize(report));
        else
            output.Write(text);
    }
}
