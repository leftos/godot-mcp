using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Wire;

/// <summary>
/// The loopback socket the bridge dials. It binds an ephemeral port once and keeps listening for the server's lifetime,
/// so the port handed to a launched game is never raced for.
/// </summary>
internal sealed class BridgeListener : IDisposable
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ILogger<BridgeListener> _logger;

    public BridgeListener(ILogger<BridgeListener> logger)
    {
        _logger = logger;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>
    /// Accepts connections until one says hello with the expected token and project; any other connection is closed.
    /// </summary>
    public async Task<BridgeConnection> AcceptBridgeAsync(HandshakeExpectation expected, CancellationToken cancellationToken)
    {
        while (true)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
            BridgeConnection? connection = await TryHandshakeAsync(client, expected, cancellationToken);
            if (connection is not null)
            {
                return connection;
            }
        }
    }

    public void Dispose() => _listener.Dispose();

    private async Task<BridgeConnection?> TryHandshakeAsync(TcpClient client, HandshakeExpectation expected, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        FrameDecoder decoder = new();
        string? mismatch;
        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            helloTimeout.CancelAfter(HelloTimeout);
            JsonObject hello = await ReadFirstFrameAsync(client.GetStream(), decoder, helloTimeout.Token);
            mismatch = expected.FindMismatch(hello);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException)
        {
            mismatch = $"no valid hello arrived ({e.Message})";
        }

        if (mismatch is null)
        {
            return new BridgeConnection(client, decoder, _logger);
        }

        Log.RefusedBridgeConnection(_logger, mismatch);
        client.Dispose();
        return null;
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
}
