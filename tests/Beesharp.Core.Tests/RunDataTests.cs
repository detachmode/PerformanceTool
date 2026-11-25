using System.Text.Json;
using System.Text.Json.Nodes;
using Beesharp.Services.Runtime;

namespace Beesharp.Core.Tests;

/// <summary>
/// Stories for <see cref="RunData"/>, <see cref="DataBag"/> and <see cref="BagAccess"/> (brain 0052):
/// sharing runtime values across preflight, scenario entries and shutdown while tracking last writer,
/// write timestamp, insertion order and safe JSON snapshotting.
/// </summary>
public sealed class RunDataTests
{
    private sealed class CyclicNode
    {
        public CyclicNode? Self { get; set; }
        public override string ToString() => "cyclic-fallback";
    }

    private sealed class HostileToString
    {
        public HostileToString? Self { get; set; }
        public override string ToString() => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Preflight_entries_and_shutdown_share_run_bag_while_keeping_entry_bags_isolated()
    {
        var clockSeconds = 0.5;
        var data = new RunData(entries: 2, clock: () => clockSeconds);

        var preflightBag = data.RunBagFor(new DataWriter(DataWriterKind.Preflight));
        var entry0Startup = data.EntryBagFor(0, new DataWriter(DataWriterKind.Startup, Entry: 0));
        var entry0Bee = data.EntryBagFor(0, new DataWriter(DataWriterKind.Bee, Entry: 0, Bee: 4));
        var entry0Run = data.RunBagFor(new DataWriter(DataWriterKind.Bee, Entry: 0, Bee: 4));
        var entry1Bee = data.EntryBagFor(1, new DataWriter(DataWriterKind.Bee, Entry: 1, Bee: 7));
        var shutdownBag = data.RunBagFor(new DataWriter(DataWriterKind.Shutdown));

        // 1. Preflight seeds tenant configuration and a token into the shared run bag.
        preflightBag["tenant"] = "acme";
        preflightBag.Set("token", "v1-token");
        var versionAfterPreflight = data.Version;
        Assert.Equal(2, versionAfterPreflight);

        // 2. Entry 0 startup prepares its private entry bag; bees can read and update it without leaking into entry 1.
        clockSeconds = 1.25;
        entry0Startup["cursor"] = 100;
        entry0Bee["cursor"] = 101;
        entry1Bee["cursor"] = 999;

        Assert.Equal(101, entry0Bee.Get<int>("cursor"));
        Assert.Equal(999, entry1Bee.Get<int>("cursor"));

        // 3. Entry 0 bee refreshes the shared token in the run bag; key order stays [tenant, token] while writer & time update.
        clockSeconds = 4.5;
        entry0Run["token"] = "v2-token";
        Assert.True(data.Version > versionAfterPreflight);

        // 4. Shutdown inspects the final values and removes temporary scratch keys.
        clockSeconds = 10.0;
        Assert.Equal("v2-token", shutdownBag.Get<string>("token"));
        shutdownBag["cleanedUp"] = true;

        var snapshot = data.Snapshot();
        Assert.True(snapshot.Run.IsRunBag);
        Assert.Equal(-1, snapshot.Run.Entry);
        Assert.Equal(["tenant", "token", "cleanedUp"], snapshot.Run.Values.Select(v => v.Key).ToArray());

        var tokenRow = snapshot.Run.Values[1];
        Assert.Equal("v2-token", tokenRow.Value?.GetValue<string>());
        Assert.Equal(new DataWriter(DataWriterKind.Bee, Entry: 0, Bee: 4), tokenRow.Writer);
        Assert.Equal(4.5, tokenRow.AtSeconds);

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.False(snapshot.Entries[0].IsRunBag);
        Assert.Equal(5, snapshot.TotalValues);

        var empty = RuntimeDataSnapshot.Empty(3);
        Assert.Equal(3, empty.Entries.Count);
        Assert.Equal(0, empty.TotalValues);
    }

