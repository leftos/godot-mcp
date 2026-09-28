using GodotMcp.Server.Session;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>The stall check: a tree whose output and CPU both stand still for the limit has stalled; either moving is progress.</summary>
public sealed class StallWatchTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void AStallKillsASilentChild()
    {
        StallWatch watch = new(_time, Limit);

        bool[] stalled = [.. Enumerable.Range(1, 4).Select(_ => Tick(watch, lines: 0, cpu: 0))];

        Assert.Equal([false, false, true, true], stalled);
        Assert.Equal(TimeSpan.FromSeconds(4), watch.Quiet);
    }

    [Fact]
    public void AChattyChildIsNotKilled()
    {
        StallWatch watch = new(_time, Limit);

        bool[] stalled = [.. Enumerable.Range(1, 10).Select(second => Tick(watch, lines: second * 5, cpu: second * 1000))];

        Assert.All(stalled, Assert.False);
        Assert.Equal(TimeSpan.Zero, watch.Quiet);
    }

    [Fact]
    public void CpuWithoutOutputIsProgress()
    {
        StallWatch watch = new(_time, Limit);

        bool[] stalled = [.. Enumerable.Range(1, 10).Select(second => Tick(watch, lines: 0, cpu: second * 1000))];

        Assert.All(stalled, Assert.False);
    }

    [Fact]
    public void OutputWithoutCpuIsProgress()
    {
        StallWatch watch = new(_time, Limit);

        bool[] stalled = [.. Enumerable.Range(1, 10).Select(second => Tick(watch, lines: second, cpu: 0))];

        Assert.All(stalled, Assert.False);
    }

    private bool Tick(StallWatch watch, long lines, long cpu)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        return watch.Observe(lines, cpu);
    }
}
