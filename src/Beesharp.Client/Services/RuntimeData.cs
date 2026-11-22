using System.Text.Json.Nodes;

namespace PerformanceTool.Client.Services;

// Runtime data (brain 0052): key-value data that the preflight, scenarios and (later) the shutdown program write during a run.
// Two scopes: one run bag shared by the whole run, and one entry bag per scenario entry (its startup, bees and teardown).
// A run keeps only the final value of each key (no history). Static values (the selected environment) are not part of this.

/// <summary>Who wrote a runtime value last.</summary>
public enum DataWriterKind { Preflight, Startup, Bee, Teardown, Shutdown }

/// <summary>The writer of a value: the kind, and for entry-level writers the scenario entry (and for a bee its sequence).</summary>
public readonly record struct DataWriter(DataWriterKind Kind, int Entry = -1, int Bee = -1);

/// <summary>One key of a bag with its final value (so far), the last writer and the run time of that write.</summary>
public sealed record RuntimeValue(string Key, JsonNode? Value, DataWriter Writer, double AtSeconds);

/// <summary>The values of one bag. <see cref="Entry"/> is -1 for the run bag, else the scenario entry index.</summary>
public sealed record RuntimeBag(int Entry, IReadOnlyList<RuntimeValue> Values)
{
    public bool IsRunBag => Entry < 0;
}

/// <summary>Immutable copy of all bags at one moment: what a <see cref="RunSnapshot"/> carries.</summary>
public sealed record RuntimeDataSnapshot(RuntimeBag Run, IReadOnlyList<RuntimeBag> Entries)
{
    public static RuntimeDataSnapshot Empty(int entries) =>
        new(new RuntimeBag(-1, []), Enumerable.Range(0, entries).Select(i => new RuntimeBag(i, [])).ToList());

    public int TotalValues => Run.Values.Count + Entries.Sum(e => e.Values.Count);
}

/// <summary>The mutable bags of a run. Single threaded (WebAssembly); <see cref="Snapshot"/> copies for the pages.</summary>
public sealed class RuntimeData
{
    private readonly Dictionary<string, RuntimeValue> runBag = [];
    private readonly Dictionary<string, RuntimeValue>[] entryBags;

    public RuntimeData(int entries) =>
        entryBags = Enumerable.Range(0, entries).Select(_ => new Dictionary<string, RuntimeValue>()).ToArray();

    /// <summary>Writes a value into the run bag (<paramref name="entry"/> below 0) or into the bag of a scenario entry.</summary>
    public void Set(int entry, string key, JsonNode? value, DataWriter writer, double atSeconds)
    {
        var bag = entry < 0 ? runBag : entryBags[entry];
        bag[key] = new RuntimeValue(key, value, writer, atSeconds);
    }

    public JsonNode? Get(int entry, string key) =>
        (entry < 0 ? runBag : entryBags[entry]).GetValueOrDefault(key)?.Value;

    public RuntimeDataSnapshot Snapshot() =>
        new(new RuntimeBag(-1, runBag.Values.ToList()),
            entryBags.Select((b, i) => new RuntimeBag(i, b.Values.ToList())).ToList());
}

/// <summary>
/// The fake program behind the demo (what a real preflight and scenarios would write): the preflight fills the run bag, entries write their
/// own bag, and a bee of the first entry refreshes the token about every 30 seconds. Own random source so the seeded run stays as it was.
/// </summary>
public sealed class FakeRuntimeScript
{
    public const double TokenRefreshSeconds = 30;

    private readonly Random rng = new(11);
    private readonly RuntimeData data;
    private double nextRefresh = TokenRefreshSeconds;
    private int refreshes;

    public FakeRuntimeScript(RuntimeData data) => this.data = data;

    /// <summary>At the start of the run: the preflight writes the run bag, every entry's startup writes its bag.</summary>
    public void Begin(int entries)
    {
        var pre = new DataWriter(DataWriterKind.Preflight);
        data.Set(-1, "authToken", JsonValue.Create(NewToken()), pre, 0);
        data.Set(-1, "refreshCount", JsonValue.Create(0), pre, 0);
        data.Set(-1, "tenantId", JsonValue.Create("acme-load-7f3"), pre, 0);
        data.Set(-1, "seedUserIds", new JsonArray(Enumerable.Range(0, 6).Select(_ => (JsonNode)JsonValue.Create(rng.Next(10_000, 99_999))).ToArray()), pre, 0);
        data.Set(-1, "seedData", new JsonObject { ["products"] = 1200, ["categories"] = 48, ["region"] = "eu-west" }, pre, 0);
        for (var i = 0; i < entries; i++)
        {
            var startup = new DataWriter(DataWriterKind.Startup, i);
            data.Set(i, "pool", new JsonObject { ["size"] = 20, ["warm"] = true }, startup, 0);
            data.Set(i, "completed", JsonValue.Create(0), startup, 0);
        }
    }

    /// <summary>A bee finished: it writes to its entry's bag, and (first entry only) refreshes the token when it is due.</summary>
    public void BeeFinished(int entry, int sequence, double at, bool failed, int completedSoFar)
    {
        var w = new DataWriter(DataWriterKind.Bee, entry, sequence);
        data.Set(entry, "completed", JsonValue.Create(completedSoFar), w, at);
        if (!failed)
            switch (entry)
            {
                case 0: AddToList(entry, "cartIds", $"c-{rng.Next(100_000, 999_999)}", w, at); break;
                case 1: data.Set(entry, "lastOrderId", JsonValue.Create($"ORD-{rng.Next(10_000, 99_999)}"), w, at); break;
                case 2: data.Set(entry, "lastCategoryId", JsonValue.Create(rng.Next(1, 48)), w, at); break;
                case 3: data.Set(entry, "lastAccount", new JsonObject { ["id"] = rng.Next(1000, 9999), ["email"] = $"user{rng.Next(100, 999)}@example.test" }, w, at); break;
                default: data.Set(entry, "lastReturnId", JsonValue.Create($"RMA-{rng.Next(1000, 9999)}"), w, at); break;
            }
        if (entry == 0 && at >= nextRefresh)
        {
            nextRefresh = at + TokenRefreshSeconds;
            refreshes++;
            data.Set(-1, "authToken", JsonValue.Create(NewToken()), w, at);
            data.Set(-1, "refreshCount", JsonValue.Create(refreshes), w, at);
        }
    }

    /// <summary>The run ended: every entry's teardown marks its bag.</summary>
    public void End(int entries, double at)
    {
        for (var i = 0; i < entries; i++)
            data.Set(i, "cleanedUp", JsonValue.Create(true), new DataWriter(DataWriterKind.Teardown, i), at);
    }

    private void AddToList(int entry, string key, string item, DataWriter w, double at)
    {
        var items = data.Get(entry, key) is JsonArray old ? old.Select(n => n!.GetValue<string>()).ToList() : [];
        items.Add(item);
        if (items.Count > 5) items.RemoveAt(0);
        data.Set(entry, key, new JsonArray(items.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()), w, at);
    }

    private string NewToken()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        string Part(int n) => new(Enumerable.Range(0, n).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
        return $"eyJ{Part(34)}.{Part(70)}.{Part(43)}";
    }
}
