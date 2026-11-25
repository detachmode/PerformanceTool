using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Beesharp.Services.Runtime;

// Runtime data (brain 0052): key-value data that the preflight, scenarios and the shutdown program read and write during a run.
// Two scopes: one run bag shared by the whole run, and one entry bag per scenario entry (its startup, bees and teardown).
// A run keeps only the final value of each key (no history), plus who wrote it last and when (run time).
// The snapshot types below are also the wire format of the REST API (and what the client shows), so they stay plain records.

/// <summary>Who wrote a runtime value last.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataWriterKind
{
    /// <summary>The preflight script run once before scenarios start.</summary>
    Preflight,
    /// <summary>A scenario entry's startup hook.</summary>
    Startup,
    /// <summary>An individual bee execution within a scenario entry.</summary>
    Bee,
    /// <summary>A scenario entry's teardown hook.</summary>
    Teardown,
    /// <summary>The shutdown program run at the end of a run.</summary>
    Shutdown
}

/// <summary>The writer of a value: the kind, and for entry-level writers the scenario entry (0-based) and for a bee its sequence (0-based).</summary>
/// <param name="Kind">The kind of writer that produced the value.</param>
/// <param name="Entry">The 0-based scenario entry index, or <c>-1</c> for run-level writers.</param>
/// <param name="Bee">The 0-based bee sequence number, or <c>-1</c> when the writer is not a bee.</param>
public readonly record struct DataWriter(DataWriterKind Kind, int Entry = -1, int Bee = -1);

/// <summary>One key of a bag with its final value (so far), the last writer and the run time of that write.</summary>
/// <param name="Key">The key name in the bag.</param>
/// <param name="Value">The JSON representation of the value, or <c>null</c>.</param>
/// <param name="Writer">The writer that last set this key.</param>
/// <param name="AtSeconds">The elapsed run time in seconds when the value was written.</param>
public sealed record RuntimeValue(string Key, JsonNode? Value, DataWriter Writer, double AtSeconds);

/// <summary>The values of one bag. <see cref="Entry"/> is -1 for the run bag, else the scenario entry index.</summary>
/// <param name="Entry">The 0-based scenario entry index, or <c>-1</c> for the run bag.</param>
/// <param name="Values">The snapshot of values currently in the bag, in insertion order.</param>
public sealed record RuntimeBag(int Entry, IReadOnlyList<RuntimeValue> Values)
{
    /// <summary>Whether this bag is the shared run bag (<see cref="Entry"/> &lt; 0).</summary>
    [JsonIgnore] public bool IsRunBag => Entry < 0;
}

/// <summary>Immutable copy of all bags at one moment: what a run snapshot carries.</summary>
/// <param name="Run">The shared run bag snapshot.</param>
/// <param name="Entries">Snapshots for each scenario entry bag, indexed by entry.</param>
public sealed record RuntimeDataSnapshot(RuntimeBag Run, IReadOnlyList<RuntimeBag> Entries)
{
    /// <summary>Creates an empty runtime data snapshot with the specified number of entry bags.</summary>
    /// <param name="entries">The number of scenario entry bags to include.</param>
    /// <returns>An empty <see cref="RuntimeDataSnapshot"/>.</returns>
    public static RuntimeDataSnapshot Empty(int entries) =>
        new(new RuntimeBag(-1, []), Enumerable.Range(0, entries).Select(i => new RuntimeBag(i, [])).ToList());

    /// <summary>The total number of values across the run bag and all entry bags.</summary>
    [JsonIgnore] public int TotalValues => Run.Values.Count + Entries.Sum(e => e.Values.Count);
}

/// <summary>The storage of one bag: thread-safe, remembers the last writer and write time per key. Code works with a <see cref="BagAccess"/>.</summary>
/// <param name="changed">Optional callback invoked whenever a key is added, updated, or removed.</param>
public sealed class DataBag(Action? changed = null)
{
    private sealed record Slot(object? Value, DataWriter Writer, double At, long Order);

    private readonly ConcurrentDictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private long order;

    /// <summary>The number of keys currently stored in this bag.</summary>
    public int Count => slots.Count;

    internal void Set(string key, object? value, DataWriter writer, double at)
    {
        slots.AddOrUpdate(key,
            _ => new Slot(value, writer, at, Interlocked.Increment(ref order)),
            (_, old) => new Slot(value, writer, at, old.Order));
        changed?.Invoke();
    }

    internal bool TryGet(string key, out object? value)
    {
        if (slots.TryGetValue(key, out var s)) { value = s.Value; return true; }
        value = null;
        return false;
    }

    internal bool TryRemove(string key, out object? value)
    {
        var ok = slots.TryRemove(key, out var s);
        value = s?.Value;
        if (ok) changed?.Invoke();
        return ok;
    }

    internal IEnumerable<KeyValuePair<string, object?>> Items() =>
        slots.OrderBy(p => p.Value.Order).Select(p => new KeyValuePair<string, object?>(p.Key, p.Value.Value));

