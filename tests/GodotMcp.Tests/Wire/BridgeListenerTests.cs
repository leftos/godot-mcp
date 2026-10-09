using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Wire;

/// <summary>The listener routing each bridge connection to the waiter whose token its hello carries, over real loopback sockets.</summary>
public sealed class BridgeListenerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>The real time one stepped second is given to show the server's close before the next step.</summary>
    private static readonly TimeSpan CloseProbe = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The ceiling on handing a second bridge over while a silent client holds a connection open. A listener that reads
    /// each connection as it arrives hands it over in milliseconds whatever the machine's load, and one that reads the
    /// silent client's hello first never gets there: a pending waiter holds that read open past its hello timeout.
    /// </summary>
    private static readonly TimeSpan SilentClientCeiling = TimeSpan.FromSeconds(30);
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
        timeout.CancelAfter(SilentClientCeiling);
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

        using FakeBridge game = await FakeBridge.DialAsync(_listener.Port, "AAAA", ProjectDir, new FakeHello { ProcessId = processId }, cancellation);
        await using BridgeConnection connection = await waiter;

        Assert.True(connection.IsOpen);
        Assert.Equal(processId, connection.GameProcessId);
    }

    [Theory]
    [InlineData(1_311_768L)]
    [InlineData(null)]
    public async Task TheConnectionCarriesTheHellosWindowHandleOrNone(long? hwnd)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Wait);
        Task<BridgeConnection> waiter = _listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), timeout.Token);

        using FakeBridge game = await FakeBridge.DialAsync(_listener.Port, "AAAA", ProjectDir, new FakeHello { WindowHandle = hwnd }, cancellation);
        await using BridgeConnection connection = await waiter;

        Assert.True(connection.IsOpen);
        Assert.Equal(hwnd, connection.WindowHandle);
    }

    [Fact]
    public async Task AHelloDelayedPastFiveSecondsIsStillAcceptedWhileItsWaiterIsPending()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        using BridgeListener listener = new(NullLogger<BridgeListener>.Instance) { Clock = clock };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        Task<BridgeConnection> waiter = listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), timeout.Token);
        using TcpClient silent = new();
        await silent.ConnectAsync(IPAddress.Loopback, listener.Port, cancellation);
        using FakeBridge paused = new(silent);

        // Past the 5 s hello timeout on the listener's clock, the pending waiter keeps the read open.
        time.Advance(TimeSpan.FromSeconds(6));
        await paused.WriteAsync(Hello("AAAA"), cancellation);
        await using BridgeConnection connection = await waiter;

        Assert.True(connection.IsOpen);
    }

    [Fact]
    public async Task ASilentConnectionIsRefusedOnceNoWaiterIsPending()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        using BridgeListener listener = new(NullLogger<BridgeListener>.Instance) { Clock = clock };
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<BridgeConnection> waiter = listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), wait.Token);
        using TcpClient silentClient = new();
        await silentClient.ConnectAsync(IPAddress.Loopback, listener.Port, cancellation);
        using FakeBridge silent = new(silentClient);

        // Stepped past the 5 s hello timeout on the listener's clock: the pending waiter holds the read open through all of it.
        bool openWhileWaiting = !await ClosesWithinSecondsAsync(silent, time, 7);
        await wait.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        bool closedOnceAbandoned = await ClosesWithinSecondsAsync(silent, time, 10);

        Assert.True(openWhileWaiting);
        Assert.True(closedOnceAbandoned);
    }

    [Fact]
    public async Task ASilentConnectionWithNoWaiterIsRefusedAfterFiveSeconds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        using BridgeListener listener = new(NullLogger<BridgeListener>.Instance) { Clock = clock };
        using var none = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<BridgeConnection> withdrawn = listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), none.Token);
        await none.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawn);
        using TcpClient silentClient = new();
        await silentClient.ConnectAsync(IPAddress.Loopback, listener.Port, cancellation);
        using FakeBridge silent = new(silentClient);

        // Four of the five seconds on the listener's clock: the connection is still open, and the fifth on from there closes it.
        bool openBeforeTheTimeout = !await ClosesWithinSecondsAsync(silent, time, 4);
        bool closedAfterTheTimeout = await ClosesWithinSecondsAsync(silent, time, 8);

        Assert.True(openBeforeTheTimeout);
        Assert.True(closedAfterTheTimeout);
    }

    [Fact]
    public async Task TheHelloTimeoutRunsOnTheListenersClock()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        using BridgeListener listener = new(NullLogger<BridgeListener>.Instance) { Clock = clock };
        using var none = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<BridgeConnection> withdrawn = listener.AcceptBridgeAsync(new HandshakeExpectation("AAAA", ProjectDir), none.Token);
        await none.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawn);
        using TcpClient silentClient = new();
        await silentClient.ConnectAsync(IPAddress.Loopback, listener.Port, cancellation);
        using FakeBridge silent = new(silentClient);

        // Six real seconds while the listener's clock stands still: a hello timeout on wall time has refused the connection by then.
        bool openWhileTheClockStands = !await silent.IsClosedByServerAsync(TimeSpan.FromSeconds(6));
        time.Advance(TimeSpan.FromSeconds(5));
        bool closedOnceItPasses = await silent.IsClosedByServerAsync(TimeSpan.FromSeconds(10));

        Assert.True(openWhileTheClockStands);
        Assert.True(closedOnceItPasses);
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

    /// <summary>
    /// Steps the listener's clock a second at a time, up to <paramref name="seconds"/>, and reports whether the server closed
    /// the connection on the way. Stepping rather than jumping keeps a hello timer the accept loop armed only after the client
    /// connected from needing seconds the clock has already spent.
    /// </summary>
    private static async Task<bool> ClosesWithinSecondsAsync(FakeBridge silent, FakeTimeProvider time, int seconds)
    {
        for (int step = 0; step < seconds; step++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            if (await silent.IsClosedByServerAsync(CloseProbe))
            {
                return true;
            }
        }

        return false;
    }
}
