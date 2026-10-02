using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>How the server shapes a watch's replies: track keys, value previews and the 40000-character cut.</summary>
public sealed class WatchTimelineTests
{
    [Fact]
    public void AValueOver200CharactersComesBackAsAPreviewAndAShorterOneAsItIs()
    {
        string longText = new('a', 300);
        string shortText = new('b', 150);
        JsonObject reply = Timeline(Track("Label", "text", [Point(0, longText), Point(1, shortText)], first: longText, last: shortText));

        JsonObject track = WatchTimeline.Shape(reply, WatchTimeline.MaxResultLength)["tracks"]![0]!.AsObject();

        JsonObject preview = track["points"]![0]![2]!.AsObject();
        Assert.Equal(200, preview["valuePreview"]!.GetValue<string>().Length);
        Assert.Equal(302, preview["valueLength"]!.GetValue<int>());
        Assert.Equal(shortText, track["points"]![1]![2]!.GetValue<string>());
        Assert.Equal(302, track["first"]!["valueLength"]!.GetValue<int>());
        Assert.Equal(shortText, track["last"]!.GetValue<string>());
    }

    [Fact]
    public void ATimelineOver40000CharactersLosesTheMiddleOfItsLongestTrackFirstAndCountsTheCut()
    {
        JsonArray longPoints = [.. Enumerable.Range(0, 250).Select(frame => Point(frame, new string('x', 190)))];
        JsonArray shortPoints = [.. Enumerable.Range(0, 10).Select(frame => Point(frame, frame))];
        JsonObject reply = Timeline(Track("Busy", "text", longPoints), Track("Quiet", "z_index", shortPoints));
        Assert.True(reply.ToJsonString().Length > WatchTimeline.MaxResultLength);

        JsonObject shaped = WatchTimeline.Shape(reply, WatchTimeline.MaxResultLength);

        Assert.True(shaped.ToJsonString().Length <= WatchTimeline.MaxResultLength, $"{shaped.ToJsonString().Length} characters");
        JsonObject busy = shaped["tracks"]![0]!.AsObject();
        JsonObject quiet = shaped["tracks"]![1]!.AsObject();
        int[] frames = [.. busy["points"]!.AsArray().Select(point => point![0]!.GetValue<int>())];
        Assert.Equal(250 - frames.Length, busy["cut"]!.GetValue<int>());
        Assert.Equal(0, frames[0]);
        Assert.Equal(249, frames[^1]);
        Assert.Single(frames.Zip(frames.Skip(1)), pair => pair.Second - pair.First > 1);
        Assert.False(quiet.ContainsKey("cut"), quiet.ToJsonString());
        Assert.Equal(10, quiet["points"]!.AsArray().Count);
    }

    [Fact]
    public void ATimelineWithinTheBudgetIsNotCut()
    {
        JsonObject reply = Timeline(Track("Player", "position", [Point(0, 1), Point(1, 2), Point(2, 3)]));

        JsonObject track = WatchTimeline.Shape(reply, WatchTimeline.MaxResultLength)["tracks"]![0]!.AsObject();

        Assert.False(track.ContainsKey("cut"));
        Assert.Equal(3, track["points"]!.AsArray().Count);
    }

    [Fact]
    public void ATimelineStillOverItsBudgetKeepsEachTracksFirstAndLastPoint()
    {
        JsonObject reply = Timeline(Track("Player", "position", [.. Enumerable.Range(0, 10).Select(frame => Point(frame, frame))]));

        JsonObject track = WatchTimeline.Shape(reply, 50)["tracks"]![0]!.AsObject();

        Assert.Equal([0, 9], track["points"]!.AsArray().Select(point => point![0]!.GetValue<int>()));
        Assert.Equal(8, track["cut"]!.GetValue<int>());
    }

    [Fact]
    public void ANamedTrackKeepsItsNodeAndPropertyBesideItsName()
    {
        JsonObject named = Track("/root/Main/Player", "position", [Point(0, 1)]);
        named["name"] = "where";
        JsonObject reply = Timeline(named, Track("/root/Main/Player", "visible", [Point(0, true)]));

        JsonArray tracks = WatchTimeline.Shape(reply, WatchTimeline.MaxResultLength)["tracks"]!.AsArray();

        Assert.Equal("where", tracks[0]!["name"]!.GetValue<string>());
        Assert.Equal("/root/Main/Player", tracks[0]!["node"]!.GetValue<string>());
        Assert.Equal("position", tracks[0]!["property"]!.GetValue<string>());
        Assert.False(tracks[1]!.AsObject().ContainsKey("name"));
        Assert.Equal("visible", tracks[1]!["property"]!.GetValue<string>());
    }

    private static JsonObject Timeline(params JsonObject[] tracks) =>
        new()
        {
            ["startFrame"] = 100,
            ["frames"] = 250,
            ["gameMs"] = 4166,
            ["wallMs"] = 4170,
            ["tracks"] = new JsonArray([.. tracks]),
        };

    private static JsonObject Track(string node, string property, JsonArray points, JsonNode? first = null, JsonNode? last = null) =>
        new()
        {
            ["node"] = node,
            ["property"] = property,
            ["first"] = first ?? points[0]![2]!.DeepClone(),
            ["last"] = last ?? points[^1]![2]!.DeepClone(),
            ["changes"] = points.Count - 1,
            ["points"] = points,
        };

    private static JsonArray Point(int frame, JsonNode? value) => [frame, frame * 16, value];
}
