using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The session clock on invented send and receipt times, and over a fake game's handshake: its bounds, its conversion, and the
/// stamps a wait's result carries.
/// </summary>
public sealed class SessionClockTests
{
    /// <summary>The half-width of the bounds <see cref="StageWideBounds"/> leaves: wider than 2 ms.</summary>
    internal const double WideErrorMs = 5;

    /// <summary>
    /// Gives a handshaken session's clock wide bounds without wall time: a pong interval 1000 s off any the handshake's round
    /// trips can give, so the bounds start over from it, <see cref="WideErrorMs"/> either side of its midpoint. The handshake's
    /// own pings are awaited inside the attach, so none can narrow it later.
    /// </summary>
    internal static void StageWideBounds(SessionClock clock)
    {
        const double sentMs = 1_000_000;
        clock.Add(0, sentMs, sentMs + (2 * WideErrorMs));
        Assert.Equal(WideErrorMs, clock.ErrorMs);
    }

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

        JsonObject result = await ReplyToWaitAsync(tools, game, MetReply(ticksUsec), cancellation);

        Assert.True(result["met"]!.GetValue<bool>(), result.ToJsonString());
        Assert.True(result["metAt"] is JsonValue, result.ToJsonString());
        double clockErrorMs = result["clockErrorMs"]!.GetValue<double>();
        Assert.True(clockErrorMs >= 0, result.ToJsonString());
        // The fake's ticks are the drive clock's own, so the offset is about 0 and the tick converts to itself within the
        // bounds' half-width and a frame.
        double offByMs = Math.Abs(result["metAt"]!.GetValue<double>() - (ticksUsec / 1000.0));
        Assert.True(offByMs <= clockErrorMs + 16.7, $"{result.ToJsonString()} is {offByMs} ms from the tick {ticksUsec} us");
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

    [Fact]
    public async Task AMetWaitOnWideBoundsSendsOneMorePingAndConvertsOnItsPong()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        StageWideBounds(harness.Sessions.Resolve("server").Clock);
        long ticksUsec = (long)(DriveClock.NowMs * 1000);

        Task<string> wait = tools.WaitForAsync(new WaitCondition(Node: "Main", Exists: true), 1_000, "server", cancellation);
        await game.ReplyAsync(await game.ReadRequestAsync(cancellation), MetReply(ticksUsec), cancellation);
        JsonObject ping = await game.ReadRequestAsync(cancellation);
        Assert.Equal("ping", ping["command"]?.GetValue<string>());
        // A pong whose game clock runs 1000 ms ahead of the drive clock cannot overlap the handshake's bounds, so the clock starts
        // over from it: the stamp lands 1000 ms earlier only if it waits for this pong and converts on the clock the pong fed.
        // Ahead, not behind: the drive clock starts at its first read in the process, so a tick behind it could be negative.
        JsonObject pong = new() { ["pong"] = true, ["ticksUsec"] = (long)((DriveClock.NowMs + 1000) * 1000) };
        await game.ReplyAsync(ping, pong, cancellation);
        JsonObject result = JsonNode.Parse(await wait)!.AsObject();

