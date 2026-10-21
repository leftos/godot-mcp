using System.Globalization;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Session;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Tools;

/// <summary>A watch run over a fake game's handshake: its points and events reach the client on the drive clock.</summary>
public sealed class WatchStampWireTests
{
    [Fact]
    public async Task ARunsPointsAndEventsCarryTheirDriveTimeAndTheClockErrorWithoutARawTick()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        WatchTracks tracks = new(
            Properties: [new WatchPropertyTrack("Main", "position:x")],
            Signals: [new WatchSignalTrack(Node: "Main", Signal: "moved")]
        );

        Task<string> watch = tools.WatchAsync("run", tracks, new WatchWindow(Frames: 2), session: "server", cancellationToken: cancellation);
        JsonObject request = await game.ReadRequestAsync(cancellation);
        long first = (long)(DriveClock.NowMs * 1000);
        long second = first + 16_667;
        long emitted = first + 17_001;
        await game.ReplyAsync(request, Timeline(first, second, emitted), cancellation);
        JsonObject result = JsonNode.Parse(await watch)!.AsObject();

        string json = result.ToJsonString();
        double errorMs = result["clockErrorMs"]?.GetValue<double>() ?? throw new InvalidOperationException($"no clockErrorMs: {json}");
        Assert.True(errorMs >= 0, json);
        JsonArray points = result["tracks"]![0]!["points"]!.AsArray();
        JsonArray item = result["events"]![0]!.AsArray();
        Assert.Equal(6, item.Count);
        (long Ticks, JsonNode? At)[] stamps = [(first, points[0]![3]), (second, points[1]![3]), (emitted, item[5])];
        // at and clockErrorMs are each rounded to 0.1 ms, and the fake's pong ticks truncate DriveClock.NowMs to a microsecond,
        // so the margin past the error is 0.2 ms.
        foreach ((long ticks, JsonNode? at) in stamps)
        {
            Assert.InRange(at!.GetValue<double>(), (ticks / 1000.0) - errorMs - 0.2, (ticks / 1000.0) + errorMs + 0.2);
        }

        foreach (long ticks in new[] { first, second, emitted })
        {
            Assert.DoesNotContain(ticks.ToString(CultureInfo.InvariantCulture), json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ARunOnWideBoundsSendsOneMorePingAndConvertsOnItsPong()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        SessionClockTests.StageWideBounds(harness.Sessions.Resolve("server").Clock);
        WatchTracks tracks = new(
            Properties: [new WatchPropertyTrack("Main", "position:x")],
            Signals: [new WatchSignalTrack(Node: "Main", Signal: "moved")]
        );

        Task<string> watch = tools.WatchAsync("run", tracks, new WatchWindow(Frames: 2), session: "server", cancellationToken: cancellation);
        long first = (long)(DriveClock.NowMs * 1000);
        long second = first + 16_667;
        long emitted = first + 17_001;
        await game.ReplyAsync(await game.ReadRequestAsync(cancellation), Timeline(first, second, emitted), cancellation);
        Task<JsonObject> nextRequest = game.ReadRequestAsync(cancellation);
        // The watch cannot end before its extra ping is answered; one that ends first never sent it.
        Assert.Same(nextRequest, await Task.WhenAny(nextRequest, watch));
        JsonObject ping = await nextRequest;
        Assert.Equal("ping", ping["command"]?.GetValue<string>());
        // A pong 1000 ms ahead of the drive clock cannot overlap the handshake's bounds, so the clock starts over from it and
        // every at lands 1000 ms earlier only if the stamp waits for it. Ahead, not behind: a tick behind could be negative.
        JsonObject pong = new() { ["pong"] = true, ["ticksUsec"] = (long)((DriveClock.NowMs + 1000) * 1000) };
        await game.ReplyAsync(ping, pong, cancellation);
        JsonObject result = JsonNode.Parse(await watch)!.AsObject();

        string json = result.ToJsonString();
        double errorMs = result["clockErrorMs"]?.GetValue<double>() ?? throw new InvalidOperationException($"no clockErrorMs: {json}");
        (long Ticks, JsonNode? At)[] stamps =
        [
            (first, result["tracks"]![0]!["points"]![0]![3]),
            (second, result["tracks"]![0]!["points"]![1]![3]),
            (emitted, result["events"]![0]![5]),
        ];
        // at and clockErrorMs are each rounded to 0.1 ms, so their roundings add up to 0.1 ms on top of the half-width.
        foreach ((long ticks, JsonNode? at) in stamps)
        {
            double offByMs = Math.Abs(at!.GetValue<double>() - ((ticks / 1000.0) - 1000));
            Assert.True(offByMs <= errorMs + 0.15, $"{json} is {offByMs} ms from the tick moved by the pong's offset");
        }
    }

    /// <summary>The bridge's run reply: two points of one track and one event, each ending with its tick.</summary>
    private static JsonObject Timeline(long first, long second, long emitted) =>
        new()
        {
            ["startFrame"] = 100,
            ["frames"] = 2,
            ["gameMs"] = 16,
            ["wallMs"] = 17,
            ["tracks"] = new JsonArray(
                new JsonObject
                {
                    ["node"] = "/root/Main",
                    ["property"] = "position:x",
                    ["first"] = 0,
                    ["last"] = 50,
                    ["changes"] = 1,
                    ["points"] = new JsonArray(new JsonArray(0, 0, 0, first), new JsonArray(1, 16, 50, second)),
                }
            ),
            ["events"] = new JsonArray(new JsonArray(1, 16, "/root/Main", "moved", new JsonArray(), 0, emitted)),
            ["eventCounts"] = new JsonObject { ["/root/Main:moved"] = 1 },
            ["eventTracks"] = new JsonArray(new JsonObject { ["kept"] = 1, ["total"] = 1 }),
        };
}
