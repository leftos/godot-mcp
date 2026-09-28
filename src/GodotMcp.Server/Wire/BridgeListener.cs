using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Wire;

/// <summary>
/// The loopback socket the bridge dials. It binds an ephemeral port once and keeps listening for the server's lifetime,
/// so the port handed to a launched game is never raced for. One accept loop reads each connection's hello and hands the
/// connection to the waiter registered under the hello's token, so several sessions can wait for their games at once.
/// A connection that has not said hello within <see cref="HelloTimeout"/> is refused, unless a session is still waiting
/// for a bridge: then the read is held open until the last waiter ends, so a game paused at a breakpoint before its first
/// frame is not refused while a session is still waiting for it.
/// </summary>
internal sealed class BridgeListener : IDisposable
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ILogger<BridgeListener> _logger;
    private readonly ConcurrentDictionary<string, Waiter> _waiters = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _startLock = new();
    private readonly Lock _waitersLock = new();
    private Task? _acceptLoop;
    private int _pendingWaiters;
    private TaskCompletionSource _noWaiterPending = CompletedSignal();

    public BridgeListener(ILogger<BridgeListener> logger)
    {
        _logger = logger;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>The clock the connections' reply timeouts run on; tests swap in a fake one.</summary>
    internal LoadClock Clock { get; init; } = LoadClock.Shared;

    /// <summary>
    /// Waits for the connection whose hello carries the expected token and project; any other connection is closed.
    /// Cancelling <paramref name="cancellationToken"/> withdraws the wait, and a bridge that dials after that is closed.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another wait is registered under the same token.</exception>
    public async Task<BridgeConnection> AcceptBridgeAsync(HandshakeExpectation expected, CancellationToken cancellationToken)
    {
        Waiter waiter = new(expected);
        if (!RegisterWaiter(waiter))
        {
            throw new InvalidOperationException("A bridge is already awaited under this session token.");
        }

        EnsureAcceptLoop();
        await using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            if (Withdraw(waiter))
            {
                waiter.Completion.TrySetCanceled(cancellationToken);
            }
        });
        return await waiter.Completion.Task;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Dispose();
        FailWaiters();
        _stopping.Dispose();
    }

    private void FailWaiters()
    {
        foreach (Waiter waiter in _waiters.Values)
        {
            if (Withdraw(waiter))
            {
                waiter.Completion.TrySetException(new ObjectDisposedException(nameof(BridgeListener)));
            }
        }
    }

    /// <summary>Registers the waiter and, when it is the first one, marks a session as waiting for a bridge.</summary>
    private bool RegisterWaiter(Waiter waiter)
    {
        lock (_waitersLock)
        {
            if (!_waiters.TryAdd(waiter.Expected.Token, waiter))
            {
                return false;
            }

            if (_pendingWaiters++ == 0)
            {
                _noWaiterPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return true;
        }
    }

    /// <summary>Removes the waiter and, when it was the last one, marks that no session is waiting for a bridge.</summary>
    private bool Withdraw(Waiter waiter)
    {
        lock (_waitersLock)
        {
            if (!_waiters.TryRemove(new KeyValuePair<string, Waiter>(waiter.Expected.Token, waiter)))
            {
                return false;
            }

            if (--_pendingWaiters == 0)
            {
                _noWaiterPending.TrySetResult();
            }

            return true;
        }
    }

    /// <summary>Completes at the moment the last waiting session ends, and is already complete while none is waiting.</summary>
    private Task NoWaiterPending
    {
        get
        {
            lock (_waitersLock)
            {
                return _noWaiterPending.Task;
            }
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private void EnsureAcceptLoop()
    {
        lock (_startLock)
        {
            _acceptLoop ??= AcceptLoopAsync(_stopping.Token);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(stopping);
                _ = HandleConnectionAsync(client, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException e)
            {
                // The socket is gone for good: nothing more can dial in, so every wait ends now instead of at its timeout.
                Log.RefusedBridgeConnection(_logger, $"the listening socket closed ({e.Message})");
                FailWaiters();
                return;
            }
            catch (SocketException e) when (!stopping.IsCancellationRequested)
            {
                Log.RefusedBridgeConnection(_logger, $"accepting it failed ({e.Message})");
                await Task.Delay(AcceptRetryDelay, stopping).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Reads the connection's hello and hands the connection to its waiter, or closes it.</summary>
    private async Task HandleConnectionAsync(TcpClient client, CancellationToken stopping)
    {
        FrameDecoder decoder = new();
        JsonObject hello;
        try
        {
            client.NoDelay = true;
            hello = await ReadHelloAsync(client, decoder, stopping);
        }
        catch (Exception e)
            when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            Refuse(client, $"no valid hello arrived ({e.Message})");
            return;
        }

        string? token = HandshakeExpectation.ReadString(hello, "token");
        if (token is null || !_waiters.TryGetValue(token, out Waiter? waiter))
        {
            Refuse(client, "no session is waiting for the token its hello carries");
            return;
        }

        string? mismatch = waiter.Expected.FindMismatch(hello);
        if (mismatch is not null)
        {
            Refuse(client, mismatch);
            return;
        }

        await HandOverAsync(waiter, new BridgeConnection(client, decoder, HandshakeExpectation.ReadProcessId(hello), Clock, _logger));
    }

    /// <summary>
    /// Reads the connection's hello, or ends at the point the connection is refused: five seconds after the accept, or, if a
    /// session is still waiting for a bridge, the moment the last waiter ends.
    /// </summary>
    private async Task<JsonObject> ReadHelloAsync(TcpClient client, FrameDecoder decoder, CancellationToken stopping)
    {
        using var hello = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        Task abandoned = HoldUntilAbandonedAsync(hello);
        try
        {
            return await ReadFirstFrameAsync(client.GetStream(), decoder, hello.Token);
        }
        finally
        {
            await hello.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await abandoned.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Cancels <paramref name="hello"/> once <see cref="HelloTimeout"/> has passed and no session is waiting for a bridge:
    /// the read outlives its timeout only while a waiter is still pending, so a game paused before its first frame is kept.
    /// </summary>
    private async Task HoldUntilAbandonedAsync(CancellationTokenSource hello)
    {
        await Task.Delay(HelloTimeout, hello.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await NoWaiterPending.WaitAsync(hello.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await hello.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Completes the waiter with the connection, or closes the connection when the waiter was withdrawn meanwhile.</summary>
    private async Task HandOverAsync(Waiter waiter, BridgeConnection connection)
    {
        if (Withdraw(waiter) && waiter.Completion.TrySetResult(connection))
        {
            return;
        }

        Log.RefusedBridgeConnection(_logger, "its session stopped waiting for it");
        await connection.DisposeAsync();
    }

    private void Refuse(TcpClient client, string reason)
    {
        Log.RefusedBridgeConnection(_logger, reason);
        client.Dispose();
    }

    private static async Task<JsonObject> ReadFirstFrameAsync(NetworkStream stream, FrameDecoder decoder, CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[4096];
        while (true)
        {
            if (decoder.TryReadFrame(out byte[] payload))
            {
                return FrameCodec.DecodeJson(payload);
            }

            int read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                throw new IOException("the connection closed before its hello");
            }

            decoder.Append(chunk.AsSpan(0, read));
        }
    }

    /// <summary>One AcceptBridgeAsync call waiting for its bridge.</summary>
    private sealed class Waiter(HandshakeExpectation expected)
    {
        public HandshakeExpectation Expected { get; } = expected;

        public TaskCompletionSource<BridgeConnection> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
