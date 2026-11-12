using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>The load clock on a fake time and a fake machine of four processors.</summary>
public sealed class LoadClockTests : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SixSeconds = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StaleGap = LoadClock.SamplePeriod * 2;
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);
    private readonly FakeTimeProvider _time = new();
    private readonly HeldTimerProvider _held;
    private readonly FakeLoadSource _machine;
    private readonly LoadClock _clock;

    public LoadClockTests()
    {
        _held = new HeldTimerProvider(_time);
        _machine = new FakeLoadSource(_time, processors: 4);
        _clock = new LoadClock(_held, _machine);
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
    public void AWallCapFiresAtItsWallTimeOnASaturatedMachine()
    {
        _machine.SetLoad(busyShare: 1, ownCores: 0);
        using LoadDeadline deadline = _clock.Start(TenSeconds, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Run(TimeSpan.FromSeconds(2.9));
        Assert.False(deadline.Expired);
        Run(TimeSpan.FromSeconds(0.2));

        Assert.Equal(DeadlineReason.WallCap, deadline.Reason);
        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.True(deadline.Adjusted < TimeSpan.FromSeconds(3), $"adjusted {deadline.Adjusted}");
    }

    [Fact]
    public void ACeilingBeforeTheWallCapEndsTheDeadlineOnItsCeiling()
    {
        using LoadDeadline deadline = _clock.Start(TenSeconds, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Run(TimeSpan.FromSeconds(10.2));

        Assert.Equal(DeadlineReason.Ceiling, deadline.Reason);
    }

    [Fact]
    public void ACeilingAndAWallCapExpiringTogetherNameTheCeiling()
    {
        using LoadDeadline deadline = _clock.Start(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Run(TimeSpan.FromSeconds(3.2));

        Assert.Equal(DeadlineReason.Ceiling, deadline.Reason);
    }

    [Fact]
    public void AWallCapAtOrAboveTheBackstopNamesTheBackstop()
    {
        // Sampled first, so the 1 s budget runs at the 5% floor from its start rather than at wall time until the first sample.
        _machine.SetLoad(busyShare: 1, ownCores: 0);
        using (Start(TimeSpan.FromSeconds(1)))
        {
            Run(TimeSpan.FromSeconds(1.1));
        }

        using LoadDeadline deadline = _clock.Start(TimeSpan.FromSeconds(1), TenSeconds, TestContext.Current.CancellationToken);

        Run(TimeSpan.FromSeconds(5.2));

        Assert.Equal(DeadlineReason.Backstop, deadline.Reason);
    }

    [Fact]
    public void AWallCapOffTheSampleGridEndsOnTime()
    {
        using LoadDeadline deadline = _clock.Start(TenSeconds, TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);

        Run(TimeSpan.FromSeconds(2.4));
        Assert.False(deadline.Expired);
        Run(TimeSpan.FromSeconds(0.2));

        Assert.Equal(DeadlineReason.WallCap, deadline.Reason);
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

    [Fact]
    public void AStarvedSamplerCreditsItsGapAtTheShareMeasuredOverIt()
    {
        using LoadDeadline deadline = Start(SixSeconds);
        Run(TimeSpan.FromSeconds(2));

        _machine.SetLoad(busyShare: 1, ownCores: 0);
        _held.Hold();
        _time.Advance(TimeSpan.FromSeconds(7));
        _held.Release();

        Assert.False(deadline.Expired, $"expired after {deadline.Wall} at adjusted {deadline.Adjusted}");
        Assert.True(deadline.Adjusted < SixSeconds, $"adjusted {deadline.Adjusted} after {deadline.Wall}");
    }

    [Fact]
    public void AReadingAfterAStarvedGapTakesAFreshSample()
    {
        using LoadDeadline deadline = Start(SixSeconds);
        Run(TimeSpan.FromSeconds(2));

        _machine.SetLoad(busyShare: 1, ownCores: 0);
        _held.Hold();
        _time.Advance(TimeSpan.FromSeconds(7));

        Assert.False(deadline.Expired);
        Assert.True(deadline.Adjusted < SixSeconds, $"adjusted {deadline.Adjusted} after {deadline.Wall}");
    }

    [Fact]
    public void AShortGapIsCreditedAtTheLastShareAsBefore()
    {
        using LoadDeadline deadline = Start(TenSeconds);

        _time.Advance(TimeSpan.FromSeconds(1));
        _machine.SetLoad(busyShare: 1, ownCores: 0);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(2, _clock.AdjustedNow.TotalSeconds, precision: 6);
        Assert.Equal(LoadMath.MinimumFree, _clock.Free, precision: 6);
        Assert.False(deadline.Expired);
    }

    [Fact]
    public void TheAdjustedTotalNeverDecreasesAcrossAStarvedGap()
    {
        using LoadDeadline deadline = Start(TenSeconds);
        Run(TimeSpan.FromSeconds(2));

        _machine.SetLoad(busyShare: 1, ownCores: 0);
        _held.Hold();
        _time.Advance(StaleGap);
        TimeSpan before = deadline.Adjusted;

        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.True(deadline.Adjusted >= before, $"adjusted {deadline.Adjusted} after {before}");
        Assert.True(_clock.AdjustedNow >= before, $"adjusted {_clock.AdjustedNow} after {before}");
    }

    [Fact]
    public void ASamplerTickRightAfterAnInlineSampleDoesNotPublishAFreeMachine()
    {
        _machine.SetLoad(busyShare: 1, ownCores: 0);
        using LoadDeadline deadline = Start(TenSeconds);

        _held.Hold();
        _time.Advance(TimeSpan.FromSeconds(7));
        TimeSpan before = _clock.AdjustedNow;
        _held.Release();

        Assert.Equal(LoadMath.MinimumFree, _clock.Free, precision: 6);

        _time.Advance(TimeSpan.FromSeconds(1));
        TimeSpan after = _clock.AdjustedNow;

        Assert.True(after - before < TimeSpan.FromSeconds(0.5), $"grew {after - before} in the second after release");
        Assert.False(deadline.Expired);
    }

    [Fact]
    public void AReadingAtTheStaleBoundaryDoesNotTakeAnInlineSample()
    {
        using LoadDeadline deadline = Start(TenSeconds);
        _held.Hold();
        _time.Advance(StaleGap);
        int reads = _machine.Reads;

        _ = _clock.AdjustedNow;

        Assert.Equal(reads, _machine.Reads);
        Assert.False(deadline.Expired);
    }

    [Fact]
    public void AReadingJustPastTheStaleBoundaryTakesAnInlineSample()
    {
        using LoadDeadline deadline = Start(TenSeconds);
        _held.Hold();
        _time.Advance(StaleGap);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        int reads = _machine.Reads;

        _ = _clock.AdjustedNow;

        Assert.Equal(reads + 1, _machine.Reads);
        Assert.False(deadline.Expired);
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
