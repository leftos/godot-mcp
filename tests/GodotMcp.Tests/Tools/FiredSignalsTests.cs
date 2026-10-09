using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// How the server shapes the bridge's raw fired list on a click or mouse_button result: runs folded into count, the first 50
/// entries kept with the rest counted in firedDropped, long arguments previewed, and the other keys passed through.
/// </summary>
public sealed class FiredSignalsTests
{
    private const string Edit = "/root/Main/Name";
    private const string Ready = "/root/Main/Ready";

    [Fact]
    public void ARunOfTheSameSignalOnTheSameNodeFoldsIntoOneEntryWithCountAndTheLastArgs()
    {
        JsonObject result = Reply(
            Raw(0, Edit, "text_changed", new JsonArray("a"), 1),
            Raw(1, Edit, "text_changed", new JsonArray("ab"), 1),
            Raw(2, Edit, "text_changed", new JsonArray("abc"), 1)
        );

        JsonObject entry = Assert.Single(Fired(FiredSignals.Shape(result)))!.AsObject();

        Assert.Equal(Edit, entry["node"]!.GetValue<string>());
        Assert.Equal("text_changed", entry["signal"]!.GetValue<string>());
        Assert.Equal("[\"abc\"]", entry["args"]!.ToJsonString());
        Assert.Equal(3, entry["count"]!.GetValue<int>());
        Assert.Equal(1, entry["listeners"]!.GetValue<int>());
    }

    [Fact]
    public void TheFoldedEntryKeepsTheRunsFirstFrame()
    {
        JsonObject result = Reply(Raw(2, Edit, "text_changed", new JsonArray("a"), 0), Raw(5, Edit, "text_changed", new JsonArray("ab"), 0));

        JsonObject entry = Assert.Single(Fired(FiredSignals.Shape(result)))!.AsObject();

        Assert.Equal(2, entry["frame"]!.GetValue<int>());
        Assert.Equal(2, entry["count"]!.GetValue<int>());
    }

    [Fact]
    public void ASingleEntryHasNoCount()
    {
        JsonObject result = Reply(Raw(1, Ready, "pressed", [], 0));

        JsonObject entry = Assert.Single(Fired(FiredSignals.Shape(result)))!.AsObject();

        Assert.False(entry.ContainsKey("count"), entry.ToJsonString());
        Assert.Equal(1, entry["frame"]!.GetValue<int>());
        Assert.Equal(0, entry["listeners"]!.GetValue<int>());
    }

    [Fact]
    public void MoreThanFiftyEntriesKeepTheFirstFiftyAndSetFiredDropped()
    {
        JsonObject result = Reply([.. Enumerable.Range(0, 60).Select(index => Raw(0, Ready, $"signal_{index}", [], 0))]);

        JsonObject shaped = FiredSignals.Shape(result);

        JsonArray fired = Fired(shaped);
        Assert.Equal(FiredSignals.MaxEntries, fired.Count);
        Assert.Equal("signal_0", fired[0]!["signal"]!.GetValue<string>());
        Assert.Equal("signal_49", fired[^1]!["signal"]!.GetValue<string>());
        Assert.Equal(10, shaped["firedDropped"]!.GetValue<int>());
    }

    [Fact]
    public void FoldingHappensBeforeTheFiftyCap()
    {
        JsonObject result = Reply([.. Enumerable.Range(0, 120).Select(index => Raw(index, Ready, index < 60 ? "toggled" : "pressed", [], 0))]);

        JsonObject shaped = FiredSignals.Shape(result);

        int[] counts = [.. Fired(shaped).Select(entry => entry!["count"]!.GetValue<int>())];
        Assert.Equal([60, 60], counts);
        Assert.False(shaped.ContainsKey("firedDropped"), shaped.ToJsonString());
    }

    [Fact]
    public void ARawEntryThatIsNotAnArrayCountsIntoFiredDropped()
    {
        JsonObject result = Reply(Raw(0, Ready, "pressed", [], 0));
        result["fired"]!.AsArray().Add("not an entry");
        result["fired"]!.AsArray().Add((JsonNode?)null);

        JsonObject shaped = FiredSignals.Shape(result);

        Assert.Single(Fired(shaped));
        Assert.Equal(2, shaped["firedDropped"]!.GetValue<int>());
    }

    [Fact]
    public void FiredOverflowAddsToFiredDroppedAndIsRemoved()
    {
        JsonObject result = Reply([.. Enumerable.Range(0, 55).Select(index => Raw(0, Ready, $"signal_{index}", [], 0))]);
        result["firedOverflow"] = 7;

        JsonObject shaped = FiredSignals.Shape(result);

        Assert.Equal(12, shaped["firedDropped"]!.GetValue<int>());
        Assert.False(shaped.ContainsKey("firedOverflow"), shaped.ToJsonString());
    }

