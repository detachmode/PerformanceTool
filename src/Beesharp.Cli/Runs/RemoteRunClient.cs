using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Cli.Runs;

/// <summary>
/// HTTP client for inspecting runs on a live Beesharp server via <c>--url</c> or <c>BEESHARP_URL</c>.
/// Never accesses the server's <c>data/beesharp.duckdb</c> file directly (brain 0061).
/// </summary>
internal sealed class RemoteRunClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Uri baseUri;
    private readonly HttpClient http;

    public string BaseUrl { get; }

    public RemoteRunClient(string rawUrl, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)
            || !Uri.TryCreate(rawUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            throw new CouldNotRunException($"Invalid server URL '{rawUrl}'. Expected an http:// or https:// URL.");
        }

        baseUri = parsed;
        BaseUrl = parsed.ToString().TrimEnd('/');
        http = handler is null
            ? new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) }
            : new HttpClient(handler, disposeHandler: false) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<RunListReport> ListRunsAsync(int limit, CancellationToken ct)
    {
        var runs = await GetJsonAsync<List<StoredRunSummary>>(
            $"api/runs?limit={limit.ToString(CultureInfo.InvariantCulture)}",
            notFoundMessage: null,
            ct) ?? [];

        var items = runs.Select(r => new RunListItemDto(
            r.RunId,
            r.StartedUtc,
            r.FinishedUtc,
            r.State,
            r.ElapsedSeconds,
            r.TotalBees,
            Math.Max(0, r.TotalBees - r.FailedBees),
            r.FailedBees,
            r.TotalLogs,
            r.Error)).ToList();

        return new RunListReport(BaseUrl, items.Count, items);
    }

    public async Task<string> ResolveRunIdAsync(string target, CancellationToken ct)
    {
        var (first, _) = await ResolvePairAsync(target, null, ct);
        return first;
    }

    public async Task<(string First, string Second)> ResolveCompareRunIdsAsync(string targetA, string targetB, CancellationToken ct)
    {
        var (a, b) = await ResolvePairAsync(targetA, targetB, ct);
        return (a, b!);
    }

    private async Task<(string First, string? Second)> ResolvePairAsync(string targetA, string? targetB, CancellationToken ct)
    {
        var needLatestOrPrev = IsAlias(targetA) || (targetB is not null && IsAlias(targetB));
        IReadOnlyList<StoredRunSummary>? recent = null;
        if (needLatestOrPrev)
            recent = await GetJsonAsync<List<StoredRunSummary>>("api/runs?limit=2", notFoundMessage: null, ct) ?? [];

        string ResolveOne(string raw)
        {
            var trimmed = raw.Trim();
            if (string.Equals(trimmed, "latest", StringComparison.OrdinalIgnoreCase))
            {
                if (recent is not { Count: >= 1 })
                    throw new CouldNotRunException($"No runs found on {BaseUrl} to resolve 'latest'.");
                return recent[0].RunId;
            }
            if (string.Equals(trimmed, "prev", StringComparison.OrdinalIgnoreCase))
            {
                if (recent is not { Count: >= 2 })
                    throw new CouldNotRunException($"Fewer than 2 runs found on {BaseUrl} to resolve 'prev'.");
                return recent[1].RunId;
            }
            return trimmed;
        }

        return (ResolveOne(targetA), targetB is null ? null : ResolveOne(targetB));
    }

    private static bool IsAlias(string raw) =>
        string.Equals(raw.Trim(), "latest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(raw.Trim(), "prev", StringComparison.OrdinalIgnoreCase);

    public async Task<IRunBundle> OpenParquetBundleAsync(string runId, BundleTables neededTables, CancellationToken ct)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"beesharp-dl-{Guid.NewGuid():N}.zip");
        try
        {
            var path = $"api/runs/{Uri.EscapeDataString(runId)}/export?format=parquet";
            using var resp = await SendAsync(path, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new CouldNotRunException($"Run '{runId}' was not found on {BaseUrl}.");
            EnsureSuccess(resp);

            await using (var fs = File.Create(tempZip))
                await resp.Content.CopyToAsync(fs, ct);

            var inner = ParquetZipRunBundle.Open(tempZip, neededTables);
            return new DownloadedBundle(tempZip, inner);
        }
        catch
        {
            TryDeleteFile(tempZip);
            throw;
        }
    }

    public async Task<RunCallsReport> GetCallsAsync(string runId, RunsCallsOptions options, CancellationToken ct)
    {
        var query = new List<string>();
        if (options.Entry is { } entry)
            query.Add($"entry={entry.ToString(CultureInfo.InvariantCulture)}");
        if (options.SlowOnly)
            query.Add("slowOnly=true");
        if (options.FailedOnly)
            query.Add("failedOnly=true");
        if (options.MinMs is { } minMs)
            query.Add($"minMs={minMs.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(options.Search))
            query.Add($"search={Uri.EscapeDataString(options.Search)}");

        var qs = query.Count > 0 ? "?" + string.Join("&", query) : "";
        var path = $"api/runs/{Uri.EscapeDataString(runId)}/calls/endpoints{qs}";
        var res = await GetJsonAsync<CallEndpointsResult>(path, $"Run '{runId}' was not found on {BaseUrl}.", ct)
            ?? new CallEndpointsResult([], 0, 0, 0);

        var endpoints = res.Endpoints.Select(ep => new RunCallEndpointDto(
            ep.Endpoint,
            ep.Method,
            ep.Host,
            ep.TotalCount,
            ep.SlowCount,
            ep.FailedCount,
            ep.TypicalMs,
            ep.P95Ms,
            ep.MaxMs,
            ep.MinMs,
            ep.AvgMs,
            ep.SlowThresholdMs,
            ep.HasCustomThreshold,
            ep.Entries.Select(e => e + 1).ToArray())).ToList();

        return new RunCallsReport(runId, res.TotalCalls, res.TotalSlowCalls, res.TotalFailedCalls, endpoints);
    }

    public async Task<RunLogsReport> QueryLogsAsync(string runId, RunsLogsOptions options, CancellationToken ct)
    {
        var query = new List<string>();
        if (options.Entry is { } entry)
            query.Add($"entry={entry.ToString(CultureInfo.InvariantCulture)}");
        if (options.Step is { } step)
            query.Add($"step={step.ToString(CultureInfo.InvariantCulture)}");
        if (options.Bee is { } beeSeq)
            query.Add($"bee={beeSeq.ToString(CultureInfo.InvariantCulture)}");
        if (options.Levels is { Count: > 0 } lvls && lvls.Count < 5)
            query.Add($"levels={Uri.EscapeDataString(string.Join(",", lvls))}");
        if (!string.IsNullOrWhiteSpace(options.Search))
            query.Add($"search={Uri.EscapeDataString(options.Search)}");
        query.Add($"offset={options.Offset.ToString(CultureInfo.InvariantCulture)}");
        query.Add($"limit={options.Limit.ToString(CultureInfo.InvariantCulture)}");

        var path = $"api/runs/{Uri.EscapeDataString(runId)}/logs?{string.Join("&", query)}";
        var page = await GetJsonAsync<LogPageResult>(path, $"Run '{runId}' was not found on {BaseUrl}.", ct)
            ?? new LogPageResult(0, options.Offset, options.Limit, []);

        var items = page.Items.Select(row =>
        {
            var (kind, entryLabel) = row.Entry switch
            {
                ProgramLogEntry.Preflight => ("preflight", "Preflight"),
                ProgramLogEntry.Shutdown => ("shutdown", "Shutdown"),
                ProgramLogEntry.Host => ("host", "Host"),
                _ => ("scenario", $"Entry {row.Entry + 1}"),
            };
            var isScenario = row.Entry >= 0;
            return new RunLogLineDto(
                row.Id,
                row.Sequence >= 0 ? row.Sequence + 1 : null,
                row.BeeName,
                isScenario ? row.Entry + 1 : null,
                kind,
                entryLabel,
                isScenario && row.Step > 0 ? row.Step + 1 : null,
                null,
                null,
                row.AtSeconds,
                row.Level,
                row.Message,
                row.Exception,
                row.CallIndex is { } ci ? ci + 1 : null,
                row.IsFailed,
                row.IsSlow);
        }).ToList();

        return new RunLogsReport(runId, page.TotalCount, page.Offset, page.Limit, page.Offset + items.Count < page.TotalCount, items);
    }

    public async Task<RunBeeReport> GetBeeAsync(string runId, int beeNumber, CancellationToken ct)
    {
        var seq = beeNumber - 1;
        var path = $"api/runs/{Uri.EscapeDataString(runId)}/bees/{seq.ToString(CultureInfo.InvariantCulture)}";
        var detail = await GetJsonAsync<StoredBeeDetail>(
            path,
            $"Bee B{beeNumber} (#{beeNumber}) or run '{runId}' was not found on {BaseUrl}.",
            ct) ?? throw new CouldNotRunException($"Bee B{beeNumber} (#{beeNumber}) or run '{runId}' was not found on {BaseUrl}.");

        var hasSteps = detail.StepVisits is { Count: > 0 } || detail.FailedStep >= 0;
        var slowCalls = detail.Calls.Count(c => c.Error is null && SlowCallRule.IsSlow(c.DurationMs, null, c.SlowThresholdMs));
        var isFailed = detail.Error is not null;
        var isCanceled = ExecutionResult.IsCanceledError(detail.Error);
        var isSlow = slowCalls > 0;
        var outcome = isCanceled ? "canceled" : isFailed ? "failed" : isSlow ? "slow" : "ok";

        var visitsDto = (detail.StepVisits ?? [])
            .Select(v => new BeeStepVisitDto(v.Step + 1, $"Step {v.Step + 1}", v.Rep + 1, v.Data))
            .ToList();

        var callsDto = detail.Calls.Select((c, idx) => new BeeCallDto(
            idx + 1,
            c.Name,
            string.IsNullOrWhiteSpace(c.Endpoint) ? EndpointNormalizer.NormalizeCallName(c.Name) : c.Endpoint,
            c.Method,
            c.Host,
            c.Url,
            hasSteps || c.Step > 0 ? c.Step + 1 : null,
            null,
            hasSteps || c.Rep > 0 ? c.Rep + 1 : null,
            c.OffsetMs,
            c.DurationMs,
            c.StatusCode,
            c.Bytes,
            c.SlowThresholdMs,
            c.Error)).ToList();

        var logsDto = detail.Logs.Select((l, idx) => new BeeLogDto(
            idx + 1,
            l.AtSeconds,
            l.Level,
            l.Message,
            l.Exception,
            hasSteps || l.Step > 0 ? l.Step + 1 : null,
            null,
            hasSteps || l.Rep > 0 ? l.Rep + 1 : null,
            l.CallIndex is { } ci ? ci + 1 : null)).ToList();

        return new RunBeeReport(
            detail.RunId,
            detail.Sequence + 1,
            $"B{detail.Sequence + 1}",
            detail.Entry + 1,
            $"Entry {detail.Entry + 1}",
            detail.StartedAt,
            detail.DurationMs,
            detail.StatusCode,
            detail.Bytes,
            outcome,
            isFailed,
            isCanceled,
            isSlow,
            slowCalls,
            detail.Error,
            detail.FailedStep >= 0 ? detail.FailedStep + 1 : null,
            null,
            detail.FailedStep >= 0 && detail.FailedRep >= 0 ? detail.FailedRep + 1 : null,
            visitsDto,
            callsDto,
            logsDto);
    }

    private async Task<T?> GetJsonAsync<T>(string relativePath, string? notFoundMessage, CancellationToken ct)
    {
        using var resp = await SendAsync(relativePath, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new CouldNotRunException(notFoundMessage ?? $"Resource '{relativePath}' was not found on {BaseUrl}.");
        EnsureSuccess(resp);

        try
        {
            return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new CouldNotRunException($"Invalid JSON response from {BaseUrl}/{relativePath}: {ex.Message}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            return await http.GetAsync(relativePath, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CouldNotRunException($"Could not reach Beesharp server at {BaseUrl}: {ex.Message}");
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CouldNotRunException($"Request to Beesharp server at {BaseUrl} timed out: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw new CouldNotRunException($"Request to Beesharp server at {BaseUrl} was canceled.");
        }
    }

    private void EnsureSuccess(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        throw new CouldNotRunException($"Server {BaseUrl} returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
    }

    public void Dispose() => http.Dispose();

    private sealed class DownloadedBundle(string tempZipPath, IRunBundle inner) : IRunBundle
    {
        public RunSummaryReport GetSummary() => inner.GetSummary();
        public RunErrorsReport GetErrors() => inner.GetErrors();
        public RunCallsReport GetCalls(RunsCallsOptions options) => inner.GetCalls(options);
        public RunLogsReport QueryLogs(RunsLogsOptions options) => inner.QueryLogs(options);
        public RunBeeReport GetBee(int beeNumber) => inner.GetBee(beeNumber);

        public void Dispose()
        {
            inner.Dispose();
            TryDeleteFile(tempZipPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
