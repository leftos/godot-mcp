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

    // Wall time: another build loading the machine would stretch a short ceiling into its backstop and change its text; the
    // unit tests cover the load on fakes.
    private readonly LoadClock _wallClock = new(TimeProvider.System, new NoLoadSource());
    private readonly BridgeListener _listener;

    public SessionHarness()
    {
        _listener = new(NullLogger<BridgeListener>.Instance) { Clock = _wallClock };
        Sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
    }

    public SessionRegistry Sessions { get; }

    public async ValueTask DisposeAsync()
    {
        // A bare kill does not wait: Godot_console.exe exits before the Godot.exe it wraps lets go of the folder, so every
        // game's own process is opened before the stop loop and killed and waited for after it. Opening the handles first
        // keeps a pid the stop frees from being reused by another process before the kill.
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
                games.Add(Process.GetProcessById(processId));
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
            try
            {
                if (game.HasExited)
                {
                    return;
                }

                game.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The game exited between the open and the kill.
                return;
            }

            using CancellationTokenSource wait = new(GameExitWait);
            try
            {
                await game.WaitForExitAsync(wait.Token);
            }
            catch (OperationCanceledException e)
            {
                throw new InvalidOperationException(
                    $"the game (pid {game.Id}) still holds its project folder {GameExitWait.TotalSeconds:0} s after it was killed",
                    e
                );
            }
        }
    }
}
