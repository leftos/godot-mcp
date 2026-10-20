using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The session clock's offset read once: the bounds' midpoint and half-width, and the conversion that uses them.</summary>
public sealed class SessionClockOffsetTests
{
    [Fact]
    public void NoOffsetBeforeThePongGivesBothBounds()
    {
        SessionClock clock = new();
        clock.AddHello(2_000, receivedMs: 10);

        Assert.False(clock.TryGetOffset(out _, out _));
    }

    [Fact]
    public void TheOffsetIsTheBoundsMidpointAndTheErrorTheirHalfWidth()
    {
        SessionClock clock = new();
        // The pong bounds the offset to [99, 109].
        clock.Add(1_000, sentMs: 100, receivedMs: 110);

        Assert.True(clock.TryGetOffset(out double offsetMs, out double errorMs));
        Assert.Equal(104.0, offsetMs);
        Assert.Equal(5.0, errorMs);
    }

    [Fact]
    public void ConvertingWithTheOffsetMatchesTryToDriveMsBitForBit()
    {
        SessionClock clock = new();
        clock.Add(1_234, sentMs: 100.03, receivedMs: 101.37);
        Assert.True(clock.TryGetOffset(out double offsetMs, out double errorMs));

        foreach (long ticksUsec in new long[] { 0, 1_234, 987_654, 5_000_049, 123_456_789_012 })
        {
            Assert.True(clock.TryToDriveMs(ticksUsec, out double driveMs, out double driveErrorMs));
            Assert.Equal(driveMs, SessionClock.ToDriveMs(offsetMs, ticksUsec));
            Assert.Equal(driveErrorMs, errorMs);
        }
    }
}
