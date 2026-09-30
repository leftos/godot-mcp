using System.ComponentModel;
using System.Diagnostics;
using GodotMcp.Server.Wire;

namespace GodotMcp.Server.Session;

/// <summary>
/// Attaching to a game the server did not launch (a second client, a server run, a script, the editor's Play button):
/// the bridge is injected the same way, but the game is found through the attach file instead of the environment, and
/// the session has no process of its own and no captured output. A detach leaves the game running; a stop quits it as it
/// quits a run, killing the game's own process when it does not quit.
/// </summary>
internal sealed partial class GodotSession
{
    /// <summary>The attached game's bridge connection; the server started no process for it.</summary>
    private BridgeConnection? _attached;

    /// <summary>
    /// The attached game's own process, opened from its hello's pid so its exit code stays readable once it exits; null
    /// without a pid, when it could not be opened, and once the session ends.
    /// </summary>
    private Process? _attachedGame;

    /// <summary>
    /// The token of the attach or join file this attach wrote, the one a game told by that file dials with; null before it
    /// wrote one.
    /// </summary>
    private string? _handoffToken;

    /// <summary>
    /// The dormant game this attach joins, by its process id, chosen when the session was reserved; null for an attach that
    /// waits for a launch, and for a run.
    /// </summary>
    public int? JoinPid { get; } = spec.JoinPid;

    /// <summary>
    /// Injects the bridge into the project and writes the one-use attach file, or the join file of the dormant game
    /// <see cref="JoinPid"/>, then waits up to <paramref name="wait"/> for the game to dial in. That file is gone whatever
    /// the outcome; the override file stays for the session, or is released when no game connected, and a failed attach leaves
    /// the registry without this session.
    /// </summary>
    /// <param name="bridgeScript">The bridge script the override's autoload names.</param>
    /// <param name="wait">How long to wait for the game's bridge.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="SessionException">The project has its own override.cfg, or no game connected in time.</exception>
    public async Task<AttachResult> AttachAsync(string bridgeScript, TimeSpan wait, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _attached = await InjectAndAwaitBridgeAsync(bridgeScript, wait, cancellationToken);
                GameProcessId = _attached.GameProcessId;
                _ = ClearSnapshotsWhenClosedAsync(_attached);
                // Last, since it cannot fail: an attach that fails after it would leave the handle open.
                KeepAttachedGameHandle();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionException(DescribeAttachTimeout(wait, null, AbandonStart(forget: true)), e);
        }
        catch (LoadTimeoutException e)
        {
            throw new SessionException(DescribeAttachTimeout(wait, e.Deadline, AbandonStart(forget: true)), e);
        }
        catch
        {
            AbandonStart(forget: true);
            throw;
        }
        finally
        {
            _pending = false;
        }

