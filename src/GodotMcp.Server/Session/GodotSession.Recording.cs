using System.ComponentModel;
using System.Diagnostics;

namespace GodotMcp.Server.Session;

/// <summary>
/// Recording a run with Godot's Movie Maker: each start of a recording run writes a new movie, and once that run has ended
/// (stopped, quit on its own or replaced by a restart) its marked clips are cut and its outcome kept on the session.
/// </summary>
internal sealed partial class GodotSession
{
    /// <summary>
    /// How long a stop waits for a recording game's clean quit, which finalises its movie: a killed game leaves a file with no
    /// duration (creating_movies.rst L291-301 in the 4.7 docs), and the writer's end runs in the engine's cleanup (main.cpp
    /// L5240-5241 in 4.7.2).
    /// </summary>
    private static readonly TimeSpan RecordingExitGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the cut waits for the game's own process to exit: the Godot_console.exe wrapper the server started can exit
    /// before the Godot.exe it wraps has let go of its files.
    /// </summary>
    private static readonly TimeSpan GameExitWait = TimeSpan.FromSeconds(10);

    private Recording? _recording;

    /// <summary>The recording of the session's run while that run goes on; null when it does not record or has ended.</summary>
    public Recording? ActiveRecording => _recording is { Outcome: null } recording && HasGame ? recording : null;

    /// <summary>
    /// The session's recording as list_sessions shows it: a Movie Maker run's file while the run goes on and how it ended
    /// afterwards, or else the session's real-time recording (<see cref="RealtimeState"/>); null when there is neither.
    /// </summary>
    public RecordingResult? RecordingState =>
        _recording is { } recording ? recording.Outcome ?? new RecordingResult { Mode = Recording.MovieMode, Path = recording.Path } : RealtimeState;

    /// <summary>
    /// The grace a stop gives the current run to quit before it is killed: <see cref="SessionRegistry.ExitGrace"/>, or the
    /// longer of it and <see cref="RecordingExitGrace"/> while a recording is being written.
    /// </summary>
    private TimeSpan CurrentExitGrace =>
        _recording is { Outcome: null } ? TimeSpan.FromTicks(Math.Max(RecordingExitGrace.Ticks, registry.ExitGrace.Ticks)) : registry.ExitGrace;

    /// <summary>A new movie's path, its folder created, for a start of a recording run; null for a run that does not record.</summary>
    private string? PrepareMoviePath(LaunchRequest request)
    {
        if (!request.Record)
        {
            return null;
        }

        string path = Recording.PathFor(ProjectDir, Name, DateTime.UtcNow);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>
    /// Under the session's gate, once <paramref name="run"/>, the run that recorded, has ended: cuts its recording's clips, once,
    /// and keeps the outcome. The cut waits for the game's exit and then for the end of the run's bridge connection, so a call
    /// in flight as the game went is noted first. A second call returns the kept outcome.
    /// </summary>
    /// <returns>The outcome; null when the run did not record.</returns>
    private async Task<RecordingResult?> FinishRecordingAsync(GodotRun run)
    {
        if (_recording is not { } recording)
        {
            return null;
        }

        if (recording.Outcome is null)
        {
            await WaitForGameExitAsync();
            await WaitForBridgeEndAsync(run);
            recording.Outcome = await RecordingCut.FinishAsync(recording, _logger, CancellationToken.None);
        }

        return recording.Outcome;
    }

    /// <summary>
    /// Waits up to <see cref="GameExitWait"/> for <paramref name="run"/>'s bridge connection to end, so its read loop has noted
    /// any call in flight on the recording. A run whose connection is already disposed (a stop or restart ends it first) or
    /// that never connected has nothing to wait for; a connection still open then is logged and the cut goes on.
    /// </summary>
    private async Task WaitForBridgeEndAsync(GodotRun run)
    {
        if (run.Connection is not { } connection)
        {
            return;
        }

        await connection.Closed.WaitAsync(GameExitWait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!connection.Closed.IsCompleted)
        {
            Log.BridgeEndWaitEnded(_logger, Name, GameExitWait.TotalSeconds);
        }
    }

    /// <summary>
    /// Waits up to <see cref="GameExitWait"/> for the game's own process. A game still running then is cut anyway; a cut that
    /// fails on the unfinished file is reported in the outcome.
    /// </summary>
    private async Task WaitForGameExitAsync()
    {
        if (GameProcessId is not { } gameProcessId)
        {
            return;
        }

        try
        {
            using var game = Process.GetProcessById(gameProcessId);
            if (!await ProcessExit.WaitUntilGoneAsync(game, GameExitWait))
            {
                Log.GameExitWaitEnded(
                    _logger,
                    new TimeoutException($"the game (pid {gameProcessId}) was still running {GameExitWait.TotalSeconds:0} s after its run ended"),
                    ProjectDir,
                    GameExitWait.TotalSeconds
                );
            }
        }
        catch (ArgumentException e)
        {
            // No process has the id any more: the game has already exited, which is what the wait is for.
            Log.GameAlreadyExited(_logger, e, ProjectDir, gameProcessId);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            Log.GameExitNotObserved(_logger, e, ProjectDir, gameProcessId);
        }
    }
}
