using System.Diagnostics;

namespace PerformanceTool.Services;

public sealed record LoadTestSettings(string Url, int TotalRequests, int Concurrency, int TimeoutSeconds);

/// <summary>Outcome of one HTTP request. <see cref="Sequence"/> is the order in which the request was started.</summary>
public sealed record RequestResult(int Sequence, double ElapsedMs, int? StatusCode, long Bytes, string? Error)
{
    public bool Success => Error is null && StatusCode is >= 200 and < 400;
}

public sealed class LoadTester
{
    public const int MaxRequests = 10_000;
    public const int MaxConcurrency = 200;

    public static bool TryValidate(LoadTestSettings s, out string? error)
    {
        if (!Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            error = "Enter a valid absolute http:// or https:// URL.";
        else if (s.TotalRequests is < 1 or > MaxRequests)
            error = $"Requests must be between 1 and {MaxRequests}.";
        else if (s.Concurrency < 1 || s.Concurrency > MaxConcurrency)
            error = $"Concurrency must be between 1 and {MaxConcurrency}.";
        else if (s.TimeoutSeconds is < 1 or > 300)
            error = "Timeout must be between 1 and 300 seconds.";
        else
            error = null;
        return error is null;
    }

    /// <summary>
    /// Fires <see cref="LoadTestSettings.TotalRequests"/> GET requests using up to
    /// <see cref="LoadTestSettings.Concurrency"/> parallel workers, reporting each result as soon as it completes.
    /// </summary>
    public async Task RunAsync(LoadTestSettings settings, Func<RequestResult, ValueTask> onResult, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = settings.Concurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PerformanceTool/1.0");

        var next = -1;
        var workers = Enumerable.Range(0, Math.Min(settings.Concurrency, settings.TotalRequests)).Select(_ => Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var seq = Interlocked.Increment(ref next);
                if (seq >= settings.TotalRequests) break;
                var result = await SendOneAsync(client, settings.Url, seq, ct);
                if (result is null) break; // cancelled
                await onResult(result);
            }
        }, CancellationToken.None));

        await Task.WhenAll(workers);
    }

    private static async Task<RequestResult?> SendOneAsync(HttpClient client, string url, int seq, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            sw.Stop();
            return new RequestResult(seq, sw.Elapsed.TotalMilliseconds, (int)response.StatusCode, body.LongLength, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var msg = ex is TaskCanceledException ? "Timeout" : (ex.InnerException?.Message ?? ex.Message);
            return new RequestResult(seq, sw.Elapsed.TotalMilliseconds, null, 0, msg);
        }
    }
}
