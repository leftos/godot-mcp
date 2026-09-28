namespace GodotMcp.Server.Session;

/// <summary>
/// Relaunching a run with the request it was last launched with, under the same session: its name, its error feed and its
/// output buffers carry over. The prep runs while the old game still runs, so a failed build or import leaves it running.
/// </summary>
internal sealed partial class GodotSession
{
    /// <summary>Whether the session is starting: a launch, an attach or a restart has not returned yet.</summary>
    internal bool IsStarting => _pending;

    /// <summary>
    /// Marks the session as starting, so it stays live and its name taken for the whole restart, even while no game runs.
    /// The registry calls it under its lock, once it has checked that the session is a run that launched and is not starting.
    /// </summary>
    internal void BeginRestart() => _pending = true;

    /// <summary>
    /// Under the session's gate: preps the project (unless <paramref name="prepare"/> is false) while the old game runs, stops
    /// it, and launches the last request again. A failure that leaves no game running releases the folder; the session stays
    /// listed with the output it has.
    /// </summary>
    /// <param name="prepare">Whether to build a stale C# assembly and run a due import first.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the prep.</param>
    /// <exception cref="SessionException">The session never launched, the prep failed, or the new game did not start.</exception>
    public async Task<RestartResult> RestartAsync(bool prepare, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await RestartRunAsync(prepare, cancellationToken);
            }
            catch when (!HasGame)
            {
                AbandonStart(forget: false);
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _pending = false;
        }
    }

    private async Task<RestartResult> RestartRunAsync(bool prepare, CancellationToken cancellationToken)
    {
        if (LastLaunch is not { } launched || _run is not { } previous || ProcessId is not { } previousProcessId)
        {
            throw new SessionException(NoneRunning);
        }

        Recording? replaced = _recording;
        bool previousAlreadyExited = !previous.IsRunning;
        try
        {
            (LaunchResult started, RunEnd? previousEnd) = await StartRunAsync(launched with { Prepare = prepare }, previous, cancellationToken);
            int? previousGameExitCode = await previous.ReleaseGameAsync();
            Log.RunRestarted(_logger, ProjectDir, previousProcessId, started.ProcessId);
            return new RestartResult(Name, started.ProjectPath, started.ProcessId, previousProcessId, previous.ExitCode, started.Prep, started.Godot)
            {
                PreviousAlreadyExited = previousAlreadyExited,
                PreviousGameExitCode = previousGameExitCode,
                PreviousKillReason = previousEnd?.KillReason,
                PreviousLeftRunning = LeftRunning(previousEnd),
                Recording = started.Recording,
                PreviousRecording = replaced?.Outcome,
                Warning = previousEnd?.Warning,
            };
        }
        finally
        {
            if (!ReferenceEquals(_run, previous))
            {
                await previous.DisposeAsync();
            }
        }
    }

    /// <summary>The replaced game's leftovers as <see cref="StopResult.LeftRunning"/> reports them: null when there were none.</summary>
    private static IReadOnlyList<string>? LeftRunning(RunEnd? ended)
    {
        if (ended is not RunEnd end || end.LeftRunning.Count == 0)
        {
            return null;
        }

        return end.LeftRunning;
    }
}