    [Fact]
    public void NoDropsLeaveFiredDroppedOut()
    {
        JsonObject result = Reply(Raw(0, Ready, "button_down", [], 0), Raw(1, Ready, "pressed", [], 0));
        result["firedOverflow"] = 0;

        JsonObject shaped = FiredSignals.Shape(result);

        Assert.False(shaped.ContainsKey("firedDropped"), shaped.ToJsonString());
        Assert.False(shaped.ContainsKey("firedOverflow"), shaped.ToJsonString());
        Assert.Equal(2, Fired(shaped).Count);
    }

    [Fact]
    public void AnArgumentOver200CharactersIsCutToValuePreviewAndValueLength()
    {
        string longText = new('x', 300);
        string shortText = new('y', 150);
        JsonObject result = Reply(Raw(0, Edit, "text_submitted", new JsonArray(longText, shortText), 0));

        JsonArray args = Assert.Single(Fired(FiredSignals.Shape(result)))!["args"]!.AsArray();

        JsonObject preview = args[0]!.AsObject();
        Assert.Equal(FiredSignals.MaxArgLength, preview["valuePreview"]!.GetValue<string>().Length);
        Assert.Equal(302, preview["valueLength"]!.GetValue<int>());
        Assert.Equal(shortText, args[1]!.GetValue<string>());
    }

    [Fact]
    public void EntryOrderIsKeptAcrossFolding()
    {
        JsonObject result = Reply(
            Raw(0, Ready, "toggled", new JsonArray(true), 1),
            Raw(0, Ready, "toggled", new JsonArray(false), 1),
            Raw(1, Ready, "pressed", [], 0),
            Raw(1, Edit, "pressed", [], 0),
            Raw(2, Ready, "toggled", new JsonArray(true), 1)
        );

        JsonArray fired = Fired(FiredSignals.Shape(result));

        string[] order = [.. fired.Select(entry => $"{entry!["node"]} {entry["signal"]} {entry["count"]?.GetValue<int>() ?? 1}")];
        Assert.Equal([$"{Ready} toggled 2", $"{Ready} pressed 1", $"{Edit} pressed 1", $"{Ready} toggled 1"], order);
    }

    [Fact]
    public void AnEmptyArgsListLeavesArgsOut()
    {
        JsonObject result = Reply(Raw(1, Ready, "pressed", [], 0));

        JsonObject entry = Assert.Single(Fired(FiredSignals.Shape(result)))!.AsObject();

        Assert.False(entry.ContainsKey("args"), entry.ToJsonString());
    }

    [Fact]
    public void ANullListenersStaysNull()
    {
        JsonObject result = Reply(Raw(1, Ready, "Played", new JsonArray(3), null));

        JsonObject entry = Assert.Single(Fired(FiredSignals.Shape(result)))!.AsObject();

        Assert.True(entry.ContainsKey("listeners"), entry.ToJsonString());
        Assert.Null(entry["listeners"]);
    }

    [Fact]
    public void ARawListWithoutFiredLeavesTheResultAlone()
    {
        JsonObject result = new()
        {
            ["pointer"] = new JsonObject { ["x"] = 10, ["y"] = 20 },
            ["heldButtonMask"] = 0,
            ["pressedOn"] = null,
            ["releasedOn"] = null,
        };
        string before = result.ToJsonString();

        JsonObject shaped = FiredSignals.Shape(result);

        Assert.Equal(before, shaped.ToJsonString());
    }

    [Fact]
    public void ListenedOnLeftTreeAndWarningPassThrough()
    {
        JsonObject result = Reply(Raw(0, Ready, "button_down", [], 0));
        result["listenedOn"] = new JsonArray(Ready, Edit);
        result["leftTree"] = new JsonArray("/root/Main/Lobby");
        result["warning"] = $"{Ready} is disabled, so the press set off none of its own signals";

        JsonObject shaped = FiredSignals.Shape(result);

        Assert.Equal($"[\"{Ready}\",\"{Edit}\"]", shaped["listenedOn"]!.ToJsonString());
        Assert.Equal("[\"/root/Main/Lobby\"]", shaped["leftTree"]!.ToJsonString());
        Assert.Equal($"{Ready} is disabled, so the press set off none of its own signals", shaped["warning"]!.GetValue<string>());
    }

    private static JsonArray Raw(int frame, string node, string signal, JsonArray args, int? listeners) => new(frame, node, signal, args, listeners);

    private static JsonObject Reply(params JsonArray[] fired) =>
        new()
        {
            ["pointer"] = new JsonObject { ["x"] = 10, ["y"] = 20 },
            ["heldButtonMask"] = 0,
            ["fired"] = new JsonArray([.. fired]),
            ["listenedOn"] = new JsonArray(),
        };

    private static JsonArray Fired(JsonObject shaped) => shaped["fired"]!.AsArray();
}
