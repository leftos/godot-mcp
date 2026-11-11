using GodotMcp.Server.Wire;

namespace GodotMcp.Server.Session;

/// <summary>
/// A registered wait for a bridge's hello, held from before the process that dials it starts: its deadline, the token source
/// that withdraws it, and the accept a hello completes, held from registration so a hello that comes before anyone awaits it is
/// kept. Disposing it withdraws the wait and closes a connection it accepted that nobody took.
/// </summary>
internal sealed class HandshakeWait : IDisposable
{
    private readonly CancellationTokenSource _timeout;
    private bool _taken;
    private int _disposed;

    /// <param name="clock">The clock the deadline runs on.</param>
    /// <param name="listener">The listener the wait is registered with.</param>
    /// <param name="expected">What the hello must carry.</param>
    /// <param name="budget">The handshake's budget, counted from now.</param>
    /// <param name="cancellationToken">The caller's token; cancelling it withdraws the wait.</param>
    public HandshakeWait(
        LoadClock clock,
        BridgeListener listener,
        HandshakeExpectation expected,
        TimeSpan budget,
        CancellationToken cancellationToken
    )
    {
        Deadline = clock.Start(budget, cancellationToken);
        _timeout = CancellationTokenSource.CreateLinkedTokenSource(Deadline.Token);
        Accept = listener.AcceptBridgeAsync(expected, _timeout.Token);
    }

    /// <summary>The handshake's deadline, started at registration.</summary>
    public LoadDeadline Deadline { get; }

    /// <summary>The accept a matching hello completes.</summary>
    public Task<BridgeConnection> Accept { get; }

    /// <summary>Cancelled when the deadline expires, the caller cancels, or the wait is withdrawn.</summary>
    public CancellationToken Token => _timeout.Token;

    /// <summary>Withdraws the wait.</summary>
    public Task CancelAsync() => _timeout.CancelAsync();

    /// <summary>
    /// The accept, handed to the caller, who from now on owns the connection it accepted: the wait's dispose leaves it open.
    /// </summary>
    public Task<BridgeConnection> TakeAccept()
    {
        _taken = true;
        return Accept;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _timeout.Cancel();
        if (!_taken)
        {
            _ = ReleaseAsync(Accept);
        }

        _timeout.Dispose();
        Deadline.Dispose();
    }

    /// <summary>Observes an accept nobody took and closes the connection it accepted, if a hello won the withdrawal.</summary>
    private static async Task ReleaseAsync(Task<BridgeConnection> accept)
    {
        try
        {
            BridgeConnection connection = await accept;
            await connection.DisposeAsync();
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            // The wait was withdrawn, the listener stopped, or the connection ended: nothing is left to close.
        }
    }
}
