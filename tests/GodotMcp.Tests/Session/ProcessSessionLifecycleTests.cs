using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A process session's start, restart and stop each keep one owner: the session stays starting until the call that marked
/// it returns, a stop during a first start's ready wait ends that start, no program starts once the server's shutdown has
/// begun, and a ready wait the shutdown ended says so. The program waits for the test's file before it prints its ready
/// line, and the clock is fake, so an end waits for its tree with no deadline of its own.
/// </summary>
public sealed partial class ProcessSessionLifecycleTests
{
    private const string Session = "proc";
    private const string Pwsh = "The program is pwsh, run in a kill-on-close job.";

    // A ready line the program prints once the test's file exists, on a timeout no passing run reaches.
    private static readonly ReadyProfile Ready = new("ready", 600_000) { Regex = ReadyLine() };

    [Fact]
    public async Task ASecondRestartDuringAReadyWaitIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Pwsh);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string readyFile = harness.Combine("ready");
        File.WriteAllText(readyFile, "");
        GodotSession session = await harness.Sessions.ReserveProcessAsync(Session, harness.Path, defaultName: false);
        await session.StartFirstProcessAsync(Launch(readyFile, Ready, null), cancellation).WaitAsync(RegistryHarness.HangGuard, cancellation);
        int firstPid = session.ProcessId!.Value;
        File.Delete(readyFile);
        Task<object> restart = harness.Sessions.RestartAsync(Session, prepare: false, cancellationToken: cancellation);
        await RegistryHarness.WaitUntilAsync(() => session.ProcessId != firstPid, restart);

        SessionException second = await Assert.ThrowsAsync<SessionException>(() =>
            harness.Sessions.RestartAsync(Session, prepare: false, cancellationToken: cancellation)
        );
        SessionException reserved = await Assert.ThrowsAsync<SessionException>(() =>
            harness.Sessions.ReserveProcessAsync(Session, harness.Path, defaultName: false)
        );
        File.WriteAllText(readyFile, "");
        ProcessRestartResult restarted = Assert.IsType<ProcessRestartResult>(await restart.WaitAsync(RegistryHarness.HangGuard, cancellation));

        Assert.Equal($"session '{Session}' is still starting or restarting; wait for its call to return, or stop_project it.", second.Message);
        Assert.Equal(
            $"A session named '{Session}' is live on {harness.Path}; stop_project or detach_project it, or pass another session name.",
            reserved.Message
        );
        Assert.Equal(firstPid, restarted.PreviousProcessId);
        Assert.Equal(session.ProcessId, restarted.ProcessId);
        Assert.NotNull(restarted.Ready);
        Assert.False(session.IsStarting);
    }

    [Fact]
    public async Task AStopDuringAFirstStartsReadyWaitEndsTheStart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Pwsh);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        GodotSession session = await harness.Sessions.ReserveProcessAsync(Session, harness.Path, defaultName: false);
        Task start = session.StartFirstProcessAsync(Launch(harness.Combine("never"), Ready, null), cancellation);
        await RegistryHarness.WaitUntilAsync(() => session.ProcessId is not null, start);

        object stopped = await harness
            .Sessions.StopAnyAsync(Session, discard: false, cancellation)
            .WaitAsync(RegistryHarness.HangGuard, cancellation);
        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => start.WaitAsync(RegistryHarness.HangGuard, cancellation));

        Assert.IsType<ProcessStopResult>(stopped);
        Assert.Equal($"The process '{Session}' was stopped by stop_project before a line matched 'ready'.", refused.Message);
        Assert.DoesNotContain(harness.Sessions.List(includeStopped: true), listed => listed.Name == Session);
    }

    [Fact]
    public async Task ARestartWhoseStopOverlapsTheShutdownStartsNoProgram()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Pwsh);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        ProcessPrepResult prepared = new(["prep"], 0, 0, harness.Combine("prep.log"));
        TaskCompletionSource<ProcessPrepResult> held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource inPrep = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int preps = 0;
        // The first start's prep returns at once; the restart's is held until the test lets it go.
        Task<ProcessPrepResult> Prep(CancellationToken _)
        {
            if (Interlocked.Increment(ref preps) == 1)
            {
                return Task.FromResult(prepared);
            }

            inPrep.TrySetResult();
            return held.Task;
        }

        GodotSession session = await harness.Sessions.ReserveProcessAsync(Session, harness.Path, defaultName: false);
        await session
            .StartFirstProcessAsync(Launch(harness.Combine("never"), null, Prep), cancellation)
            .WaitAsync(RegistryHarness.HangGuard, cancellation);
        int firstPid = session.ProcessId!.Value;
        Task<object> restart = harness.Sessions.RestartAsync(Session, prepare: false, cancellationToken: cancellation);
        await inPrep.Task.WaitAsync(RegistryHarness.HangGuard, cancellation);
        var shutdown = Task.Run(harness.Sessions.Shutdown, cancellation);
        await RegistryHarness.WaitUntilAsync(() => ShutdownBegun(harness), shutdown);

        held.SetResult(prepared);
        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => restart.WaitAsync(RegistryHarness.HangGuard, cancellation));
        await shutdown.WaitAsync(RegistryHarness.HangGuard, cancellation);

        Assert.Equal(SessionRegistry.ShuttingDown, refused.Message);
        Assert.Equal(firstPid, session.ProcessId);
    }

    [Fact]
    public async Task AShutdownDuringAReadyWaitSaysTheServerIsShuttingDown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Pwsh);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        GodotSession session = await harness.Sessions.ReserveProcessAsync(Session, harness.Path, defaultName: false);
        Task start = session.StartFirstProcessAsync(Launch(harness.Combine("never"), Ready, null), cancellation);
        await RegistryHarness.WaitUntilAsync(() => session.ProcessId is not null, start);

        var shutdown = Task.Run(harness.Sessions.Shutdown, cancellation);
        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => start.WaitAsync(RegistryHarness.HangGuard, cancellation));
        await shutdown.WaitAsync(RegistryHarness.HangGuard, cancellation);

        Assert.Equal(SessionRegistry.ShuttingDown, refused.Message);
    }

    /// <summary>
    /// The session's launch: pwsh waiting until <paramref name="readyFile"/> exists, then printing "ready" and sleeping until its
    /// job ends it.
    /// </summary>
    private static GodotSession.ProcessLaunch Launch(string readyFile, ReadyProfile? ready, Func<CancellationToken, Task<ProcessPrepResult>>? prep)
    {
        string script = $"while (-not (Test-Path '{readyFile}')) {{ Start-Sleep -Milliseconds 20 }}; 'ready'; [System.Threading.Thread]::Sleep(-1)";
        string[] command = ["pwsh", "-NoProfile", "-NonInteractive", "-Command", script];
        ProcessProfile profile = new(command, null, new Dictionary<string, string>(), ready, null);
        return new GodotSession.ProcessLaunch(Session, profile, command, ready, prep);
    }

    /// <summary>Whether the registry's shutdown has begun, as a start's own check sees it.</summary>
    private static bool ShutdownBegun(RegistryHarness harness)
    {
        try
        {
            harness.Sessions.RefuseStartAtShutdown();
            return false;
        }
        catch (SessionException)
        {
            return true;
        }
    }

    [GeneratedRegex("^ready$")]
    private static partial Regex ReadyLine();
}
