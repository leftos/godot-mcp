using System.Diagnostics;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// A <see cref="SessionRegistry"/> on its own listener. Disposing it detaches every attached session and stops every live
/// run the way stop_project does (quit, then kill after the grace), then kills and waits for each game's own process, so
/// the probe's folder can be deleted after it.
/// </summary>
internal sealed class SessionHarness : IAsyncDisposable
{
    /// <summary>How long a killed game gets to let go of its project folder before the test fails.</summary>
    private static readonly TimeSpan GameExitWait = TimeSpan.FromSeconds(10);

    // Wall time for every request's and tool's ceiling: another build loading the machine would stretch a short ceiling into
    // its backstop and change its text, which the tests assert; the unit tests cover the load on fakes. A launch's handshake
    // and an attach's wait run load-adjusted, as the server's do, so a game slow to start on a busy machine is waited for.
    private readonly LoadClock _wallClock = new(TimeProvider.System, new NoLoadSource());
    private readonly BridgeListener _listener;
    private readonly CancellationTokenSource _stopRestoring = new();
    private readonly Task _restoring = Task.CompletedTask;

    public SessionHarness()
        : this(launchesUnfocused: false) { }

    /// <param name="launchesUnfocused">
    /// Whether its sessions that are not quiet have their windows created unfocused and shown minimized
    /// (<see cref="SessionRegistry.LaunchesUnfocused"/>); the harness then restores each behind the user's windows.
    /// </param>
    public SessionHarness(bool launchesUnfocused)
    {
        _listener = new(NullLogger<BridgeListener>.Instance) { Clock = _wallClock };
        Sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance)
        {
            LaunchClock = LoadClock.Shared,
            LaunchesUnfocused = launchesUnfocused,
        };
        if (launchesUnfocused)
        {
            _restoring = Task.Run(() => RestoreBehindAsync(_stopRestoring.Token));
        }
    }

    public SessionRegistry Sessions { get; }

    /// <summary>
    /// Fails at once when the restore loop has failed, with its exception inside: a wait on a launched window calls it on
    /// every poll, so a broken restore fails the test with its cause rather than at the wait's hang guard.
    /// </summary>
    /// <exception cref="InvalidOperationException">The restore loop failed.</exception>
    public void ThrowIfRestoreFailed()
    {
        if (_restoring.Exception?.InnerException is Exception fault)
        {
            throw new InvalidOperationException($"the harness's restore loop failed: {fault.Message}", fault);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopRestoring.CancelAsync();
        try
        {
            // A restore loop that failed is rethrown here, after the games, the registry and the listener are cleaned up.
            await _restoring;
        }
        finally
        {
            _stopRestoring.Dispose();
            await StopAllAsync();
        }
    }

    /// <summary>
    /// Detaches every attached session, stops every live run, kills and waits for every game, and disposes the registry and listener.
    /// </summary>
    private async Task StopAllAsync()
    {
        // A bare kill does not wait: Windows sets a process's exit code before it closes the process's handles, its current
        // directory among them, so a game's folder stays held a few tens of ms past the exit. Every game's own process is
        // opened before the stop loop and killed and waited for after it; opening the handles first keeps a pid the stop
        // frees from being reused by another process before the kill.
        List<Process> games = OpenGames();
        foreach (SessionInfo session in Sessions.List(includeStopped: true))
        {
            if (session.Kind == "attach")
            {
                await Sessions.DetachAsync(session.Name, CancellationToken.None);
            }
            else if (session.Live)
            {
                await Sessions.StopAsync(session.Name, CancellationToken.None);
            }
        }

        foreach (Process game in games)
        {
            await KillAsync(game);
        }

        Sessions.Dispose();
        _listener.Dispose();
        _wallClock.Dispose();
    }

    /// <summary>
    /// Every 20 ms until stopped, finds each live session's game through its run's process and restores its main window
    /// shown minimized behind the user's windows (<see cref="CaptureWindows.RestoreBehind"/>): the unfocused override shows
    /// it minimized, so it never covers the user's windows, and a minimized window can stop drawing. A run is left alone once
    /// its window is restored and its hello names the same game and window.
    /// </summary>
    private async Task RestoreBehindAsync(CancellationToken stop)
    {
        Dictionary<int, RestoreTarget> targets = [];
        while (!stop.IsCancellationRequested)
        {
            foreach (SessionInfo session in Sessions.List(includeStopped: false))
            {
                if (session.ProcessId is int run && TargetOf(run, targets) is { Done: false } target)
                {
                    Restore(session.Name, run, target);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), stop);
            }
            catch (OperationCanceledException)
            {
                // The harness is being disposed.
                return;
            }
        }
    }

    /// <summary>What the loop knows of a run, remembered from its first sight; null when the run's process has already exited.</summary>
    private static RestoreTarget? TargetOf(int run, Dictionary<int, RestoreTarget> targets)
    {
        if (targets.TryGetValue(run, out RestoreTarget? known))
        {
            return known;
        }

        if (CaptureWindows.StartTimeOf(run) is not DateTime started)
        {
            return null;
        }

        RestoreTarget target = new(started);
        targets[run] = target;
        return target;
    }

    /// <summary>Finds the run's game, restores its minimized main window, and checks both against the hello once it has arrived.</summary>
    private void Restore(string session, int run, RestoreTarget target)
    {
        target.Game ??= CaptureWindows.GameOf(run, target.LaunchStart);
        if (target.Game is not int game)
        {
            return;
        }

        if (target.Window == nint.Zero)
        {
            foreach (nint window in CaptureWindows.MinimizedMainWindowsOf(game))
            {
                CaptureWindows.RestoreBehind(window);
                // Marked once the restore is posted, and never restored again: no Godot path minimizes the main window again.
                target.Window = window;
            }
        }

        target.Done = HelloAgrees(session, run, game, target.Window);
    }

    /// <summary>
    /// Whether the session's hello has arrived and the run is done with: the hello must name the game found for the run and,
    /// once one is restored, its window. A session still starting is checked once its launch or restart has returned.
    /// </summary>
    /// <exception cref="InvalidOperationException">The hello names another game or window than the ones found for the run.</exception>
    private bool HelloAgrees(string session, int run, int game, nint window)
    {
        GodotSession hello = Sessions.Resolve(session);
        // A restart's new run is the session's before its hello is: until the restart returns, the pid is the old game's.
        if (hello.IsStarting || hello.GameProcessId is not int helloGame)
        {
            return false;
        }

        if (helloGame != game || (window != nint.Zero && hello.WindowHandle != window))
        {
            throw new InvalidOperationException(
                $"the harness took pid {game} (window {window}) for the game of run {run}, but the hello names pid {helloGame} "
                    + $"(window {hello.WindowHandle})"
            );
        }

        return window != nint.Zero;
    }

    /// <summary>Opens a handle on every session's game, live or long stopped; a session without the bridge's pid is skipped.</summary>
    private List<Process> OpenGames()
    {
        List<Process> games = [];
        foreach (SessionInfo session in Sessions.List(includeStopped: true))
        {
            if (session.GameProcessId is not int processId)
            {
                continue;
            }

            try
            {
                var game = Process.GetProcessById(processId);
                // Reading Handle opens the process handle and keeps it on the object, so the handle is held before the stop frees the pid.
                _ = game.Handle;
                games.Add(game);
            }
            catch (ArgumentException)
            {
                // The game exited before its session was disposed.
            }
        }

        return games;
    }

    /// <summary>
    /// Kills a game with its tree at once, since a stopped or detached game that is still running never exits on its own, then
    /// waits for it to let go of the folder; one that outlives the wait is a leak, not a slow exit.
    /// </summary>
    private static async Task KillAsync(Process game)
    {
        using (game)
        {
            try
            {
                game.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The game exited between the handle's opening and the kill; the wait below still waits for its signal.
            }

            if (!await ProcessExit.WaitUntilGoneAsync(game, GameExitWait))
            {
                throw new InvalidOperationException(
                    $"the game (pid {game.Id}) still holds its project folder {GameExitWait.TotalSeconds:0} s after it was killed"
                );
            }
        }
    }

    /// <summary>What the restore loop knows of one run: when it started, its game once found, and the main window it restored.</summary>
    private sealed class RestoreTarget(DateTime launchStart)
    {
        public DateTime LaunchStart { get; } = launchStart;

        public int? Game { get; set; }

        public nint Window { get; set; }

        public bool Done { get; set; }
    }
}
