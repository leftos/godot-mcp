using System.Text.Json.Nodes;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Wire;

/// <summary>The bridge's errors frames reaching the connection's handler, over real loopback sockets.</summary>
public sealed class BridgeConnectionTests : IDisposable
{
    private const string Token = "ERRORS";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly string ProjectDir = Path.Combine(Path.GetTempPath(), "godot-mcp-connection", "game");
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task AnErrorsFrameBeforeOnErrorsIsReplayed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
        await bridge.WriteAsync(ErrorsFrame("early"), cancellation);
        await using BridgeConnection connection = await accept;

        // Frames are read in order, so once the reply is in, the errors frame ahead of it has been read too.
        Task answer = bridge.AnswerOneAsync("pong", cancellation);
        await connection.SendAsync("ping", null, Wait, cancellation);
        await answer;
        List<string> received = [];
        connection.OnErrors(frame => received.Add(MessageIn(frame)));

        Assert.Equal(["early"], received);
    }

    [Fact]
    public async Task ErrorsSentBeforeAReplyAreDeliveredBeforeSendAsyncCompletes()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
        await using BridgeConnection connection = await accept;
        List<string> received = [];
        connection.OnErrors(frame => received.Add(MessageIn(frame)));

        Task answer = bridge.AnswerOneAfterAsync([ErrorsFrame("first"), ErrorsFrame("second")], "pong", cancellation);
        JsonNode? reply = await connection.SendAsync("ping", null, Wait, cancellation);
        string[] atReply = [.. received];
        await answer;

        Assert.Equal("pong", reply?["name"]?.GetValue<string>());
        Assert.Equal(["first", "second"], atReply);
    }

    [Fact]
    public async Task AThrowingErrorsHandlerDoesNotStopReplies()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
        await using BridgeConnection connection = await accept;
        int calls = 0;
        connection.OnErrors(_ =>
        {
            calls++;
            throw new InvalidOperationException("the handler failed");
        });

        Task answer = bridge.AnswerOneAfterAsync([ErrorsFrame("boom")], "pong", cancellation);
        JsonNode? reply = await connection.SendAsync("ping", null, Wait, cancellation);
        await answer;

        Assert.Equal(1, calls);
        Assert.Equal("pong", reply?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task ACapturedFrameReachesItsHandlerAndAnUnknownIdlessFrameIsDropped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
        await using BridgeConnection connection = await accept;
        List<string> captured = [];
        List<string> errors = [];
        connection.OnCaptured(frame => captured.Add(frame["events"]![0]!["key"]!.GetValue<string>()));
        connection.OnErrors(frame => errors.Add(MessageIn(frame)));

        JsonObject unknown = new() { ["type"] = "mystery", ["events"] = new JsonArray(new JsonObject { ["key"] = "X" }) };
        JsonObject frame = new() { ["type"] = "captured", ["events"] = new JsonArray(new JsonObject { ["type"] = "key", ["key"] = "A" }) };
        Task answer = bridge.AnswerOneAfterAsync([unknown, frame], "pong", cancellation);
        JsonNode? reply = await connection.SendAsync("ping", null, Wait, cancellation);
        string[] atReply = [.. captured];
        await answer;

        Assert.Equal("pong", reply?["name"]?.GetValue<string>());
        Assert.Equal(["A"], atReply);
        Assert.Empty(errors);
    }

    private async Task<BridgeConnection> AcceptAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Wait);
        return await _listener.AcceptBridgeAsync(new HandshakeExpectation(Token, ProjectDir), timeout.Token);
    }

    private static JsonObject ErrorsFrame(string message) =>
        new()
        {
            ["type"] = "errors",
            ["entries"] = new JsonArray(new JsonObject { ["type"] = "error", ["message"] = message }),
            ["dropped"] = 0,
        };

    private static string MessageIn(JsonObject frame) => frame["entries"]![0]!["message"]!.GetValue<string>();
}
