using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The session clock on invented send and receipt times, and over a fake game's handshake: its bounds, its conversion, and the
/// stamps a wait's result carries.
/// </summary>
public sealed class SessionClockTests
{
    [Fact]
    public void NoBoundsBeforeTheFirstPong()
    {
        SessionClock clock = new();

        Assert.False(clock.TryToDriveMs(5_000, out _, out _));
        Assert.True(clock.IsWide);
        Assert.True(double.IsPositiveInfinity(clock.ErrorMs));
    }

    [Fact]
    public void APongBoundsTheOffsetAndItsMidpointConverts()
    {
        SessionClock clock = new();
        clock.Add(1_000, sentMs: 100, receivedMs: 110);

        bool converted = clock.TryToDriveMs(5_000, out double driveMs, out double errorMs);

        Assert.True(converted);
        Assert.Equal(109.0, driveMs);
        Assert.Equal(5.0, errorMs);
        Assert.Equal(5.0, clock.ErrorMs);
        Assert.True(clock.IsWide);
    }

    [Fact]
    public void ATighterPongNarrowsBothBounds()
    {
        SessionClock clock = new();
        clock.Add(1_000, 100, 110);
        clock.Add(100_000, 200, 204);

        clock.TryToDriveMs(100_000, out double driveMs, out double errorMs);

        Assert.Equal(202.0, driveMs);
        Assert.Equal(2.0, errorMs);
        Assert.False(clock.IsWide);
    }

    [Fact]
    public void TheConversionRoundsToATenthOfAMillisecond()
    {
        SessionClock clock = new();
        clock.Add(0, 100, 100);

        clock.TryToDriveMs(1_234, out double driveMs, out double errorMs);

        Assert.Equal(101.2, driveMs);
        Assert.Equal(0.0, errorMs);
    }

    [Fact]
    public void TheErrorRoundsToATenthOfAMillisecond()
    {
        SessionClock clock = new();
        clock.Add(0, 100, 100.06);

        clock.TryToDriveMs(0, out _, out double errorMs);

        Assert.Equal(0.0, errorMs);
    }

    [Fact]
    public void AHelloOnlyLowersTheUpperBound()
    {
        SessionClock clock = new();
        clock.AddHello(2_000, receivedMs: 10);

        Assert.False(clock.TryToDriveMs(2_000, out _, out _));

        // The pong alone bounds the offset to [8.0, 8.4]; the hello's receipt already puts it at or below 8.
        clock.Add(92_000, 100, 100.4);

        Assert.True(clock.TryToDriveMs(92_000, out double driveMs, out double errorMs));
        Assert.Equal(100.0, driveMs);
        Assert.Equal(0.0, errorMs);
    }

    [Fact]
    public void AnEmptyIntersectionStartsOverFromTheNewestPong()
    {
        SessionClock clock = new();
        clock.Add(1_000, 100, 110);
        clock.Add(0, 500, 501);

        clock.TryToDriveMs(0, out double driveMs, out double errorMs);

        Assert.Equal(500.5, driveMs);
        Assert.Equal(0.5, errorMs);
    }

    [Fact]
    public void IsWideIsTrueOverTwoMillisecondsAndFalseAtOrUnder()
    {
        SessionClock wide = new();
        wide.Add(0, 100, 110);
        SessionClock atTwo = new();
        atTwo.Add(0, 100, 104);
        SessionClock narrow = new();
        narrow.Add(0, 100, 101);

        Assert.True(wide.IsWide);
        Assert.False(atTwo.IsWide);
        Assert.False(narrow.IsWide);
    }

    [Fact]
    public void APongReceivedBeforeItsSendTimeIsIgnored()
    {
        SessionClock clock = new();
        clock.Add(0, 110, 100);

        Assert.False(clock.TryToDriveMs(0, out _, out _));
    }

    // The pump answers the handshake's five pings with the fake's own ticks, so the session's clock takes a real pong and its
    // success branch, rather than leaving every attach to the 500 ms timeout.
    [Fact]
    public async Task AHandshakenSessionsClockHasFiniteBounds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());

        SessionClock clock = harness.Sessions.Resolve("server").Clock;

        Assert.False(double.IsPositiveInfinity(clock.ErrorMs));
        Assert.True(clock.TryToDriveMs(0, out double driveMs, out double errorMs));
        Assert.False(double.IsNaN(driveMs));
        Assert.True(errorMs >= 0);
    }

    [Fact]
    public async Task AMetWaitCarriesTheClockStampWithoutTheRawTick()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        long ticksUsec = (long)(DriveClock.NowMs * 1000);

        Task<string> wait = tools.WaitForAsync(new WaitCondition(Node: "Main", Exists: true), 1_000, "server", cancellation);
        JsonObject request = await game.ReadRequestAsync(cancellation);
        await game.ReplyAsync(
            request,
            new JsonObject
            {
                ["met"] = true,
                ["elapsedMs"] = 3,
                ["value"] = new JsonObject { ["frames"] = 1, ["value"] = true },
                ["ticksUsec"] = ticksUsec,
            },
            cancellation
        );
        JsonObject result = JsonNode.Parse(await wait)!.AsObject();

        Assert.True(result["met"]!.GetValue<bool>(), result.ToJsonString());
        Assert.True(result["metAt"] is JsonValue, result.ToJsonString());
        Assert.True(result["metAt"]!.GetValue<double>() >= 0, result.ToJsonString());
        Assert.True(result["clockErrorMs"]!.GetValue<double>() >= 0, result.ToJsonString());
        Assert.False(result.ContainsKey("ticksUsec"), result.ToJsonString());
    }

    [Fact]
    public async Task ATimedOutWaitCarriesNoClockStamp()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());

        Task<string> wait = tools.WaitForAsync(new WaitCondition(Node: "Main", Exists: true), 300, "server", cancellation);
        JsonObject request = await game.ReadRequestAsync(cancellation);
        // A tick on a reply that did not meet is stripped too: no raw tick reaches the client, and no stamp is added.
        await game.ReplyAsync(
            request,
            new JsonObject
            {
                ["met"] = false,
                ["elapsedMs"] = 300,
                ["ticksUsec"] = 1_234,
            },
            cancellation
        );
        JsonObject result = JsonNode.Parse(await wait)!.AsObject();

        Assert.False(result["met"]!.GetValue<bool>(), result.ToJsonString());
        Assert.False(result.ContainsKey("metAt"), result.ToJsonString());
        Assert.False(result.ContainsKey("clockErrorMs"), result.ToJsonString());
        Assert.False(result.ContainsKey("ticksUsec"), result.ToJsonString());
    }
}
