using System.Text.Json.Nodes;
using Beesharp.Client.Services;
using Beesharp.Services.Runtime;

namespace Beesharp.Client.Tests;

/// <summary>The run bag and the entry bags keep the last value and its writer (brain 0052).</summary>
public sealed class RuntimeDataBagTests
{
    [Fact]
    public void Bags_keep_only_the_last_value_and_its_writer()
    {
        var data = new RuntimeData(2);
        data.Set(-1, "k", JsonValue.Create(1), new DataWriter(DataWriterKind.Preflight), 0);
        data.Set(-1, "k", JsonValue.Create(2), new DataWriter(DataWriterKind.Bee, 1, 41), 12.5);
        data.Set(1, "k", JsonValue.Create("entry"), new DataWriter(DataWriterKind.Startup, 1), 1);

        var snap = data.Snapshot();
        var run = Assert.Single(snap.Run.Values);
        Assert.Equal(2, run.Value!.GetValue<int>());
        Assert.Equal(new DataWriter(DataWriterKind.Bee, 1, 41), run.Writer);
        Assert.Equal(12.5, run.AtSeconds);
        Assert.Empty(snap.Entries[0].Values);
        Assert.Equal("entry", Assert.Single(snap.Entries[1].Values).Value!.GetValue<string>());
        Assert.True(snap.Run.IsRunBag);
        Assert.False(snap.Entries[1].IsRunBag);
    }
}
