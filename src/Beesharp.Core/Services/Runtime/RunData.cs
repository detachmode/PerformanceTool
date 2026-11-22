using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PerformanceTool.Services.Runtime;

// Runtime data (brain 0052): key-value data that the preflight, scenarios and the shutdown program read and write during a run.
// Two scopes: one run bag shared by the whole run, and one entry bag per scenario entry (its startup, bees and teardown).
// A run keeps only the final value of each key (no history), plus who wrote it last and when (run time).
// The snapshot types below are also the wire format of the REST API (and what the client shows), so they stay plain records.

/// <summary>Who wrote a runtime value last.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataWriterKind { Preflight, Startup, Bee, Teardown, Shutdown }

/// <summary>The writer of a value: the kind, and for entry-level writers the scenario entry (0-based) and for a bee its sequence (0-based).</summary>
public readonly record struct DataWriter(DataWriterKind Kind, int Entry = -1, int Bee = -1);

/// <summary>One key of a bag with its final value (so far), the last writer and the run time of that write.</summary>
public sealed record RuntimeValue(string Key, JsonNode? Value, DataWriter Writer, double AtSeconds);

/// <summary>The values of one bag. <see cref="Entry"/> is -1 for the run bag, else the scenario entry index.</summary>
public sealed record RuntimeBag(int Entry, IReadOnlyList<RuntimeValue> Values)
{
    [JsonIgnore] public bool IsRunBag => Entry < 0;
}

/// <summary>Immutable copy of all bags at one moment: what a run snapshot carries.</summary>
public sealed record RuntimeDataSnapshot(RuntimeBag Run, IReadOnlyList<RuntimeBag> Entries)
{
    public static RuntimeDataSnapshot Empty(int entries) =>
        new(new RuntimeBag(-1, []), Enumerable.Range(0, entries).Select(i => new RuntimeBag(i, [])).ToList());

    [JsonIgnore] public int TotalValues => Run.Values.Count + Entries.Sum(e => e.Values.Count);
}

/// <summary>The storage of one bag: thread-safe, remembers the last writer and write time per key. Code works with a <see cref="BagAccess"/>.</summary>
public sealed class DataBag(Action? changed = null)
{
    private sealed record Slot(object? Value, DataWriter Writer, double At, long Order);

    private readonly ConcurrentDictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private long order;

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
    public RuntimeBag Snapshot(int entry) =>
        new(entry, slots.OrderBy(p => p.Value.Order)
            .Select(p => new RuntimeValue(p.Key, ToJson(p.Value.Value), p.Value.Writer, p.Value.At))
            .ToList());

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

    public BagAccess(DataBag bag, DataWriter writer, Func<double>? now = null)
    {
        this.bag = bag;
        this.writer = writer;
        this.now = now ?? (() => 0);
    }

    public int Count => bag.Count;
    public IReadOnlyCollection<string> Keys => bag.Keys;

    public object? this[string key]
    {
        get => bag.TryGet(key, out var v) ? v : throw new KeyNotFoundException($"The data bag has no key '{key}'.");
        set => Set(key, value);
    }

    public void Set(string key, object? value) => bag.Set(key, value, writer, now());
    public bool TryGetValue(string key, out object? value) => bag.TryGet(key, out value);
    public bool ContainsKey(string key) => bag.TryGet(key, out _);

    /// <summary>The value as <typeparamref name="T"/>, or <paramref name="fallback"/> when the key is missing or holds another type.</summary>
    public T? Get<T>(string key, T? fallback = default) =>
        bag.TryGet(key, out var v) && v is T t ? t : fallback;

    public bool TryAdd(string key, object? value)
    {
        if (bag.TryGet(key, out _)) return false;
        Set(key, value);
        return true;
    }

    public object? GetOrAdd(string key, object? value)
    {
        if (bag.TryGet(key, out var v)) return v;
        Set(key, value);
        return value;
    }

    public object? GetOrAdd(string key, Func<string, object?> create)
    {
        if (bag.TryGet(key, out var v)) return v;
        var made = create(key);
        Set(key, made);
        return made;
    }

    public bool TryRemove(string key, out object? value) => bag.TryRemove(key, out value);
    public bool Remove(string key) => bag.TryRemove(key, out _);

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

    public RunData(int entries, Func<double>? clock = null)
    {
        this.clock = clock ?? (() => 0);
        Run = new DataBag(Touch);
        Entries = Enumerable.Range(0, Math.Max(0, entries)).Select(_ => new DataBag(Touch)).ToArray();
    }

    public DataBag Run { get; }
    public IReadOnlyList<DataBag> Entries { get; }
    public long Version => Interlocked.Read(ref version);

    private void Touch() => Interlocked.Increment(ref version);

    /// <summary>The run bag as seen by <paramref name="writer"/> (preflight, shutdown, or an entry's startup, bee, teardown).</summary>
    public BagAccess RunBagFor(DataWriter writer) => new(Run, writer, clock);

    /// <summary>The bag of scenario entry <paramref name="entry"/> as seen by <paramref name="writer"/>.</summary>
    public BagAccess EntryBagFor(int entry, DataWriter writer) => new(Entries[entry], writer, clock);

    public RuntimeDataSnapshot Snapshot() =>
        new(Run.Snapshot(-1), Entries.Select((b, i) => b.Snapshot(i)).ToList());
}
