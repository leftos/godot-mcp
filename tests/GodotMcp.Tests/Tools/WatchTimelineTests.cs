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
    public void MonitorsAndTheWarningPassThroughATimelineTheCutShortens()
    {
        JsonArray longPoints = [.. Enumerable.Range(0, 250).Select(frame => Point(frame, new string('x', 190)))];
        JsonObject reply = Timeline(Track("Busy", "text", longPoints));
        JsonArray spikes = [.. Enumerable.Range(0, 20).Select(frame => new JsonArray(frame, 300.5 - frame))];
        reply["monitors"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "frame_ms",
                ["samples"] = 600,
                ["p50"] = 16.667,
                ["max"] = 300.5,
                ["maxAt"] = 0,
                ["over"] = new JsonObject
                {
                    ["budget"] = 25.0,
                    ["count"] = 20,
                    ["frames"] = 600,
                },
                ["spikes"] = spikes,
            },
            new JsonObject
            {
                ["name"] = "game/score",
                ["samples"] = 0,
                ["custom"] = true,
            }
        );
        reply["warning"] = "sampling took 2.50 ms a frame on average (2 tracks), which moves the frame_ms it measures; watch fewer tracks";
        string monitors = reply["monitors"]!.ToJsonString();

        JsonObject shaped = WatchTimeline.Shape(reply, WatchTimeline.MaxResultLength);

        Assert.True(shaped["tracks"]![0]!.AsObject().ContainsKey("cut"), "the cut shortened the track");
        Assert.Equal(monitors, shaped["monitors"]!.ToJsonString());
        Assert.StartsWith("sampling took 2.50 ms", shaped["warning"]!.GetValue<string>(), StringComparison.Ordinal);
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

    [Fact]
    public void ASignalTrackUnderItsShareLendsTheRestAndEveryTrackKeepsItsEarliestInEmissionOrder()
    {
        List<JsonArray> events = [];
        for (int frame = 0; frame < 300; frame++)
        {
            events.Add(Event(frame, 0));
            if (frame < 20)
            {
                events.Add(Event(frame, 1));
            }
        }

        JsonObject shaped = WatchTimeline.Shape(WithEvents(Timeline(), events, (300, 500), (20, 20)), WatchTimeline.MaxResultLength);

        JsonArray kept = shaped["events"]!.AsArray();
        Assert.Equal(300, kept.Count);
        Assert.Equal(280, kept.Count(item => item![2]!.GetValue<string>() == "/root/E0"));
        Assert.Equal(20, kept.Count(item => item![2]!.GetValue<string>() == "/root/E1"));
        Assert.Equal(279, kept.Where(item => item![2]!.GetValue<string>() == "/root/E0").Max(item => item![0]!.GetValue<int>()));
        // Emission order: E0 then E1 in each of the first 20 frames, then E0 alone.
        Assert.Equal(["/root/E0", "/root/E1", "/root/E0", "/root/E1"], kept.Take(4).Select(item => item![2]!.GetValue<string>()));
        Assert.All(kept, item => Assert.Equal(5, item!.AsArray().Count));
        Assert.Equal(500 + 20 - 300, shaped["eventsDropped"]!.GetValue<int>());
        Assert.False(shaped.ContainsKey("eventTracks"), "the bridge's per-track counts are not in the result");
    }

    [Fact]
    public void ThreeSignalTracksOverTheirShareKeepTheirFirstHundredEach()
    {
        List<JsonArray> events = [];
        for (int frame = 0; frame < 300; frame++)
        {
            events.AddRange([Event(frame, 0), Event(frame, 1), Event(frame, 2)]);
        }

        JsonObject shaped = WatchTimeline.Shape(WithEvents(Timeline(), events, (300, 300), (300, 300), (300, 300)), WatchTimeline.MaxResultLength);

        JsonArray kept = shaped["events"]!.AsArray();
        Assert.Equal(300, kept.Count);
        Assert.Equal(Enumerable.Range(0, 100).SelectMany(frame => new[] { frame, frame, frame }), kept.Select(item => item![0]!.GetValue<int>()));
        Assert.Equal(600, shaped["eventsDropped"]!.GetValue<int>());
    }

    [Fact]
    public void EventsWithinTheirShareDropNothingAndATimelineWithoutSignalsGainsNoEvents()
    {
        JsonObject shaped = WatchTimeline.Shape(WithEvents(Timeline(), [Event(0, 0), Event(3, 0)], (2, 2)), WatchTimeline.MaxResultLength);
        JsonObject plain = WatchTimeline.Shape(Timeline(Track("Player", "position", [Point(0, 1)])), WatchTimeline.MaxResultLength);

        Assert.Equal(2, shaped["events"]!.AsArray().Count);
        Assert.False(shaped.ContainsKey("eventsDropped"), shaped.ToJsonString());
        Assert.False(plain.ContainsKey("events") || plain.ContainsKey("eventsDropped") || plain.ContainsKey("eventCounts"), plain.ToJsonString());
    }

    [Fact]
    public void AnEventArgumentOver200CharactersComesBackAsAPreview()
    {
        JsonArray args = [new string('a', 300), 7];
        JsonObject shaped = WatchTimeline.Shape(WithEvents(Timeline(), [Event(0, 0, args)], (1, 1)), WatchTimeline.MaxResultLength);

        JsonArray shown = shaped["events"]![0]![4]!.AsArray();
        Assert.Equal(200, shown[0]!["valuePreview"]!.GetValue<string>().Length);
        Assert.Equal(302, shown[0]!["valueLength"]!.GetValue<int>());
        Assert.Equal(7, shown[1]!.GetValue<int>());
    }

    [Fact]
    public void EventsOverTheBudgetAreCutFromTheMiddleAndCounted()
    {
        List<JsonArray> events = [.. Enumerable.Range(0, 300).Select(frame => Event(frame, 0, [new string('x', 150)]))];
        JsonObject reply = WithEvents(
            Timeline(Track("Quiet", "z_index", [.. Enumerable.Range(0, 10).Select(frame => Point(frame, frame))])),
            events,
            (300, 300)
        );
        Assert.True(reply.ToJsonString().Length > WatchTimeline.MaxResultLength);

        JsonObject shaped = WatchTimeline.Shape(reply, WatchTimeline.MaxResultLength);

        Assert.True(shaped.ToJsonString().Length <= WatchTimeline.MaxResultLength, $"{shaped.ToJsonString().Length} characters");
        int[] frames = [.. shaped["events"]!.AsArray().Select(item => item![0]!.GetValue<int>())];
        Assert.Equal(300 - frames.Length, shaped["eventsCut"]!.GetValue<int>());
        Assert.Equal((0, 299), (frames[0], frames[^1]));
        Assert.Single(frames.Zip(frames.Skip(1)), pair => pair.Second - pair.First > 1);
        Assert.False(shaped.ContainsKey("eventsDropped"), "a cut is not a drop");
        Assert.False(shaped["tracks"]![0]!.AsObject().ContainsKey("cut"));
    }

    /// <summary>Adds the bridge's events, each [frame, gameMs, node, signal, args, track], and each track's kept and total
    /// counts.</summary>
    private static JsonObject WithEvents(JsonObject reply, IEnumerable<JsonArray> events, params (int Kept, int Total)[] counts)
    {
        reply["events"] = new JsonArray([.. events]);
        reply["eventTracks"] = new JsonArray([.. counts.Select(count => new JsonObject { ["kept"] = count.Kept, ["total"] = count.Total })]);
        reply["eventCounts"] = new JsonObject { ["/root/E0:fired"] = counts.Sum(count => count.Total) };
        return reply;
    }

    private static JsonArray Event(int frame, int track, JsonArray? args = null) =>
        [frame, frame * 16, $"/root/E{track}", "fired", args ?? [], track];

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
