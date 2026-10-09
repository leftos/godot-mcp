using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using GodotMcp.Server.Wire;

namespace GodotMcp.Tests.Wire;

/// <summary>The game's side of the wire, without Godot: dials, says hello, and answers a request with a name of its own.</summary>
internal sealed class FakeBridge(TcpClient client) : IDisposable
{
    private readonly FrameDecoder _requests = new();

    /// <summary>Dials with a hello that carries no pid and no window handle, as a bridge older than the fields does.</summary>
    public static Task<FakeBridge> DialAsync(int port, string token, string projectPath, CancellationToken cancellationToken) =>
        DialAsync(port, token, projectPath, new FakeHello(), cancellationToken);

    /// <summary>
    /// Dials with a hello that carries <paramref name="hello"/>'s pid and window handle, each when it is not null.
    /// </summary>
    public static async Task<FakeBridge> DialAsync(int port, string token, string projectPath, FakeHello hello, CancellationToken cancellationToken)
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
        return new FakeBridge(client);
    }

    /// <summary>
    /// Answers every ping and leaves every other request unanswered, as a game whose main thread runs while a command awaits,
    /// until the server closes the connection or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task AnswerPingsOnlyAsync(CancellationToken cancellationToken)
    {
        FrameDecoder decoder = new();
        byte[] chunk = new byte[4096];
        try
        {
            while (true)
            {
                while (decoder.TryReadFrame(out byte[] payload))
                {
                    await AnswerIfPingAsync(FrameCodec.DecodeJson(payload), cancellationToken);
                }

                int read = await client.GetStream().ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                decoder.Append(chunk.AsSpan(0, read));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The test is over, or the server let go of the connection.
        }
    }

    /// <summary>Reads one request and answers it with <paramref name="name"/>; returns the request's command.</summary>
    public Task<string?> AnswerOneAsync(string name, CancellationToken cancellationToken) => AnswerOneAfterAsync([], name, cancellationToken);

    /// <summary>Writes one frame as it is, as the bridge writes its unsolicited errors frames.</summary>
    public async Task WriteAsync(JsonObject frame, CancellationToken cancellationToken) =>
        await client.GetStream().WriteAsync(FrameCodec.EncodeJson(frame), cancellationToken);

    /// <summary>
    /// Reads one request, then writes <paramref name="frames"/> and the reply after them, as the bridge flushes its errors before
    /// replying; returns the request's command.
    /// </summary>
    public async Task<string?> AnswerOneAfterAsync(JsonObject[] frames, string name, CancellationToken cancellationToken)
    {
        FrameDecoder decoder = new();
        byte[] chunk = new byte[4096];
        byte[] payload;
        while (!decoder.TryReadFrame(out payload))
        {
            int read = await client.GetStream().ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                throw new IOException("the server closed the connection before sending a request");
            }

            decoder.Append(chunk.AsSpan(0, read));
        }

        JsonObject request = FrameCodec.DecodeJson(payload);
        foreach (JsonObject frame in frames)
        {
            await WriteAsync(frame, cancellationToken);
        }

        JsonObject reply = new()
        {
            ["id"] = request["id"]?.DeepClone(),
            ["ok"] = true,
            ["result"] = new JsonObject { ["name"] = name },
        };
        await client.GetStream().WriteAsync(FrameCodec.EncodeJson(reply), cancellationToken);
        return HandshakeExpectation.ReadString(request, "command");
    }

    /// <summary>Reads the next request without answering it, keeping any bytes after it for the next read.</summary>
    public async Task<JsonObject> ReadRequestAsync(CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[4096];
        byte[] payload;
        while (!_requests.TryReadFrame(out payload))
        {
            int read = await client.GetStream().ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                throw new IOException("the server closed the connection before sending a request");
            }

            _requests.Append(chunk.AsSpan(0, read));
        }

        return FrameCodec.DecodeJson(payload);
    }

    /// <summary>Answers <paramref name="request"/>, read by <see cref="ReadRequestAsync"/>, with <paramref name="result"/>.</summary>
    public Task ReplyAsync(JsonObject request, JsonObject result, CancellationToken cancellationToken) =>
        WriteAsync(
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
            return await client.GetStream().ReadAsync(new byte[1], timeout.Token) == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public void Dispose() => client.Dispose();

    private async Task AnswerIfPingAsync(JsonObject request, CancellationToken cancellationToken)
    {
        if (HandshakeExpectation.ReadString(request, "command") != "ping")
        {
            return;
        }

        JsonObject reply = new()
        {
            ["id"] = request["id"]?.DeepClone(),
            ["ok"] = true,
            ["result"] = new JsonObject { ["pong"] = true },
        };
        await WriteAsync(reply, cancellationToken);
    }
}

/// <summary>The pid and window handle a fake game's hello carries, each when it is not null.</summary>
internal sealed record FakeHello
{
    public int? ProcessId { get; init; }

    public long? WindowHandle { get; init; }
}