    [Fact]
    public void Bag_access_supports_typed_reads_conditional_adds_enumeration_and_removals()
    {
        var data = new RunData(entries: 1);
        var bag = data.RunBagFor(new DataWriter(DataWriterKind.Preflight));

        Assert.Throws<KeyNotFoundException>(() => _ = bag["missing"]);
        Assert.False(bag.ContainsKey("missing"));
        Assert.False(bag.TryGetValue("missing", out _));
        Assert.Equal("fallback", bag.Get("missing", "fallback"));

        // TryAdd only inserts when absent.
        Assert.True(bag.TryAdd("region", "eu-west"));
        Assert.False(bag.TryAdd("region", "us-east"));
        Assert.Equal("eu-west", bag["region"]);

        // Type mismatch on Get<T> returns the fallback cleanly.
        Assert.Equal(42, bag.Get("region", 42));

        // GetOrAdd supports both eager value and lazy factory.
        Assert.Equal("eu-west", bag.GetOrAdd("region", "ignored"));
        Assert.Equal(5, bag.GetOrAdd("retries", 5));
        var factoryCalls = 0;
        Assert.Equal("created:seed", bag.GetOrAdd("seed", k => { factoryCalls++; return $"created:{k}"; }));
        Assert.Equal("created:seed", bag.GetOrAdd("seed", k => { factoryCalls++; return "never"; }));
        Assert.Equal(1, factoryCalls);

        Assert.Equal(3, bag.Count);
        Assert.Equal(3, bag.Keys.Count);
        Assert.Equal(["region", "retries", "seed"], bag.Select(kv => kv.Key).ToArray());
        // Exercise non-generic IEnumerable path too.
        Assert.Equal(3, ((System.Collections.IEnumerable)bag).Cast<object>().Count());

        // Remove & TryRemove update version only when key existed.
        var vBefore = data.Version;
        Assert.False(bag.Remove("nonexistent"));
        Assert.Equal(vBefore, data.Version);

        Assert.True(bag.TryRemove("retries", out var removed));
        Assert.Equal(5, removed);
        Assert.True(data.Version > vBefore);
        Assert.True(bag.Remove("seed"));
        Assert.Equal(1, bag.Count);
    }

    [Fact]
    public void Snapshot_serialization_handles_json_nodes_elements_nulls_and_unserializable_objects()
    {
        var cyclic = new CyclicNode();
        cyclic.Self = cyclic;

        var hostile = new HostileToString();
        hostile.Self = hostile;

        using var doc = JsonDocument.Parse("""{"id":7,"tags":["a","b"]}""");
        var sourceNode = new JsonObject { ["nested"] = true };

        var bag = new DataBag();
        var access = new BagAccess(bag, new DataWriter(DataWriterKind.Bee));
        access["nil"] = null;
        access["node"] = sourceNode;
        access["element"] = doc.RootElement.Clone();
        access["text"] = "hello";
        access["record"] = new { Count = 3 };
        access["cyclic"] = cyclic;
        access["hostile"] = hostile;

        var snap = bag.Snapshot(-1);
        // Mutating the original JsonNode must not mutate the deep-cloned snapshot.
        sourceNode["nested"] = false;

        var byKey = snap.Values.ToDictionary(v => v.Key, v => v.Value);
        Assert.Null(byKey["nil"]);
        Assert.True(byKey["node"]!["nested"]!.GetValue<bool>());
        Assert.Equal(7, byKey["element"]!["id"]!.GetValue<int>());
        Assert.Equal("hello", byKey["text"]!.GetValue<string>());
        Assert.Equal(3, byKey["record"]!["Count"]!.GetValue<int>());
        Assert.Equal("cyclic-fallback", byKey["cyclic"]!.GetValue<string>());
        Assert.Equal(nameof(HostileToString), byKey["hostile"]!.GetValue<string>());
    }
}
