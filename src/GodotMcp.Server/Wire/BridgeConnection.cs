using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Wire;

/// <summary>
/// One accepted, handshaken bridge. Requests <c>{id, command, params}</c> go out; replies <c>{id, ok, result|error}</c>
/// come back in any order and are matched by id, so several requests may be in flight at once.
/// </summary>
internal sealed class BridgeConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly FrameDecoder _decoder;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _readLoop;
    private long _nextId;

    /// <param name="client">The accepted connection, its hello already read.</param>
    /// <param name="decoder">The decoder that read the hello, holding any bytes that arrived after it.</param>
    /// <param name="logger">Where dropped replies and the connection's end are reported.</param>
    public BridgeConnection(TcpClient client, FrameDecoder decoder, ILogger logger)
    {
        _client = client;
        _stream = client.GetStream();
        _decoder = decoder;
        _logger = logger;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public bool IsOpen => !_readLoop.IsCompleted;

    /// <summary>Sends one command and waits for its reply's <c>result</c>.</summary>
    /// <exception cref="TimeoutException">No reply within <paramref name="timeout"/>; a later reply is dropped.</exception>
    /// <exception cref="InvalidOperationException">The bridge answered <c>ok: false</c>.</exception>
    /// <exception cref="IOException">The connection ended before the reply.</exception>
    public async Task<JsonNode?> SendAsync(string command, JsonObject? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextId);
        PendingRequest pending = new(command);
        _pending[id] = pending;
        try
        {
            JsonObject request = new()
            {
                ["id"] = id,
                ["command"] = command,
                ["params"] = parameters ?? [],
            };
            await WriteAsync(request, cancellationToken);
            return await pending.Reply.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(
                $"The bridge did not answer '{command}' (request {id}) within {timeout.TotalSeconds:0.#} s; a late reply will be dropped.",
                e
            );
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        _client.Dispose();
        await _readLoop;
        _writeLock.Dispose();
        _closing.Dispose();
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        byte[] frame = FrameCodec.EncodeJson(message);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        byte[] chunk = new byte[64 * 1024];
        IOException reason;
        try
        {
            DispatchFrames();
            while (true)
            {
                int read = await _stream.ReadAsync(chunk, _closing.Token);
                if (read == 0)
                {
                    reason = new IOException("The bridge closed the connection.");
                    break;
                }

                _decoder.Append(chunk.AsSpan(0, read));
                DispatchFrames();
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            reason = new IOException($"The bridge connection ended: {e.Message}", e);
        }

        string message = reason.Message;
        Log.BridgeConnectionEnded(_logger, message);
        foreach (PendingRequest pending in _pending.Values)
        {
            pending.Reply.TrySetException(reason);
        }
    }

    private void DispatchFrames()
    {
        while (_decoder.TryReadFrame(out byte[] payload))
        {
            HandleReply(FrameCodec.DecodeJson(payload), payload.Length);
        }
    }

    private void HandleReply(JsonObject reply, int length)
    {
        if (!TryReadId(reply, out long id))
        {
            Log.DroppedFrameWithoutId(_logger, length);
            return;
        }

        if (!_pending.TryRemove(id, out PendingRequest? pending))
        {
            Log.DroppedLateReply(_logger, id);
            return;
        }

        if (IsSuccess(reply))
        {
            pending.Reply.TrySetResult(reply["result"]?.DeepClone());
            return;
        }

        pending.Reply.TrySetException(new InvalidOperationException($"The bridge refused '{pending.Command}': {ReadError(reply)}"));
    }

    private static bool IsSuccess(JsonObject reply) => reply["ok"] is JsonValue ok && ok.TryGetValue(out bool succeeded) && succeeded;

    private static string ReadError(JsonObject reply) =>
        HandshakeExpectation.ReadString(reply, "error") ?? reply["error"]?.ToJsonString() ?? "no error message";

    private static bool TryReadId(JsonObject reply, out long id)
    {
        id = 0;
        if (reply["id"] is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue(out id))
        {
            return true;
        }

        // GDScript's JSON parser reads every number as a float, so a bridge may echo 7 as 7.0.
        if (value.TryGetValue(out double number) && number == Math.Floor(number))
        {
            id = (long)number;
            return true;
        }

        return false;
    }

    private sealed class PendingRequest(string command)
    {
        public string Command { get; } = command;

        public TaskCompletionSource<JsonNode?> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
