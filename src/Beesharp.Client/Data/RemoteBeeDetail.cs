using Beesharp.Services;
using Beesharp.Client.Runs;

namespace Beesharp.Client.Data;

/// <summary>Turns the server's bee detail into the <see cref="BeeDetail"/> the popup and the bee panel read. Shared by the live run and the assistant's bee sets.</summary>
public static class RemoteBeeDetail
{
    /// <param name="entry">The scenario entry index the bee gets in <paramref name="store"/> (a bee set has only entry 0).</param>
    public static BeeDetail Build(StoredBeeDetail detail, RunStore store, DateTimeOffset runStartedUtc, int entry)
    {
        var calls = new List<FakeCall>(detail.Calls.Count);
        for (var i = 0; i < detail.Calls.Count; i++)
        {
            var c = detail.Calls[i];
            var slow = c.Error is null && c.DurationMs >= 1500;
            calls.Add(new FakeCall(i, c.Name, c.OffsetMs, c.DurationMs, c.StatusCode,
                store.Errors.Intern(c.Error), Skipped: false, ThinkBeforeMs: 0, Slow: slow, TypicalMs: null, Errors: store.Errors, Step: c.Step, Rep: c.Rep));
        }
        if (calls.Count == 0)
            calls.Add(new FakeCall(0, "Run", 0, detail.DurationMs, detail.StatusCode,
                store.Errors.Intern(detail.Error), Skipped: false, ThinkBeforeMs: 0, Slow: false, TypicalMs: null, Errors: store.Errors, Step: Math.Max(0, detail.FailedStep), Rep: Math.Max(0, detail.FailedRep)));

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
                l.CallIndex,
                l.Step,
                l.Rep)).ToList(),
            detail.StepVisits);
    }
}
