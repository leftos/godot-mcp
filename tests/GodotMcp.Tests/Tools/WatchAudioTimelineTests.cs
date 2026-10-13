using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>How the server shapes a watch's audio.voices: fair shares across players, the drop count, stream previews and
/// the 40000-character cut.</summary>
public sealed class WatchAudioTimelineTests
{
    [Fact]
    public void VoicesAreSharedFairlyAcrossPlayersEarliestFirst()
    {
        List<JsonArray> voices = [];
        for (int frame = 0; frame < 300; frame++)
        {
            voices.Add(Voice(frame, "/root/Busy"));
            if (frame < 20)
            {
                voices.Add(Voice(frame, "/root/Quiet"));
            }
        }

        JsonObject audio = Shape(Timeline(Audio(voices, total: 320)))["audio"]!.AsObject();

        JsonArray kept = audio["voices"]!.AsArray();
        Assert.Equal(300, kept.Count);
        Assert.Equal(280, kept.Count(item => Player(item) == "/root/Busy"));
        Assert.Equal(20, kept.Count(item => Player(item) == "/root/Quiet"));
        Assert.Equal(279, kept.Where(item => Player(item) == "/root/Busy").Max(item => item![0]!.GetValue<int>()));
        Assert.Equal(["/root/Busy", "/root/Quiet", "/root/Busy", "/root/Quiet"], kept.Take(4).Select(Player));
        Assert.All(kept, item => Assert.Equal(5, item!.AsArray().Count));
    }

    [Fact]
    public void VoiceCountsCountEveryEventAndVoicesDroppedCountsTheRest()
    {
        List<JsonArray> voices = [];
        for (int frame = 0; frame < 300; frame++)
        {
            voices.AddRange([Voice(frame, "/root/A"), Voice(frame, "/root/B"), Voice(frame, "/root/C")]);
        }

        JsonObject reply = Timeline(Audio(voices, total: 1000));
        string counts = reply["audio"]!["voiceCounts"]!.ToJsonString();

        JsonObject audio = Shape(reply)["audio"]!.AsObject();

        Assert.Equal(
            Enumerable.Range(0, 100).SelectMany(frame => new[] { frame, frame, frame }),
            audio["voices"]!.AsArray().Select(item => item![0]!.GetValue<int>())
        );
        Assert.Equal(700, audio["voicesDropped"]!.GetValue<int>());
        Assert.Equal(counts, audio["voiceCounts"]!.ToJsonString());
        Assert.False(audio.ContainsKey("voiceTotal"), "the bridge's total is not in the result");

        JsonObject within = Shape(Timeline(Audio([Voice(0, "/root/A"), Voice(3, "/root/A", "end")], total: 2)))["audio"]!.AsObject();
        Assert.Equal(2, within["voices"]!.AsArray().Count);
        Assert.False(within.ContainsKey("voicesDropped"), within.ToJsonString());
    }

    [Fact]
    public void MorePathsThanVoicesKeepTheEarliestPathsFirstVoice()
    {
        List<JsonArray> voices = [.. Enumerable.Range(0, 1200).Select(frame => Voice(frame, $"/root/P{frame}"))];

        JsonObject audio = Shape(Timeline(Audio(voices, total: 1200)))["audio"]!.AsObject();

        Assert.Equal(Enumerable.Range(0, 300), audio["voices"]!.AsArray().Select(item => item![0]!.GetValue<int>()));
        Assert.Equal(900, audio["voicesDropped"]!.GetValue<int>());
    }

    [Fact]
    public void TheRemainderOfTheShareGoesToTheEarliestPaths()
    {
        List<JsonArray> voices = [];
        for (int frame = 0; frame < 100; frame++)
        {
            voices.AddRange(Enumerable.Range(0, 7).Select(path => Voice(frame, $"/root/P{path}")));
        }

        JsonArray kept = Shape(Timeline(Audio(voices, total: 700)))["audio"]!["voices"]!.AsArray();

        Assert.Equal([43, 43, 43, 43, 43, 43, 42], Enumerable.Range(0, 7).Select(path => kept.Count(item => Player(item) == $"/root/P{path}")));
    }

    [Fact]
    public void VoiceCountsKeepTheHundredPathsWithTheMostStartsAndEndsTheEarliestOnTies()
    {
        JsonObject fewest = ShapeCounts(paths: 101, endsOf: path => path == 50 ? 0 : 1);
        Assert.Equal(
            Enumerable.Range(0, 101).Where(path => path != 50).Select(path => $"/root/P{path}"),
            fewest["voiceCounts"]!.AsObject().Select(pair => pair.Key)
        );
        Assert.Equal(1, fewest["voiceCountsCut"]!.GetValue<int>());

        JsonObject ties = ShapeCounts(paths: 101, endsOf: _ => 0);
        Assert.Equal(100, ties["voiceCounts"]!.AsObject().Count);
        Assert.False(ties["voiceCounts"]!.AsObject().ContainsKey("/root/P100"), "the latest path is cut");
        Assert.Equal(1, ties["voiceCountsCut"]!.GetValue<int>());

        JsonObject within = ShapeCounts(paths: 100, endsOf: _ => 0);
        Assert.Equal(100, within["voiceCounts"]!.AsObject().Count);
        Assert.False(within.ContainsKey("voiceCountsCut"), "nothing cut");
    }

