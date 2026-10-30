using System.Collections.Concurrent;
using System.Diagnostics;
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
/// test wants. The clocks are fake and advance only where a test says so. A run whose process ends on its own has its exit
/// handled, the folder released, before the session says so.
/// </summary>
[Collection(LaunchRefusesNoGlTests.GodotPathCollection)]
public sealed class StopKilledGameTests : IAsyncDisposable
{
    private const string StillUsesTheFolder = "; its project folder stays in use until it is gone.";

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LoadClock _clock;
    private readonly BridgeListener _listener;
    private readonly SessionRegistry _sessions;
    private readonly ConcurrentQueue<FakeRunProcess> _launched = new();
    private readonly List<ExitHold> _holds = [];
    private readonly List<Process> _games = [];
    private readonly string? _configuredGodot;

    public StopKilledGameTests()
    {
        _clock = new LoadClock(_time, new NoLoadSource());
        _listener = new BridgeListener(NullLogger<BridgeListener>.Instance) { Clock = _clock };
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance)
        {
            LaunchClock = _clock,
            OverrideFolders = new OverrideFolders(_temp.Combine("override-folders.txt"), TextWriter.Null) { Clock = _clock },
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

    /// <summary>The run's process factory: a fake over a real child process, its start info kept for the bridge's token.</summary>
    private FakeRunProcess Launch(ProcessStartInfo startInfo, bool _)
    {
        var run = new FakeRunProcess(startInfo);
        _launched.Enqueue(run);
        return run;
    }

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

        public FakeRunProcess(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            Process = RegistryHarness.StartStandInGame();
            Process.EnableRaisingEvents = true;
            // As a killed run's streams end and call the handler's `ended`, which closes its output file.
            Process.Exited += (_, _) => EndOutput();
        }

        public ProcessStartInfo StartInfo { get; }

        public Process Process { get; }

        /// <summary>The token the run's bridge must prove itself with.</summary>
        public string Token => StartInfo.Environment[GodotCommandLine.TokenVariable]!;

        public void Start()
        {
            // The child is already running; the run only needs its pid and its exit.
        }

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