        Log.Attached(_logger, ProjectDir);
        return new AttachResult(Name, ProjectDir, Quiet) { Window = _attached?.Window, JoinedPid = JoinPid };
    }

    /// <summary>Closes the attached game's connection, drops the session and releases the override file; the game keeps running.</summary>
    /// <exception cref="SessionException">The session is not an attached one.</exception>
    public async Task<DetachResult> DetachAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            BridgeConnection attached = _attached ?? throw new SessionException(DescribeNothingToDetach());
            await EndAttachmentAsync(attached);
            bool removed = registry.ReleaseFolder(this);
            Log.Detached(_logger, ProjectDir);
            return new DetachResult(Name, ProjectDir, removed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends the attached game as a run's stop ends a run: one silent for <see cref="HangProbe.PingTimeout"/> is killed at once,
    /// one that answers is asked to quit and its own process killed if it has not exited within the grace. Then the session
    /// ends as a detach ends it. The caller holds the gate.
    /// </summary>
    /// <exception cref="SessionException">The attach never reached a game.</exception>
    private async Task<StopResult> StopAttachedAsync()
    {
        BridgeConnection attached =
            _attached ?? throw new SessionException("No game is attached, so there is nothing to stop. attach_project starts one.");
        Process? game = _attachedGame;
        bool alreadyExited = !attached.IsOpen || HasExited(game);
        string? debugged = WarnIfDebugged(!alreadyExited);
        RunEnd ended = alreadyExited
            ? new RunEnd(Killed: false, Warning: null, KillReason: null, LeftRunning: [], QuitMs: null)
            : await QuitAttachedGameAsync(attached, game);
        // Read before the handle goes with the session; the code read after a kill is not the game's own.
        int? gameExitCode = ended.Killed ? null : ExitCodeOf(game);
        await EndAttachmentAsync(attached);
        bool removed = registry.ReleaseFolder(this);
        return new StopResult(Name, ProjectDir, ExitCode: null, ended.Killed, removed)
        {
            AlreadyExited = alreadyExited,
            GameExitCode = gameExitCode,
            KillReason = ended.KillReason,
            QuitMs = ended.QuitMs,
            Warning = debugged ?? ended.Warning,
        };
    }

    /// <summary>
    /// Quits a connected attached game. Without a handle on its process it is only asked to quit, and the stop waits up to the
    /// grace for its connection to close: it cannot be killed, so the warning says it may still be running.
    /// </summary>
    private async Task<RunEnd> QuitAttachedGameAsync(BridgeConnection attached, Process? game)
    {
        if (game is not null)
        {
            return await StopGameAsync(attached, game, () => TerminateAsync(game), () => Task.FromResult<IReadOnlyList<string>>([]));
        }

        await AskToQuitAsync(attached);
        await attached.Closed.WaitAsync(CurrentExitGrace).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return new RunEnd(Killed: false, DescribeUnkillable(), KillReason: null, LeftRunning: [], QuitMs: null);
    }

    private static bool HasExited(Process? game) => game is { HasExited: true };

    /// <summary>The exit code of a game that has exited; null while it runs or without a handle on it.</summary>
    private static int? ExitCodeOf(Process? game) => game is { HasExited: true } exited ? exited.ExitCode : null;

    /// <summary>What a stop warns when it held no handle on the attached game's process, so it could not have killed it.</summary>
    private string DescribeUnkillable() =>
        GameProcessId is int pid
            ? $"The game's process (pid {pid}) could not be opened when it was attached, so the game could not be killed if it did "
                + "not quit; it may still be running."
            : "The game's bridge sent no process id, so the game could not be killed if it did not quit; it may still be running.";

    /// <summary>
    /// Lets go of the attached game, as a detach and a stop both do: its connection, its snapshots, its capture and the handle
    /// on its process, and drops the session from the registry. The caller releases the folder.
    /// </summary>
    private async Task EndAttachmentAsync(BridgeConnection attached)
    {
        _attached = null;
        Snapshots.Clear();
        registry.Captures.End(Name, CaptureStore.EndedByStop);
        await attached.DisposeAsync();
        DisposeAttachedGame();
        registry.Forget(this);
    }

    /// <summary>
    /// Opens and keeps a handle on the attached game's own process, from its hello's pid, so a stop can wait for it, kill it and
    /// read its exit code. A game gone already, or one the server cannot open, is logged and gets no handle.
    /// </summary>
    private void KeepAttachedGameHandle()
    {
        if (GameProcessId is not int pid)
        {
            return;
        }

        Process? game = null;
        try
        {
            game = Process.GetProcessById(pid);
            // Reading Handle opens the process handle and keeps it on the object, which keeps the exit code readable.
            _ = game.Handle;
            _attachedGame = game;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            game?.Dispose();
            Log.GameHandleFailed(_logger, e, pid, ProjectDir);
        }
    }

    private void DisposeAttachedGame()
    {
        _attachedGame?.Dispose();
        _attachedGame = null;
    }

    /// <summary>
    /// Drops the session's snapshots once the attached game's connection ends (the game quit, or a detach closed it), as a
    /// run's exit does: their ids name a game that is gone. A capture still running ends as an exit (a detach has already ended
    /// it as a stop).
    /// </summary>
    private async Task ClearSnapshotsWhenClosedAsync(BridgeConnection connection)
    {
        await connection.Closed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Snapshots.Clear();
        registry.Captures.End(Name, CaptureStore.EndedByExit);
    }

    /// <summary>
    /// Writes the override under the folder's prep lock, so it never lands while a launch prepares or a headless run (which
    /// reads override.cfg) holds the folder; the lock is let go before the wait for the game.
    /// </summary>
    private async Task WriteOverrideUnderPrepLockAsync(string bridgeScript, CancellationToken cancellationToken)
    {
        SemaphoreSlim folderLock = registry.PrepLock(ProjectDir);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            registry.WriteOverride(this, bridgeScript);
            GitExclude.Ensure(ProjectDir, OverrideFile.FileName, _logger);
        }
        finally
        {
            folderLock.Release();
        }
    }

    /// <summary>
    /// Writes the attach or join file and the override, waits for the game, and removes that file before the connection is
    /// kept, so a failed removal closes the connection instead of leaking it.
    /// </summary>
    private async Task<BridgeConnection> InjectAndAwaitBridgeAsync(string bridgeScript, TimeSpan wait, CancellationToken cancellationToken)
    {
        BridgeEndpoint endpoint = new(registry.Listener.Port, CreateToken());
        _handoffToken = endpoint.Token;
        BridgeConnection connection;
        try
        {
            if (JoinPid is int pid)
            {
                // The override goes through the prep lock as an attach's does, before the join file wakes the game.
                await WriteOverrideUnderPrepLockAsync(bridgeScript, cancellationToken);
                connection = await JoinDormantGameAsync(endpoint, pid, wait, cancellationToken);
            }
            else
            {
                connection = await AwaitLaunchedGameAsync(bridgeScript, endpoint, wait, cancellationToken);
            }
        }
        catch
        {
            RemoveHandoffFileAfterFailure();
            throw;
        }

        try
        {
            RemoveHandoffFile();
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        connection.OnErrors(Errors.Receive);
        connection.OnCaptured(ReceiveCaptured);
        return connection;
    }

    private async Task<BridgeConnection> AwaitLaunchedGameAsync(
        string bridgeScript,
        BridgeEndpoint endpoint,
        TimeSpan wait,
        CancellationToken cancellationToken
    )
    {
        // The attach file goes first: a game that starts between the two writes then finds it once override.cfg loads the bridge.
        // Under the folder list's hold, so it never lands between another server's read of the file's token and its delete.
        registry.OverrideFolders.Hold(
            $"writing the attach file in {ProjectDir}",
            () => AttachFile.Write(ProjectDir, endpoint, new ArmSettings(Quiet, ShutOutRealGamepads, Mute))
        );
        await WriteOverrideUnderPrepLockAsync(bridgeScript, cancellationToken);
        return await AcceptAttachedBridgeAsync(new HandshakeExpectation(endpoint.Token, ProjectDir), wait, cancellationToken);
    }

    /// <summary>
    /// Joins the dormant game <paramref name="pid"/>: the join file is written only once the wait is registered, since the game
    /// dials within one poll of it and the listener refuses a hello whose token no session awaits.
    /// </summary>
    private async Task<BridgeConnection> JoinDormantGameAsync(BridgeEndpoint endpoint, int pid, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<BridgeConnection> accepted = AcceptAttachedBridgeAsync(new HandshakeExpectation(endpoint.Token, ProjectDir), wait, abandon.Token);
        try
        {
            registry.OverrideFolders.Hold(
                $"writing the join file in {ProjectDir}",
                () => DormantGames.WriteJoinFile(ProjectDir, pid, endpoint, new ArmSettings(Quiet, ShutOutRealGamepads, Mute))
            );
        }
        catch
        {
            await abandon.CancelAsync();
            await ((Task)accepted).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        return await accepted;
    }

    /// <summary>
    /// Deletes the file that told the game where to dial, the attach file or a join's join file, while it still carries this
    /// attach's token, since another server's attach, or another join of the same game, may have written its own since. The
    /// token is read and the file deleted under the folder list's hold, so no other server's write lands between the two.
    /// </summary>
    private void RemoveHandoffFile()
    {
        if (_handoffToken is not { } token)
        {
            return;
        }

        registry.OverrideFolders.Hold(
            $"removing the handoff file in {ProjectDir}",
            () =>
            {
                if (JoinPid is int pid)
                {
                    DormantGames.RemoveJoinFile(ProjectDir, pid, token);
                }
                else
                {
                    AttachFile.Remove(ProjectDir, token);
                }
            }
        );
    }

    private async Task<BridgeConnection> AcceptAttachedBridgeAsync(HandshakeExpectation expected, TimeSpan wait, CancellationToken cancellationToken)
    {
        using LoadDeadline deadline = registry.LaunchClock.Start(wait, cancellationToken);
        try
        {
            return await registry.Listener.AcceptBridgeAsync(expected, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.Expired)
        {
            throw new LoadTimeoutException($"No game connected within {wait.TotalSeconds:0} s.", deadline);
        }
    }

    /// <summary>
    /// Removes the attach or join file after a failed attach; a failure is logged so the attach's own error still reaches the
    /// caller.
    /// </summary>
    private void RemoveHandoffFileAfterFailure()
    {
        try
        {
            RemoveHandoffFile();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.CleanupFailed(_logger, e, JoinPid is null ? "attach file" : "join file", ProjectDir);
        }
    }

    private string DescribeAttachTimeout(TimeSpan wait, LoadDeadline? deadline, bool overrideRemoved)
    {
        string backstop = deadline is { Reason: DeadlineReason.Backstop } ? deadline.BackstopClause() : string.Empty;
        if (JoinPid is int pid)
        {
            return $"The dormant game (pid {pid}) on {ProjectDir} did not answer within {wait.TotalSeconds:0} s{backstop}, so the join is "
                + "abandoned and its join file is removed. It may be paused under a debugger or frozen; resume it, then call "
                + "attach_project again.";
        }

        string removed = overrideRemoved
            ? "its override.cfg and attach file are removed"
            : "its attach file is removed (override.cfg stays while another live session uses the folder or it is armed)";
        return $"No game on {ProjectDir} connected within {wait.TotalSeconds:0} s{backstop}, so the attach is abandoned and {removed}. A game "
            + "attaches only when it starts after attach_project has written them: launch it (a script, godot --path <project>, "
            + "the editor's Play button) within waitSeconds of the call, then call attach_project again, or arm_project the "
            + "folder first so a game already running can be joined.";
    }

    private string DescribeNothingToDetach()
    {
        if (Kind == SessionKind.Attach)
        {
            return "No session is attached, so there is nothing to detach. attach_project starts one.";
        }

        return _run is { IsRunning: true }
            ? $"The session for {ProjectDir} was started by run_project; stop_project ends it."
            : $"Session '{Name}' was started by run_project; use stop_project.";
    }
}
