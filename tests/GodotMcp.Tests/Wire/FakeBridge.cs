using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using GodotMcp.Server.Wire;

namespace GodotMcp.Tests.Wire;

/// <summary>The game's side of the wire, without Godot: dials, says hello, and answers a request with a name of its own.</summary>
internal sealed class FakeBridge(TcpClient client) : IDisposable
{
    public static async Task<FakeBridge> DialAsync(int port, string token, string projectPath, CancellationToken cancellationToken)
    {
        TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        JsonObject hello = new()
        {
            ["type"] = "hello",
            ["token"] = token,
            ["projectPath"] = projectPath,
        };
        await client.GetStream().WriteAsync(FrameCodec.EncodeJson(hello), cancellationToken);
        return new FakeBridge(client);
    }

    public Task AnswerOneAsync(string name, CancellationToken cancellationToken) => AnswerOneAfterAsync([], name, cancellationToken);

    /// <summary>Writes one frame as it is, as the bridge writes its unsolicited errors frames.</summary>
    public async Task WriteAsync(JsonObject frame, CancellationToken cancellationToken) =>
        await client.GetStream().WriteAsync(FrameCodec.EncodeJson(frame), cancellationToken);

    /// <summary>Reads one request, then writes <paramref name="frames"/> and the reply after them, as the bridge flushes its errors before replying.</summary>
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
}
