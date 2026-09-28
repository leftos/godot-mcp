using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>The load clock on a fake time and a fake machine of four processors.</summary>
public sealed class LoadClockTests : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);
    private readonly FakeTimeProvider _time = new();
    private readonly FakeLoadSource _machine;
    private readonly LoadClock _clock;

    public LoadClockTests()
    {
        _machine = new FakeLoadSource(_time, processors: 4);
        _clock = new LoadClock(_time, _machine);
    }

    public void Dispose() => _clock.Dispose();

    [Fact]
    public void AnIdleMachineRunsAtWallTime()
    {
        using LoadDeadline deadline = Start(TenSeconds);

        Run(TimeSpan.FromSeconds(9.9));
        Assert.False(deadline.Expired);
        Run(TimeSpan.FromSeconds(0.2));

        Assert.Equal(DeadlineReason.Ceiling, deadline.Reason);
        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.Equal(1, deadline.MeanFree, precision: 2);
    }

    [Fact]
    public void ADeadlineStretchesUnderLoad()
    {
        // Half of four processors busy with other work leaves half the machine: after the first sample, 2 s of wall per second.
        _machine.SetLoad(busyShare: 0.5, ownCores: 0);
        using LoadDeadline deadline = Start(TenSeconds);

        Run(TimeSpan.FromSeconds(18.5));
        Assert.False(deadline.Expired, $"adjusted {deadline.Adjusted} after {deadline.Wall}");
        Run(TimeSpan.FromSeconds(1));

        Assert.Equal(DeadlineReason.Ceiling, deadline.Reason);
        Assert.Equal(0.5, _clock.Free, precision: 6);
        Assert.StartsWith("within 10 s of load-adjusted time (wall 19", deadline.CeilingClause(), StringComparison.Ordinal);
        Assert.EndsWith("machine free 53% on average)", deadline.CeilingClause(), StringComparison.Ordinal);
    }

    [Fact]
    public void OwnWorkDoesNotSlowTheClock()
    {
        _machine.SetLoad(busyShare: 0.5, ownCores: 2);
        using LoadDeadline deadline = Start(TenSeconds);

        Run(TimeSpan.FromSeconds(10.2));

        Assert.Equal(DeadlineReason.Ceiling, deadline.Reason);
        Assert.Equal(1, _clock.Free, precision: 6);
    }

    [Fact]
    public void TheFreeShareNeverDropsBelowFivePercent()
    {
        _machine.SetLoad(busyShare: 1, ownCores: 0);
        using LoadDeadline deadline = Start(TenSeconds);

        Run(TimeSpan.FromSeconds(2));

        Assert.Equal(LoadMath.MinimumFree, _clock.Free, precision: 6);
    }

    [Fact]
    public void TheBackstopFiresAtFiveTimesTheCeiling()
    {
        _machine.SetLoad(busyShare: 1, ownCores: 0);
        using LoadDeadline deadline = Start(TenSeconds);

        Run(TimeSpan.FromSeconds(49.9));
        Assert.False(deadline.Expired);
        Run(TimeSpan.FromSeconds(0.2));

        Assert.Equal(DeadlineReason.Backstop, deadline.Reason);
        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.True(deadline.Adjusted < TenSeconds, $"adjusted {deadline.Adjusted}");
        Assert.StartsWith(
            "; that is 5 x its 10 s ceiling in wall time, the backstop (load-adjusted 3.",
            deadline.BackstopClause(),
            StringComparison.Ordinal
        );
        Assert.EndsWith("machine free 7% on average)", deadline.BackstopClause(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnavailableSourceRunsAtWallTime()
    {
        _machine.SetLoad(busyShare: 1, ownCores: 0);
        _machine.Unavailable = true;
        using LoadDeadline deadline = Start(TenSeconds);

        Run(TimeSpan.FromSeconds(10.2));

        Assert.Equal(DeadlineReason.Ceiling, deadline.Reason);
        Assert.Equal(1, _clock.Free);
    }

    [Fact]
    public void ALinkedCancelEndsTheDeadlineWithoutAReason()
    {
        using CancellationTokenSource caller = new();
        using LoadDeadline deadline = _clock.Start(TenSeconds, caller.Token);

        Run(TimeSpan.FromSeconds(1));
        caller.Cancel();
        Run(TimeSpan.FromSeconds(20));

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.False(deadline.Expired);
        Assert.Null(deadline.Reason);
        Assert.True(deadline.Wall < TimeSpan.FromSeconds(3), $"wall {deadline.Wall}");
    }

    [Fact]
    public void ShortBudgetsFireWithoutWaitingForASample()
    {
        using LoadDeadline deadline = Start(TimeSpan.FromMilliseconds(100));

        _time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.False(deadline.Expired);
        _time.Advance(TimeSpan.FromMilliseconds(60));

        Assert.Equal(DeadlineReason.Ceiling, deadline.Reason);
        Assert.Equal(1, _machine.Reads);
    }

    [Fact]
    public void OneSamplerServesManyDeadlines()
    {
        using LoadDeadline first = Start(TenSeconds);
        using LoadDeadline second = Start(TenSeconds);
        using LoadDeadline third = Start(TenSeconds);

        Run(TimeSpan.FromSeconds(5));

        // One read when sampling starts, then one a second.
        Assert.Equal(6, _machine.Reads);
    }

    private LoadDeadline Start(TimeSpan budget) => _clock.Start(budget, TestContext.Current.CancellationToken);

    private void Run(TimeSpan span)
    {
        for (TimeSpan passed = TimeSpan.Zero; passed < span; passed += Step)
        {
            _time.Advance(Step);
        }
    }
}
