using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;

namespace GodotMcp.Tests.Wire;

/// <summary>
/// The game's side of the wire, without Godot: dials, says hello, answers a request with a name of its own, and answers the
/// handshake's clock pings itself with a pong carrying its own ticks, as a real bridge does.
/// </summary>
internal sealed class FakeBridge : IDisposable
{
    // The handshake's five clock pings, 7 ms apart from one start (design part F); the pump answers them without a test's
    // help, and a later ping, a debugger's or a stop's, goes to the test as any other request does.
    private const int HandshakePingCount = 5;
    private const int GracefulStopMs = 5_000;
    private readonly TcpClient _client;
    private readonly bool _answerPings;
    private readonly FrameDecoder _frames = new();
    private readonly Channel<JsonObject> _requests = Channel.CreateUnbounded<JsonObject>();
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task _pump;
    private int _disposed;
    private int _pingsAnswered;
    private volatile bool _answerAllPings;
    private volatile bool _sawNonPing;

    /// <summary>
    /// Stands in for a game's bridge on <paramref name="client"/>, already connected and, unless <paramref name="answerPings"/>
    /// is false, answering the handshake's clock pings; the pump reads the server's requests from the moment it is made.
    /// </summary>
    public FakeBridge(TcpClient client, bool answerPings = true)
    {
        _client = client;
        _answerPings = answerPings;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    /// Dials with a hello that carries no pid, no window handle and no ticks, as a bridge older than the fields does.
    /// </summary>
    public static Task<FakeBridge> DialAsync(
        int port,
        string token,
        string projectPath,
        CancellationToken cancellationToken,
        bool answerPings = true
    ) => DialAsync(port, token, projectPath, new FakeHello(), cancellationToken, answerPings);

    /// <summary>
    /// Dials with a hello that carries <paramref name="hello"/>'s pid and window handle, each when it is not null; unless
    /// <paramref name="answerPings"/> is false, the pump then answers the handshake's clock pings with a pong carrying ticks.
    /// </summary>
    public static async Task<FakeBridge> DialAsync(
        int port,
        string token,
        string projectPath,
        FakeHello hello,
        CancellationToken cancellationToken,
        bool answerPings = true
    )
    {
        TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        JsonObject frame = new()
        {
            ["type"] = "hello",
            ["token"] = token,
            ["projectPath"] = projectPath,
        };
        if (hello.ProcessId is not null)
        {
            frame["pid"] = hello.ProcessId;
        }

        if (hello.WindowHandle is not null)
        {
            frame["hwnd"] = hello.WindowHandle;
        }

        await client.GetStream().WriteAsync(FrameCodec.EncodeJson(frame), cancellationToken);
        return new FakeBridge(client, answerPings);
    }

    /// <summary>
    /// Answers every ping the pump sees, and leaves every other request in the queue, as a game whose main thread runs while a
    /// command awaits, until the server closes the connection or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task AnswerPingsOnlyAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        _answerAllPings = true;
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        }
        catch (OperationCanceledException)
        {
            // The test answered what it asked for.
        }
        finally
        {
            _answerAllPings = false;
        }
    }

    /// <summary>Reads one request and answers it with <paramref name="name"/>; returns the request's command.</summary>
    public Task<string?> AnswerOneAsync(string name, CancellationToken cancellationToken) => AnswerOneAfterAsync([], name, cancellationToken);

    /// <summary>Writes one frame as it is, as the bridge writes its unsolicited errors frames.</summary>
    public Task WriteAsync(JsonObject frame, CancellationToken cancellationToken) => WriteFrameAsync(frame, cancellationToken);

    /// <summary>
    /// Reads one request, then writes <paramref name="frames"/> and the reply after them, as the bridge flushes its errors before
    /// replying; returns the request's command.
    /// </summary>
    public async Task<string?> AnswerOneAfterAsync(JsonObject[] frames, string name, CancellationToken cancellationToken)
    {
        JsonObject request = await ReadNextAsync(cancellationToken);
        foreach (JsonObject frame in frames)
        {
            await WriteAsync(frame, cancellationToken);
        }

        await ReplyAsync(request, new JsonObject { ["name"] = name }, cancellationToken);
        return HandshakeExpectation.ReadString(request, "command");
    }

    /// <summary>Reads the next request the pump queued without answering it.</summary>
    public Task<JsonObject> ReadRequestAsync(CancellationToken cancellationToken) => ReadNextAsync(cancellationToken);

    /// <summary>Answers <paramref name="request"/>, read by <see cref="ReadRequestAsync"/>, with <paramref name="result"/>.</summary>
    public Task ReplyAsync(JsonObject request, JsonObject result, CancellationToken cancellationToken) =>
        WriteFrameAsync(
            new JsonObject
            {
                ["id"] = request["id"]?.DeepClone(),
                ["ok"] = true,
                ["result"] = result,
            },
            cancellationToken
        );

    /// <summary>Whether the server closes the connection within <paramref name="wait"/>.</summary>
    public async Task<bool> IsClosedByServerAsync(TimeSpan wait)
    {
        using CancellationTokenSource timeout = new(wait);
        try
        {
            await _pump.WaitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stopping.Cancel();
        _client.Dispose();
        try
        {
            _pump.Wait(TimeSpan.FromMilliseconds(GracefulStopMs));
        }
        catch (AggregateException)
        {
            // The pump ends on the closed connection; nothing it saw is a test's to fail on.
        }

        _stopping.Dispose();
        _writeLock.Dispose();
    }

    /// <summary>
    /// Reads every frame the server sends: a handshake ping is answered here, as the game's bridge would, and every other frame
    /// waits in order for the test's own read helpers. Ends with the connection.
    /// </summary>
    private async Task PumpAsync()
    {
        byte[] chunk = new byte[4096];
        try
        {
            while (true)
            {
                while (_frames.TryReadFrame(out byte[] payload))
                {
                    await HandleAsync(FrameCodec.DecodeJson(payload));
                }

                int read = await _client.GetStream().ReadAsync(chunk, _stopping.Token);
                if (read == 0)
                {
                    return;
                }

                _frames.Append(chunk.AsSpan(0, read));
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The server let go of the connection, or the test did.
        }
        finally
        {
            _requests.Writer.TryComplete();
        }
    }

    private async Task HandleAsync(JsonObject frame)
    {
        if (!IsPing(frame) || !IsHandshakePing())
        {
            _sawNonPing = true;
            _requests.Writer.TryWrite(frame);
            return;
        }

        _pingsAnswered++;
        await ReplyAsync(frame, new JsonObject { ["pong"] = true, ["ticksUsec"] = (long)(DriveClock.NowMs * 1000) }, _stopping.Token);
    }

    /// <summary>
    /// Whether <paramref name="frame"/>'s ping is one of the handshake's: a test that asks for pongs answers later pings itself,
    /// and a fake with <c>answerPings</c> false, one the server never handshakes, leaves every ping to its test.
    /// </summary>
    private bool IsHandshakePing() => _answerAllPings || (_answerPings && !_sawNonPing && _pingsAnswered < HandshakePingCount);

    /// <summary>
    /// The next request that is not one the pump answered, taking each frame the pump queued in order; a ping the pump left
    /// behind before its handshake was done is answered here as a fallback.
    /// </summary>
    private async Task<JsonObject> ReadNextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            JsonObject request;
            try
            {
                request = await _requests.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("the server closed the connection before sending a request");
            }

            if (IsPing(request) && IsHandshakePing())
            {
                await ReplyAsync(request, new JsonObject { ["pong"] = true }, cancellationToken);
                continue;
            }

            return request;
        }
    }

    private async Task WriteFrameAsync(JsonObject frame, CancellationToken cancellationToken)
    {
        byte[] bytes = FrameCodec.EncodeJson(frame);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _client.GetStream().WriteAsync(bytes, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Whether <paramref name="request"/> is a clock ping, a command named <c>ping</c>.</summary>
    private static bool IsPing(JsonObject request) => HandshakeExpectation.ReadString(request, "command") == "ping";
}

/// <summary>The pid and window handle a fake game's hello carries, each when it is not null.</summary>
internal sealed record FakeHello
{
    public int? ProcessId { get; init; }

    public long? WindowHandle { get; init; }
}
