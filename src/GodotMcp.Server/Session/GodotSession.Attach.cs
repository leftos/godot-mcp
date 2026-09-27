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

    /// <summary>
    /// Injects the bridge into the project and writes the one-use attach file, then waits up to <paramref name="wait"/> for a
    /// game started on the project to dial in. The attach file is gone whatever the outcome; the override file stays for the
    /// session, or is released when no game connected, and a failed attach leaves the registry without this session.
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
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionException(DescribeAttachTimeout(wait, AbandonStart(forget: true)), e);
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
        return new AttachResult(Name, ProjectDir);
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
    /// run's exit does: their ids name a game that is gone.
    /// </summary>
    private async Task ClearSnapshotsWhenClosedAsync(BridgeConnection connection)
    {
        await connection.Closed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Snapshots.Clear();
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
    /// Writes the attach file and the override, waits for the game, and removes the attach file before the connection is
    /// kept, so a failed removal closes the connection instead of leaking it.
    /// </summary>
    private async Task<BridgeConnection> InjectAndAwaitBridgeAsync(string bridgeScript, TimeSpan wait, CancellationToken cancellationToken)
    {
        string token = CreateToken();

        // The attach file goes first: a game that starts between the two writes then finds it once override.cfg loads the bridge.
        AttachFile.Write(ProjectDir, new BridgeEndpoint(registry.Listener.Port, token), ShutOutRealGamepads);
        BridgeConnection connection;
        try
        {
            await WriteOverrideUnderPrepLockAsync(bridgeScript, cancellationToken);
            connection = await AcceptAttachedBridgeAsync(new HandshakeExpectation(token, ProjectDir), wait, cancellationToken);
        }
        catch
        {
            RemoveAttachFileAfterFailure();
            throw;
        }

        try
        {
            AttachFile.Remove(ProjectDir);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        connection.OnErrors(Errors.Receive);
        return connection;
    }

    private async Task<BridgeConnection> AcceptAttachedBridgeAsync(HandshakeExpectation expected, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        return await registry.Listener.AcceptBridgeAsync(expected, timeout.Token);
    }

    /// <summary>Removes the attach file after a failed attach; a failure is logged so the attach's own error still reaches the caller.</summary>
    private void RemoveAttachFileAfterFailure()
    {
        try
        {
            AttachFile.Remove(ProjectDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.CleanupFailed(_logger, e, "attach file", ProjectDir);
        }
    }

    private string DescribeAttachTimeout(TimeSpan wait, bool overrideRemoved)
    {
        string removed = overrideRemoved
            ? "its override.cfg and attach file are removed"
            : "its attach file is removed (override.cfg stays while another live session uses the folder)";
        return $"No game on {ProjectDir} connected within {wait.TotalSeconds:0} s, so the attach is abandoned and {removed}. A game "
            + "attaches only when it starts after attach_project has written them: launch it (a script, godot --path <project>, "
            + "the editor's Play button) within waitSeconds of the call, then call attach_project again.";
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