    internal IReadOnlyCollection<string> Keys => slots.Keys.ToList();

    /// <summary>Copies the bag for the UI. Values that cannot be turned into JSON show as their <c>ToString()</c>.</summary>
    /// <param name="entry">The 0-based scenario entry index, or <c>-1</c> for the run bag.</param>
    /// <returns>An immutable <see cref="RuntimeBag"/> containing the current values in insertion order.</returns>
    public RuntimeBag Snapshot(int entry) =>
        new(entry, slots.OrderBy(p => p.Value.Order)
            .Select(p => new RuntimeValue(p.Key, ToJson(p.Value.Value), p.Value.Writer, p.Value.At))
            .ToList());

    /// <summary>Converts a runtime bag value into a <see cref="JsonNode"/>, falling back to its string representation if serialization fails.</summary>
    /// <param name="value">The value to convert, or <c>null</c>.</param>
    /// <returns>The JSON node representation of <paramref name="value"/>, or <c>null</c> when <paramref name="value"/> is <c>null</c>.</returns>
    public static JsonNode? ToJson(object? value)
    {
        switch (value)
        {
            case null: return null;
            case JsonNode node: return node.DeepClone();
            case JsonElement el: return JsonNode.Parse(el.GetRawText());
            case string s: return JsonValue.Create(s);
        }
        try { return JsonSerializer.SerializeToNode(value, value.GetType()); }
        catch (Exception) { return JsonValue.Create(SafeToString(value)); }
    }

    private static string SafeToString(object value)
    {
        try { return value.ToString() ?? value.GetType().Name; }
        catch (Exception) { return value.GetType().Name; }
    }
}

/// <summary>
/// What scenario and program code sees of a bag (<c>context.State</c>, <c>context.RunBag</c>, <c>Data</c>): a dictionary-like surface where every write
/// is recorded as written by this context's writer (startup of entry N, bee N, ..., shutdown) at the current run time. Thread-safe.
/// </summary>
public sealed class BagAccess : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly DataBag bag;
    private readonly DataWriter writer;
    private readonly Func<double> now;

    /// <summary>Creates a view over <paramref name="bag"/> that attributes writes to <paramref name="writer"/>.</summary>
    /// <param name="bag">The underlying thread-safe data bag.</param>
    /// <param name="writer">The writer identity recorded on every write through this view.</param>
    /// <param name="now">Optional clock returning the current run time in seconds; defaults to <c>0</c>.</param>
    public BagAccess(DataBag bag, DataWriter writer, Func<double>? now = null)
    {
        this.bag = bag;
        this.writer = writer;
        this.now = now ?? (() => 0);
    }

    /// <summary>The number of keys currently stored in the bag.</summary>
    public int Count => bag.Count;

    /// <summary>A snapshot of the keys currently stored in the bag.</summary>
    public IReadOnlyCollection<string> Keys => bag.Keys;

    /// <summary>Gets or sets the value associated with the specified key.</summary>
    /// <param name="key">The key to look up or assign.</param>
    /// <value>The value stored for <paramref name="key"/>.</value>
    /// <exception cref="KeyNotFoundException">Thrown on get when <paramref name="key"/> is not present in the bag.</exception>
    public object? this[string key]
    {
        get => bag.TryGet(key, out var v) ? v : throw new KeyNotFoundException($"The data bag has no key '{key}'.");
        set => Set(key, value);
    }

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>, recording the current writer and run time.</summary>
    /// <param name="key">The key to add or overwrite.</param>
    /// <param name="value">The value to store.</param>
    public void Set(string key, object? value) => bag.Set(key, value, writer, now());

    /// <summary>Attempts to retrieve the value associated with <paramref name="key"/>.</summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">When this method returns, contains the value if found, or <c>null</c> otherwise.</param>
    /// <returns><c>true</c> if the key was found; otherwise, <c>false</c>.</returns>
    public bool TryGetValue(string key, out object? value) => bag.TryGet(key, out value);

    /// <summary>Determines whether the bag contains the specified key.</summary>
    /// <param name="key">The key to check.</param>
    /// <returns><c>true</c> if the key exists in the bag; otherwise, <c>false</c>.</returns>
    public bool ContainsKey(string key) => bag.TryGet(key, out _);

    /// <summary>The value as <typeparamref name="T"/>, or <paramref name="fallback"/> when the key is missing or holds another type.</summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="key">The key to look up.</param>
    /// <param name="fallback">The value to return when the key is absent or its value is not of type <typeparamref name="T"/>.</param>
    /// <returns>The stored value cast to <typeparamref name="T"/>, or <paramref name="fallback"/>.</returns>
    public T? Get<T>(string key, T? fallback = default) =>
        bag.TryGet(key, out var v) && v is T t ? t : fallback;

    /// <summary>Adds <paramref name="key"/> with <paramref name="value"/> if the key does not already exist.</summary>
    /// <param name="key">The key to add.</param>
    /// <param name="value">The value to store.</param>
    /// <returns><c>true</c> if the key was added; <c>false</c> if it already existed.</returns>
    public bool TryAdd(string key, object? value)
    {
        if (bag.TryGet(key, out _)) return false;
        Set(key, value);
        return true;
    }

    /// <summary>Gets the existing value for <paramref name="key"/>, or adds and returns <paramref name="value"/> if missing.</summary>
    /// <param name="key">The key to look up or add.</param>
    /// <param name="value">The value to store if <paramref name="key"/> is not present.</param>
    /// <returns>The existing value, or <paramref name="value"/> if the key was added.</returns>
    public object? GetOrAdd(string key, object? value)
    {
        if (bag.TryGet(key, out var v)) return v;
        Set(key, value);
        return value;
    }

    /// <summary>Gets the existing value for <paramref name="key"/>, or invokes <paramref name="create"/> to add and return a new value.</summary>
    /// <param name="key">The key to look up or add.</param>
    /// <param name="create">Factory invoked with <paramref name="key"/> when the key is not present.</param>
    /// <returns>The existing value, or the newly created value if the key was added.</returns>
    public object? GetOrAdd(string key, Func<string, object?> create)
    {
        if (bag.TryGet(key, out var v)) return v;
        var made = create(key);
        Set(key, made);
        return made;
    }

    /// <summary>Attempts to remove the entry with <paramref name="key"/> and return its value.</summary>
    /// <param name="key">The key to remove.</param>
    /// <param name="value">When this method returns, contains the removed value if found, or <c>null</c> otherwise.</param>
    /// <returns><c>true</c> if the key was removed; otherwise, <c>false</c>.</returns>
    public bool TryRemove(string key, out object? value) => bag.TryRemove(key, out value);

    /// <summary>Removes the entry with <paramref name="key"/> from the bag.</summary>
    /// <param name="key">The key to remove.</param>
    /// <returns><c>true</c> if the key was removed; otherwise, <c>false</c>.</returns>
    public bool Remove(string key) => bag.TryRemove(key, out _);

    /// <summary>Returns an enumerator that iterates through the bag's key-value pairs in insertion order.</summary>
    /// <returns>An enumerator over the key-value pairs in the bag.</returns>
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => bag.Items().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The runtime data of one run: the run bag and one entry bag per scenario entry. The runner hands out <see cref="BagAccess"/> views
/// bound to a writer. <see cref="Version"/> grows with every change, so a poller knows when to fetch the snapshot again.
/// </summary>
public sealed class RunData
{
    private readonly Func<double> clock;
    private long version;

