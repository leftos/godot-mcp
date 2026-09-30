using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Wire;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

/// <summary>Attaching, detaching and restarting a session, and the folder's prep lock around them.</summary>
public sealed class SessionAttachTests : IAsyncDisposable
{
    private readonly RegistryHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

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
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", null);
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
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", null);
        GodotSession session = _harness.Sessions.Resolve("server");
        using Process neverStarted = new();

        await session.OnRunExitedAsync(new GodotRun(alpha, neverStarted, previous: null));

        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
        Assert.True(session.IsLive);
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
        FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", null);
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
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", child.Id);

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
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", child.Id);
        Task<StopResult> stop = _harness.Sessions.StopAsync("server", cancellation);

        string? ping = await game.AnswerOneAsync("pong", cancellation);
        string? quit = await game.AnswerOneAsync("quit", cancellation);
        StopResult stopped = await stop;

        Assert.Equal(["ping", "shutdown"], [ping, quit]);
        Assert.True(stopped.Killed);
        Assert.Equal(GameKillReason.AfterGrace(QuitRequest.Acknowledged, TimeSpan.FromSeconds(3)), stopped.KillReason);
        Assert.Null(stopped.GameExitCode);
        Assert.True(child.HasExited);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    [Fact]
    public async Task RestartingAnAttachedSessionIsRefused()
    {
        string alpha = _harness.Project("alpha");
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", null);

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
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", null);

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
        string alpha = _harness.Project("alpha");
        string overrideFile = OverrideFile.PathIn(alpha);
        SemaphoreSlim folderLock = _harness.Sessions.PrepLock(alpha);
        await folderLock.WaitAsync(TestContext.Current.CancellationToken);
        Task<AttachResult> attach;
        try
        {
            attach = _harness.Sessions.AttachAsync(
                new AttachRequest(alpha, "server", TimeSpan.FromSeconds(2), false, false, null),
                TestContext.Current.CancellationToken
            );
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(File.Exists(overrideFile));
        }
        finally
        {
            folderLock.Release();
        }

        await RegistryHarness.WaitUntilAsync(() => File.Exists(overrideFile));
        await Assert.ThrowsAsync<SessionException>(() => attach);
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

        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", null);

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
        using FakeBridge firstGame = await FakeBridge.DialAsync(_harness.Listener.Port, firstToken, alpha, first, cancellation);
        using FakeBridge secondGame = await FakeBridge.DialAsync(_harness.Listener.Port, secondToken, alpha, second, cancellation);

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
        using FakeBridge game = await FakeBridge.DialAsync(_harness.Listener.Port, token, alpha, pid, cancellation);

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
    // the removal must wait for it. The hold stays well inside the list's 2 s retry budget.
    [Fact]
    public async Task AConnectedAttachWaitsForTheFolderListBeforeRemovingItsFile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        string attachFile = AttachFile.PathIn(alpha);
        Task<AttachResult> attach = _harness.Sessions.AttachAsync(
            new AttachRequest(alpha, "server", RegistryHarness.LongWait, false, false, null),
            cancellation
        );
        // The override is written after the attach file and takes the list too, so the hold starts once both are in place.
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile) && File.Exists(OverrideFile.PathIn(alpha)));
        string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        var holder = Task.Run(
            () =>
                _harness.Sessions.OverrideFolders.Hold(
                    "the test's hold",
                    () =>
                    {
                        held.Set();
                        release.Wait(cancellation);
                    }
                ),
            cancellation
        );
        held.Wait(cancellation);

        using FakeBridge game = await FakeBridge.DialAsync(_harness.Listener.Port, token, alpha, cancellation);
        await Task.Delay(300, cancellation);
        bool keptWhileHeld = File.Exists(attachFile) && !attach.IsCompleted;
        release.Set();
        await holder;
        await attach;

        Assert.True(keptWhileHeld);
        Assert.False(File.Exists(attachFile));
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
}
