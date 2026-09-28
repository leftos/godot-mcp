using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The free share: other work's load is the busy share of all processors less the server's own cores.</summary>
public sealed class LoadMathTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void FreeShareSubtractsOwnCoresFromBusy()
    {
        // Four processors for one second: half of them busy, one core of that the server's own, so one core of other work.
        LoadCounters after = new(Idle: 2 * Second.Ticks, Kernel: 2 * Second.Ticks, User: 2 * Second.Ticks, OwnCpu: Second.Ticks);

        double free = LoadMath.FreeShare(default, after, processors: 4, Second);

        Assert.Equal(0.75, free, precision: 6);
    }

    [Fact]
    public void ZeroElapsedCountersReadAsFree()
    {
        LoadCounters counters = new(Idle: 10, Kernel: 20, User: 30, OwnCpu: 5);

        Assert.Equal(1, LoadMath.FreeShare(counters, counters, processors: 4, Second));
    }
}
