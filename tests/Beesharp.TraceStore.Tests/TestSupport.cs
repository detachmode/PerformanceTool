using System.Collections.Concurrent;
using Beesharp.Services.TraceStore;
using Microsoft.Extensions.Logging;

namespace Beesharp.TraceStore.Tests;

/// <summary>An <see cref="ITraceStore"/> whose answers the test decides: gets the trace ID and how often it was asked (1 = first request).</summary>
public sealed class FakeTraceStore(Func<string, int, TraceLookup> answer) : ITraceStore, IDisposable
{
    private readonly ConcurrentDictionary<string, int> attempts = new();
    private readonly ConcurrentQueue<(string TraceId, long Ticks)> requests = new();

    public string Kind => "fake";
    public string Description => "Fake store at http://fake.test";
    public bool Disposed { get; private set; }

    public int RequestCount => requests.Count;
    public IReadOnlyList<(string TraceId, long Ticks)> Requests => requests.ToArray();
    public int AttemptsFor(string traceId) => attempts.GetValueOrDefault(traceId);

    public Task<TraceLookup> GetTraceAsync(string traceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var attempt = attempts.AddOrUpdate(traceId, 1, (_, n) => n + 1);
        requests.Enqueue((traceId, System.Diagnostics.Stopwatch.GetTimestamp()));
        return Task.FromResult(answer(traceId, attempt));
    }

    public void Dispose() => Disposed = true;

    /// <summary>A found trace: a bee root and one service span below it.</summary>
    public static TraceLookup FoundTrace(string traceId, int spans = 2) =>
        new(TraceLookupOutcome.Found, Enumerable.Range(0, spans)
            .Select(i => new TraceSpanDto(traceId, $"{i + 1:x16}", i == 0 ? null : $"{i:x16}", i == 0 ? "beesharp" : "shop-api", i == 0 ? "bee" : $"span {i}", "internal", 1_000_000_000L * (i + 1), 10 - i))
            .ToList());
}

/// <summary>A logger that keeps what was logged, to assert on the lines users read.</summary>
public sealed class CapturingLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> lines = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Lines => lines.ToArray();
    public IReadOnlyList<string> Messages(LogLevel level) => lines.Where(l => l.Level == level).Select(l => l.Message).ToList();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        lines.Enqueue((logLevel, formatter(state, exception)));
}

/// <summary>Builders for what the run reports about its bees.</summary>
public static class Bees
{
    public static string TraceId(int sequence) => sequence.ToString("x32");

    public static TraceBeeObservation Bee(int sequence, DateTimeOffset? endedUtc = null, bool failed = false, params TraceCallObservation[] calls) =>
        new(TraceId(sequence), sequence, failed, endedUtc ?? DateTimeOffset.UtcNow, calls);

    public static TraceCallObservation Call(string endpoint, double ms, bool slow = false, bool failed = false) => new(endpoint, ms, slow, failed);

    /// <summary>Options that make everything fast: no waiting, small caps are set by the test.</summary>
    public static TraceFetchOptions FastOptions() => new()
    {
        SettleDelay = TimeSpan.Zero,
        PollInterval = TimeSpan.FromMilliseconds(15),
        RetryDelay = TimeSpan.FromMilliseconds(30),
        RequestInterval = TimeSpan.FromMilliseconds(1),
        FinalPassTimeout = TimeSpan.FromSeconds(20),
        TypicalMinSample = 1,
    };
}