    [Fact]
    public void ALongStreamValueIsPreviewed()
    {
        string longPath = "res://" + new string('a', 294);
        List<JsonArray> voices = [Voice(0, "/root/A", stream: longPath), Voice(1, "/root/B", stream: "res://hit.wav")];

        JsonArray kept = Shape(Timeline(Audio(voices, total: 2)))["audio"]!["voices"]!.AsArray();

        JsonObject preview = kept[0]![4]!["stream"]!.AsObject();
        Assert.Equal(200, preview["valuePreview"]!.GetValue<string>().Length);
        Assert.Equal(302, preview["valueLength"]!.GetValue<int>());
        Assert.Equal("res://hit.wav", kept[1]![4]!["stream"]!.GetValue<string>());
        Assert.Equal("Master", kept[0]![4]!["bus"]!.GetValue<string>());
    }

    [Fact]
    public void VoicesAreCutFromTheMiddleLongestListFirstPastTheResultBudget()
    {
        List<JsonArray> voices = [.. Enumerable.Range(0, 300).Select(frame => Voice(frame, "/root/A", stream: "res://" + new string('x', 150)))];
        JsonObject reply = Timeline(Audio(voices, total: 300));
        reply["tracks"] = new JsonArray(Track());
        Assert.True(reply.ToJsonString().Length > WatchTimeline.MaxResultLength);

        JsonObject shaped = Shape(reply);

        Assert.True(shaped.ToJsonString().Length <= WatchTimeline.MaxResultLength, $"{shaped.ToJsonString().Length} characters");
        JsonObject audio = shaped["audio"]!.AsObject();
        int[] frames = [.. audio["voices"]!.AsArray().Select(item => item![0]!.GetValue<int>())];
        Assert.Equal(300 - frames.Length, audio["voicesCut"]!.GetValue<int>());
        Assert.Equal((0, 299), (frames[0], frames[^1]));
        Assert.Single(frames.Zip(frames.Skip(1)), pair => pair.Second - pair.First > 1);
        Assert.False(audio.ContainsKey("voicesDropped"), "a cut is not a drop");
        Assert.False(shaped["tracks"]![0]!.AsObject().ContainsKey("cut"));
    }

    [Fact]
    public void FiftyHeardRangesPassThroughAndFiftyOneKeepTheFirst25AndTheLast25()
    {
        JsonObject fifty = Shape(Timeline(BusesAudio(Bus("Effects", ranges: 50))))["audio"]!["buses"]![0]!.AsObject();
        Assert.Equal(Enumerable.Range(0, 50), StartsOf(fifty));
        Assert.False(fifty.ContainsKey("heardCut"), fifty.ToJsonString());

        JsonObject fiftyOne = Shape(Timeline(BusesAudio(Bus("Effects", ranges: 51))))["audio"]!["buses"]![0]!.AsObject();
        Assert.Equal(Enumerable.Range(0, 25).Concat(Enumerable.Range(26, 25)), StartsOf(fiftyOne));
        Assert.Equal(1, fiftyOne["heardCut"]!.GetValue<int>());
        Assert.Equal(51, fiftyOne["heardFrames"]!.GetValue<int>());
    }

    [Fact]
    public void EachBusIsCutOnItsOwnAndTheTimelineStaysUnderTheBudget()
    {
        JsonObject[] buses = [.. Enumerable.Range(0, 16).Select(bus => Bus($"Bus{bus}", ranges: bus == 0 ? 10 : 3600))];

        JsonObject shaped = Shape(Timeline(BusesAudio(buses)));

        Assert.True(shaped.ToJsonString().Length <= WatchTimeline.MaxResultLength, $"{shaped.ToJsonString().Length} characters");
        JsonArray kept = shaped["audio"]!["buses"]!.AsArray();
        Assert.Equal(10, kept[0]!["heard"]!.AsArray().Count);
        Assert.False(kept[0]!.AsObject().ContainsKey("heardCut"));
        Assert.All(kept.Skip(1), bus => Assert.Equal((50, 3550), (bus!["heard"]!.AsArray().Count, bus["heardCut"]!.GetValue<int>())));
        Assert.Equal([.. Enumerable.Range(0, 25), .. Enumerable.Range(3575, 25)], StartsOf(kept[1]!.AsObject()));
    }

