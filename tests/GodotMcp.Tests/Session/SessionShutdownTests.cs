using System.Diagnostics;
using GodotMcp.Server.Session;
using GodotMcp.Tests.Wire;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The server's exit: an attached game is left running and asked nothing, a session that arrives after a pass is taken by
/// the next one, and no game starts once the exit has begun.
/// </summary>
public sealed class SessionShutdownTests : IAsyncDisposable
{
    // Anything the shutdown sent is already in the socket when it returns; this only lets the read see it.
    private static readonly TimeSpan ReadWindow = TimeSpan.FromMilliseconds(500);

    private readonly RegistryHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task AnAttachedGameIsLeftRunningAndAskedNothing()
    {
        string projectDir = _harness.Project("Attached");
        Process game = _harness.StartOwnedGame();
        using FakeBridge bridge = await _harness.AttachFakeGameAsync(projectDir, "attached", game.Id);

        _harness.Sessions.Shutdown();
        _harness.Sessions.Shutdown();
        using CancellationTokenSource window = new(ReadWindow);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.ReadRequestAsync(window.Token));
        Assert.False(game.HasExited);
        Assert.False(File.Exists(AttachFile.PathIn(projectDir)));
    }

    [Fact]
    public async Task ASessionAttachedAfterAPassIsTakenByTheNextPass()
    {
        string projectDir = _harness.Project("Late");
        string attachFile = AttachFile.PathIn(projectDir);
        _harness.Sessions.Shutdown();
        // An attach waiting for its game keeps its attach file, which only a shutdown pass that takes the session removes.
        await _harness.StartWaitingAttachAsync(projectDir, "late");
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile));

        _harness.Sessions.Shutdown();

        Assert.False(File.Exists(attachFile), "the second pass did not take the session the first one never saw");
    }

    [Fact]
    public async Task ALaunchAfterTheShutdownBeganIsRefused()
    {
        string projectDir = _harness.Project("Refused");
        _harness.Sessions.Shutdown();

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.LaunchAsync(
                new LaunchRequest(projectDir, null, [], [], true, false, Prepare: true),
                null,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("The godot-mcp server is shutting down; no game can start now.", refused.Message);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }
}