        double clockErrorMs = result["clockErrorMs"]!.GetValue<double>();
        double offByMs = Math.Abs(result["metAt"]!.GetValue<double>() - ((ticksUsec / 1000.0) - 1000));
        // metAt and clockErrorMs are each rounded to 0.1 ms, so their two roundings add up to 0.1 ms on top of the half-width.
        Assert.True(offByMs <= clockErrorMs + 0.15, $"{result.ToJsonString()} is {offByMs} ms from the tick moved by the pong's offset");
    }

    // The ping is left unanswered and the caller cancels during it, as a batch group's cancel or the batch deadline does.
    [Fact]
    public async Task ACancelDuringTheExtraPingStillReturnsTheMetWait()
    {
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        StageWideBounds(harness.Sessions.Resolve("server").Clock);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<string> wait = tools.WaitForAsync(new WaitCondition(Node: "Main", Exists: true), 1_000, "server", cancel.Token);
        await game.ReplyAsync(await game.ReadRequestAsync(cancel.Token), MetReply((long)(DriveClock.NowMs * 1000)), cancel.Token);
        JsonObject ping = await game.ReadRequestAsync(cancel.Token);
        Assert.Equal("ping", ping["command"]?.GetValue<string>());
        await cancel.CancelAsync();
        JsonObject result = JsonNode.Parse(await wait)!.AsObject();

        Assert.True(result["met"]!.GetValue<bool>(), result.ToJsonString());
        Assert.True(result["metAt"] is JsonValue, result.ToJsonString());
        Assert.Equal(WideErrorMs, result["clockErrorMs"]!.GetValue<double>());
    }

    // The tool form reads the screenshot the bridge saved after the ping; a cancel during the ping must not fail that read.
    [Fact]
    public async Task ACancelDuringTheExtraPingStillReturnsTheMetWaitsScreenshot()
    {
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        StageWideBounds(harness.Sessions.Resolve("server").Clock);
        string shot = Path.Combine(alpha, "met.png");
        byte[] picture = [137, 80, 78, 71];
        await File.WriteAllBytesAsync(shot, picture, TestContext.Current.CancellationToken);
        JsonObject reply = MetReply((long)(DriveClock.NowMs * 1000));
        reply["screenshot"] = new JsonObject
        {
            ["path"] = shot,
            ["width"] = 4,
            ["height"] = 4,
        };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<IEnumerable<ContentBlock>> wait = tools.WaitForAsync(
            new WaitCondition(Node: "Main", Exists: true),
            1_000,
            new WaitOptions(Screenshot: true),
            "server",
            cancel.Token
        );
        await game.ReplyAsync(await game.ReadRequestAsync(cancel.Token), reply, cancel.Token);
        JsonObject ping = await game.ReadRequestAsync(cancel.Token);
        Assert.Equal("ping", ping["command"]?.GetValue<string>());
        await cancel.CancelAsync();
        List<ContentBlock> blocks = [.. await wait];

        JsonObject result = JsonNode.Parse(Assert.IsType<TextContentBlock>(blocks[0]).Text)!.AsObject();
        Assert.True(result["met"]!.GetValue<bool>(), result.ToJsonString());
        Assert.True(result["metAt"] is JsonValue, result.ToJsonString());
        Assert.Equal(shot, result["screenshot"]?["path"]?.GetValue<string>());
        Assert.Single(blocks.OfType<ImageContentBlock>());
    }

    [Fact]
    public async Task AMetWaitOnTightBoundsSendsNoExtraPing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        // A zero-width interval leaves the bounds tight whatever the handshake's round trips were.
        harness.Sessions.Resolve("server").Clock.Add(0, 0, 0);

        JsonObject result = await ReplyToWaitAsync(tools, game, MetReply((long)(DriveClock.NowMs * 1000)), cancellation);

        Assert.True(result["metAt"] is JsonValue, result.ToJsonString());
        Assert.Equal(GodotSession.ClockPingCount, game.PingsSeen);
    }

    [Fact]
    public async Task AWaitThatTimesOutSendsNoExtraPing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        StageWideBounds(harness.Sessions.Resolve("server").Clock);
        JsonObject timedOut = new()
        {
            ["met"] = false,
            ["elapsedMs"] = 300,
            ["ticksUsec"] = (long)(DriveClock.NowMs * 1000),
        };

        JsonObject result = await ReplyToWaitAsync(tools, game, timedOut, cancellation);

        Assert.False(result.ContainsKey("metAt"), result.ToJsonString());
        Assert.Equal(GodotSession.ClockPingCount, game.PingsSeen);
    }

    // The extra ping waits out ClockPingTimeout, 500 ms, which has no test knob.
    [Fact]
    public async Task AnUnansweredExtraPingStillReturnsTheMetWaitOnTheWideBounds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        StageWideBounds(harness.Sessions.Resolve("server").Clock);

        JsonObject result = await ReplyToWaitAsync(tools, game, MetReply((long)(DriveClock.NowMs * 1000)), cancellation);

        JsonObject ping = await game.ReadRequestAsync(cancellation);
        Assert.Equal("ping", ping["command"]?.GetValue<string>());
        Assert.True(result["metAt"] is JsonValue, result.ToJsonString());
        Assert.Equal(WideErrorMs, result["clockErrorMs"]!.GetValue<double>());
    }

    [Fact]
    public async Task ARefreshOnAClosedConnectionKeepsTheBoundsWithoutThrowing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using RegistryHarness harness = new();
        string alpha = harness.Project("alpha");
        FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        GodotSession session = harness.Sessions.Resolve("server");
        StageWideBounds(session.Clock);

        game.Dispose();
        await RegistryHarness.WaitUntilAsync(() => !session.HasGame);
        SessionClock refreshed = await session.RefreshClockAsync(cancellation);

        Assert.Same(session.Clock, refreshed);
        Assert.Equal(WideErrorMs, refreshed.ErrorMs);
    }

    private static JsonObject MetReply(long ticksUsec) =>
        new()
        {
            ["met"] = true,
            ["elapsedMs"] = 3,
            ["value"] = new JsonObject { ["frames"] = 1, ["value"] = true },
            ["ticksUsec"] = ticksUsec,
        };

    /// <summary>Starts a node wait on the session <c>server</c>, answers its request with <paramref name="reply"/>, and returns the result.</summary>
    private static async Task<JsonObject> ReplyToWaitAsync(RuntimeTools tools, FakeBridge game, JsonObject reply, CancellationToken cancellation)
    {
        Task<string> wait = tools.WaitForAsync(new WaitCondition(Node: "Main", Exists: true), 1_000, "server", cancellation);
        JsonObject request = await game.ReadRequestAsync(cancellation);
        await game.ReplyAsync(request, reply, cancellation);
        return JsonNode.Parse(await wait)!.AsObject();
    }
}