    [Fact]
    public void HeardRangesAreCutFromTheMiddlePastTheResultBudgetAndCountedInHeardCut()
    {
        JsonObject reply = Timeline(BusesAudio(Bus("Effects", ranges: 50)));
        int budget = reply.ToJsonString().Length - 100;

        JsonObject bus = WatchTimeline.Shape(reply, budget)["audio"]!["buses"]![0]!.AsObject();

        int[] starts = [.. StartsOf(bus)];
        Assert.Equal(50 - starts.Length, bus["heardCut"]!.GetValue<int>());
        Assert.True(starts.Length < 50, $"{starts.Length} ranges");
        Assert.Equal((0, 49), (starts[0], starts[^1]));
        Assert.Single(starts.Zip(starts.Skip(1)), pair => pair.Second - pair.First > 1);
    }

    [Fact]
    public void TheFiftyRangeCutRunsBeforeTheBudgetCutWhichAddsToHeardCut()
    {
        int fullLength = Shape(Timeline(BusesAudio(Bus("Effects", ranges: 60)))).ToJsonString().Length;

        JsonObject bus = WatchTimeline.Shape(Timeline(BusesAudio(Bus("Effects", ranges: 60))), fullLength - 60)["audio"]!["buses"]![0]!.AsObject();

        int[] starts = [.. StartsOf(bus)];
        Assert.InRange(starts.Length, 1, 49);
        Assert.Equal(60 - starts.Length, bus["heardCut"]!.GetValue<int>());
        Assert.All(starts, start => Assert.True(start is < 25 or >= 35, $"range {start} is one the 50-range cut drops"));
        Assert.Equal((0, 59), (starts[0], starts[^1]));
    }

    private static JsonObject Shape(JsonObject reply) => WatchTimeline.Shape(reply, WatchTimeline.MaxResultLength);

    private static IEnumerable<int> StartsOf(JsonObject bus) => bus["heard"]!.AsArray().Select(range => range![0]!.GetValue<int>() / 2);

    /// <summary>The bridge's audio object for a watch of buses only.</summary>
    private static JsonObject BusesAudio(params JsonObject[] buses) => new() { ["buses"] = new JsonArray([.. buses]) };

    /// <summary>A bus summary as the bridge sends it, with <paramref name="ranges"/> one-frame heard ranges on every second
    /// frame.</summary>
    private static JsonObject Bus(string name, int ranges) =>
        new()
        {
            ["name"] = name,
            ["peakMaxDb"] = -12.5,
            ["peakMaxAt"] = 0,
            ["heard"] = new JsonArray([.. Enumerable.Range(0, ranges).Select(range => new JsonArray(range * 2, range * 2))]),
            ["heardFrames"] = ranges,
            ["sampledFrames"] = ranges * 2,
            ["volumeDb"] = new JsonObject
            {
                ["first"] = 0,
                ["last"] = 0,
                ["changes"] = 0,
            },
            ["mute"] = new JsonObject
            {
                ["first"] = false,
                ["last"] = false,
                ["changes"] = 0,
            },
        };

    private static string Player(JsonNode? item) => item![3]!.GetValue<string>();

    /// <summary>The bridge's audio object: the voices, each player's starts and ends, and every voice it read.</summary>
    private static JsonObject Audio(IEnumerable<JsonArray> voices, int total)
    {
        JsonArray list = [.. voices];
        JsonObject counts = [];
        foreach (string player in list.Select(Player).Distinct())
        {
            counts[player] = new JsonObject { ["starts"] = 1, ["ends"] = 0 };
        }

        return new JsonObject
        {
            ["voices"] = list,
            ["voiceCounts"] = counts,
            ["voiceTotal"] = total,
        };
    }

    /// <summary>The shaped audio of a watch whose voiceCounts has <paramref name="paths"/> paths, /root/P0 first, each with one
    /// start and the ends <paramref name="endsOf"/> gives its index.</summary>
    private static JsonObject ShapeCounts(int paths, Func<int, int> endsOf)
    {
        JsonObject audio = Audio([Voice(0, "/root/P0")], total: 1);
        JsonObject counts = [];
        for (int path = 0; path < paths; path++)
        {
            counts[$"/root/P{path}"] = new JsonObject { ["starts"] = 1, ["ends"] = endsOf(path) };
        }

        audio["voiceCounts"] = counts;
        return Shape(Timeline(audio))["audio"]!.AsObject();
    }

    private static JsonArray Voice(int frame, string player, string voiceEvent = "start", string stream = "res://hit.wav") =>
        [frame, frame * 16, voiceEvent, player, new JsonObject { ["stream"] = stream, ["bus"] = "Master" }];

    private static JsonObject Timeline(JsonObject audio) =>
        new()
        {
            ["startFrame"] = 100,
            ["frames"] = 300,
            ["gameMs"] = 4800,
            ["wallMs"] = 4810,
            ["tracks"] = new JsonArray(),
            ["audio"] = audio,
        };

    private static JsonObject Track() =>
        new()
        {
            ["node"] = "Quiet",
            ["property"] = "z_index",
            ["first"] = 0,
            ["last"] = 9,
            ["changes"] = 9,
            ["points"] = new JsonArray([.. Enumerable.Range(0, 10).Select(frame => new JsonArray(frame, frame * 16, frame))]),
        };
}
