using System.Diagnostics;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>The stop's grace counts load-adjusted time: a game slow to exit on a busy machine is waited for, on an idle one it is not.</summary>
public sealed class ProcessExitTests : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);
    private readonly FakeTimeProvider _time = new();
    private readonly FakeLoadSource _machine;
    private readonly LoadClock _clock;

    public ProcessExitTests()
    {
        _machine = new FakeLoadSource(_time, processors: 4);
        _clock = new LoadClock(_time, _machine);
    }

    public void Dispose() => _clock.Dispose();

    [Fact]
    public async Task AGameThatExitsAfterTheGraceInWallTimeButWithinItInLoadAdjustedTimeQuitsCleanly()
    {
        // Three of four processors busy with other work leave a quarter of the machine: 4 s of wall time is 1.75 s adjusted.
        _machine.SetLoad(busyShare: 0.75, ownCores: 0);
        TaskCompletionSource<bool> gone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using LoadDeadline deadline = _clock.Start(Grace, TestContext.Current.CancellationToken);
        Task<bool> waited = ProcessExit.GoneWithinAsync(gone.Task, deadline);

        Run(TimeSpan.FromSeconds(4));
        Assert.False(waited.IsCompleted, $"the wait ended after {deadline.Wall} of wall time, {deadline.Adjusted} adjusted");
        gone.SetResult(true);

        Assert.True(await waited);
        Assert.False(deadline.Expired);
    }

    [Fact]
    public async Task OnAnIdleMachineTheGraceEndsAtItsWallTime()
    {
        TaskCompletionSource<bool> gone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using LoadDeadline deadline = _clock.Start(Grace, TestContext.Current.CancellationToken);
        Task<bool> waited = ProcessExit.GoneWithinAsync(gone.Task, deadline);

        Run(TimeSpan.FromSeconds(3.1));

        Assert.False(await waited);
        Assert.Equal(DeadlineReason.Ceiling, deadline.Reason);
    }

    [Fact]
    public async Task TheWaitOnARealProcessOutlastsTheGraceInWallTimeWhileTheClockHasNotRunItOut()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The child is cmd.exe running ping -n, Windows' syntax.");
        using LoadDeadline deadline = _clock.Start(Grace, TestContext.Current.CancellationToken);
        using Process child = Process.Start(
            new ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true }
        )!;

        // ping -n 5 takes about 4 s of wall time, past the 3 s grace; the stopped clock has not run the grace out.
        Assert.True(await ProcessExit.WaitUntilGoneAsync(child, deadline));
        Assert.False(deadline.Expired);
    }

    private void Run(TimeSpan span)
    {
        for (TimeSpan passed = TimeSpan.Zero; passed < span; passed += Step)
        {
            _time.Advance(Step);
        }
    }
}
