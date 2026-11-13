namespace GodotMcp.Server.Session;

/// <summary>
/// The token a scene's steps play on: cancelled by the run's cancellation, by its own <see cref="Source"/> (the memory watch's)
/// and, until <see cref="ReleaseCeiling"/>, by the scene's ceiling. A derived ceiling is released at the exit hold's start, so
/// a hold a slowed game takes long to run is bounded by its own stall guard (<see cref="ScratchHoldWait"/>) rather than by a
/// ceiling sized for an unslowed game; a ceiling that passed before the release still ends the play.
/// </summary>
internal sealed class ScratchPlayLimit : IDisposable
{
    private readonly CancellationTokenRegistration _ceiling;

    /// <summary>A play limit that <paramref name="ceiling"/>'s expiry and <paramref name="run"/> cancel.</summary>
    public ScratchPlayLimit(LoadDeadline ceiling, CancellationToken run)
    {
        Source = CancellationTokenSource.CreateLinkedTokenSource(run);
        _ceiling = ceiling.Token.Register(Source.Cancel);
    }

    /// <summary>The play's source: cancelling it cancels the play.</summary>
    public CancellationTokenSource Source { get; }

    /// <summary>The token the steps play on.</summary>
    public CancellationToken Token => Source.Token;

    /// <summary>Stops the ceiling cancelling the play, once a cancellation it already started has finished.</summary>
    /// <exception cref="OperationCanceledException">The play was cancelled before the release: the ceiling passed first.</exception>
    public void ReleaseCeiling()
    {
        // Dispose waits for a callback already running, so the check after it sees a ceiling that passed meanwhile.
        _ceiling.Dispose();
        Token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        _ceiling.Dispose();
        Source.Dispose();
    }
}
