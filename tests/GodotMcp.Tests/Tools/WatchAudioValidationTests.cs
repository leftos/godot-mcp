using System.Globalization;
using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>watch's checks of tracks.audio, which refuse before anything reaches a game; no Godot runs here.</summary>
public sealed class WatchAudioValidationTests
{
    internal const string NoTrack =
        "tracks needs at least one track: {properties: [{node, property}]}, {expressions: [{name, expression}]}, "
        + "{signals: [{node, signal}]}, {monitors: [\"frame_ms\"]}, {audio: {players: {}}} or {audio: {buses: [\"*\"]}}.";

    [Fact]
    public void AWatchWithOnlyAudioPlayersIsAcceptedAndSentToTheBridge()
    {
        JsonObject parameters = Build(new WatchAudioTrack(new WatchAudioPlayers()));

        Assert.Equal("""{"players":{}}""", parameters["audio"]!.ToJsonString());
        Assert.False(parameters.ContainsKey("properties") || parameters.ContainsKey("signals") || parameters.ContainsKey("monitors"));
    }

    [Fact]
    public void UnderIsSentAsGivenForTheBridgeToResolve()
    {
        JsonObject parameters = Build(new WatchAudioTrack(new WatchAudioPlayers("Main/Missing")));

        Assert.Equal("""{"players":{"under":"Main/Missing"}}""", parameters["audio"]!.ToJsonString());
    }

    [Fact]
    public void AnAudioTrackWithNeitherPlayersNorBusesIsRefused() =>
        Assert.Equal(
            "tracks.audio needs players or buses: {players: {}} follows every audio player under the root, {players: {under: "
                + "\"<node>\"}} those at or under a node, {buses: [\"Effects\"]} reads the named buses' levels and {buses: [\"*\"]} "
                + "every bus's.",
            Refusal(() => Build(new WatchAudioTrack())),
            StringComparer.Ordinal
        );

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyUnderIsRefused(string under) =>
        Assert.StartsWith("under is empty.", Refusal(() => Build(new WatchAudioTrack(new WatchAudioPlayers(under)))), StringComparison.Ordinal);

    [Fact]
    public void AWatchWithNoTrackIsRefusedNamingAudio()
    {
        Assert.Equal(NoTrack, Refusal(() => RuntimeTools.BuildWatchParameters("start", new WatchTracks(Audio: null), null, null)));
        Assert.Equal(NoTrack, Refusal(() => RuntimeTools.BuildWatchParameters("start", null, null, null)));
    }

    [Fact]
    public void AudioBesideOtherTracksIsSentBesideThem()
    {
        WatchTracks tracks = new(Monitors: ["frame_ms"], Audio: new WatchAudioTrack(new WatchAudioPlayers("Level")));

        JsonObject parameters = RuntimeTools.BuildWatchParameters("start", tracks, null, null);

        Assert.Equal("""["frame_ms"]""", parameters["monitors"]!.ToJsonString());
        Assert.Equal("""{"players":{"under":"Level"}}""", parameters["audio"]!.ToJsonString());
    }

    [Fact]
    public void BusesAloneAreAcceptedAndSentWithoutAThresholdForTheBridgeToDefault()
    {
        JsonObject parameters = Build(new WatchAudioTrack(Buses: ["Effects", "Music"]));

        Assert.Equal("""{"buses":["Effects","Music"]}""", parameters["audio"]!.ToJsonString());
    }

    [Theory]
    [InlineData(-100.0, "-100")]
    [InlineData(-40.5, "-40.5")]
    [InlineData(0.0, "0")]
    public void BusesAThresholdAndPlayersAreSentTogether(double threshold, string json)
    {
        JsonObject parameters = Build(new WatchAudioTrack(new WatchAudioPlayers(), ["*"], threshold));

        Assert.Equal($$"""{"players":{},"buses":["*"],"thresholdDb":{{json}}}""", parameters["audio"]!.ToJsonString());
    }

    [Fact]
    public void AnEmptyBusListIsRefused() =>
        Assert.Equal(
            "tracks.audio.buses is empty; name the buses to read, such as [\"Effects\"], or [\"*\"] for every bus.",
            Refusal(() => Build(new WatchAudioTrack(Buses: []))),
            StringComparer.Ordinal
        );

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyBusNameIsRefused(string bus) =>
        Assert.Equal(
            "tracks.audio.buses has an empty name; pass a bus's name as the Audio bus layout shows it, such as Master or Effects.",
            Refusal(() => Build(new WatchAudioTrack(Buses: ["Effects", bus]))),
            StringComparer.Ordinal
        );

    [Fact]
    public void ABusNamedTwiceIsRefused() =>
        Assert.Equal(
            "tracks.audio.buses names 'Effects' twice; each bus once.",
            Refusal(() => Build(new WatchAudioTrack(Buses: ["Effects", "Music", "Effects"]))),
            StringComparer.Ordinal
        );

    [Fact]
    public void MoreThan16BusesAreRefusedAnd16AreAccepted()
    {
        string[] sixteen = [.. Enumerable.Range(0, 16).Select(index => $"Bus{index}")];
        Assert.Equal(16, Build(new WatchAudioTrack(Buses: sixteen))["audio"]!["buses"]!.AsArray().Count);

        Assert.Equal(
            "tracks.audio.buses names 17 buses; at most 16, or [\"*\"] for every bus up to 16.",
            Refusal(() => Build(new WatchAudioTrack(Buses: [.. sixteen, "Bus16"]))),
            StringComparer.Ordinal
        );
    }

    [Theory]
    [InlineData("*", "Effects")]
    [InlineData("Effects", "*")]
    [InlineData("*", "*")]
    public void StarBesideAnotherNameIsRefused(string first, string second) =>
        Assert.Equal(
            "tracks.audio.buses takes \"*\" alone: [\"*\"] reads every bus, Master included, up to 16; or name the buses without it.",
            Refusal(() => Build(new WatchAudioTrack(Buses: [first, second]))),
            StringComparer.Ordinal
        );

    [Theory]
    [InlineData(-100.1)]
    [InlineData(0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void AThresholdOutsideMinus100To0IsRefused(double threshold) =>
        Assert.Equal(
            $"tracks.audio.thresholdDb must be between -100 and 0; got {threshold.ToString(CultureInfo.InvariantCulture)}.",
            Refusal(() => Build(new WatchAudioTrack(Buses: ["Effects"], ThresholdDb: threshold))),
            StringComparer.Ordinal
        );

    [Fact]
    public void AThresholdWithoutBusesIsRefused() =>
        Assert.Equal(
            "tracks.audio.thresholdDb applies to buses; add tracks.audio.buses or drop thresholdDb.",
            Refusal(() => Build(new WatchAudioTrack(new WatchAudioPlayers(), ThresholdDb: -40))),
            StringComparer.Ordinal
        );

    private static JsonObject Build(WatchAudioTrack audio) => RuntimeTools.BuildWatchParameters("start", new WatchTracks(Audio: audio), null, null);

    private static string Refusal(Func<JsonObject> build) => Assert.Throws<McpException>(build).Message;
}
