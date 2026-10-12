using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>watch's checks of tracks.audio, which refuse before anything reaches a game; no Godot runs here.</summary>
public sealed class WatchAudioValidationTests
{
    internal const string NoTrack =
        "tracks needs at least one track: {properties: [{node, property}]}, {expressions: [{name, expression}]}, "
        + "{signals: [{node, signal}]}, {monitors: [\"frame_ms\"]} or {audio: {players: {}}}.";

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
    public void AnAudioTrackWithoutPlayersIsRefused() =>
        Assert.Equal(
            "tracks.audio needs players: {players: {}} follows every audio player under the root, {players: {under: \"<node>\"}} "
                + "those at or under a node.",
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

    private static JsonObject Build(WatchAudioTrack audio) => RuntimeTools.BuildWatchParameters("start", new WatchTracks(Audio: audio), null, null);

    private static string Refusal(Func<JsonObject> build) => Assert.Throws<McpException>(build).Message;
}
