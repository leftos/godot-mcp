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

    public SessionHarness()
    {
        _listener = new(NullLogger<BridgeListener>.Instance) { Clock = _wallClock };
        Sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance) { LaunchClock = LoadClock.Shared };
    }

    public SessionRegistry Sessions { get; }

    public async ValueTask DisposeAsync()
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

    /// <summary>Kills a game and waits for it to let go of the folder; one that outlives the wait is a leak, not a slow exit.</summary>
    private static async Task KillAsync(Process game)
    {
        using (game)
        {
            if (await ProcessExit.WaitUntilGoneAsync(game, GameExitWait))
            {
                return;
            }

            try
            {
                game.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The game exited between the wait and the kill.
                return;
            }

            if (!await ProcessExit.WaitUntilGoneAsync(game, GameExitWait))
            {
                throw new InvalidOperationException(
                    $"the game (pid {game.Id}) still holds its project folder {GameExitWait.TotalSeconds:0} s after it was killed"
                );
            }
        }
    }
}
