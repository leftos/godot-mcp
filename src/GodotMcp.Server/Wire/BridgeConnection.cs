using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Wire;

/// <summary>
/// One accepted, handshaken bridge. Requests <c>{id, command, params}</c> go out; replies <c>{id, ok, result|error}</c>
/// come back in any order and are matched by id, so several requests may be in flight at once. Frames without an id whose
/// type is "errors" carry the game's logged errors and go to the handler <see cref="OnErrors"/> sets, in arrival order,
/// before any reply read after them; those whose type is "captured" carry a running input capture's events and go to the
/// handler <see cref="OnCaptured"/> sets. Any other frame without an id is dropped.
/// </summary>
internal sealed class BridgeConnection : IAsyncDisposable
{
    private const string ErrorsFrameType = "errors";
    private const string CapturedFrameType = "captured";
    private static readonly TimeSpan CancelReplyTimeout = TimeSpan.FromSeconds(2);
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly FrameDecoder _decoder;
    private readonly LoadClock _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Lock _errorsLock = new();
    private readonly List<JsonObject> _unhandledErrors = [];
    private readonly Task _readLoop;
    private Action<JsonObject>? _errorsHandler;
    private volatile Action<JsonObject>? _capturedHandler;
    private long _nextId;

    /// <param name="client">The accepted connection, its hello already read.</param>
    /// <param name="decoder">The decoder that read the hello, holding any bytes that arrived after it.</param>
    /// <param name="gameProcessId">The game's own process id from the hello, or null when the hello carried none.</param>
    /// <param name="clock">The clock a request's reply timeout and release run on.</param>
    /// <param name="logger">Where dropped replies and the connection's end are reported.</param>
    public BridgeConnection(TcpClient client, FrameDecoder decoder, int? gameProcessId, LoadClock clock, ILogger logger)
    {
        _client = client;
        _stream = client.GetStream();
        _decoder = decoder;
        _clock = clock;
        _logger = logger;
        GameProcessId = gameProcessId;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public bool IsOpen => !_readLoop.IsCompleted;

    /// <summary>Completes once the connection has ended, whichever side closed it.</summary>
    public Task Closed => _readLoop;

    /// <summary>The game's own process id, as its hello reported it; null when the bridge predates the field.</summary>
    public int? GameProcessId { get; }

    /// <summary>
    /// Sends one command and waits for its reply's <c>result</c> for <paramref name="timeout"/> of load-adjusted time, bounded by
    /// <see cref="LoadClock.BackstopFactor"/> times it in wall time. With <paramref name="release"/>, the request carries
    /// <c>backstopMs</c>, <see cref="LoadClock.BackstopFactor"/> times the release in milliseconds, as the bridge's own real-time
    /// limit; when the release passes in load-adjusted time first, a <c>cancel</c> for the request goes to the bridge, which ends
    /// the request with its usual answer, and the wait goes on for that answer.
    /// </summary>
    /// <param name="command">The bridge command.</param>
    /// <param name="parameters">
    /// Its parameters, left as they are; the request adds <c>backstopMs</c> to a copy when <paramref name="release"/> is set.
    /// </param>
    /// <param name="timeout">How long to wait for the reply, in load-adjusted time.</param>
    /// <param name="cancellationToken">Withdraws the wait.</param>
    /// <param name="release">How long the bridge may run the request, in load-adjusted time, before it is cancelled; null sends no cancel.</param>
    /// <exception cref="LoadTimeoutException">No reply within the timeout; a later reply is dropped.</exception>
    /// <exception cref="InvalidOperationException">The bridge answered <c>ok: false</c>.</exception>
    /// <exception cref="IOException">The connection ended before the reply.</exception>
    public async Task<JsonNode?> SendAsync(
        string command,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeSpan? release = null
    )
    {
        using LoadDeadline deadline = _clock.Start(timeout, cancellationToken);
        JsonObject sent = WithBackstop(parameters, release);

        (long id, PendingRequest pending) = Register(command);
        using LoadDeadline? released = release is { } releaseAfter ? _clock.Start(releaseAfter) : null;
        using CancellationTokenRegistration cancelling = released?.Token.Register(() => CancelOnce(id, pending)) ?? default;
        try
        {
            await WriteAsync(Request(id, command, sent), cancellationToken);
            pending.MarkWritten();
            if (released is { Token.IsCancellationRequested: true })
            {
                // The release passed while the write was still going out, when there was nothing yet to cancel.
                CancelOnce(id, pending);
            }

            return await pending.Reply.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.Expired)
        {
            string backstop = deadline.Reason == DeadlineReason.Backstop ? deadline.BackstopClause() : string.Empty;
            throw new LoadTimeoutException(TimedOut(command, id, timeout, backstop), deadline);
        }
        catch (OperationCanceledException e) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(e.Message, e, cancellationToken);
        }
        finally
        {
            if (released is not null)
            {
                // A caller that walked away (a timeout or a cancelled token) from a request still running in the game.
                CancelOnce(id, pending);
            }

            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>The parameters as sent: with <c>backstopMs</c> added to a copy when the request has a release, else as given.</summary>
    private static JsonObject WithBackstop(JsonObject? parameters, TimeSpan? release)
    {
        if (release is not { } allowance)
        {
            return parameters ?? [];
        }

        JsonObject sent = parameters?.DeepClone().AsObject() ?? [];
        sent["backstopMs"] = (long)(allowance * LoadClock.BackstopFactor).TotalMilliseconds;
        return sent;
    }

    /// <summary>
    /// Asks the bridge, on its own task, to end a released request early rather than let it hold the bridge until its
    /// <c>backstopMs</c>: when its release passes, or when the caller walks away from it. Both paths claim the request's one
    /// cancel, so a release and a walk-away landing together send exactly one; a request whose frame never went out, or whose
    /// reply is already in, is not running in the game and gets none.
    /// </summary>
    private void CancelOnce(long id, PendingRequest pending)
    {
        if (pending.IsWritten && !pending.Reply.Task.IsCompleted && pending.TryClaimCancel())
        {
            _ = CancelAsync(id, pending.Command);
        }
    }

    /// <summary>
    /// Sends one command and waits for its reply's <c>result</c> for <paramref name="timeout"/> of wall time, whatever the
    /// machine's load: the pings that tell a busy game from a stuck one, a cancel, and the shutdown.
    /// </summary>
    /// <exception cref="TimeoutException">No reply within <paramref name="timeout"/>; a later reply is dropped.</exception>
    /// <exception cref="InvalidOperationException">The bridge answered <c>ok: false</c>.</exception>
    /// <exception cref="IOException">The connection ended before the reply.</exception>
    public async Task<JsonNode?> SendRawAsync(string command, JsonObject? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        (long id, PendingRequest pending) = Register(command);
        try
        {
            await WriteAsync(Request(id, command, parameters ?? []), cancellationToken);
            return await pending.Reply.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(TimedOut(command, id, timeout, string.Empty), e);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Sets the handler for the bridge's errors frames and hands it, first, those that arrived before it was set (the read
    /// loop starts with the connection, before its session takes it).
    /// </summary>
    public void OnErrors(Action<JsonObject> handler)
    {
        lock (_errorsLock)
        {
            _errorsHandler = handler;
            foreach (JsonObject frame in _unhandledErrors)
            {
                InvokeErrorsHandler(handler, frame);
            }

            _unhandledErrors.Clear();
        }
    }

    /// <summary>
    /// Sets the handler for the bridge's captured frames. A capture starts only through a command sent after the session took the
    /// connection, so none arrives before it is set; one that does is logged and dropped.
    /// </summary>
    public void OnCaptured(Action<JsonObject> handler) => _capturedHandler = handler;

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        _client.Dispose();
        await _readLoop;
        _writeLock.Dispose();
        _closing.Dispose();
    }

    private static string TimedOut(string command, long id, TimeSpan timeout, string backstop) =>
        $"The bridge did not answer '{command}' (request {id}) within {timeout.TotalSeconds:0.#} s{backstop}; a late reply will be dropped.";

    private static JsonObject Request(long id, string command, JsonObject parameters) =>
        new()
        {
            ["id"] = id,
            ["command"] = command,
            ["params"] = parameters,
        };

    private (long Id, PendingRequest Pending) Register(string command)
    {
        long id = Interlocked.Increment(ref _nextId);
        PendingRequest pending = new(command);
        _pending[id] = pending;
        return (id, pending);
    }

    /// <summary>
    /// Sends the cancel; one that goes unanswered or is refused (a bridge that predates it) is logged, and the request then ends at
    /// its <c>backstopMs</c> in the game.
    /// </summary>
    private async Task CancelAsync(long id, string command)
    {
        try
        {
            await SendRawAsync("cancel", new JsonObject { ["request"] = id }, CancelReplyTimeout, CancellationToken.None);
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            Log.CancelUnanswered(_logger, command, id, e.Message);
        }
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
            HandleFrameWithoutId(reply, length);
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

    private void HandleFrameWithoutId(JsonObject frame, int length)
    {
        switch (HandshakeExpectation.ReadString(frame, "type"))
        {
            case ErrorsFrameType:
                DeliverErrors(frame);
                break;
            case CapturedFrameType:
                DeliverCaptured(frame, length);
                break;
            default:
                Log.DroppedFrameWithoutId(_logger, length);
                break;
        }
    }

    /// <summary>Hands one captured frame to its handler; with none set, or one that throws, the frame is logged and dropped.</summary>
    private void DeliverCaptured(JsonObject frame, int length)
    {
        if (_capturedHandler is not { } handler)
        {
            Log.DroppedFrameWithoutId(_logger, length);
            return;
        }

        try
        {
            handler(frame);
        }
        catch (Exception e)
        {
            Log.CapturedFrameDropped(_logger, e);
        }
    }

    private void DeliverErrors(JsonObject frame)
    {
        lock (_errorsLock)
        {
            if (_errorsHandler is null)
            {
                _unhandledErrors.Add(frame);
                return;
            }

            InvokeErrorsHandler(_errorsHandler, frame);
        }
    }

    /// <summary>Hands one errors frame to the handler; a handler that throws loses that frame, never the read loop.</summary>
    private void InvokeErrorsHandler(Action<JsonObject> handler, JsonObject frame)
    {
        try
        {
            handler(frame);
        }
        catch (Exception e)
        {
            Log.ErrorsFrameDropped(_logger, e);
        }
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
        private volatile bool _written;
        private int _cancelClaimed;

        public string Command { get; } = command;

        public TaskCompletionSource<JsonNode?> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the request's frame has gone out to the bridge; only a written request can be running there.</summary>
        public bool IsWritten => _written;

        public void MarkWritten() => _written = true;

        /// <summary>True for the first caller only: the request's one cancel is theirs to send.</summary>
        public bool TryClaimCancel() => Interlocked.Exchange(ref _cancelClaimed, 1) == 0;
    }
}
