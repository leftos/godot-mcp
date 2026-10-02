using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>watch's argument checks, which refuse before anything reaches a game; no Godot runs here.</summary>
public sealed class WatchValidationTests
{
    private static readonly WatchTracks OneTrack = new(Properties: [new WatchPropertyTrack("Player", "position")]);

    [Fact]
    public void ThirtyTwoTracksAreAcceptedAndAThirtyThirdIsRefused()
    {
        JsonObject parameters = RuntimeTools.BuildWatchParameters("start", Tracks(20, 12), null, null);

        Assert.Equal(20, parameters["properties"]!.AsArray().Count);
        Assert.Equal(12, parameters["expressions"]!.AsArray().Count);
        Assert.Equal(
            "tracks holds 33 property and expression tracks; at most 32 together.",
            Refusal(() => RuntimeTools.BuildWatchParameters("start", Tracks(20, 13), null, null))
        );
    }

    [Fact]
    public void AWatchWithNoTrackIsRefused()
    {
        const string Message = "tracks needs at least one track: {properties: [{node, property}]} or {expressions: [{name, expression}]}.";

        Assert.Equal(Message, Refusal(() => RuntimeTools.BuildWatchParameters("start", null, null, null)));
        Assert.Equal(Message, Refusal(() => RuntimeTools.BuildWatchParameters("run", new WatchTracks([], []), new WatchWindow(Frames: 1), null)));
    }

    [Theory]
    [InlineData(0, null, "frames must be between 1 and 7200; got 0.")]
    [InlineData(7201, null, "frames must be between 1 and 7200; got 7201.")]
    [InlineData(null, 0, "gameMs must be between 1 and 120000; got 0.")]
    [InlineData(null, 120_001, "gameMs must be between 1 and 120000; got 120001.")]
    [InlineData(5, 100, "window needs exactly one of frames or gameMs.")]
    [InlineData(null, null, "window needs exactly one of frames or gameMs.")]
    public void AWindowOutOfRangeOrWithoutExactlyOneKeyIsRefused(int? frames, int? gameMs, string expected) =>
        Assert.Equal(expected, Refusal(() => RuntimeTools.BuildWatchParameters("run", OneTrack, new WatchWindow(frames, gameMs), null)));

    [Theory]
    [InlineData(1, null, "frames", 1, 10_100)]
    [InlineData(7200, null, "frames", 7200, 600_000)]
    [InlineData(null, 1, "gameMs", 1, 10_001)]
    [InlineData(null, 120_000, "gameMs", 120_000, 130_000)]
    public void AWindowAtItsBoundsIsSentWithItsAllowance(int? frames, int? gameMs, string key, int value, long deadlineMs)
    {
        JsonObject parameters = RuntimeTools.BuildWatchParameters("run", OneTrack, new WatchWindow(frames, gameMs), null);

        Assert.Equal(value, parameters[key]!.GetValue<int>());
        Assert.Equal(deadlineMs, parameters["deadlineMs"]!.GetValue<long>());
    }

    [Fact]
    public void RunNeedsAWindowAndStartDefaultsTo600Frames()
    {
        Assert.Equal(
            "run needs a window: {frames} (1 to 7200) or {gameMs} (1 to 120000); start defaults to 600 frames.",
            Refusal(() => RuntimeTools.BuildWatchParameters("run", OneTrack, null, null))
        );

        JsonObject started = RuntimeTools.BuildWatchParameters("start", OneTrack, null, null);
        Assert.Equal(600, started["frames"]!.GetValue<int>());
        Assert.Equal(70_000, started["deadlineMs"]!.GetValue<long>());
        Assert.Equal("process", started["unit"]!.GetValue<string>());
    }