    /// <summary>Creates runtime data storage for a run with <paramref name="entries"/> scenario entry bags.</summary>
    /// <param name="entries">The number of scenario entries in the run.</param>
    /// <param name="clock">Optional clock returning the current run time in seconds; defaults to <c>0</c>.</param>
    public RunData(int entries, Func<double>? clock = null)
    {
        this.clock = clock ?? (() => 0);
        Run = new DataBag(Touch);
        Entries = Enumerable.Range(0, Math.Max(0, entries)).Select(_ => new DataBag(Touch)).ToArray();
    }

    /// <summary>The shared run bag accessible to preflight, all scenario entries, and shutdown.</summary>
    public DataBag Run { get; }

    /// <summary>The per-entry bags indexed by 0-based scenario entry.</summary>
    public IReadOnlyList<DataBag> Entries { get; }

    /// <summary>Monotonically increasing version incremented on every bag mutation.</summary>
    public long Version => Interlocked.Read(ref version);

    private void Touch() => Interlocked.Increment(ref version);

    /// <summary>The run bag as seen by <paramref name="writer"/> (preflight, shutdown, or an entry's startup, bee, teardown).</summary>
    /// <param name="writer">The writer identity recorded on writes through the returned view.</param>
    /// <returns>A <see cref="BagAccess"/> bound to the shared run bag and <paramref name="writer"/>.</returns>
    public BagAccess RunBagFor(DataWriter writer) => new(Run, writer, clock);

    /// <summary>The bag of scenario entry <paramref name="entry"/> as seen by <paramref name="writer"/>.</summary>
    /// <param name="entry">The 0-based scenario entry index.</param>
    /// <param name="writer">The writer identity recorded on writes through the returned view.</param>
    /// <returns>A <see cref="BagAccess"/> bound to the entry bag and <paramref name="writer"/>.</returns>
    public BagAccess EntryBagFor(int entry, DataWriter writer) => new(Entries[entry], writer, clock);

    /// <summary>Captures an immutable snapshot of the run bag and all entry bags.</summary>
    /// <returns>A <see cref="RuntimeDataSnapshot"/> of the current runtime data.</returns>
    public RuntimeDataSnapshot Snapshot() =>
        new(Run.Snapshot(-1), Entries.Select((b, i) => b.Snapshot(i)).ToList());
}
