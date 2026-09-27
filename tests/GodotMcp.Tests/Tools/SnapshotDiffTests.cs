using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>diff_snapshots' comparison: added, removed and changed nodes and properties, the numeric tolerance and the caps.</summary>
public sealed class SnapshotDiffTests
{
    [Fact]
    public void ReportsAddedRemovedAndChanged()
    {
        JsonObject before = Nodes("""{".": {"visible": true}, "Gone": {"x": 1}, "Kept": {"text": "a", "count": 2}}""");
        JsonObject after = Nodes("""{".": {"visible": true}, "Kept": {"text": "b", "count": 2}, "New": {"x": 1}}""");

        JsonObject diff = SnapshotDiff.Compare(before, after);

        Assert.Equal(["New"], Strings(diff["added"]));
        Assert.Equal(["Gone"], Strings(diff["removed"]));
        JsonNode change = Assert.Single(diff["changed"]!.AsArray())!;
        Assert.Equal("Kept", change["node"]!.GetValue<string>());
        Assert.Equal("text", change["property"]!.GetValue<string>());
        Assert.Equal("a", change["before"]!.GetValue<string>());
        Assert.Equal("b", change["after"]!.GetValue<string>());
        Assert.Equal(1, diff["addedCount"]!.GetValue<int>());
        Assert.Equal(1, diff["removedCount"]!.GetValue<int>());
        Assert.Equal(1, diff["changedCount"]!.GetValue<int>());
    }

    [Fact]
    public void NumbersWithinTheToleranceAreEqual()
    {
        JsonObject before = Nodes("""{".": {"a": 1.0, "b": 1.0, "c": {"x": 5, "y": 2.0}}}""");
        JsonObject after = Nodes("""{".": {"a": 1.0000001, "b": 1.01, "c": {"x": 5.0, "y": 2.0000004}}}""");

        JsonObject diff = SnapshotDiff.Compare(before, after);

        JsonNode change = Assert.Single(diff["changed"]!.AsArray())!;
        Assert.Equal("b", change["property"]!.GetValue<string>());
        Assert.Equal(1.01, change["after"]!.GetValue<double>());
    }

    [Fact]
    public void ANestedDictionaryChangeIsTheWholeProperty()
    {
        JsonObject before = Nodes("""{".": {"scores": {"alice": 1, "bob": {"level": 2}}}}""");
        JsonObject after = Nodes("""{".": {"scores": {"alice": 1, "bob": {"level": 3}}}}""");

        JsonObject diff = SnapshotDiff.Compare(before, after);

        JsonNode change = Assert.Single(diff["changed"]!.AsArray())!;
        Assert.Equal("scores", change["property"]!.GetValue<string>());
        Assert.Equal(2, change["before"]!["bob"]!["level"]!.GetValue<int>());
        Assert.Equal(3, change["after"]!["bob"]!["level"]!.GetValue<int>());
    }

    [Fact]
    public void ADictionaryWithAnExtraKeyOrAnArrayOfAnotherLengthChanges()
    {
        JsonObject before = Nodes("""{".": {"d": {"a": 1}, "list": [1, 2]}}""");
        JsonObject after = Nodes("""{".": {"d": {"a": 1, "b": 2}, "list": [1, 2, 3]}}""");

        JsonObject diff = SnapshotDiff.Compare(before, after);

        Assert.Equal(["d", "list"], diff["changed"]!.AsArray().Select(change => change!["property"]!.GetValue<string>()));
    }

    [Fact]
    public void AGroupsChangeIsThePropertyGroups()
    {
        JsonObject before = Nodes("""{"Enemy": {"groups": ["enemies"]}}""");
        JsonObject after = Nodes("""{"Enemy": {"groups": ["enemies", "stunned"]}}""");

        JsonObject diff = SnapshotDiff.Compare(before, after);

        JsonNode change = Assert.Single(diff["changed"]!.AsArray())!;
        Assert.Equal("Enemy", change["node"]!.GetValue<string>());
        Assert.Equal("groups", change["property"]!.GetValue<string>());
        Assert.Equal(["enemies", "stunned"], Strings(change["after"]));
    }

    [Fact]
    public void APropertyOnOneSideLeavesTheOtherSideOut()
    {
        JsonObject before = Nodes("""{"N": {"old": null}}""");
        JsonObject after = Nodes("""{"N": {"new": 1}}""");

        JsonArray changed = SnapshotDiff.Compare(before, after)["changed"]!.AsArray();

        Assert.Equal(2, changed.Count);
        Assert.True(changed[0]!.AsObject().ContainsKey("before"));
        Assert.False(changed[0]!.AsObject().ContainsKey("after"));
        Assert.False(changed[1]!.AsObject().ContainsKey("before"));
        Assert.Equal(1, changed[1]!["after"]!.GetValue<int>());
    }

    [Fact]
    public void ListsAreCappedAtTwoHundredWithFullCounts()
    {
        JsonObject before = [];
        JsonObject after = [];
        for (int index = 0; index < 250; index++)
        {
            before[$"Gone{index}"] = new JsonObject();
            after[$"New{index}"] = new JsonObject();
            before[$"Kept{index}"] = new JsonObject { ["x"] = index };
            after[$"Kept{index}"] = new JsonObject { ["x"] = index + 1 };
        }

        JsonObject diff = SnapshotDiff.Compare(before, after);

        Assert.Equal(SnapshotDiff.MaxEntries, diff["added"]!.AsArray().Count);
        Assert.Equal(SnapshotDiff.MaxEntries, diff["removed"]!.AsArray().Count);
        Assert.Equal(SnapshotDiff.MaxEntries, diff["changed"]!.AsArray().Count);
        Assert.Equal(250, diff["addedCount"]!.GetValue<int>());
        Assert.Equal(250, diff["removedCount"]!.GetValue<int>());
        Assert.Equal(250, diff["changedCount"]!.GetValue<int>());
    }

    [Fact]
    public void IdenticalSnapshotsHaveNoDifferences()
    {
        JsonObject nodes = Nodes("""{".": {"s": "x", "n": null, "b": false, "v": {"x": 1.5, "y": 2}}}""");

        JsonObject diff = SnapshotDiff.Compare(nodes, nodes.DeepClone().AsObject());

        Assert.Empty(diff["changed"]!.AsArray());
        Assert.Empty(diff["added"]!.AsArray());
        Assert.Empty(diff["removed"]!.AsArray());
    }

    private static JsonObject Nodes(string json) => JsonNode.Parse(json)!.AsObject();

    private static IEnumerable<string> Strings(JsonNode? array) => array!.AsArray().Select(item => item!.GetValue<string>());
}
