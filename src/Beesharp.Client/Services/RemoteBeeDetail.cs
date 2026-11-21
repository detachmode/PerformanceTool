using PerformanceTool.Services;

namespace PerformanceTool.Client.Services;

/// <summary>One bee's calls and log lines as the server stores them (<c>GET /api/runs/{id}/bees/{sequence}</c>).</summary>
public sealed record ServerBeeDetailDto(
    string RunId,
    int Sequence,
    int Entry,
    double StartedAt,
    double DurationMs,
    int? StatusCode,
    long Bytes,
    string? Error,
    List<RecordedCall> Calls,
    List<RecordedLog> Logs);

/// <summary>Turns the server's bee detail into the <see cref="BeeDetail"/> the popup and the bee panel read. Shared by the live run and the assistant's bee sets.</summary>
public static class RemoteBeeDetail
{
    /// <param name="entry">The scenario entry index the bee gets in <paramref name="store"/> (a bee set has only entry 0).</param>
    public static BeeDetail Build(ServerBeeDetailDto detail, RunStore store, DateTimeOffset runStartedUtc, int entry)
    {
        var calls = new List<FakeCall>(detail.Calls.Count);
        for (var i = 0; i < detail.Calls.Count; i++)
        {
            var c = detail.Calls[i];
            var slow = c.Error is null && c.DurationMs >= 1500;
            calls.Add(new FakeCall(i, c.Name, c.OffsetMs, c.DurationMs, c.StatusCode,
                store.Errors.Intern(c.Error), Skipped: false, ThinkBeforeMs: 0, Slow: slow, TypicalMs: null, Errors: store.Errors));
        }
        if (calls.Count == 0)
            calls.Add(new FakeCall(0, "Run", 0, detail.DurationMs, detail.StatusCode,
                store.Errors.Intern(detail.Error), Skipped: false, ThinkBeforeMs: 0, Slow: detail.Error is null && detail.DurationMs >= 1500, TypicalMs: null, Errors: store.Errors));

        var sequence = detail.Sequence;
        var scope = new Dictionary<string, string> { ["Bee"] = $"B{sequence + 1}" };
        var logs = detail.Logs;
        return new BeeDetail(
            sequence,
            entry,
            calls,
            () => logs.Select(l => new FakeLogEntry(
                l.AtSeconds,
                runStartedUtc.AddSeconds(l.AtSeconds),
                l.Level,
                l.Message,
                store.Errors.Intern(l.Exception),
                store.Errors,
                entry,
                sequence,
                scope,
                l.CallIndex)).ToList());
    }
}
