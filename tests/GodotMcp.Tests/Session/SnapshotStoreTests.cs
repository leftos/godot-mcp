using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>A session's snapshot store: the 16 most recently used kept, ids that never repeat, and clearing.</summary>
public sealed class SnapshotStoreTests
{
    [Fact]
    public void IdsIncreaseFromS1()
    {
        SnapshotStore store = new();

        Assert.Equal("s1", store.Add(Capture("/root/A")));
        Assert.Equal("s2", store.Add(Capture("/root/B")));
        Assert.Equal("s3", store.Add(Capture("/root/C")));
    }

    [Fact]
    public void KeepsSixteenAndEvictsTheLeastRecentlyUsed()
    {
        SnapshotStore store = new();
        List<string> ids = [.. Enumerable.Range(0, SnapshotStore.Capacity).Select(index => store.Add(Capture($"/root/N{index}")))];

        // Using s1 makes s2 the least recently used, so the seventeenth snapshot evicts s2 and keeps s1.
        Assert.Equal("/root/N0", store.Find(ids[0])!.Node);
        string newest = store.Add(Capture("/root/Newest"));

        Assert.Equal(SnapshotStore.Capacity, store.Count);
        Assert.Null(store.Find(ids[1]));
        Assert.NotNull(store.Find(ids[0]));
        Assert.Equal("/root/Newest", store.Find(newest)!.Node);
        Assert.All(ids.Skip(2), id => Assert.NotNull(store.Find(id)));
    }

    [Fact]
    public void AnUnknownIdIsNotHeld()
    {
        SnapshotStore store = new();
        store.Add(Capture("/root/A"));

        Assert.Null(store.Find("s2"));
        Assert.Null(store.Find("nope"));
    }

    [Fact]
    public void ClearingEmptiesItAndIdsKeepCounting()
    {
        SnapshotStore store = new();
        string first = store.Add(Capture("/root/A"));
        store.Add(Capture("/root/B"));

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Null(store.Find(first));
        Assert.Equal("s3", store.Add(Capture("/root/C")));
    }

    private static Snapshot Capture(string node) => new(node, null, null, 2000, new JsonObject { ["."] = new JsonObject() });
}
