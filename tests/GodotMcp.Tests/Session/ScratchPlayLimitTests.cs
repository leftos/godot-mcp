using GodotMcp.Server.Session;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The token a scene's steps play on (<see cref="ScratchPlayLimit"/>): the run's cancellation and the scene's ceiling cancel it
/// until the ceiling is released at the exit hold's start, after which only the run's cancellation and its own source (the
/// memory watch's) do; a ceiling that passed before the release still ends the play. The clock is fake.
/// </summary>
public sealed class ScratchPlayLimitTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ACeilingThatPassesBeforeTheReleaseCancelsThePlay()
    {
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        using CancellationTokenSource run = new();
        using LoadDeadline ceiling = clock.Start(Budget, run.Token);
        using ScratchPlayLimit play = new(ceiling, run.Token);

        await PassAsync(time, ceiling, Budget * 2);
        await UntilAsync(() => play.Token.IsCancellationRequested);

        Assert.True(play.Token.IsCancellationRequested);
        Assert.ThrowsAny<OperationCanceledException>(play.ReleaseCeiling);
    }

    [Fact]
    public async Task ADerivedCeilingReleasedNoLongerCancelsThePlay()
    {
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        using CancellationTokenSource run = new();
        using LoadDeadline ceiling = clock.Start(Budget, run.Token);
        using ScratchPlayLimit play = new(ceiling, run.Token);

        play.ReleaseCeiling();
        await PassAsync(time, ceiling, Budget * 6);
        await UntilAsync(() => ceiling.Token.IsCancellationRequested);

        Assert.True(ceiling.Expired);
        Assert.False(play.Token.IsCancellationRequested);
    }

    [Fact]
    public void TheRunsCancellationStillCancelsAReleasedPlay()
    {
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        using CancellationTokenSource run = new();
        using LoadDeadline ceiling = clock.Start(Budget, run.Token);
        using ScratchPlayLimit play = new(ceiling, run.Token);

        play.ReleaseCeiling();
        run.Cancel();

        Assert.True(play.Token.IsCancellationRequested);
    }

    [Fact]
    public void CancellingTheSourceCancelsThePlay()
    {
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        using CancellationTokenSource run = new();
        using LoadDeadline ceiling = clock.Start(Budget, run.Token);
        using ScratchPlayLimit play = new(ceiling, run.Token);

        play.Source.Cancel();

        Assert.True(play.Token.IsCancellationRequested);
        Assert.False(run.IsCancellationRequested);
    }

    /// <summary>Advances the fake clock by <paramref name="span"/> a second at a time, giving the deadline's timer room between.</summary>
    private static async Task PassAsync(FakeTimeProvider time, LoadDeadline ceiling, TimeSpan span)
    {
        for (TimeSpan passed = TimeSpan.Zero; passed < span; passed += TimeSpan.FromSeconds(1))
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken);
        }

        Assert.True(ceiling.Expired, "the ceiling passed");
    }

    /// <summary>Waits in real time, at most the hang guard, for <paramref name="done"/>: a deadline cancels its token off its timer.</summary>
    private static async Task UntilAsync(Func<bool> done)
    {
        DateTime guard = DateTime.UtcNow + RegistryHarness.HangGuard;
        while (!done() && DateTime.UtcNow < guard)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
        }
    }
}
