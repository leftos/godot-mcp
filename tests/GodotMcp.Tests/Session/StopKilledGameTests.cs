using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// stop_project on a folder whose stopped session's game a stop killed and that outlived the kill wait: the call waits for that
/// game once more, killing it again, and reports it when it outlives this wait too. Each run's process is a fake over a stand-in
/// child, its game another stand-in whose exit the test's own debugger holds, so the game is not gone exactly as long as the
/// test wants. The clocks are fake and advance only where a test says so, the machine's load with them. A run whose process
/// ends on its own has its exit handled, the folder released, before the session says so. The tail of a stop: the console
/// wrapper of a game that quit is waited for on the load-adjusted clock, the processes the game left running are waited for on
/// the kill wait and named when they outlast it, and a run whose game had no handle says so.
/// </summary>
[Collection(LaunchRefusesNoGlTests.GodotPathCollection)]
public sealed class StopKilledGameTests : IAsyncDisposable
{
    private const string StillUsesTheFolder = "; its project folder stays in use until it is gone.";
    private const string OwnersList = "override-folders.txt";

    /// <summary>The exit code of the wrapper stand-in that exits by itself (<see cref="StartWrapperThatExitsOnInput"/>).</summary>
    private const int WrapperOwnExitCode = 7;

    /// <summary>The exit code <see cref="Process.Kill()"/> leaves on Windows.</summary>
    private const int KilledExitCode = -1;

    /// <summary>A pid no process has: Windows hands out multiples of four far below it, so the run gets no handle on its game.</summary>
    private const int NoProcessId = int.MaxValue & ~3;

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeLoadSource _machine;
    private readonly LoadClock _clock;
    private readonly BridgeListener _listener;
    private readonly SessionRegistry _sessions;
    private readonly ConcurrentQueue<FakeRunProcess> _launched = new();
    private readonly List<ExitHold> _holds = [];
    private readonly List<Process> _games = [];
    private readonly string? _configuredGodot;
    private Process? _nextWrapper;

    public StopKilledGameTests()
    {
        // Idle until a test sets a load.
        _machine = new FakeLoadSource(_time, processors: 4);
        _clock = new LoadClock(_time, _machine);
        _listener = new BridgeListener(NullLogger<BridgeListener>.Instance) { Clock = _clock };
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance)
        {
            LaunchClock = _clock,
            OverrideFolders = new OverrideFolders(_temp.Combine(OwnersList), TextWriter.Null) { Clock = _clock },
            Dormant = new DormantGames(_ => new ProcessStart(Exists: true, StartTime: null), TextWriter.Null),
            RunProcessFactory = Launch,
        };
        // The launch only has to find a file: the factory replaces the process it would start.
        _configuredGodot = Environment.GetEnvironmentVariable(Installation.GodotPathVariable);
        Environment.SetEnvironmentVariable(Installation.GodotPathVariable, typeof(StopKilledGameTests).Assembly.Location);
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(Installation.GodotPathVariable, _configuredGodot);
        foreach (ExitHold hold in _holds)
        {
            hold.Dispose();
        }

        foreach (FakeRunProcess run in _launched)
        {
            await run.EndAsync(CancellationToken.None);
        }

        _sessions.Dispose();
        _listener.Dispose();
        _clock.Dispose();
        foreach (FakeRunProcess run in _launched)
        {
            run.Dispose();
        }

        foreach (Process game in _games)
        {
            RegistryHarness.EndStandInGame(game);
        }

