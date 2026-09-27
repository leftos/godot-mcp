using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Wire;

/// <summary>The listener routing each bridge connection to the waiter whose token its hello carries, over real loopback sockets.</summary>
public sealed class BridgeListenerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly string ProjectDir = Path.Combine(Path.GetTempPath(), "godot-mcp-listener", "game");
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);

    public void Dispose() => _listener.Dispose();

    // Both dial orders, since which pending accept the socket layer completes first is its own business.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoWaitersEachGetTheirOwnBridge(bool secondDialsFirst)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Wait);
        Task<BridgeConnection> first = _listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), timeout.Token);
        Task<BridgeConnection> second = _listener.AcceptBridgeAsync(new HandshakeExpectation("BBBB", ProjectDir), timeout.Token);

        string[] dialOrder = secondDialsFirst ? ["BBBB", "AAAA"] : ["AAAA", "BBBB"];
        using FakeBridge earlier = await FakeBridge.DialAsync(_listener.Port, dialOrder[0], ProjectDir, cancellation);
        using FakeBridge later = await FakeBridge.DialAsync(_listener.Port, dialOrder[1], ProjectDir, cancellation);
        FakeBridge firstBridge = secondDialsFirst ? later : earlier;
        FakeBridge secondBridge = secondDialsFirst ? earlier : later;
        await using BridgeConnection firstConnection = await first;
        await using BridgeConnection secondConnection = await second;
        Task firstAnswer = firstBridge.AnswerOneAsync("first", cancellation);
        Task secondAnswer = secondBridge.AnswerOneAsync("second", cancellation);
        JsonNode? firstReply = await firstConnection.SendAsync("ping", null, Wait, cancellation);
        JsonNode? secondReply = await secondConnection.SendAsync("ping", null, Wait, cancellation);
        await Task.WhenAll(firstAnswer, secondAnswer);

        Assert.Equal("first", NameIn(firstReply));
        Assert.Equal("second", NameIn(secondReply));
    }

    [Fact]
    public async Task AHelloWithAnUnknownTokenIsClosedAndTheWaiterKeepsWaiting()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Wait);
        Task<BridgeConnection> waiter = _listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), timeout.Token);

        using FakeBridge stranger = await FakeBridge.DialAsync(_listener.Port, "ZZZZ", ProjectDir, cancellation);
        bool strangerClosed = await stranger.IsClosedByServerAsync(Wait);
        bool waitingAfterStranger = !waiter.IsCompleted;
        using FakeBridge own = await FakeBridge.DialAsync(_listener.Port, "AAAA", ProjectDir, cancellation);
        await using BridgeConnection connection = await waiter;

        Assert.True(strangerClosed);
        Assert.True(waitingAfterStranger);
        Assert.True(connection.IsOpen);
    }

    [Fact]
    public async Task ACancelledWaiterNoLongerReceivesItsBridge()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<BridgeConnection> waiter = _listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), cancel.Token);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        using FakeBridge late = await FakeBridge.DialAsync(_listener.Port, "AAAA", ProjectDir, cancellation);

        Assert.True(await late.IsClosedByServerAsync(Wait));
    }

    [Fact]
    public async Task ASlowHelloDoesNotBlockAnotherBridge()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        Task<BridgeConnection> waiter = _listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), timeout.Token);

        using TcpClient silent = new();
        await silent.ConnectAsync(IPAddress.Loopback, _listener.Port, cancellation);
        using FakeBridge own = await FakeBridge.DialAsync(_listener.Port, "AAAA", ProjectDir, cancellation);
        await using BridgeConnection connection = await waiter;

        Assert.True(connection.IsOpen);
    }

    [Theory]
    [InlineData(4242)]
    [InlineData(null)]
    public async Task TheConnectionCarriesTheHellosPidOrNone(int? processId)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Wait);
        Task<BridgeConnection> waiter = _listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), timeout.Token);

        using FakeBridge game = await FakeBridge.DialAsync(_listener.Port, "AAAA", ProjectDir, processId, cancellation);
        await using BridgeConnection connection = await waiter;

        Assert.True(connection.IsOpen);
        Assert.Equal(processId, connection.GameProcessId);
    }

    [Fact]
    public async Task AHelloDelayedPastFiveSecondsIsStillAcceptedWhileItsWaiterIsPending()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        Task<BridgeConnection> waiter = _listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), timeout.Token);
        using TcpClient silent = new();
        await silent.ConnectAsync(IPAddress.Loopback, _listener.Port, cancellation);
        using FakeBridge paused = new(silent);

        await Task.Delay(TimeSpan.FromSeconds(6), cancellation);
        await paused.WriteAsync(Hello("AAAA"), cancellation);
        await using BridgeConnection connection = await waiter;

        Assert.True(connection.IsOpen);
    }

    [Fact]
    public async Task ASilentConnectionIsRefusedOnceNoWaiterIsPending()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<BridgeConnection> waiter = _listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), wait.Token);
        using TcpClient silentClient = new();
        await silentClient.ConnectAsync(IPAddress.Loopback, _listener.Port, cancellation);
        using FakeBridge silent = new(silentClient);

        await Task.Delay(TimeSpan.FromSeconds(5.5), cancellation);
        bool openWhileWaiting = !await silent.IsClosedByServerAsync(TimeSpan.FromMilliseconds(500));
        await wait.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        bool closedOnceAbandoned = await silent.IsClosedByServerAsync(TimeSpan.FromSeconds(1.5));

        Assert.True(openWhileWaiting);
        Assert.True(closedOnceAbandoned);
    }

    [Fact]
    public async Task ASilentConnectionWithNoWaiterIsRefusedAfterFiveSeconds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var none = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<BridgeConnection> withdrawn = _listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), none.Token);
        await none.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawn);
        using TcpClient silentClient = new();
        await silentClient.ConnectAsync(IPAddress.Loopback, _listener.Port, cancellation);
        using FakeBridge silent = new(silentClient);

        bool openBeforeTheTimeout = !await silent.IsClosedByServerAsync(TimeSpan.FromSeconds(3));
        bool closedAfterTheTimeout = await silent.IsClosedByServerAsync(TimeSpan.FromSeconds(4));

        Assert.True(openBeforeTheTimeout);
        Assert.True(closedAfterTheTimeout);
    }

    /// <summary>The hello <see cref="FakeBridge"/> dials with, written by a raw client that stayed silent past the timeout.</summary>
    private static JsonObject Hello(string token) =>
        new()
        {
            ["type"] = "hello",
            ["token"] = token,
            ["projectPath"] = ProjectDir,
            ["pid"] = 4242,
        };

    private static string? NameIn(JsonNode? reply) => reply?["name"]?.GetValue<string>();
}
