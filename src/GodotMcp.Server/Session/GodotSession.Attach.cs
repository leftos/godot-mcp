using GodotMcp.Server.Wire;

namespace GodotMcp.Server.Session;

/// <summary>
/// Attaching to a game the server did not launch (a second client, a server run, a script, the editor's Play button):
/// the bridge is injected the same way, but the game is found through the attach file instead of the environment, and
/// the session has no process, no captured output and never stops the game.
/// </summary>
internal sealed partial class GodotSession
{
    /// <summary>The attached game's bridge connection; the server holds no process for it.</summary>
    private BridgeConnection? _attached;

    /// <summary>The dormant game this attach joins, by its process id; null for an attach that waits for a launch.</summary>
    private int? _joinPid;

    /// <summary>
    /// Injects the bridge into the project and writes the one-use attach file, or the join file of the dormant game
    /// <paramref name="joinPid"/>, then waits up to <paramref name="wait"/> for the game to dial in. That file is gone whatever
    /// the outcome; the override file stays for the session, or is released when no game connected, and a failed attach leaves
    /// the registry without this session.
    /// </summary>
    /// <param name="bridgeScript">The bridge script the override's autoload names.</param>
    /// <param name="joinPid">The dormant game to join; null to wait for a game started on the project after the call.</param>
    /// <param name="wait">How long to wait for the game's bridge.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="SessionException">The project has its own override.cfg, or no game connected in time.</exception>
    public async Task<AttachResult> AttachAsync(string bridgeScript, int? joinPid, TimeSpan wait, CancellationToken cancellationToken)
    {
        _joinPid = joinPid;
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _attached = await InjectAndAwaitBridgeAsync(bridgeScript, wait, cancellationToken);
                GameProcessId = _attached.GameProcessId;
                _ = ClearSnapshotsWhenClosedAsync(_attached);
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
        return new AttachResult(Name, ProjectDir, Quiet) { Window = _attached?.Window, JoinedPid = joinPid };
    }

    /// <summary>Closes the attached game's connection, drops the session and releases the override file; the game keeps running.</summary>
    /// <exception cref="SessionException">The session is not an attached one.</exception>
    public async Task<DetachResult> DetachAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            BridgeConnection attached = _attached ?? throw new SessionException(DescribeNothingToDetach());
            _attached = null;
            registry.Captures.End(Name, CaptureStore.EndedByStop);
            await attached.DisposeAsync();
            registry.Forget(this);
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
        BridgeConnection connection;
        try
        {
            if (_joinPid is int pid)
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
        AttachFile.Write(ProjectDir, endpoint, ShutOutRealGamepads, Quiet);
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
            DormantGames.WriteJoinFile(ProjectDir, pid, endpoint, ShutOutRealGamepads, Quiet);
        }
        catch
        {
            await abandon.CancelAsync();
            await ((Task)accepted).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        return await accepted;
    }

    /// <summary>Deletes the file that told the game where to dial: the join file of a join, else the attach file.</summary>
    private void RemoveHandoffFile()
    {
        if (_joinPid is int pid)
        {
            DormantGames.RemoveJoinFile(ProjectDir, pid);
        }
        else
        {
            AttachFile.Remove(ProjectDir);
        }
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
            Log.CleanupFailed(_logger, e, _joinPid is null ? "attach file" : "join file", ProjectDir);
        }
    }

    private string DescribeAttachTimeout(TimeSpan wait, LoadDeadline? deadline, bool overrideRemoved)
    {
        string backstop = deadline is { Reason: DeadlineReason.Backstop } ? deadline.BackstopClause() : string.Empty;
        if (_joinPid is int pid)
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
