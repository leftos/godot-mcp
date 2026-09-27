using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using GodotMcp.Server.Wire;

namespace GodotMcp.Tests.Wire;

/// <summary>The game's side of the wire, without Godot: dials, says hello, and answers a request with a name of its own.</summary>
internal sealed class FakeBridge(TcpClient client) : IDisposable
{
    /// <summary>Dials with a hello that carries no pid, as a bridge older than the field does.</summary>
    public static Task<FakeBridge> DialAsync(int port, string token, string projectPath, CancellationToken cancellationToken) =>
        DialAsync(port, token, projectPath, null, cancellationToken);

    /// <summary>Dials with a hello that carries <paramref name="processId"/> as its pid, when it is not null.</summary>
    public static async Task<FakeBridge> DialAsync(int port, string token, string projectPath, int? processId, CancellationToken cancellationToken)
    {
        TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        JsonObject hello = new()
        {
            ["type"] = "hello",
            ["token"] = token,
            ["projectPath"] = projectPath,
        };
        if (processId is not null)
        {
            hello["pid"] = processId;
        }

        await client.GetStream().WriteAsync(FrameCodec.EncodeJson(hello), cancellationToken);
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

    public Task AnswerOneAsync(string name, CancellationToken cancellationToken) => AnswerOneAfterAsync([], name, cancellationToken);

    /// <summary>Writes one frame as it is, as the bridge writes its unsolicited errors frames.</summary>
    public async Task WriteAsync(JsonObject frame, CancellationToken cancellationToken) =>
        await client.GetStream().WriteAsync(FrameCodec.EncodeJson(frame), cancellationToken);

    /// <summary>
    /// Reads one request, then writes <paramref name="frames"/> and the reply after them, as the bridge flushes its errors before replying.
    /// </summary>
    public async Task AnswerOneAfterAsync(JsonObject[] frames, string name, CancellationToken cancellationToken)
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
    }

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