    [Fact]
    public void TwoTracksWithOneKeyAreRefused()
    {
        WatchTracks sameProperty = new(Properties: [new WatchPropertyTrack("Player", "position"), new WatchPropertyTrack("Player", "position")]);
        WatchTracks sameName = new(
            Properties: [new WatchPropertyTrack("Player", "position", Name: "where")],
            Expressions: [new WatchExpressionTrack("where", "root.get_child_count()")]
        );

        Assert.Equal(
            "Two tracks are keyed 'Player:position'; give one of them a different name.",
            Refusal(() => RuntimeTools.BuildWatchParameters("start", sameProperty, null, null))
        );
        Assert.Equal(
            "Two tracks are keyed 'where'; give one of them a different name.",
            Refusal(() => RuntimeTools.BuildWatchParameters("start", sameName, null, null))
        );
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    public void AMinDeltaThatIsNotAboveZeroIsRefusedNamingItsTrack(double minDelta)
    {
        WatchTracks property = new(Properties: [new WatchPropertyTrack("Player", "position", MinDelta: minDelta)]);
        WatchTracks expression = new(Expressions: [new WatchExpressionTrack("count", "root.get_child_count()", MinDelta: minDelta)]);
        string shown = minDelta.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(
            $"minDelta must be greater than 0; got {shown} on track 'Player:position'.",
            Refusal(() => RuntimeTools.BuildWatchParameters("start", property, null, null))
        );
        Assert.Equal(
            $"minDelta must be greater than 0; got {shown} on track 'count'.",
            Refusal(() => RuntimeTools.BuildWatchParameters("start", expression, null, null))
        );
    }

    [Fact]
    public void TracksAreSentAsGivenWithTheirOptionalFieldsOnlyWhenSet()
    {
        WatchTracks tracks = new(
            Properties: [new WatchPropertyTrack("Player", "position:y", MinDelta: 1)],
            Expressions: [new WatchExpressionTrack("screens", "node.get_children()", Node: "Main/Screens")]
        );

        JsonObject parameters = RuntimeTools.BuildWatchParameters("run", tracks, new WatchWindow(GameMs: 500), new WatchOptions(Unit: "physics"));

        Assert.Equal("""[{"node":"Player","property":"position:y","minDelta":1}]""", parameters["properties"]!.ToJsonString());
        Assert.Equal("""[{"expression":"node.get_children()","node":"Main/Screens","name":"screens"}]""", parameters["expressions"]!.ToJsonString());
        Assert.Equal("physics", parameters["unit"]!.GetValue<string>());
    }

    [Fact]
    public void AnUnknownActionAStopWithArgumentsAndAnUnknownUnitAreRefused()
    {
        Assert.Equal("action must be start, stop or run.", Refusal(() => RuntimeTools.BuildWatchParameters("Start", OneTrack, null, null)));
        Assert.Equal(
            "stop takes no tracks, window or options: it ends the watch that runs and returns its timeline.",
            Refusal(() => RuntimeTools.BuildWatchParameters("stop", OneTrack, null, null))
        );
        Assert.Equal("""{"action":"stop"}""", RuntimeTools.BuildWatchParameters("stop", null, null, null).ToJsonString());
        Assert.Equal(
            "unit must be process or physics.",
            Refusal(() => RuntimeTools.BuildWatchParameters("start", OneTrack, null, new WatchOptions(Unit: "idle")))
        );
    }

    [Fact]
    public void AnEmptyNodePropertyNameOrExpressionIsRefused()
    {
        Assert.StartsWith(
            "node is empty.",
            Refusal(() => RuntimeTools.BuildWatchParameters("start", new WatchTracks([new WatchPropertyTrack("", "position")]), null, null))
        );
        Assert.StartsWith(
            "property is empty.",
            Refusal(() => RuntimeTools.BuildWatchParameters("start", new WatchTracks([new WatchPropertyTrack("Player", " ")]), null, null))
        );
        Assert.StartsWith(
            "name is empty.",
            Refusal(() => RuntimeTools.BuildWatchParameters("start", new WatchTracks(Expressions: [new WatchExpressionTrack("", "1")]), null, null))
        );
        Assert.StartsWith(
            "expression is empty.",
            Refusal(() => RuntimeTools.BuildWatchParameters("start", new WatchTracks(Expressions: [new WatchExpressionTrack("one", "")]), null, null))
        );
    }

    private static WatchTracks Tracks(int properties, int expressions) =>
        new(
            [.. Enumerable.Range(0, properties).Select(index => new WatchPropertyTrack($"Node{index}", "position"))],
            [.. Enumerable.Range(0, expressions).Select(index => new WatchExpressionTrack($"value{index}", "root.get_child_count()"))]
        );

    private static string Refusal(Func<JsonObject> build) => Assert.Throws<McpException>(build).Message;
}
