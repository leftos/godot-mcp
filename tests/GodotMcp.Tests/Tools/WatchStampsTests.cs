using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// A watch reply's bridge ticks converted to drive time: each point's, event's and voice's last element, the timeline's
/// clockErrorMs, and the null an unconverted tick leaves.
/// </summary>
public sealed class WatchStampsTests
{
    // A pong sent at 100 ms and received at 104 ms with a reading of 0 bounds the offset to [100, 104]: midpoint 102, error 2.
    private const double MidMs = 102;

    [Fact]
    public void APointsAnEventsAndAVoicesTickConvertToTheDriveClock()
    {
        JsonObject reply = Reply(
            [0, 0, 1.5, 1_234_567L],
            [3, 48, "/root/A", "hit", new JsonArray(), 0, 2_000_049L],
            [4, 64, "start", "/root/S", new JsonObject(), 3_000_051L]
        );

        WatchStamps.Stamp(reply, Clock());

        Assert.Equal(Math.Round(MidMs + 1_234.567, 1), reply["tracks"]![0]!["points"]![0]![3]!.GetValue<double>());
        Assert.Equal(Math.Round(MidMs + 2_000.049, 1), reply["events"]![0]![6]!.GetValue<double>());
        Assert.Equal(Math.Round(MidMs + 3_000.051, 1), reply["audio"]!["voices"]![0]![5]!.GetValue<double>());
        Assert.Equal(1336.6, reply["tracks"]![0]!["points"]![0]![3]!.GetValue<double>());
    }

    [Fact]
    public void OneReadOfTheBoundsGivesEveryItemOneMidpointAndTheTimelineTheirHalfWidthOnce()
    {
        JsonObject reply = Reply(
            [0, 0, 1.5, 1_000L],
            [3, 48, "/root/A", "hit", new JsonArray(), 0, 2_100L],
            [4, 64, "start", "/root/S", new JsonObject(), 3_200L]
        );
        SessionClock clock = Clock();
        Assert.True(clock.TryGetOffset(out double offsetMs, out double halfWidthMs));

        WatchStamps.Stamp(reply, clock);

        Assert.Equal(halfWidthMs, reply["clockErrorMs"]!.GetValue<double>());
        (long Ticks, JsonNode? At)[] stamps =
        [
            (1_000L, reply["tracks"]![0]!["points"]![0]![3]),
            (2_100L, reply["events"]![0]![6]),
            (3_200L, reply["audio"]!["voices"]![0]![5]),
        ];
        Assert.All(stamps, stamp => Assert.Equal(MidMs, stamp.At!.GetValue<double>() - (stamp.Ticks / 1000.0), 9));
        Assert.All(stamps, stamp => Assert.Equal(SessionClock.ToDriveMs(offsetMs, stamp.Ticks), stamp.At!.GetValue<double>()));
        string json = reply.ToJsonString();
        Assert.Equal(json.IndexOf("clockErrorMs", StringComparison.Ordinal), json.LastIndexOf("clockErrorMs", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoBoundsEveryAtIsNullAndThereIsNoClockError()
    {
        JsonObject reply = Reply(
            [0, 0, 1.5, 1_000L],
            [3, 48, "/root/A", "hit", new JsonArray(), 0, 2_000L],
            [4, 64, "start", "/root/S", new JsonObject(), 3_000L]
        );

        WatchStamps.Stamp(reply, new SessionClock());

        Assert.Null(reply["tracks"]![0]!["points"]![0]![3]);
        Assert.Null(reply["events"]![0]![6]);
        Assert.Null(reply["audio"]!["voices"]![0]![5]);
        Assert.Equal(4, reply["tracks"]![0]!["points"]![0]!.AsArray().Count);
        Assert.False(reply.ContainsKey("clockErrorMs"), reply.ToJsonString());
    }

    [Fact]
    public void AnOldLengthItemGetsANullAppendedAndKeepsItsValue()
    {
        JsonObject reply = Reply(
            [0, 0, 1_234_567L],
            [3, 48, "/root/A", "hit", new JsonArray(9), 2],
            [4, 64, "start", "/root/S", new JsonObject { ["bus"] = "Master" }]
        );

        WatchStamps.Stamp(reply, Clock());

        JsonArray point = reply["tracks"]![0]!["points"]![0]!.AsArray();
        JsonArray item = reply["events"]![0]!.AsArray();
        JsonArray voice = reply["audio"]!["voices"]![0]!.AsArray();
        Assert.Equal("[0,0,1234567,null]", point.ToJsonString());
        Assert.Equal("""[3,48,"/root/A","hit",[9],2,null]""", item.ToJsonString());
        Assert.Equal("""[4,64,"start","/root/S",{"bus":"Master"},null]""", voice.ToJsonString());
        Assert.False(reply.ContainsKey("clockErrorMs"), reply.ToJsonString());
    }

    [Fact]
    public void NoRawTickSurvivesAnywhereInTheReply()
    {
        const long Ticks = 987_654_321;
        JsonObject reply = Reply(
            [0, 0, 1.5, Ticks],
            [3, 48, "/root/A", "hit", new JsonArray(), 0, Ticks],
            [4, 64, "start", "/root/S", new JsonObject(), Ticks]
        );

        WatchStamps.Stamp(reply, Clock());

        Assert.DoesNotContain("987654321", reply.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AReplyWithOnlyMonitorsGetsNoClockError()
    {
        JsonObject reply = new()
        {
            ["startFrame"] = 100,
            ["frames"] = 10,
            ["gameMs"] = 160,
            ["wallMs"] = 170,
            ["tracks"] = new JsonArray(),
            ["monitors"] = new JsonArray(new JsonObject { ["name"] = "frame_ms", ["spikes"] = new JsonArray(new JsonArray(3, 40.5)) }),
        };

        WatchStamps.Stamp(reply, Clock());

        Assert.False(reply.ContainsKey("clockErrorMs"), reply.ToJsonString());
    }

    private static SessionClock Clock()
    {
        SessionClock clock = new();
        clock.Add(0, sentMs: 100, receivedMs: 104);
        return clock;
    }

    /// <summary>A timeline as the bridge sends it, with one point on one track, one event and one voice.</summary>
    private static JsonObject Reply(JsonArray point, JsonArray item, JsonArray voice) =>
        new()
        {
            ["startFrame"] = 100,
            ["frames"] = 10,
            ["gameMs"] = 160,
            ["wallMs"] = 170,
            ["tracks"] = new JsonArray(
                new JsonObject
                {
                    ["node"] = "/root/Main",
                    ["property"] = "position",
                    ["points"] = new JsonArray(point),
                }
            ),
            ["events"] = new JsonArray(item),
            ["audio"] = new JsonObject { ["voices"] = new JsonArray(voice) },
        };
}
