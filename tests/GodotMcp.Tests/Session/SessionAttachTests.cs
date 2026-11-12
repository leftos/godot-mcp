using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

/// <summary>Attaching, detaching and restarting a session, and the folder's prep lock around them.</summary>
public sealed class SessionAttachTests : IAsyncDisposable
{
    // OverrideFolders.RetryBudgetMs: how long, in load-adjusted time, a refused open of the list is retried before the work
    // goes ahead without it.
    private static readonly TimeSpan ListRetryBudget = TimeSpan.FromSeconds(2);

    private readonly RegistryHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    /// <summary>
    /// Advances <paramref name="time"/> 100 ms at a time, pausing 5 ms of real time after each step, until
    /// <paramref name="task"/> completes or 10 s of fake time have passed.
    /// </summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider time, Task task, CancellationToken cancellationToken)
    {
        var step = TimeSpan.FromMilliseconds(100);
        for (TimeSpan passed = TimeSpan.Zero; passed < TimeSpan.FromSeconds(10) && !task.IsCompleted; passed += step)
        {
            time.Advance(step);
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }

    [Fact]
    public async Task AnAttachThatTimesOutLeavesNoSessionAndNoOverride()
    {
        string alpha = _harness.Project("alpha");

        await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(
                new AttachRequest(alpha, null, TimeSpan.FromSeconds(1), false, false, null),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task DetachingOneSessionLeavesAnotherAttachsFileInPlace()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        string attachFile = AttachFile.PathIn(alpha);
        Task<AttachResult> first = _harness.Sessions.AttachAsync(
            new AttachRequest(alpha, "first", RegistryHarness.LongWait, false, false, null),
            cancellation
        );
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile));
        string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
        using FakeBridge game = await FakeBridge.DialAsync(_harness.Listener.Port, token, alpha, cancellation);
        await first;

        await _harness.StartWaitingAttachAsync(alpha, "second");
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile));
        DetachResult detached = await _harness.Sessions.DetachAsync("first", cancellation);

        Assert.False(detached.OverrideRemoved);
        Assert.True(File.Exists(attachFile));
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
    }

    // Another server holding the folder list stands for its read-modify-write of the same override.cfg: the detach must wait
    // for it, not delete the file under it. The hold stays well inside the list's 2 s retry budget.
    [Fact]
    public async Task ADetachWaitsForTheFolderListBeforeReleasingTheOverride()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        Task<DetachResult> detach;

        using (new FileStream(_harness.Combine("override-folders.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            detach = Task.Run(() => _harness.Sessions.DetachAsync("server", cancellation), cancellation);
            await Task.Delay(300, cancellation);

            Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
            Assert.False(detach.IsCompleted);
        }

        DetachResult detached = await detach;
        Assert.True(detached.OverrideRemoved);
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    // An attached session has no run, so any run's exit is the exit of a run that is not its current one, as the old run's is
    // once a restart has replaced it. A guard of "_run is null" would pass this too: the real case, an old run exiting while
    // the session's current run is a newer one, needs a launched Godot to make that newer run, so it is covered by the
    // integration test RestartTests.TheOverrideSurvivesTheRestartAndGoesWithTheStop, not here.
    [Fact]
    public async Task AReplacedRunsExitLeavesTheOverrideAndTheFolderReserved()
    {
        string alpha = _harness.Project("alpha");
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        GodotSession session = _harness.Sessions.Resolve("server");
        using Process neverStarted = new();

        await session.OnRunExitedAsync(new GodotRun(alpha, neverStarted, previous: null));

        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
        Assert.True(session.IsLive);
    }

    [Fact]
    public async Task AnAttachedSessionKeepsTheHellosWindowHandle()
    {
        string alpha = _harness.Project("alpha");
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello { WindowHandle = 1_311_768L });

        Assert.Equal(1_311_768L, _harness.Sessions.Resolve("server").WindowHandle);
    }

    // The launch is parked on the folder's prep lock, which the test holds, so the session stays starting. It takes the lock
    // before it looks Godot up, so this needs no Godot installed.
    [Fact]
    public async Task RestartingASessionStillStartingIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        SemaphoreSlim folderLock = _harness.Sessions.PrepLock(alpha);
        await folderLock.WaitAsync(cancellation);
        using CancellationTokenSource cancelLaunch = new();
        LaunchRequest request = new(alpha, null, [], [], true, false, Prepare: true);
        Task<LaunchResult> launch = _harness.Sessions.LaunchAsync(request, "server", cancelLaunch.Token);
        try
        {
            await RegistryHarness.WaitUntilAsync(() => _harness.Sessions.List(includeStopped: true).Any(session => session.Name == "server"));

            SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
                _harness.Sessions.RestartAsync("server", prepare: true, cancellation)
            );

            Assert.Equal("session 'server' is still starting or restarting; wait for its call to return, or stop_project it.", refused.Message);
        }
        finally
        {
            await cancelLaunch.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch);
            folderLock.Release();
        }
    }

    [Fact]
    public async Task StoppingAnAttachedGameWithNoPidAsksItToQuitAndWarnsItCouldNotBeKilled()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        Task<StopResult> stop = _harness.Sessions.StopAsync("server", cancellation);
        string? command;
        using (game)
        {
            command = await game.AnswerOneAsync("quit", cancellation);
        }

        StopResult stopped = await stop;

        Assert.Equal("shutdown", command);
        Assert.False(stopped.Killed);
        Assert.False(stopped.AlreadyExited);
        Assert.Null(stopped.ExitCode);
        Assert.Null(stopped.GameExitCode);
        Assert.Null(stopped.KillReason);
        Assert.Null(stopped.QuitMs);
        Assert.Equal(
            "The game's bridge sent no process id, so the game could not be killed if it did not quit; it may still be running.",
            stopped.Warning
        );
        Assert.True(stopped.OverrideRemoved);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    // The bridge stays connected after it answers the quit, so the stop's wait for its connection to close runs out the whole
    // grace and the warning gains the clause that its connection was still open then.
    [Fact]
    public async Task AStopOfAnAttachedGameWithNoProcessIdWhoseConnectionStaysOpenSaysSo()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        Task<StopResult> stop = harness.Sessions.StopAsync("server", cancellation);
        await game.AnswerOneAsync("quit", cancellation);

        // The grace starts once the quit is acknowledged; run it out with the connection still open.
        await AdvanceUntilAsync(time, stop, cancellation);

        StopResult stopped = await stop;

        Assert.False(stopped.Killed);
        Assert.Null(stopped.KillReason);
        Assert.NotNull(stopped.Warning);
        Assert.StartsWith(
            "The game's bridge sent no process id, so the game could not be killed if it did not quit; it may still be running; "
                + "its connection was still open after 3 s of load-adjusted time (wall ",
            stopped.Warning,
            StringComparison.Ordinal
        );
        Assert.EndsWith("on average).", stopped.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingAnAttachedGameThatHasGoneSaysItHadAlreadyExited()
    {
        string alpha = _harness.Project("alpha");
        await _harness.EndAttachedGameAsync(alpha, "server");

        StopResult stopped = await _harness.Sessions.StopAsync("server", TestContext.Current.CancellationToken);

        Assert.True(stopped.AlreadyExited);
        Assert.False(stopped.Killed);
        Assert.Null(stopped.ExitCode);
        Assert.Null(stopped.GameExitCode);
        Assert.Null(stopped.KillReason);
        Assert.Null(stopped.QuitMs);
        Assert.Null(stopped.Warning);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task StoppingASilentAttachedGameKillsItsProcess()
    {
        string alpha = _harness.Project("alpha");
        Process child = _harness.StartOwnedGame();
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello { ProcessId = child.Id });

        StopResult stopped = await _harness.Sessions.StopAsync("server", TestContext.Current.CancellationToken);

        Assert.True(stopped.Killed);
        Assert.Equal(GameKillReason.Silent, stopped.KillReason);
        Assert.Null(stopped.ExitCode);
        Assert.Null(stopped.GameExitCode);
        Assert.True(child.HasExited);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    [Fact]
    public async Task StoppingAnAttachedGameThatDoesNotExitKillsItAfterTheGrace()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        Process child = _harness.StartOwnedGame();
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello { ProcessId = child.Id });
        Task<StopResult> stop = _harness.Sessions.StopAsync("server", cancellation);

        string? ping = await game.AnswerOneAsync("pong", cancellation);
        string? quit = await game.AnswerOneAsync("quit", cancellation);
        StopResult stopped = await stop;

        Assert.Equal(["ping", "shutdown"], [ping, quit]);
        Assert.True(stopped.Killed);
        Assert.NotNull(stopped.KillReason);
        Assert.StartsWith("the game acknowledged the quit but was still shutting down after ", stopped.KillReason, StringComparison.Ordinal);
        // The shared clock reads the real machine's load, so on a busy machine the grace's backstop ends it instead.
        Assert.Matches(@"after (3 s of load-adjusted time|\d+(\.\d+)? s of wall time, 5 x its 3 s grace) \(", stopped.KillReason);
        Assert.Contains(
            $"the bridge's connection was still open, so the scene tree had not been freed.\nProcess {child.Id}: ",
            stopped.KillReason,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("Last stderr lines", stopped.KillReason, StringComparison.Ordinal);
        Assert.Null(stopped.GameExitCode);
        Assert.True(child.HasExited);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    [Fact]
    public async Task AnAttachedGameThatExitsWhileItsStateIsSampledAfterTheGraceIsNotKilled()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        Process child = harness.StartOwnedGame();
        // Ending the game inside the sample the stop takes before killing it puts the exit in the sample by construction.
        harness.Sessions.DescribeGameProcess = _ =>
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            return Task.FromResult("sampled");
        };
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello { ProcessId = child.Id });
        Task<StopResult> stop = harness.Sessions.StopAsync("server", cancellation);
        await game.AnswerOneAsync("pong", cancellation);
        await game.AnswerOneAsync("quit", cancellation);

        // The grace starts once the quit is acknowledged; run it out, and the sample that follows finds the game gone.
        await AdvanceUntilAsync(time, stop, cancellation);

        StopResult stopped = await stop;

        Assert.False(stopped.Killed);
        Assert.Null(stopped.KillReason);
        Assert.NotNull(stopped.QuitMs);
        Assert.True(child.HasExited);
    }

    // The stand-in's exit is held by the test's own debugger until the stop has returned, so the killed game is still not gone
    // when the stop's wait ends, however busy the machine is: the stop must have spent its load-adjusted wait and say so, after
    // the debugger's own warning, which the hold also makes true.
    [Fact]
    public async Task AStopWhoseKilledGameIsNotGoneWhenItsWaitEndsSaysItIsStillRunning()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        Process child = harness.StartOwnedGame();
        using ExitHold hold = await ExitHold.AttachAsync(child, RegistryHarness.HangGuard);
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello { ProcessId = child.Id });

        Task<StopResult> stop = harness.Sessions.StopAsync("server", cancellation);
        await RegistryHarness.AdvanceUntilDoneAsync(time, stop, cancellation);
        StopResult stopped = await stop.WaitAsync(RegistryHarness.HangGuard, cancellation);
        bool goneWhenTheStopReturned = child.WaitForExit(0);
        hold.Release();

        Assert.False(goneWhenTheStopReturned);
        Assert.True(stopped.Killed);
        Assert.Equal(GameKillReason.Silent, stopped.KillReason);
        Assert.Null(stopped.GameExitCode);
        Assert.StartsWith(
            $"A debugger was attached to the game (pid {child.Id}); its debug session ended with the game. "
                + $"The killed game (pid {child.Id}) was still running after 5 s of load-adjusted time (wall ",
            stopped.Warning,
            StringComparison.Ordinal
        );
        Assert.EndsWith("; its project folder stays in use until it is gone.", stopped.Warning, StringComparison.Ordinal);
    }

    // The grace has run out when the game is killed: a kill wait on the grace's deadline would end at once, so a warning that
    // reports a 5 s wait proves the kill waited on a deadline of its own.
    [Fact]
    public async Task AGraceKillWhoseGameIsNotGoneWaitsItsOwnKillWaitAndSaysSo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        Process child = harness.StartOwnedGame();
        harness.Sessions.DescribeGameProcess = _ => Task.FromResult("sampled");
        using ExitHold hold = await ExitHold.AttachAsync(child, RegistryHarness.HangGuard);
        using FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello { ProcessId = child.Id });
        Task<StopResult> stop = harness.Sessions.StopAsync("server", cancellation);
        await game.AnswerOneAsync("pong", cancellation);
        await game.AnswerOneAsync("quit", cancellation);

        await RegistryHarness.AdvanceUntilDoneAsync(time, stop, cancellation);
        StopResult stopped = await stop.WaitAsync(RegistryHarness.HangGuard, cancellation);
        bool goneWhenTheStopReturned = child.WaitForExit(0);
        hold.Release();

        Assert.False(goneWhenTheStopReturned);
        Assert.True(stopped.Killed);
        Assert.StartsWith("the game acknowledged the quit but was still shutting down after ", stopped.KillReason, StringComparison.Ordinal);
        Assert.Contains(
            $"The killed game (pid {child.Id}) was still running after 5 s of load-adjusted time (wall ",
            stopped.Warning,
            StringComparison.Ordinal
        );
    }

    // A game that acknowledged the quit and then closed its bridge has freed its scene tree and is in the engine's own teardown,
    // measured to take up to about 3 s on an idle machine: the stop gives it the teardown grace past the quit grace before a kill.
    [Fact]
    public async Task AGameThatClosedItsBridgeAfterTheQuitIsKilledOnlyAfterTheTeardownGrace()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        Process child = harness.StartOwnedGame();
        harness.Sessions.DescribeGameProcess = _ => Task.FromResult("sampled");
        FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello { ProcessId = child.Id });
        Task<StopResult> stop = await CloseBridgeAfterTheQuitAsync(harness, game, cancellation);

        await RegistryHarness.AdvanceUntilDoneAsync(time, stop, cancellation);
        StopResult stopped = await stop.WaitAsync(RegistryHarness.HangGuard, cancellation);

        Assert.True(stopped.Killed);
        Assert.StartsWith(
            "the game acknowledged the quit but was still shutting down after 10 s of load-adjusted time",
            stopped.KillReason,
            StringComparison.Ordinal
        );
        Assert.True(child.HasExited);
    }

    // The fake clock stands still at 5 s while the game ends, between the 3 s quit grace and the 10 s the teardown grace runs to,
    // so only the game's own exit can end the stop's wait.
    [Fact]
    public async Task AGameThatClosedItsBridgeAndExitsInTheTeardownGraceIsNotKilled()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        Process child = harness.StartOwnedGame();
        harness.Sessions.DescribeGameProcess = _ => Task.FromResult("sampled");
        FakeBridge game = await harness.AttachFakeGameAsync(alpha, "server", new FakeHello { ProcessId = child.Id });
        Task<StopResult> stop = await CloseBridgeAfterTheQuitAsync(harness, game, cancellation);

        for (int step = 0; step < 50 && !stop.IsCompleted; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellation);
        }

        child.Kill(entireProcessTree: true);
        Assert.True(child.WaitForExit(RegistryHarness.HangGuard));
        StopResult stopped = await stop.WaitAsync(RegistryHarness.HangGuard, cancellation);

        Assert.False(stopped.Killed);
        Assert.Null(stopped.KillReason);
        Assert.NotNull(stopped.QuitMs);
    }

    /// <summary>
    /// Starts a stop of the attached session "server", answers its ping and its quit, then closes the game's end of the bridge
    /// and waits, with no fake time passing, until the session's connection has seen the close.
    /// </summary>
    private static async Task<Task<StopResult>> CloseBridgeAfterTheQuitAsync(RegistryHarness harness, FakeBridge game, CancellationToken cancellation)
    {
        Task closed = harness.Sessions.Resolve("server").AttachedBridgeClosed ?? throw new InvalidOperationException("no attached bridge");
        Task<StopResult> stop = harness.Sessions.StopAsync("server", cancellation);
        await game.AnswerOneAsync("pong", cancellation);
        await game.AnswerOneAsync("quit", cancellation);
        game.Dispose();
        await closed.WaitAsync(RegistryHarness.HangGuard, cancellation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Assert.True(closed.IsCompleted, $"the session's bridge connection did not close within its {RegistryHarness.HangGuard} hang guard");
        return stop;
    }

    [Fact]
    public async Task RestartingAnAttachedSessionIsRefused()
    {
        string alpha = _harness.Project("alpha");
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.RestartAsync("server", prepare: true, TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            "session 'server' is attached, not started by run_project, so it cannot be restarted; detach_project, then start the game "
                + "again yourself.",
            refused.Message
        );
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task RestartingWithNoSessionOrAnUnknownNameIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        SessionException none = await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.RestartAsync(null, prepare: true, cancellation));
        SessionException unknown = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.RestartAsync("client", prepare: true, cancellation)
        );

        Assert.Equal("No Godot session is running; start one with run_project or attach_project.", none.Message);
        Assert.Equal("No session named 'client'. Live sessions: none.", unknown.Message);
    }

    [Fact]
    public async Task RestartsPrepareTakesAutoOrNever()
    {
        ProjectTools tools = new(_harness.Sessions);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            tools.RestartProjectAsync(new RestartOptions("sometimes"), cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal("prepare takes \"auto\" or \"never\"; got \"sometimes\".", refused.Message);
        Assert.True(new RestartOptions().ShouldPrepare());
        Assert.True(new RestartOptions("auto").ShouldPrepare());
        Assert.False(new RestartOptions("never").ShouldPrepare());
    }

    [Fact]
    public async Task AHeadlessRunLeavesALiveSessionsOverrideInPlace()
    {
        string alpha = _harness.Project("alpha");
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());

        HeadlessRunner.ClearFolder(_harness.Sessions, alpha);

        Assert.True(OverrideFile.IsOurs(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public void AHeadlessRunRemovesAMarkedOverrideNoLiveSessionHolds()
    {
        string alpha = _harness.Project("alpha");
        OverrideOwner dead = new(Environment.ProcessId, OverrideOwner.Current.StartTicks - 1);
        File.WriteAllText(
            OverrideFile.PathIn(alpha),
            $"{OverrideFile.Marker}\n{OverrideFile.OwnersPrefix}{dead}\n[autoload]\n\nGodotMcpBridge=\"*D:/tools/bridge.gd\"\n"
        );

        HeadlessRunner.ClearFolder(_harness.Sessions, alpha);

        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task AnAttachWritesItsOverrideOnlyOnceThePrepLockIsFree()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        string overrideFile = OverrideFile.PathIn(alpha);
        SemaphoreSlim folderLock = _harness.Sessions.PrepLock(alpha);
        await folderLock.WaitAsync(cancellation);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<AttachResult> attach;
        try
        {
            attach = _harness.Sessions.AttachAsync(new AttachRequest(alpha, "server", RegistryHarness.LongWait, false, false, null), cancel.Token);
            await RegistryHarness.WaitUntilAsync(() => File.Exists(AttachFile.PathIn(alpha)), attach);
            Assert.False(File.Exists(overrideFile));
        }
        finally
        {
            folderLock.Release();
        }

        await RegistryHarness.WaitUntilAsync(() => File.Exists(overrideFile), attach);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attach);
    }

    // The wait's deadline ends the call even while a prep holds the lock: the attach file goes at once, and the override,
    // which no game can use any more, is never written.
    [Fact]
    public async Task AnAttachWhoseWaitEndsWhileThePrepLockIsHeldWritesNoOverride()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        SemaphoreSlim folderLock = _harness.Sessions.PrepLock(alpha);
        await folderLock.WaitAsync(cancellation);
        try
        {
            SessionException timedOut = await Assert.ThrowsAsync<SessionException>(() =>
                _harness.Sessions.AttachAsync(new AttachRequest(alpha, "server", TimeSpan.FromSeconds(2), false, false, null), cancellation)
            );

            Assert.False(File.Exists(AttachFile.PathIn(alpha)));
            Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
            Assert.StartsWith($"No game on {alpha} connected within 2 s", timedOut.Message);
            Assert.Contains(
                "a prep held the folder for the whole wait, so override.cfg was never written",
                timedOut.Message,
                StringComparison.Ordinal
            );
        }
        finally
        {
            folderLock.Release();
        }
    }

    // A game handed over while the attach still waits on the prep lock belongs to the attach: when the attach then fails, it
    // closes the game's connection instead of leaving the game connected to nothing.
    [Fact]
    public async Task AGameHandedOverToAnAttachThatThenFailsIsClosed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // A completed attach elsewhere starts the listener's accept loop, as a server that has served a session before has it.
        using FakeBridge other = await _harness.AttachFakeGameAsync(_harness.Project("beta"), "other", new FakeHello());
        string alpha = _harness.Project("alpha");
        string attachFile = AttachFile.PathIn(alpha);
        TaskCompletionSource<bool> judged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only alpha's own hello: beta's, or a late verdict on another attach's, carries another token.
        string? alphaToken = null;
        void onJudged(string? judgedToken, bool handedOver)
        {
            if (judgedToken is not null && judgedToken == Volatile.Read(ref alphaToken))
            {
                judged.TrySetResult(handedOver);
            }
        }
        _harness.Listener.HelloJudged += onJudged;
        SemaphoreSlim folderLock = _harness.Sessions.PrepLock(alpha);
        await folderLock.WaitAsync(cancellation);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        FakeBridge game;
        bool handedOver;
        try
        {
            Task<AttachResult> attach = _harness.Sessions.AttachAsync(
                new AttachRequest(alpha, "server", RegistryHarness.LongWait, false, false, null),
                cancel.Token
            );
            await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile), attach);
            string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
            Volatile.Write(ref alphaToken, token);
            game = await FakeBridge.DialAsync(_harness.Listener.Port, token, alpha, new FakeHello(), cancellation);
            handedOver = await judged.Task.WaitAsync(RegistryHarness.HangGuard, cancellation);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attach);
        }
        finally
        {
            folderLock.Release();
            _harness.Listener.HelloJudged -= onJudged;
        }

        using (game)
        {
            Assert.True(handedOver, "the hello sent while the attach waited on the prep lock was refused");
            Assert.True(await game.IsClosedByServerAsync(RegistryHarness.HangGuard), "the failed attach left the game connected");
        }
    }

    // A game that boots while the attach waits on the prep lock reads the attach file and dials before the override is written:
    // the listener refuses a hello whose token no session awaits, so the attach must await the token before it publishes it.
    [Fact]
    public async Task AGameThatDialsAsSoonAsTheAttachFileAppearsIsAccepted()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // A completed attach elsewhere starts the listener's accept loop, as a server that has served a session before has it.
        using FakeBridge other = await _harness.AttachFakeGameAsync(_harness.Project("beta"), "other", new FakeHello());
        string alpha = _harness.Project("alpha");
        string attachFile = AttachFile.PathIn(alpha);
        TaskCompletionSource<bool> judged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only alpha's own hello: beta's, or a late verdict on another attach's, carries another token.
        string? alphaToken = null;
        void onJudged(string? judgedToken, bool handedOver)
        {
            if (judgedToken is not null && judgedToken == Volatile.Read(ref alphaToken))
            {
                judged.TrySetResult(handedOver);
            }
        }
        _harness.Listener.HelloJudged += onJudged;
        SemaphoreSlim folderLock = _harness.Sessions.PrepLock(alpha);
        await folderLock.WaitAsync(cancellation);
        Task<AttachResult> attach;
        FakeBridge game;
        bool handedOver;
        try
        {
            attach = _harness.Sessions.AttachAsync(new AttachRequest(alpha, "server", RegistryHarness.LongWait, false, false, null), cancellation);
            await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile), attach);
            string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
            Volatile.Write(ref alphaToken, token);
            game = await FakeBridge.DialAsync(_harness.Listener.Port, token, alpha, new FakeHello(), cancellation);
            handedOver = await judged.Task.WaitAsync(RegistryHarness.HangGuard, cancellation);
        }
        finally
        {
            folderLock.Release();
            _harness.Listener.HelloJudged -= onJudged;
        }

        using (game)
        {
            Assert.True(handedOver, "the hello sent while the attach waited on the prep lock was refused");
            AttachResult attached = await attach.WaitAsync(RegistryHarness.HangGuard, cancellation);
            Assert.NotNull(attached);
        }
    }

    [Fact]
    public async Task AWaitingAttachIsNotAGameRunningOnItsFolder()
    {
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(alpha, "server");

        Assert.Empty(_harness.Sessions.RunningSessionNames(alpha, except: null));
    }

    [Fact]
    public async Task APidThatIsDormantIsJoinedThroughItsJoinFile()
    {
        string alpha = _harness.Project("alpha");
        int pid = _harness.StartOwnedGame().Id;
        RegistryHarness.WriteDormant(alpha, 4101);
        RegistryHarness.WriteDormant(alpha, pid);

        (AttachResult result, FakeBridge game) = await _harness.JoinFakeGameAsync(alpha, "joined", pid: pid, joinedPid: pid);
        using FakeBridge joined = game;

        Assert.Equal(pid, result.JoinedPid);
        Assert.Equal(pid, _harness.Sessions.Resolve("joined").GameProcessId);
        Assert.False(File.Exists(DormantGames.JoinPathIn(alpha, pid)));
        Assert.False(File.Exists(DormantGames.JoinPathIn(alpha, 4101)));
        Assert.False(File.Exists(AttachFile.PathIn(alpha)));
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task APidThatIsNotDormantIsRefusedListingTheDormantGames()
    {
        string alpha = _harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4101);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(
                new AttachRequest(alpha, null, RegistryHarness.LongWait, false, false, 999),
                TestContext.Current.CancellationToken
            )
        );

        Assert.StartsWith($"No dormant game with pid 999 waits on {alpha}. The dormant games on {alpha} are: pid 4101, started ", refused.Message);
        Assert.EndsWith(
            "A game started before arm_project, or on a folder that is not armed, has no bridge to join: relaunch it while the folder is armed.",
            refused.Message
        );
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task APidOnAFolderWithNoDormantGameSaysThereAreNone()
    {
        string alpha = _harness.Project("alpha");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(
                new AttachRequest(alpha, null, RegistryHarness.LongWait, false, false, 999),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains($"No dormant game waits on {alpha}.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOnlyDormantGameIsJoinedWithoutAPid()
    {
        string alpha = _harness.Project("alpha");
        int pid = _harness.StartOwnedGame().Id;
        RegistryHarness.WriteDormant(alpha, pid);

        (AttachResult result, FakeBridge game) = await _harness.JoinFakeGameAsync(alpha, "joined", pid: null, joinedPid: pid);
        using FakeBridge joined = game;

        Assert.Equal(pid, result.JoinedPid);
        Assert.False(File.Exists(AttachFile.PathIn(alpha)));
    }

    [Fact]
    public async Task SeveralDormantGamesWithoutAPidAreRefusedListingEach()
    {
        string alpha = _harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4102);
        RegistryHarness.WriteDormant(alpha, 4101);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(
                new AttachRequest(alpha, null, RegistryHarness.LongWait, false, false, null),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Matches(
            $"^2 dormant games wait on {Regex.Escape(alpha)}: pid 4101, started [0-9TZ:.-]+; pid 4102, started [0-9TZ:.-]+\\. "
                + "Pass options\\.pid to choose one\\.$",
            refused.Message
        );
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    [Fact]
    public async Task NoDormantGameMeansTheAttachWaitsForALaunch()
    {
        string alpha = _harness.Project("alpha");

        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(AttachFile.PathIn(alpha))!, "join-*.json"));
        Assert.False(File.Exists(AttachFile.PathIn(alpha)));
        Assert.Equal("server", Assert.Single(_harness.Sessions.List(includeStopped: false)).Name);
    }

    [Fact]
    public async Task AJoinThatIsNeverAnsweredRemovesItsJoinFileAndSaysTheGameDidNotAnswer()
    {
        string alpha = _harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4101);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(
                new AttachRequest(alpha, null, TimeSpan.FromSeconds(1), false, false, null),
                TestContext.Current.CancellationToken
            )
        );

        Assert.StartsWith($"The dormant game (pid 4101) on {alpha} did not answer within 1 s", refused.Message);
        Assert.False(File.Exists(DormantGames.JoinPathIn(alpha, 4101)));
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    // A plain attach on the folder can take the dormant game a join targets: the join's timeout then says the game is no
    // longer dormant, not that it may be frozen.
    [Fact]
    public async Task AJoinWhoseGameIsNoLongerDormantSaysAnotherAttachTookIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // On fake time, so the deadline ends only once the entry is gone, and never as a backstop.
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4101);

        Task<AttachResult> join = harness.Sessions.AttachAsync(
            new AttachRequest(alpha, null, TimeSpan.FromSeconds(1), false, false, null),
            cancellation
        );
        await RegistryHarness.WaitUntilAsync(() => File.Exists(DormantGames.JoinPathIn(alpha, 4101)), join);
        File.Delete(DormantGames.EntryPathIn(alpha, 4101));
        await RegistryHarness.AdvanceUntilDoneAsync(time, join, cancellation);
        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => join);

        Assert.Equal(
            $"The dormant game (pid 4101) on {alpha} did not answer within 1 s and is no longer dormant: another attach_project took "
                + "it, or it quit. The join is abandoned and its join file is removed; call attach_project again.",
            refused.Message
        );
    }

    // The join's wait starts with the call, not with the prep lock: a prep holding the folder for the whole wait ends the call
    // at its deadline, with nothing written and the game still dormant.
    [Fact]
    public async Task AJoinWhoseWaitEndsWhileThePrepLockIsHeldWritesNoJoinFile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4101);
        SemaphoreSlim folderLock = harness.Sessions.PrepLock(alpha);
        await folderLock.WaitAsync(cancellation);
        Task<AttachResult> join = harness.Sessions.AttachAsync(
            new AttachRequest(alpha, null, TimeSpan.FromSeconds(1), false, false, null),
            cancellation
        );
        try
        {
            await RegistryHarness.AdvanceUntilDoneAsync(time, join, cancellation);
        }
        finally
        {
            folderLock.Release();
        }

        SessionException timedOut = await Assert.ThrowsAsync<SessionException>(() => join);
        Assert.Equal(
            $"The dormant game (pid 4101) on {alpha} was not joined within 1 s: a prep held the folder for the whole wait, so the "
                + "join file was never written and the game is still dormant. Call attach_project again once the prep is done.",
            timedOut.Message
        );
        Assert.False(File.Exists(DormantGames.JoinPathIn(alpha, 4101)));
        Assert.True(File.Exists(DormantGames.EntryPathIn(alpha, 4101)));
        Assert.Empty(harness.Sessions.List(includeStopped: true));
    }

    // Another server holds the folder list for the whole call. The override's write retries the list for its 2 s budget and
    // then goes ahead without it, so the join file's hold starts at about 2 s and retries until about 4 s: the 3 s deadline ends
    // the join inside that retry, which must stop it with nothing written, not give up at 4 s and write the file then.
    [Fact]
    public async Task AJoinWhoseDeadlineEndsWhileTheFolderListIsHeldWritesNoJoinFile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4101);
        string joinFile = DormantGames.JoinPathIn(alpha, 4101);
        using FolderEvents events = new(Path.GetDirectoryName(joinFile)!);
        using FileStream list = HoldFolderList(harness);
        bool overrideRefused = false;
        bool joinFileRefused = false;
        harness.Sessions.OverrideFolders.OpenRefused += purpose =>
        {
            Volatile.Write(ref overrideRefused, overrideRefused || purpose.StartsWith("recording ", StringComparison.Ordinal));
            Volatile.Write(ref joinFileRefused, joinFileRefused || purpose.StartsWith("writing the join file in ", StringComparison.Ordinal));
        };

        // Run off the test's thread: the list's retries block the caller until fake time passes.
        Task<AttachResult> join = Task.Run(
            () => harness.Sessions.AttachAsync(new AttachRequest(alpha, null, TimeSpan.FromSeconds(3), false, false, null), cancellation),
            cancellation
        );
        // The override's write is already retrying the refused list: fake time stands still, so the 2 s budget whose end this
        // test is about cannot run out before the advance below starts it.
        await RegistryHarness.WaitUntilAsync(() => Volatile.Read(ref overrideRefused), join);
        // Past that budget and short of the 3 s deadline, so the override goes ahead and the join file's hold, retrying for its
        // own 2 s, starts now: the deadline then ends the join from inside that hold, never before it started.
        time.Advance(TimeSpan.FromMilliseconds(2100));
        await RegistryHarness.WaitUntilAsync(() => Volatile.Read(ref joinFileRefused), join);
        await RegistryHarness.AdvanceUntilDoneAsync(time, join, cancellation);

        SessionException timedOut = await Assert.ThrowsAsync<SessionException>(() => join);
        Assert.Equal(
            $"The dormant game (pid 4101) on {alpha} was not joined within 3 s: another session held the folder list "
                + "(override-folders.txt) for the whole wait, so the join file was never written and the game is still dormant. "
                + "Call attach_project again.",
            timedOut.Message
        );
        AssertNeverWritten(await events.SeenAsync(), joinFile);
        Assert.True(File.Exists(DormantGames.EntryPathIn(alpha, 4101)));
    }

    // The attach file is written under the folder list's hold before the override: a list another server holds past the 1 s
    // deadline must end the attach at its deadline with no attach file, not write one once the list's 2 s retry gives up.
    [Fact]
    public async Task AnAttachWhoseDeadlineEndsWhileTheFolderListIsHeldWritesNoAttachFile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        string attachFile = AttachFile.PathIn(alpha);
        Directory.CreateDirectory(Path.GetDirectoryName(attachFile)!);
        using FolderEvents events = new(Path.GetDirectoryName(attachFile)!);
        using FileStream list = HoldFolderList(harness);
        bool attachFileRefused = false;
        harness.Sessions.OverrideFolders.OpenRefused += purpose =>
            Volatile.Write(ref attachFileRefused, attachFileRefused || purpose.StartsWith("writing the attach file in ", StringComparison.Ordinal));

        Task<AttachResult> attach = Task.Run(
            () => harness.Sessions.AttachAsync(new AttachRequest(alpha, null, TimeSpan.FromSeconds(1), false, false, null), cancellation),
            cancellation
        );
        // The attach file's hold is already retrying the refused list: fake time stands still, so the 1 s deadline cannot end the
        // attach before the hold it is meant to cut has started, however slow the machine is.
        await RegistryHarness.WaitUntilAsync(() => Volatile.Read(ref attachFileRefused), attach);
        await RegistryHarness.AdvanceUntilDoneAsync(time, attach, cancellation);

        SessionException timedOut = await Assert.ThrowsAsync<SessionException>(() => attach);
        Assert.Equal(
            $"No game on {alpha} connected within 1 s, so the attach is abandoned: another session held the folder list "
                + "(override-folders.txt) for the whole wait, so neither the attach file nor override.cfg was written. Call "
                + "attach_project again.",
            timedOut.Message
        );
        AssertNeverWritten(await events.SeenAsync(), attachFile);
        Assert.Empty(harness.Sessions.List(includeStopped: true));
    }

    // The caller's cancel reaches an attach whose attach file waits on the folder list another server holds: the retry stops at
    // once and nothing is written.
    [Fact]
    public async Task ACancelledAttachWhileTheFolderListIsHeldWritesNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        string attachFile = AttachFile.PathIn(alpha);
        Directory.CreateDirectory(Path.GetDirectoryName(attachFile)!);
        using FolderEvents events = new(Path.GetDirectoryName(attachFile)!);
        using FileStream list = HoldFolderList(harness);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        bool retrying = false;
        harness.Sessions.OverrideFolders.OpenRefused += purpose =>
            Volatile.Write(ref retrying, retrying || purpose.StartsWith("writing the attach file in ", StringComparison.Ordinal));

        Task<AttachResult> attach = Task.Run(
            () => harness.Sessions.AttachAsync(new AttachRequest(alpha, null, RegistryHarness.LongWait, false, false, null), cancel.Token),
            cancellation
        );
        // The attach file's hold is retrying the refused list; fake time stands still, so the retry cannot give up meanwhile.
        await RegistryHarness.WaitUntilAsync(() => Volatile.Read(ref retrying), attach);
        await cancel.CancelAsync();
        // The failed attach's cleanup retries the held list too; fake time lets that retry give up.
        await RegistryHarness.AdvanceUntilDoneAsync(time, attach, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attach);
        AssertNeverWritten(await events.SeenAsync(), attachFile);
        Assert.Empty(harness.Sessions.List(includeStopped: true));
    }

    /// <summary>Opens the harness's folder list alone, as another server's read-modify-write of it does.</summary>
    private static FileStream HoldFolderList(RegistryHarness harness) =>
        new(harness.Sessions.OverrideFolders.ListPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    /// <summary>Asserts that neither <paramref name="file"/> nor the temporary file it is written through ever appeared.</summary>
    private static void AssertNeverWritten(IReadOnlyCollection<string> seen, string file)
    {
        string name = Path.GetFileName(file);
        Assert.DoesNotContain(name, seen);
        Assert.DoesNotContain(name + ".tmp", seen);
    }

    /// <summary>
    /// The names of the files created, changed or renamed into place in one folder from its start, so a file written and removed
    /// again before the test looks is still seen.
    /// </summary>
    private sealed class FolderEvents : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly ConcurrentQueue<string> _names = new();
        private volatile string? _error;

        public FolderEvents(string folder)
        {
            _watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            _watcher.Created += (_, e) => _names.Enqueue(e.Name!);
            _watcher.Changed += (_, e) => _names.Enqueue(e.Name!);
            _watcher.Renamed += (_, e) => _names.Enqueue(e.Name!);
            _watcher.Error += (_, e) => _error = e.GetException().Message;
            _watcher.EnableRaisingEvents = true;
        }

        /// <summary>
        /// Every name seen so far: a marker file is written and awaited, and the watcher reports a folder's events in order, so
        /// every event before the marker's has been delivered.
        /// </summary>
        public async Task<IReadOnlyCollection<string>> SeenAsync()
        {
            string marker = $"marker-{Guid.NewGuid():N}";
            await File.WriteAllTextAsync(Path.Combine(_watcher.Path, marker), "", TestContext.Current.CancellationToken);
            await RegistryHarness.WaitUntilAsync(() => _names.Contains(marker) || _error is not null);
            Assert.Null(_error);
            return [.. _names];
        }

        public void Dispose() => _watcher.Dispose();
    }

    // The wait's budget covers the time a prep holds the folder: 1.5 s of it are spent on the lock, and the join ends at 2 s
    // from the call, not 2 s from the lock's release.
    [Fact]
    public async Task AJoinsWaitCountsTheTimeThePrepLockWasHeld()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        await using RegistryHarness harness = new(clock);
        string alpha = harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4101);
        SemaphoreSlim folderLock = harness.Sessions.PrepLock(alpha);
        await folderLock.WaitAsync(cancellation);
        // The accept registers synchronously inside the call, and with it the deadline the lock wait spends.
        Task<AttachResult> join = harness.Sessions.AttachAsync(
            new AttachRequest(alpha, null, TimeSpan.FromSeconds(2), false, false, null),
            cancellation
        );
        try
        {
            for (int step = 0; step < 15; step++)
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(TimeSpan.FromMilliseconds(5), cancellation);
            }
        }
        finally
        {
            folderLock.Release();
        }

        await RegistryHarness.WaitUntilAsync(() => File.Exists(DormantGames.JoinPathIn(alpha, 4101)));
        for (int step = 0; step < 6; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellation);
        }

        // No more of the clock is spent: a join whose deadline started at the release would end only at 3.5 s, so it would
        // hang here and fail as hung.
        SessionException timedOut = await Assert.ThrowsAsync<SessionException>(() => join.WaitAsync(RegistryHarness.HangGuard, cancellation));
        Assert.Equal(
            $"The dormant game (pid 4101) on {alpha} did not answer within 2 s, so the join is abandoned and its join file is "
                + "removed. It may be paused under a debugger or frozen; resume it, then call attach_project again.",
            timedOut.Message
        );
        Assert.False(File.Exists(DormantGames.JoinPathIn(alpha, 4101)));
    }

    // A join cancelled while it waits on the prep lock writes nothing and leaves the lock to the prep.
    [Fact]
    public async Task ACancelledJoinWaitingOnThePrepLockWritesNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4101);
        SemaphoreSlim folderLock = _harness.Sessions.PrepLock(alpha);
        await folderLock.WaitAsync(cancellation);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<AttachResult> join = _harness.Sessions.AttachAsync(
            new AttachRequest(alpha, null, RegistryHarness.LongWait, false, false, 4101),
            cancel.Token
        );
        try
        {
            await RegistryHarness.WaitUntilAsync(() => _harness.Sessions.List(includeStopped: true).Count > 0, join);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => join);

            Assert.False(File.Exists(DormantGames.JoinPathIn(alpha, 4101)));
            Assert.Empty(_harness.Sessions.List(includeStopped: true));
            Assert.Equal(0, folderLock.CurrentCount);
        }
        finally
        {
            folderLock.Release();
        }
    }

    // The project's own override.cfg is refused as a plain attach's is, and the join then writes no join file.
    [Fact]
    public async Task AJoinWhoseOverrideIsRefusedWritesNoJoinFile()
    {
        string alpha = _harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4101);
        File.WriteAllText(OverrideFile.PathIn(alpha), "[autoload]\n\nGodotMcpBridge=\"*res://game/bridge.gd\"\n");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(
                new AttachRequest(alpha, null, RegistryHarness.LongWait, false, false, 4101),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains($"{OverrideFile.PathIn(alpha)} already exists and is the project's own", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(DormantGames.JoinPathIn(alpha, 4101)));
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    [Fact]
    public async Task JoinsOfTwoDormantGamesWaitAtOnce()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        int first = _harness.StartOwnedGame().Id;
        int second = _harness.StartOwnedGame().Id;
        RegistryHarness.WriteDormant(alpha, first);
        RegistryHarness.WriteDormant(alpha, second);

        (Task<AttachResult> firstJoin, string firstToken) = await _harness.StartWaitingJoinAsync(alpha, "first", first);
        (Task<AttachResult> secondJoin, string secondToken) = await _harness.StartWaitingJoinAsync(alpha, "second", second);
        bool bothJoinFiles = File.Exists(DormantGames.JoinPathIn(alpha, first)) && File.Exists(DormantGames.JoinPathIn(alpha, second));
        using FakeBridge firstGame = await FakeBridge.DialAsync(
            _harness.Listener.Port,
            firstToken,
            alpha,
            new FakeHello { ProcessId = first },
            cancellation
        );
        using FakeBridge secondGame = await FakeBridge.DialAsync(
            _harness.Listener.Port,
            secondToken,
            alpha,
            new FakeHello { ProcessId = second },
            cancellation
        );

        Assert.True(bothJoinFiles);
        Assert.Equal(first, (await firstJoin).JoinedPid);
        Assert.Equal(second, (await secondJoin).JoinedPid);
        Assert.True(_harness.Sessions.Resolve("first").HasGame);
        Assert.True(_harness.Sessions.Resolve("second").HasGame);
        Assert.False(File.Exists(DormantGames.JoinPathIn(alpha, first)));
        Assert.False(File.Exists(DormantGames.JoinPathIn(alpha, second)));
    }

    [Fact]
    public async Task ASecondJoinOfTheSamePidIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4101);
        await _harness.StartWaitingJoinAsync(alpha, "first", 4101);

        SessionException byPid = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(new AttachRequest(alpha, "second", RegistryHarness.LongWait, false, false, 4101), cancellation)
        );
        SessionException withoutPid = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(new AttachRequest(alpha, "third", RegistryHarness.LongWait, false, false, null), cancellation)
        );

        string expected = $"A join of the game (pid 4101) on {alpha} is already waiting in session 'first'; wait for it or let it time out first.";
        Assert.Equal(expected, byPid.Message);
        Assert.Equal(expected, withoutPid.Message);
        Assert.Equal(["first"], _harness.Sessions.List(includeStopped: true).Select(session => session.Name));
        Assert.True(File.Exists(DormantGames.JoinPathIn(alpha, 4101)));
        Assert.False(File.Exists(AttachFile.PathIn(alpha)));
    }

    [Fact]
    public async Task AJoinWaitsBesideAPlainAttach()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(alpha, "server");
        await RegistryHarness.WaitUntilAsync(() => File.Exists(AttachFile.PathIn(alpha)));
        int pid = _harness.StartOwnedGame().Id;
        RegistryHarness.WriteDormant(alpha, pid);

        (Task<AttachResult> join, string token) = await _harness.StartWaitingJoinAsync(alpha, "joined", pid);
        using FakeBridge game = await FakeBridge.DialAsync(_harness.Listener.Port, token, alpha, new FakeHello { ProcessId = pid }, cancellation);

        Assert.Equal(pid, (await join).JoinedPid);
        Assert.True(_harness.Sessions.Resolve("joined").HasGame);
        Assert.True(_harness.Sessions.Resolve("server").IsWaitingForGame);
        Assert.True(File.Exists(AttachFile.PathIn(alpha)));
    }

    // Another server's join of the same game rewrote the join file with its own token while this one waited.
    [Fact]
    public async Task ATimedOutJoinLeavesAJoinFileWithAnotherToken()
    {
        string alpha = _harness.Project("alpha");
        RegistryHarness.WriteDormant(alpha, 4101);
        string joinFile = DormantGames.JoinPathIn(alpha, 4101);
        Task<AttachResult> join = _harness.Sessions.AttachAsync(
            new AttachRequest(alpha, null, TimeSpan.FromSeconds(2), false, false, 4101),
            TestContext.Current.CancellationToken
        );
        await RegistryHarness.WaitUntilAsync(() => File.Exists(joinFile) || join.IsCompleted);
        ArmSettings plain = new(Quiet: false, ShutOutRealGamepads: false, Mute: false);
        DormantGames.WriteJoinFile(alpha, 4101, new BridgeEndpoint(51234, "ANOTHER"), plain);

        await Assert.ThrowsAsync<SessionException>(() => join);

        Assert.True(File.Exists(joinFile));
        Assert.Equal("ANOTHER", JsonNode.Parse(File.ReadAllText(joinFile))!["token"]!.GetValue<string>());
    }

    // Another server's plain attach on the folder rewrote the attach file with its own token while this one waited.
    [Fact]
    public async Task ATimedOutAttachLeavesAnAttachFileWithAnotherToken()
    {
        string alpha = _harness.Project("alpha");
        string attachFile = AttachFile.PathIn(alpha);
        Task<AttachResult> attach = _harness.Sessions.AttachAsync(
            new AttachRequest(alpha, null, TimeSpan.FromSeconds(2), false, false, null),
            TestContext.Current.CancellationToken
        );
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile) || attach.IsCompleted);
        ArmSettings plain = new(Quiet: false, ShutOutRealGamepads: false, Mute: false);
        AttachFile.Write(alpha, new BridgeEndpoint(51234, "ANOTHER"), plain);

        await Assert.ThrowsAsync<SessionException>(() => attach);

        Assert.True(File.Exists(attachFile));
        Assert.Equal("ANOTHER", JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>());
    }

    // Another server holding the folder list stands for its write of an attach file between this one's token read and delete:
    // the removal must wait for it. The list's retry budget is 2 s on the harness's load clock, after which the removal goes
    // ahead without the list. That clock counts this test process's own work as the server's, so a stall the suite's own
    // parallel tests cause does not stretch it: an attempt whose hold the test measured at 2 s of wall time or past it (no
    // less than the budget's load-adjusted time) proves nothing, and is made once more on a fresh folder.
    [Fact]
    public async Task AConnectedAttachWaitsForTheFolderListBeforeRemovingItsFile()
    {
        HeldRemoval first = await HoldTheListAcrossAConnectedAttachAsync("alpha", "server");
        HeldRemoval held = first.Hold < ListRetryBudget ? first : await HoldTheListAcrossAConnectedAttachAsync("beta", "second");

        Assert.True(
            held.Hold < ListRetryBudget,
            $"the list was held {first.Hold.TotalMilliseconds:0} ms, then {held.Hold.TotalMilliseconds:0} ms: "
                + "past the removal's retry budget both times"
        );
        Assert.True(held.KeptWhileHeld);
        Assert.False(held.LeftAfter);
    }

    /// <summary>
    /// Starts an attach on <paramref name="folder"/>, holds the folder list, dials the game in, and releases the hold 300 ms
    /// later; returns whether the attach file was still there with the attach unfinished at the release, whether it was left
    /// once the attach ended, and how long the list was held, from the hold's start until its release had returned.
    /// </summary>
    private async Task<HeldRemoval> HoldTheListAcrossAConnectedAttachAsync(string folder, string session)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = _harness.Project(folder);
        string attachFile = AttachFile.PathIn(project);
        Task<AttachResult> attach = _harness.Sessions.AttachAsync(
            new AttachRequest(project, session, RegistryHarness.LongWait, false, false, null),
            cancellation
        );
        // The override is written after the attach file and takes the list too, so the hold starts once both are in place.
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile) && File.Exists(OverrideFile.PathIn(project)));
        string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Stopwatch holding = new();
        var holder = Task.Run(
            () =>
                _harness.Sessions.OverrideFolders.Hold(
                    "the test's hold",
                    () =>
                    {
                        holding.Start();
                        held.Set();
                        release.Wait(cancellation);
                    }
                ),
            cancellation
        );
        held.Wait(cancellation);

        using FakeBridge game = await FakeBridge.DialAsync(_harness.Listener.Port, token, project, cancellation);
        await Task.Delay(300, cancellation);
        bool keptWhileHeld = File.Exists(attachFile) && !attach.IsCompleted;
        release.Set();
        await holder;
        holding.Stop();
        await attach;

        return new HeldRemoval(keptWhileHeld, File.Exists(attachFile), holding.Elapsed);
    }

    [Fact]
    public async Task ACallToAnAttachedGameThatClosedItsConnectionNamesStopAndDetach()
    {
        string alpha = _harness.Project("alpha");
        await _harness.EndAttachedGameAsync(alpha, "server");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.Resolve("server").SendAsync("get_ui_elements", null, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"The attached game on {alpha} has closed its connection (it may have quit). stop_project or detach_project ends the session; "
                + "then attach_project again, which joins the game if it is still running on an armed folder.",
            refused.Message
        );
    }

    private sealed record HeldRemoval(bool KeptWhileHeld, bool LeftAfter, TimeSpan Hold);
}
