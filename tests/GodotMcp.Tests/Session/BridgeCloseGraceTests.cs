using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.Tests.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The bounded wait a run's end gives a game that quit for its bridge connection to close (<see cref="GodotSession.AwaitBridgeCloseAsync"/>),
/// so a watchFinal frame the bridge sent in its exit work is handed on: only after a quit that was not a kill, and only while a
/// handler awaits the frame. On a fake game over a real loopback socket, the registry's clock on a fake time that only the test
/// advances, so a wait that has not returned is still waiting and a bounded one returns once the time passes.
/// </summary>
public sealed class BridgeCloseGraceTests : IAsyncDisposable
{
    private const string Token = "GRACE";

    private readonly FakeTimeProvider _time = new();
    private readonly LoadClock _clock;
    private readonly RegistryHarness _harness;
    private readonly string _projectDir;

    public BridgeCloseGraceTests()
    {
        _clock = new LoadClock(_time, new NoLoadSource());
        _harness = new RegistryHarness(_clock);
        _projectDir = _harness.Project("game");
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _clock.Dispose();
    }

    [Fact]
    public async Task AQuitWhileAHandlerAwaitsWaitsUntilTheGraceEnds()
    {
        (FakeBridge game, BridgeConnection connection) = await ConnectAsync();
        using (game)
        await using (connection)
        {
            connection.OnWatchFinal(_ => { });

            Task wait = AwaitCloseAsync(connection, quit: true);
            bool returnedAtOnce = wait.IsCompleted;
            await RegistryHarness.AdvanceUntilDoneAsync(_time, wait, Cancellation);
            await wait;

            Assert.False(returnedAtOnce, "the wait returned before its grace with the connection still open");
            Assert.False(connection.Closed.IsCompleted, "the connection closed, so the grace's end was not what returned the wait");
        }
    }

    [Fact]
    public async Task AQuitWhileAHandlerAwaitsReturnsOnceTheConnectionCloses()
    {
        (FakeBridge game, BridgeConnection connection) = await ConnectAsync();
        await using (connection)
        {
            connection.OnWatchFinal(_ => { });

            Task wait = AwaitCloseAsync(connection, quit: true);
            bool returnedAtOnce = wait.IsCompleted;
            game.Dispose();
            // The fake time never moves, so only the close can end the wait.
            await wait.WaitAsync(RegistryHarness.HangGuard, Cancellation);

            Assert.False(returnedAtOnce, "the wait returned with the connection still open");
            Assert.True(connection.Closed.IsCompleted, "the wait returned before the connection closed");
        }
    }

    [Fact]
    public async Task NoWaitWithoutAHandler()
    {
        (FakeBridge game, BridgeConnection connection) = await ConnectAsync();
        using (game)
        await using (connection)
        {
            Assert.True(AwaitCloseAsync(connection, quit: true).IsCompleted, "a run awaiting no watchFinal frame waited");
        }
    }

    [Fact]
    public async Task NoWaitOnceTheHandlerHasHadItsFrame()
    {
        (FakeBridge game, BridgeConnection connection) = await ConnectAsync();
        using (game)
        await using (connection)
        {
            TaskCompletionSource delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.OnWatchFinal(_ => delivered.TrySetResult());
            await game.WriteAsync(new JsonObject { ["type"] = "watchFinal", ["timeline"] = new JsonObject() }, Cancellation);
            await delivered.Task.WaitAsync(RegistryHarness.HangGuard, Cancellation);

            Assert.True(AwaitCloseAsync(connection, quit: true).IsCompleted, "a run whose watchFinal frame came waited");
        }
    }

    [Fact]
    public async Task NoWaitAfterAKillOrWithoutAGameThatRan()
    {
        (FakeBridge game, BridgeConnection connection) = await ConnectAsync();
        using (game)
        await using (connection)
        {
            connection.OnWatchFinal(_ => { });

            Assert.True(AwaitCloseAsync(connection, quit: false).IsCompleted, "a run that did not quit on its own waited");
        }
    }

    private Task AwaitCloseAsync(BridgeConnection connection, bool quit) =>
        GodotSession.AwaitBridgeCloseAsync(connection, quit, _clock, NullLogger.Instance, _projectDir);

    /// <summary>A fake game dialled into the harness's listener, and the server's connection to it.</summary>
    private async Task<(FakeBridge Game, BridgeConnection Connection)> ConnectAsync()
    {
        Task<BridgeConnection> accept = _harness.Listener.AcceptBridgeAsync(new HandshakeExpectation(Token, _projectDir), Cancellation);
        FakeBridge game = await FakeBridge.DialAsync(_harness.Listener.Port, Token, _projectDir, Cancellation, answerPings: false);
        return (game, await accept.WaitAsync(RegistryHarness.HangGuard, Cancellation));
    }
}
