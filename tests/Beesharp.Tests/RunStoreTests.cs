using PerformanceTool.Client.Services;

namespace PerformanceTool.Tests;

/// <summary>Retention priority and the error dictionary of the client's RunStore (brain 0028).</summary>
public class RunStoreTests
{
    private static BeeRecord Record(RunStore store, int seq, BeeOutcome outcome, int entry = 0, string? error = null) =>
        new(seq, entry, seq, 100, outcome == BeeOutcome.Slow ? 1 : 0,
            outcome == BeeOutcome.Failed ? store.Errors.Intern(error ?? "boom") : ErrorDictionary.None, store.Errors);

    private static void Add(RunStore store, int seq, BeeOutcome outcome, int entry = 0)
    {
        var record = Record(store, seq, outcome, entry);
        store.Complete(record, new BeeDetail(seq, entry, [], () => []));
    }

    [Fact]
    public void Evicts_successful_first_then_slow_then_failed_oldest_first()
    {
        var store = new RunStore(1, new RunStoreOptions { MaxDetailedBeesPerScenario = 4 });
        Add(store, 0, BeeOutcome.Failed);
        Add(store, 1, BeeOutcome.Ok);
        Add(store, 2, BeeOutcome.Slow);
        Add(store, 3, BeeOutcome.Ok);
        Assert.All(new[] { 0, 1, 2, 3 }, s => Assert.True(store.HasDetail(s)));

        Add(store, 4, BeeOutcome.Ok); // over the limit: the oldest successful one (1) goes
        Assert.False(store.HasDetail(1));
        Assert.True(store.HasDetail(0) && store.HasDetail(2) && store.HasDetail(3) && store.HasDetail(4));

        Add(store, 5, BeeOutcome.Failed); // next oldest successful (3)
        Assert.False(store.HasDetail(3));

        Add(store, 6, BeeOutcome.Failed); // successful 4 goes
        Assert.False(store.HasDetail(4));

        Add(store, 7, BeeOutcome.Failed); // no successful left: oldest slow (2) goes, failed stay
        Assert.False(store.HasDetail(2));
        Assert.True(store.HasDetail(0) && store.HasDetail(5) && store.HasDetail(6) && store.HasDetail(7));

        Add(store, 8, BeeOutcome.Failed); // only failed left: oldest failed (0) goes
        Assert.False(store.HasDetail(0));
        Assert.True(store.HasDetail(5) && store.HasDetail(8));
        Assert.True(store.DetailTrimmed);
    }

    [Fact]
    public void Bee_records_and_outcomes_are_kept_for_the_whole_run()
    {
        var store = new RunStore(1, new RunStoreOptions { MaxDetailedBeesPerScenario = 2 });
        for (var i = 0; i < 10; i++) Add(store, i, i % 3 == 0 ? BeeOutcome.Failed : BeeOutcome.Ok);
        Assert.Equal(10, store.Bees(0).Count);
        Assert.Equal(BeeOutcome.Failed, store.OutcomeOf(0));
        Assert.Equal(BeeOutcome.Ok, store.OutcomeOf(1));
        Assert.Equal("boom", store.Bees(0)[0].Error);
    }

    [Fact]
    public void Limit_is_per_scenario_and_settable_live()
    {
        var options = new RunStoreOptions { MaxDetailedBeesPerScenario = 2 };
        var store = new RunStore(2, options);
        Add(store, 0, BeeOutcome.Ok, entry: 0);
        Add(store, 1, BeeOutcome.Ok, entry: 0);
        Add(store, 2, BeeOutcome.Ok, entry: 1);
        Add(store, 3, BeeOutcome.Ok, entry: 1);
        Assert.True(store.HasDetail(0) && store.HasDetail(2)); // the other scenario's bees do not count

        options.MaxDetailedBeesPerScenario = 1;
        Add(store, 4, BeeOutcome.Ok, entry: 0); // evicts down to the new limit
        Assert.False(store.HasDetail(0));
        Assert.False(store.HasDetail(1));
        Assert.True(store.HasDetail(4));
    }

    [Fact]
    public void Evicted_detail_is_flagged_and_its_log_lines_leave_the_index()
    {
        var store = new RunStore(1, new RunStoreOptions { MaxDetailedBeesPerScenario = 1 });
        BeeDetail Make(int seq) => new(seq, 0, [], () => [new FakeLogEntry(seq, DateTimeOffset.UnixEpoch, "Information", $"line {seq}", null, 0, seq, new Dictionary<string, string>())]);
        var first = Make(0);
        store.Complete(Record(store, 0, BeeOutcome.Ok), first);
        Assert.Single(store.Logs());
        store.Complete(Record(store, 1, BeeOutcome.Ok), Make(1));
        Assert.True(first.Evicted);
        Assert.Null(store.Detail(0));
        Assert.Contains(store.Logs(), l => l.Sequence == 1);
    }

    [Fact]
    public void Error_dictionary_gives_the_same_id_for_the_same_text_and_resolves_back()
    {
        var d = new ErrorDictionary();
        var a = d.Intern("HTTP 500");
        var b = d.Intern("HTTP 503");
        Assert.NotEqual(a, b);
        Assert.Equal(a, d.Intern("HTTP 500"));
        Assert.Equal(2, d.Count);
        Assert.Equal("HTTP 500", d.Resolve(a));
        Assert.Equal("HTTP 503", d.Resolve(b));
        Assert.Equal(ErrorDictionary.None, d.Intern(null));
        Assert.Null(d.Resolve(ErrorDictionary.None));
        Assert.Null(d.Resolve(999));
        Assert.Equal(a, d.Intern(new string("HTTP 500".ToCharArray()))); // by value, not by reference
    }

    [Fact]
    public void Records_calls_and_log_entries_resolve_their_texts_through_the_dictionary()
    {
        var store = new RunStore(1);
        var errId = store.Errors.Intern("Payment declined");
        var record = new BeeRecord(0, 0, 0, 10, 0, errId, store.Errors);
        Assert.True(record.Failed);
        Assert.Equal("Payment declined", record.Error);
        Assert.Equal(BeeOutcome.Failed, record.Outcome);

        var call = new FakeCall(0, "POST /pay", 0, 10, 422, errId, false, 0, Errors: store.Errors);
        Assert.True(call.Failed);
        Assert.Equal("Payment declined", call.Error);

        var ex = store.Errors.Intern("System.Exception: boom\n   at X");
        var msg = store.Errors.Intern("Retrying partner call (attempt 2)");
        var scope = new Dictionary<string, string>();
        var interned = new FakeLogEntry(1, DateTimeOffset.UnixEpoch, "Warning", msg, ex, store.Errors, 0, 0, scope);
        Assert.Equal("Retrying partner call (attempt 2)", interned.Message);
        Assert.Equal("System.Exception: boom\n   at X", interned.Exception);
        var mixed = new FakeLogEntry(1, DateTimeOffset.UnixEpoch, "Error", "unique text 42", ex, store.Errors, 0, 0, scope);
        Assert.Equal("unique text 42", mixed.Message);
        Assert.Equal("System.Exception: boom\n   at X", mixed.Exception);
        var plain = new FakeLogEntry(1, DateTimeOffset.UnixEpoch, "Debug", "hello", null, 0, 0, scope);
        Assert.Equal("hello", plain.Message);
        Assert.Null(plain.Exception);
        Assert.Equal(FakeLogEntry.Rank("Warning"), interned.LevelRank);
    }
}