        _temp.Dispose();
    }

    [Fact]
    public async Task ASecondStopWaitsForAKilledGameStillRunning()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        (Process game, ExitHold hold) = await StopWithTheGameHeldAsync(project);
        // The hold ends once the call has found the game still running, so only a wait for it sees it gone.
        _sessions.OpenStoppedGame = (pid, started) => ReleasedOnceOpened(pid, started, hold);

        object stopped = await _sessions.StopFolderAsync(project, null, cancellation).WaitAsync(RegistryHarness.HangGuard, cancellation);

        HostStopResult result = Assert.IsType<HostStopResult>(stopped);
        Assert.Null(result.GameStillRunning);
        Assert.Null(result.Warning);
        Assert.True(game.WaitForExit(0), "the call returned before the killed game was gone");
    }

    [Fact]
    public async Task ASecondStopReportsAGameThatOutlivesItsWaitToo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        (Process game, ExitHold hold) = await StopWithTheGameHeldAsync(project);

        Task<object> stop = _sessions.StopFolderAsync(project, null, cancellation);
        await RegistryHarness.AdvanceUntilDoneAsync(_time, stop, cancellation);
        HostStopResult result = Assert.IsType<HostStopResult>(await stop);
        bool goneWhenTheStopReturned = game.WaitForExit(0);
        hold.Release();

        Assert.False(goneWhenTheStopReturned);
        Assert.Equal([game.Id], result.GameStillRunning);
        Assert.StartsWith(
            $"The killed game (pid {game.Id}) was still running after 5 s of load-adjusted time (wall ",
            result.Warning,
            StringComparison.Ordinal
        );
        Assert.EndsWith(StillUsesTheFolder, result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoppedGamesPidReusedByAnotherProcessCountsAsGone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        _ = await StopWithTheGameHeldAsync(project);
        Process bystander = StartGame();
        // The pid now names the bystander, a process with another creation time.
        _sessions.OpenStoppedGame = (_, started) => RunKill.OpenIfStillRunning(bystander.Id, started);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _sessions.StopFolderAsync(project, null, cancellation));

        Assert.Equal($"No Godot session or warm headless host runs in {project}.", refused.Message);
        Assert.False(bystander.WaitForExit(0), "the bystander that took the pid was killed");
    }

    [Fact]
    public async Task ASecondStopWithNothingLeftRefusesAsBefore()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        (Process game, ExitHold hold) = await StopWithTheGameHeldAsync(project);
        _sessions.OpenStoppedGame = (pid, started) => ReleasedOnceOpened(pid, started, hold);
        _ = Assert.IsType<HostStopResult>(
            await _sessions.StopFolderAsync(project, null, cancellation).WaitAsync(RegistryHarness.HangGuard, cancellation)
        );
        Assert.True(game.WaitForExit(0), "the second call returned before the killed game was gone");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _sessions.StopFolderAsync(project, null, cancellation));

        Assert.Equal($"No Godot session or warm headless host runs in {project}.", refused.Message);
    }

    // The run's process ends on its own, as a game that quits does. ExitHandled completes only after the exit's handler has
    // released the folder, so the override is already gone when the completion's continuation looks.
    [Fact]
    public async Task ARunsExitIsHandledOnlyOnceItsFolderIsReleased()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        Task<LaunchResult> launch = _sessions.LaunchAsync(Request(project), "server", cancellation);
        await RegistryHarness.WaitUntilAsync(() => !_launched.IsEmpty, launch);
        FakeRunProcess run = _launched.First();
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, run.Token, project, new FakeHello(), cancellation);
        _ = await launch.WaitAsync(RegistryHarness.HangGuard, cancellation);
        string overrideFile = OverrideFile.PathIn(project);
        Assert.True(File.Exists(overrideFile));
        Task<bool> overrideLeftWhenHandled = _sessions
            .Resolve("server")
            .ExitHandled.ContinueWith(
                _ => File.Exists(overrideFile),
                cancellation,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );

        run.Process.Kill(entireProcessTree: true);

        Assert.False(await overrideLeftWhenHandled.WaitAsync(RegistryHarness.HangGuard, cancellation));
    }

    // The run's process ends on its own while the test holds the owners list, which the exit's release must take. A handler of
    // the Exited event subscribed after the session's runs while the event is still being raised: the session's handler only
    // queued its work, so the override is still there and the exit not yet handled. Once the list is let go, the release runs.
    [Fact]
    public async Task ARunsExitHandlerNeverRunsInsideTheExitedEvent()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        Task<LaunchResult> launch = _sessions.LaunchAsync(Request(project), "server", cancellation);
        await RegistryHarness.WaitUntilAsync(() => !_launched.IsEmpty, launch);
        FakeRunProcess run = _launched.First();
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, run.Token, project, new FakeHello(), cancellation);
        _ = await launch.WaitAsync(RegistryHarness.HangGuard, cancellation);
        string overrideFile = OverrideFile.PathIn(project);
        GodotSession session = _sessions.Resolve("server");
        var seen = new TaskCompletionSource<(bool OverrideLeft, bool Handled)>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.AfterExitHandlers = () => seen.TrySetResult((File.Exists(overrideFile), session.ExitHandled.IsCompleted));

        (bool OverrideLeft, bool Handled) inTheEvent;
        using (HoldOwnersList())
        {
            run.Process.Kill(entireProcessTree: true);
            inTheEvent = await seen.Task.WaitAsync(RegistryHarness.HangGuard, cancellation);
        }

        Assert.True(inTheEvent.OverrideLeft, "the exit's release ran inside the Exited event");
        Assert.False(inTheEvent.Handled, "the exit was handled inside the Exited event");
        await session.ExitHandled.WaitAsync(RegistryHarness.HangGuard, cancellation);
        Assert.False(File.Exists(overrideFile));
    }

    // A queued exit handler can run after a replace by name has retired its session and before the session is disposed. Here it
    // takes the gate once the session is retired, the test holds it at the owners list (after it has ended the session's capture)
    // until the session is disposed, and the handler still ends without a fault and completes ExitHandled.
    [Fact]
    public async Task ALateExitHandlerAfterRetirementCompletesAndDoesNotThrow()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        Task<LaunchResult> launch = _sessions.LaunchAsync(Request(project), "server", cancellation);
        await RegistryHarness.WaitUntilAsync(() => !_launched.IsEmpty, launch);
        FakeRunProcess run = _launched.First();
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, run.Token, project, new FakeHello(), cancellation);
        _ = await launch.WaitAsync(RegistryHarness.HangGuard, cancellation);
        GodotSession session = _sessions.Resolve("server");
        GodotRun current = session.CurrentRun!;
        // The retirement disposes the run's process, so the child is ended through a handle of the test's own.
        _games.Add(Process.GetProcessById(run.Process.Id));
        Assert.True(_launched.TryDequeue(out _));
        Assert.True(_sessions.Captures.Begin("server"));
        await session.RetireAsync().WaitAsync(RegistryHarness.HangGuard, cancellation);

        Task handler;
        using (HoldOwnersList())
        {
            handler = Task.Run(() => session.OnRunExitedAsync(current), cancellation);
            // The handler ends the capture under the gate, then waits for the list to release the folder.
            await RegistryHarness.WaitUntilAsync(() => !_sessions.Captures.IsRunning("server"), handler);
            session.Dispose();
        }

        await handler.WaitAsync(RegistryHarness.HangGuard, cancellation);
        Assert.True(session.ExitHandled.IsCompletedSuccessfully);
    }

    // The test holds override.cfg open without delete sharing, as a peer reading it does, so the stop's release fails after the
    // game was killed: the stop still succeeds, says so in its warning, and deletes the run's scratch user data.
    [Fact]
    public async Task AStopWhoseOverrideDeleteIsRefusedSucceedsWithAWarning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        LaunchRequest request = Request(project) with { ScratchUserData = new ScratchUserDataRequest(null) };
        Task<LaunchResult> launch = _sessions.LaunchAsync(request, "server", cancellation);
        await RegistryHarness.WaitUntilAsync(() => !_launched.IsEmpty, launch);
        FakeRunProcess run = _launched.First();
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, run.Token, project, new FakeHello(), cancellation);
        _ = await launch.WaitAsync(RegistryHarness.HangGuard, cancellation);
        string userData = ScratchUserData.RootFor(project, "server");
        Assert.True(Directory.Exists(userData));

        StopResult stopped;
        using (StopFolderReleaseFailureTests.HoldOverrideOpen(project))
        {
            // The bridge never answers the stop's ping, so the game counts as silent and its wrapper, a real process, is killed:
            // the fake time stands still once the kill is seen, so the kill wait cannot run out on the wrapper's teardown.
            Task<StopResult> stop = _sessions.StopAsync("server", cancellation);
            await AdvanceUntilAsync(() => run.Process.HasExited, stop);
            stopped = await stop.WaitAsync(RegistryHarness.HangGuard, cancellation);
        }

        Assert.False(stopped.OverrideRemoved);
        Assert.Contains(StopFolderReleaseFailureTests.CouldNotBeRemoved(project), stopped.Warning, StringComparison.Ordinal);
        Assert.False(_sessions.Resolve("server").IsLive);
        Assert.False(Directory.Exists(userData));
    }

    // The game quits; its console wrapper is still ending on a machine so busy that two seconds of wall time are half a second
    // of load-adjusted time, and then exits by itself: it is waited for, not killed, and its own exit code is the stop's.
    [Fact]
    public async Task AWrapperStillTearingDownAfterACleanQuitIsWaitedForOnTheLoadClock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in processes are cmd.exe.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        Process wrapper = await StartWrapperThatExitsOnInputAsync();
        _nextWrapper = wrapper;
        Process game = StartGame();
        using FakeBridge bridge = await LaunchAsync(project, game.Id);
        await MakeMachineBusyAsync();

        Task<StopResult> stop = _sessions.StopAsync("server", cancellation);
        await QuitAsync(bridge, game, withItsChildren: true);
        var sinceQuit = Stopwatch.StartNew();
        await AdvanceAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
        // A lower bound only, which load only lengthens: a wait counted in wall time, a second of it, has run out by now. The
        // stopwatch starts when the test sees the game gone, such a wait when the stop's continuation does, a little later on a
        // busy machine, so the bound is three times the second rather than just past it.
        while (sinceQuit.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(20, cancellation);
        }

        bool endedBeforeTheWrapper = stop.IsCompleted;
        if (!wrapper.HasExited)
        {
            await wrapper.StandardInput.WriteLineAsync("exit".AsMemory(), cancellation);
        }

        StopResult result = await stop.WaitAsync(RegistryHarness.HangGuard, cancellation);

        Assert.False(endedBeforeTheWrapper, "the stop ended before the wrapper exited");
        Assert.False(result.Killed);
        Assert.Equal(WrapperOwnExitCode, result.ExitCode);
        Assert.Null(result.LeftRunning);
        Assert.Null(result.Warning);
    }

    // The game quits and its console wrapper runs on past the load-adjusted second: the wrapper is killed, as before.
    [Fact]
    public async Task AWrapperThatOutlastsTheLoadAdjustedSecondIsEnded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in processes are cmd.exe.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        Process game = StartGame();
        using FakeBridge bridge = await LaunchAsync(project, game.Id);
        FakeRunProcess run = _launched.First();
        await MakeMachineBusyAsync();

        Task<StopResult> stop = _sessions.StopAsync("server", cancellation);
        await QuitAsync(bridge, game, withItsChildren: true);
        await AdvanceUntilAsync(() => run.Process.HasExited, stop);
        StopResult result = await stop.WaitAsync(RegistryHarness.HangGuard, cancellation);

        Assert.False(result.Killed);
        Assert.Equal(KilledExitCode, result.ExitCode);
        Assert.True(run.Process.WaitForExit(0), "the stop returned before the killed wrapper was gone");
    }

    // The game quits and leaves a child running, which nothing here ends (the fake wrapper has no job to close): the stop kills
    // the wrapper alone, waits the kill wait for the child, and names it in its warning, with the folder it still holds.
    [Fact]
    public async Task AQuitGameWhoseChildStaysPastTheKillWaitIsNamedInTheWarning()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in processes are cmd.exe and ping.exe.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        (Process game, Process child, string described) = await StartGameWithRunningChildAsync();
        using FakeBridge bridge = await LaunchAsync(project, game.Id);
        FakeRunProcess run = _launched.First();

        Task<StopResult> stop = _sessions.StopAsync("server", cancellation);
        await QuitAsync(bridge, game, withItsChildren: false);
        await AdvanceUntilAsync(() => run.Process.HasExited, stop);
        // The killed wrapper's teardown ends before the kill wait is let run out on the child.
        Assert.True(await ProcessExit.WaitUntilGoneAsync(run.Process, RegistryHarness.HangGuard), "the killed wrapper was not gone");
        await RegistryHarness.AdvanceUntilDoneAsync(_time, stop, cancellation);
        StopResult result = await stop;

        Assert.False(child.HasExited, "the stop killed the game's child");
        Assert.False(result.Killed);
        Assert.Contains(described, result.LeftRunning ?? []);
        // The game's console host, made for it as a console without a window, stays too while the ping is attached to it.
        IReadOnlyList<string> children = LeftBehind.Find(game, NullLogger.Instance);
        Assert.Contains(described, children);
        Assert.StartsWith(
            $"The game left {string.Join(", ", children)} running, and they were still running after 5 s of load-adjusted time (wall ",
            result.Warning,
            StringComparison.Ordinal
        );
        Assert.EndsWith("; its project folder stays in use until they are gone.", result.Warning, StringComparison.Ordinal);
    }

    // The game quits and leaves a child running that goes once the wrapper is killed, as the wrapper's job closing ends it: the
    // stop returns only once it is gone, and lists it in leftRunning without a warning.
    [Fact]
    public async Task AQuitGameWhoseChildGoesWithinTheKillWaitIsWaitedForAndNotWarned()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in processes are cmd.exe and ping.exe.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        (Process game, Process child, string described) = await StartGameWithRunningChildAsync();
        using FakeBridge bridge = await LaunchAsync(project, game.Id);
        FakeRunProcess run = _launched.First();

        Task<StopResult> stop = _sessions.StopAsync("server", cancellation);
        await QuitAsync(bridge, game, withItsChildren: false);
        await AdvanceUntilAsync(() => run.Process.HasExited, stop);
        // The child was opened before the wrapper's kill; it ends now, with the fake time standing still.
        child.Kill();
        StopResult result = await stop.WaitAsync(RegistryHarness.HangGuard, cancellation);

        Assert.True(child.WaitForExit(0), "the stop returned before the game's child was gone");
        Assert.False(result.Killed);
        Assert.Contains(described, result.LeftRunning ?? []);
        Assert.Null(result.Warning);
    }

    // The game's hello names a pid the server cannot open, so the run holds no handle on the game: the silent game's stop kills
    // the wrapper alone and says the game itself was not waited for.
    [Fact]
    public async Task AStopOfARunWithNoGameHandleNamesTheUnwaitedGame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        using FakeBridge bridge = await LaunchAsync(project, NoProcessId);
        FakeRunProcess run = _launched.First();
        Assert.Null(_sessions.Resolve("server").CurrentRun!.Game);

        // The bridge never answers the stop's ping, so the game counts as silent and is killed at once.
        Task<StopResult> stop = _sessions.StopAsync("server", cancellation);
        await AdvanceUntilAsync(() => run.Process.HasExited, stop);
        StopResult result = await stop.WaitAsync(RegistryHarness.HangGuard, cancellation);

        Assert.True(result.Killed);
        Assert.Equal(NoHandleWarning(NoProcessId), result.Warning);
    }

    // With no handle on the game, the stop watches the console wrapper, which exits by itself only once the game has quit and
    // left nothing running: the game is gone, so there is nothing to warn of.
    [Fact]
    public async Task ACleanQuitOfARunWithNoGameHandleAddsNoNoHandleWarning()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in processes are cmd.exe.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        Process wrapper = await StartWrapperThatExitsOnInputAsync();
        _nextWrapper = wrapper;
        using FakeBridge bridge = await LaunchAsync(project, NoProcessId);

        Task<StopResult> stop = _sessions.StopAsync("server", cancellation);
        await AnswerAsync(bridge, "ping", new JsonObject { ["pong"] = true });
        await AnswerAsync(bridge, "shutdown", new JsonObject());
        await wrapper.StandardInput.WriteLineAsync("exit".AsMemory(), cancellation);
        StopResult result = await stop.WaitAsync(RegistryHarness.HangGuard, cancellation);

        Assert.False(result.Killed);
        Assert.Equal(WrapperOwnExitCode, result.ExitCode);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task AStopOfARunWithAGameHandleAddsNoNoHandleWarning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        Process game = StartGame();
        using FakeBridge bridge = await LaunchAsync(project, game.Id);
        FakeRunProcess run = _launched.First();
        Assert.NotNull(_sessions.Resolve("server").CurrentRun!.Game);

        Task<StopResult> stop = _sessions.StopAsync("server", cancellation);
        await AdvanceUntilAsync(() => run.Process.HasExited && game.HasExited, stop);
        StopResult result = await stop.WaitAsync(RegistryHarness.HangGuard, cancellation);

        Assert.True(result.Killed);
        Assert.Null(result.Warning);
    }

    // restart_project ends the replaced run the way stop_project does, so its warning names the game it could not wait for.
    [Fact]
    public async Task ARestartWithNoGameHandleWarnsForTheReplacedGame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        using FakeBridge first = await LaunchAsync(project, NoProcessId);
        FakeRunProcess replaced = _launched.First();

        Task<object> restart = _sessions.RestartAsync("server", prepare: false, cancellation);
        // The replaced game is silent, so the fake time runs until it is killed; then it stands still until the new run is launched.
        await AdvanceUntilAsync(() => replaced.Process.HasExited, restart);
        await RegistryHarness.WaitUntilAsync(() => _launched.Count >= 2, restart);

        FakeRunProcess next = _launched.Last();
        using FakeBridge second = await FakeBridge.DialAsync(_listener.Port, next.Token, project, new FakeHello(), cancellation);
        RestartResult result = Assert.IsType<RestartResult>(await restart.WaitAsync(RegistryHarness.HangGuard, cancellation));
        // The restart killed the replaced run and disposed its process, so the harness has nothing left to end of it.
        Assert.True(_launched.TryDequeue(out _));

        Assert.Equal(NoHandleWarning(NoProcessId), result.Warning);
    }

    private static string NoHandleWarning(int pid) =>
        $"The game's process (pid {pid}) could not be opened when the run started, so the stop could not wait for it; "
        + "it may still be running and holding the project folder.";

    /// <summary>
    /// A stand-in game, cmd.exe, with a ping as its child, returned once the ping has printed its first line, so it is running and
    /// not merely listed; the child is returned with its handle and as leftRunning names it. Both end with the test.
    /// </summary>
    private async Task<(Process Game, Process Child, string Described)> StartGameWithRunningChildAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Process game = Process.Start(
            new ProcessStartInfo("cmd.exe", "/d /c ping -n 600 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            }
        )!;
        _games.Add(game);
        string? line;
        do
        {
            line = await game.StandardOutput.ReadLineAsync(cancellation).AsTask().WaitAsync(RegistryHarness.HangGuard, cancellation);
        } while (line is { Length: 0 });

        Assert.NotNull(line);
        // The ping writes into the pipe this process holds; the rest is read so it never blocks on a full one.
        _ = game.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellation);
        LeftBehind.ListedProcess listed = Assert.Single(
            LeftBehind.FindProcesses(game, NullLogger.Instance),
            process => process.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase)
        );
        var child = Process.GetProcessById(listed.Id);
        _games.Add(child);
        return (game, child, LeftBehind.Describe(listed));
    }

    /// <summary>
    /// Advances the fake time 100 ms at a time, pausing 5 ms of real time after each, until <paramref name="seen"/> holds, such as a
    /// kill seen through the killed process's own handle; then the fake time stands still, so no fake deadline runs out while a
    /// real process is torn down. Fails when <paramref name="running"/> ends first, or at the hang guard.
    /// </summary>
    private async Task AdvanceUntilAsync(Func<bool> seen, Task running)
    {
        DateTime guard = DateTime.UtcNow + RegistryHarness.HangGuard;
        while (!seen())
        {
            if (running.IsCompleted)
            {
                await running;
                Assert.Fail("the call ended before what the test waited for was seen");
            }

            Assert.True(DateTime.UtcNow < guard, $"what the test waited for was not seen within its {RegistryHarness.HangGuard} hang guard");
            _time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A stand-in console wrapper that exits by itself, with <see cref="WrapperOwnExitCode"/>, once a line reaches its input.</summary>
    private static async Task<Process> StartWrapperThatExitsOnInputAsync()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Process wrapper = Process.Start(
            new ProcessStartInfo("cmd.exe", $"/d /c echo ready& set /p line=& exit {WrapperOwnExitCode}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            }
        )!;
        try
        {
            Assert.Equal(
                "ready",
                await wrapper.StandardOutput.ReadLineAsync(cancellation).AsTask().WaitAsync(RegistryHarness.HangGuard, cancellation)
            );
            return wrapper;
        }
        catch
        {
            RegistryHarness.EndStandInGame(wrapper);
            throw;
        }
    }

    /// <summary>
    /// Launches a run named server whose bridge's hello carries <paramref name="gameProcessId"/>; returns the bridge, which the
    /// caller disposes.
    /// </summary>
    private async Task<FakeBridge> LaunchAsync(string project, int? gameProcessId)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<LaunchResult> launch = _sessions.LaunchAsync(Request(project), "server", cancellation);
        await RegistryHarness.WaitUntilAsync(() => !_launched.IsEmpty, launch);
        FakeRunProcess run = _launched.First();
        FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, run.Token, project, new FakeHello { ProcessId = gameProcessId }, cancellation);
        try
        {
            _ = await launch.WaitAsync(RegistryHarness.HangGuard, cancellation);
            return bridge;
        }
        catch
        {
            bridge.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sets three of the four processors busy with other work, and lets the clock publish it, so a second of wall time is a
    /// quarter of a second of load-adjusted time from now on.
    /// </summary>
    private async Task MakeMachineBusyAsync()
    {
        // A deadline starts the clock's sampling, if nothing has yet.
        _clock.Start(TimeSpan.Zero).Dispose();
        _machine.SetLoad(busyShare: 0.75, ownCores: 0);
        await AdvanceAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Advances the fake time by <paramref name="total"/>, a <paramref name="step"/> at a time, pausing 5 ms of real time after each.
    /// </summary>
    private async Task AdvanceAsync(TimeSpan total, TimeSpan step)
    {
        for (TimeSpan passed = TimeSpan.Zero; passed < total; passed += step)
        {
            _time.Advance(step);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Answers the stop's ping and its shutdown as a game that quits does, then ends the game, with what it started or alone,
    /// and waits for it to be gone.
    /// </summary>
    private static async Task QuitAsync(FakeBridge bridge, Process game, bool withItsChildren)
    {
        await AnswerAsync(bridge, "ping", new JsonObject { ["pong"] = true });
        await AnswerAsync(bridge, "shutdown", new JsonObject());
        game.Kill(entireProcessTree: withItsChildren);
        Assert.True(await ProcessExit.WaitUntilGoneAsync(game, RegistryHarness.HangGuard), "the quitting game was not gone");
    }

    /// <summary>Answers the next request for <paramref name="command"/> with <paramref name="result"/>, leaving those before it unanswered.</summary>
    private static async Task AnswerAsync(FakeBridge bridge, string command, JsonObject result)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        while (true)
        {
            JsonObject request = await bridge.ReadRequestAsync(cancellation).WaitAsync(RegistryHarness.HangGuard, cancellation);
            if (request["command"]?.GetValue<string>() == command)
            {
                await bridge.ReplyAsync(request, result, cancellation);
                return;
            }
        }
    }

    /// <summary>The product's own open of a stopped game, which lets the game's held exit go on once the game is opened.</summary>
    private static Process? ReleasedOnceOpened(int pid, DateTime started, ExitHold hold)
    {
        Process? opened = RunKill.OpenIfStillRunning(pid, started);
        hold.Release();
        return opened;
    }

    /// <summary>
    /// Launches a run named server whose game is a stand-in with its exit held, then stops it: the silent game is killed and is
    /// still running when the stop's kill wait ends, as the stop's warning says.
    /// </summary>
    private async Task<(Process Game, ExitHold Hold)> StopWithTheGameHeldAsync(string project)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Process game = StartGame();
        ExitHold hold = await ExitHold.AttachAsync(game, RegistryHarness.HangGuard);
        _holds.Add(hold);
        Task<LaunchResult> launch = _sessions.LaunchAsync(Request(project), "server", cancellation);
        await RegistryHarness.WaitUntilAsync(() => !_launched.IsEmpty, launch);
        FakeRunProcess run = _launched.First();
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, run.Token, project, new FakeHello { ProcessId = game.Id }, cancellation);
        _ = await launch.WaitAsync(RegistryHarness.HangGuard, cancellation);

        // The bridge never answers the stop's ping, so the game counts as silent and is killed at once.
        Task<StopResult> stop = _sessions.StopAsync("server", cancellation);
        await RegistryHarness.AdvanceUntilDoneAsync(_time, stop, cancellation);
        StopResult stopped = await stop;

        Assert.True(stopped.Killed);
        Assert.EndsWith(StillUsesTheFolder, stopped.Warning, StringComparison.Ordinal);
        Assert.False(game.WaitForExit(0));
        return (game, hold);
    }

    private Process StartGame()
    {
        Process game = RegistryHarness.StartStandInGame();
        _games.Add(game);
        return game;
    }

    /// <summary>
    /// The run's process factory: a fake over a real child process, the one a test set as the next wrapper or else a stand-in,
    /// its start info kept for the bridge's token.
    /// </summary>
    private FakeRunProcess Launch(ProcessStartInfo startInfo, bool _)
    {
        var run = new FakeRunProcess(startInfo, Interlocked.Exchange(ref _nextWrapper, null) ?? RegistryHarness.StartStandInGame());
        _launched.Enqueue(run);
        return run;
    }

    /// <summary>The owners list opened alone, as another server's release holds it: the session's own release waits until it is closed.</summary>
    private FileStream HoldOwnersList() => new(_temp.Combine(OwnersList), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private string Project(string folder)
    {
        string projectDir = _temp.Combine(folder);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "project.godot"), "config_version=5\n");
        return ProjectPaths.Normalise(projectDir);
    }

    private static LaunchRequest Request(string project) => new(project, null, [], [], Quiet: false, ShutOutRealGamepads: false, Prepare: false);

    /// <summary>
    /// A fake run process: a real started child process standing in for the console wrapper, so the run has a pid and an exit;
    /// its output is never fed, and ends when the child exits.
    /// </summary>
    private sealed class FakeRunProcess : IRunProcess, IDisposable
    {
        private Action? _ended;
        private int _endedOnce;

        public FakeRunProcess(ProcessStartInfo startInfo, Process process)
        {
            StartInfo = startInfo;
            Process = process;
            Process.EnableRaisingEvents = true;
            // As a killed run's streams end and call the handler's `ended`, which closes its output file.
            Process.Exited += (_, _) => EndOutput();
        }

        public ProcessStartInfo StartInfo { get; }

        public Process Process { get; }

        /// <summary>The token the run's bridge must prove itself with.</summary>
        public string Token => StartInfo.Environment[GodotCommandLine.TokenVariable]!;

        /// <summary>Called from a handler of the exit subscribed at the start, so after the run's own, while the event is raised.</summary>
        public Action? AfterExitHandlers { get; set; }

        public void Start() =>
            // The child is already running; the run only needs its pid and its exit.
            Process.Exited += (_, _) => AfterExitHandlers?.Invoke();

        public void BeginRead(Action<string> stdout, Action<string> stderr, Action ended) => _ended = ended;

        public void CancelRead()
        {
            // No line is ever fed, so there is nothing to stop passing on.
        }

        /// <summary>Kills the child, waits for it to be gone on the hang guard alone, and lets the run's output end.</summary>
        public async Task EndAsync(CancellationToken cancellationToken)
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }

            await Process.WaitForExitAsync(cancellationToken).WaitAsync(RegistryHarness.HangGuard, cancellationToken);
            EndOutput();
        }

        public void Dispose() => Process.Dispose();

        private void EndOutput()
        {
            if (Interlocked.Increment(ref _endedOnce) == 1)
            {
                _ended?.Invoke();
            }
        }
    }
}
