using Beesharp.Client.Services;
using Beesharp.Services;

namespace Beesharp.Client.Components.Assistant;

/// <summary>
/// A hand-picked set of bees (the assistant's timeline, brain 0049) shaped like one scenario entry of a finished run, so the popup chart
/// (<c>ScenarioDetailDialog</c>, <c>BeeBars</c>, <c>DockZoom</c>, the side list) can draw it without knowing the difference. Bees of several
/// scenario entries share the single entry 0 here; their real entry is kept in <see cref="Entries"/>.
/// </summary>
public sealed class BeeSet
{
    public const string EntryId = "assistant-selection";

    public required string RunId { get; init; }
    public required DateTimeOffset RunStartedUtc { get; init; }
    public required RunSnapshot Snapshot { get; init; }
    /// <summary>The real entry index of each bee (by sequence).</summary>
    public required IReadOnlyDictionary<int, int> Entries { get; init; }

    /// <summary>Builds the set. Bees get placeholder calls until their detail is fetched (see <see cref="RemoteBeeDetail"/>).</summary>
    public static BeeSet Build(string title, string runId, DateTimeOffset runStartedUtc, IReadOnlyList<AssistantBeeDto> bees)
    {
        var store = new RunStore(1, new RunStoreOptions { MaxDetailedBeesPerScenario = int.MaxValue });
        var entries = new Dictionary<int, int>();
        int failed = 0, slow = 0, slowCalls = 0;
        double end = 0;
        foreach (var b in bees.OrderBy(b => b.StartedAt).ThenBy(b => b.Sequence))
        {
            var errorId = store.Errors.Intern(b.Error);
            var record = new BeeRecord(b.Sequence, 0, b.StartedAt, b.DurationMs, b.SlowCalls, errorId, store.Errors);
            var placeholder = new FakeCall(0, "Loading…", 0, b.DurationMs, b.StatusCode, errorId, Skipped: false, ThinkBeforeMs: 0, Slow: b.SlowCalls > 0, TypicalMs: null, Errors: store.Errors);
            store.Complete(record, new BeeDetail(b.Sequence, 0, [placeholder], () => []));
            entries[b.Sequence] = b.Entry;
            if (b.Error is not null) failed++;
            else if (b.SlowCalls > 0) slow++;
            slowCalls += b.SlowCalls;
            end = Math.Max(end, b.StartedAt + b.DurationMs / 1000);
        }

        var plan = new LoadPlan { DurationSeconds = Math.Max(1, (int)Math.Ceiling(end)), Scenarios = [new ScenarioRunConfig { ScenarioId = EntryId, Label = title, RatePerMinute = 1 }] };
        var view = new ScenarioView(bees.Count, failed, slow, slowCalls, 0, bees.Count);
        var snapshot = new RunSnapshot(FakeRunState.Finished, end, plan, [title], [view], store);
        return new BeeSet { RunId = runId, RunStartedUtc = runStartedUtc, Snapshot = snapshot, Entries = entries };
    }

    /// <summary>Fills in the calls and logs of a bee from the server's detail.</summary>
    public void ApplyDetail(ServerBeeDetailDto detail) =>
        Snapshot.Store.ReplaceDetail(RemoteBeeDetail.Build(detail, Snapshot.Store, RunStartedUtc, 0));
}
