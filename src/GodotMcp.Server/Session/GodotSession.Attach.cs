using GodotMcp.Server.Wire;

namespace GodotMcp.Server.Session;

/// <summary>
/// Attaching to a game the server did not launch (a second client, a server run, a script, the editor's Play button):
/// the bridge is injected the same way, but the game is found through the attach file instead of the environment, and
/// the session has no process, no captured output and never stops the game.
/// </summary>
internal sealed partial class GodotSession
{
    private AttachedRun? _attached;
    private string? _attachingDir;

    /// <summary>Whether the session is one attach_project started and detach_project has not ended.</summary>
    public bool IsAttached => _attached is not null;

    /// <summary>
    /// Injects the bridge into the project and writes the one-use attach file, then waits up to <paramref name="wait"/> for a
    /// game started on the project to dial in. The attach file is gone whatever the outcome; the override file stays for the
    /// session, or is removed when no game connected.
    /// </summary>
    /// <param name="projectPath">The project folder.</param>
    /// <param name="wait">How long to wait for the game's bridge.</param>
    /// <param name="shutOutRealGamepads">Whether the bridge shuts the machine's real pads out of the game.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="SessionException">A session is live, the project is missing, or no game connected in time.</exception>
    public async Task<AttachResult> AttachAsync(string projectPath, TimeSpan wait, bool shutOutRealGamepads, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RetirePreviousRunAsync();
            string projectDir = NormaliseProjectDir(projectPath);
            string bridgeScript = Installation.FindBridgeScript();
            _attachingDir = projectDir;
            try
            {
                BridgeConnection connection = await InjectAndAwaitBridgeAsync(projectDir, bridgeScript, shutOutRealGamepads, wait, cancellationToken);
                _attached = new AttachedRun(projectDir, connection);
            }
            finally
            {
                _attachingDir = null;
                AttachFile.Remove(projectDir);
            }

            Log.Attached(logger, projectDir);
            return new AttachResult(projectDir);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the attached game's connection and removes the override file; the game keeps running.</summary>
    /// <exception cref="SessionException">No session is attached.</exception>
    public async Task<DetachResult> DetachAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            AttachedRun attached = _attached ?? throw new SessionException(DescribeNothingToDetach());
            _attached = null;
            await attached.Connection.DisposeAsync();
            bool removed = OverrideFile.Remove(attached.ProjectDir);
            AttachFile.Remove(attached.ProjectDir);
            Log.Detached(logger, attached.ProjectDir);
            return new DetachResult(attached.ProjectDir, removed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BridgeConnection> InjectAndAwaitBridgeAsync(
        string projectDir,
        string bridgeScript,
        bool shutOutRealGamepads,
        TimeSpan wait,
        CancellationToken cancellationToken
    )
    {
        string token = CreateToken();

        // The attach file goes first: a game that starts between the two writes then finds it once override.cfg loads the bridge.
        AttachFile.Write(projectDir, new BridgeEndpoint(listener.Port, token), shutOutRealGamepads);
        try
        {
            OverrideFile.Write(projectDir, bridgeScript, shutOutRealGamepads);
            GitExclude.Ensure(projectDir, OverrideFile.FileName, logger);
            return await AcceptAttachedBridgeAsync(new HandshakeExpectation(token, projectDir), wait, cancellationToken);
        }
        catch
        {
            OverrideFile.Remove(projectDir);
            throw;
        }
    }

    private async Task<BridgeConnection> AcceptAttachedBridgeAsync(HandshakeExpectation expected, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            return await listener.AcceptBridgeAsync(expected, timeout.Token);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionException(
                $"No game on {expected.ProjectPath} connected within {wait.TotalSeconds:0} s, so the attach is abandoned and its "
                    + "override.cfg and attach file are removed. A game attaches only when it starts after attach_project has written "
                    + "them: launch it (a script, godot --path <project>, the editor's Play button) within waitSeconds of the call, "
                    + "then call attach_project again.",
                e
            );
        }
    }

    /// <summary>Fails when an attached game is still connected; otherwise forgets a session whose game has gone.</summary>
    private async Task RetireAttachedAsync()
    {
        if (_attached is null)
        {
            return;
        }

        if (_attached.Connection.IsOpen)
        {
            throw new SessionException($"A session is attached to {_attached.ProjectDir}; detach_project first.");
        }

        await _attached.Connection.DisposeAsync();
        OverrideFile.Remove(_attached.ProjectDir);
        _attached = null;
    }

    private string DescribeNothingToDetach() =>
        _run is { IsRunning: true }
            ? $"The session for {_run.ProjectDir} was started by run_project; stop_project ends it."
            : "No session is attached, so there is nothing to detach. attach_project starts one.";

    /// <summary>The last-resort cleanup at the server's exit: the attached or attaching project's override and attach files.</summary>
    private void RemoveAttachFiles()
    {
        string? projectDir = _attached?.ProjectDir ?? _attachingDir;
        if (projectDir is null)
        {
            return;
        }

        try
        {
            OverrideFile.Remove(projectDir);
            AttachFile.Remove(projectDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging may already be torn down while the process exits, so this goes straight to stderr.
            Console.Error.WriteLine($"godot-mcp: cleanup of {projectDir} at shutdown failed: {e.Message}");
        }
    }

    /// <summary>A game attach_project connected to: only its bridge connection, since the server holds no process for it.</summary>
    private sealed record AttachedRun(string ProjectDir, BridgeConnection Connection);
}
