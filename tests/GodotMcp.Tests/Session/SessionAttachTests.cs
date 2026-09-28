using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Wire;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

/// <summary>Attaching, detaching and restarting a session, and the folder's prep lock around them.</summary>
public sealed class SessionAttachTests : IAsyncDisposable
{
    private readonly RegistryHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task AnAttachThatTimesOutLeavesNoSessionAndNoOverride()
    {
        string alpha = _harness.Project("alpha");

        await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(alpha, null, TimeSpan.FromSeconds(1), false, false, TestContext.Current.CancellationToken)
        );

        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task DetachingOneSessionLeavesAnotherAttachsFileInPlace()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        string attachFile = AttachFile.PathIn(alpha);
        Task<AttachResult> first = _harness.Sessions.AttachAsync(alpha, "first", RegistryHarness.LongWait, false, false, cancellation);
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile));
        string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
        using FakeBridge game = await FakeBridge.DialAsync(_harness.Listener.Port, token, alpha, cancellation);
        await first;

        await _harness.StartWaitingAttachAsync(alpha, "second");
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile));
        DetachResult detached = await _harness.Sessions.DetachAsync("first", cancellation);

        Assert.False(detached.OverrideRemoved);
        Assert.True(File.Exists(attachFile));
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
    }

    // An attached session has no run, so any run's exit is the exit of a run that is not its current one, as the old run's is
    // once a restart has replaced it. A guard of "_run is null" would pass this too: the real case, an old run exiting while
    // the session's current run is a newer one, needs a launched Godot to make that newer run, so it is covered by the
    // integration test RestartTests.TheOverrideSurvivesTheRestartAndGoesWithTheStop, not here.
    [Fact]
    public async Task AReplacedRunsExitLeavesTheOverrideAndTheFolderReserved()
    {
        string alpha = _harness.Project("alpha");
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", null);
        GodotSession session = _harness.Sessions.Resolve("server");
        using Process neverStarted = new();

        await session.OnRunExitedAsync(new GodotRun(alpha, neverStarted, previous: null));

        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
        Assert.True(session.IsLive);
    }

    // The launch is parked on the folder's prep lock, which the test holds, so the session stays starting. It takes the lock
    // before it looks Godot up, so this needs no Godot installed.
    [Fact]
    public async Task RestartingASessionStillStartingIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = _harness.Project("alpha");
        SemaphoreSlim folderLock = _harness.Sessions.PrepLock(alpha);
        await folderLock.WaitAsync(cancellation);
        using CancellationTokenSource cancelLaunch = new();
        LaunchRequest request = new(alpha, null, [], [], true, false, Prepare: true);
        Task<LaunchResult> launch = _harness.Sessions.LaunchAsync(request, "server", cancelLaunch.Token);
        try
        {
            await RegistryHarness.WaitUntilAsync(() => _harness.Sessions.List(includeStopped: true).Any(session => session.Name == "server"));

            SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
                _harness.Sessions.RestartAsync("server", prepare: true, cancellation)
            );

            Assert.Equal("session 'server' is still starting or restarting; wait for its call to return, or stop_project it.", refused.Message);
        }
        finally
        {
            await cancelLaunch.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch);
            folderLock.Release();
        }
    }

    [Fact]
    public async Task RestartingAnAttachedSessionIsRefused()
    {
        string alpha = _harness.Project("alpha");
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", null);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.RestartAsync("server", prepare: true, TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            "session 'server' is attached, not started by run_project, so it cannot be restarted; detach_project, then start the game "
                + "again yourself.",
            refused.Message
        );
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task RestartingWithNoSessionOrAnUnknownNameIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        SessionException none = await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.RestartAsync(null, prepare: true, cancellation));
        SessionException unknown = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.RestartAsync("client", prepare: true, cancellation)
        );

        Assert.Equal("No Godot session is running; start one with run_project or attach_project.", none.Message);
        Assert.Equal("No session named 'client'. Live sessions: none.", unknown.Message);
    }

    [Fact]
    public async Task RestartsPrepareTakesAutoOrNever()
    {
        ProjectTools tools = new(_harness.Sessions);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            tools.RestartProjectAsync(new RestartOptions("sometimes"), cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal("prepare takes \"auto\" or \"never\"; got \"sometimes\".", refused.Message);
        Assert.True(new RestartOptions().ShouldPrepare());
        Assert.True(new RestartOptions("auto").ShouldPrepare());
        Assert.False(new RestartOptions("never").ShouldPrepare());
    }

    [Fact]
    public async Task AHeadlessRunIsRefusedWhileASessionIsLive()
    {
        string alpha = _harness.Project("alpha");
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", null);
        HeadlessRequest request = new(alpha, "validate", [], Prepare: false, Ceiling: TimeSpan.FromSeconds(60));

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            HeadlessRunner.RunAsync(_harness.Sessions, request, TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"a headless run is refused while session(s) server run on {alpha}: a --script run would load the bridge from its override.cfg. "
                + "stop_project or detach_project them first.",
            refused.Message
        );
        Assert.True(OverrideFile.IsOurs(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task AnAttachWritesItsOverrideOnlyOnceThePrepLockIsFree()
    {
        string alpha = _harness.Project("alpha");
        string overrideFile = OverrideFile.PathIn(alpha);
        SemaphoreSlim folderLock = _harness.Sessions.PrepLock(alpha);
        await folderLock.WaitAsync(TestContext.Current.CancellationToken);
        Task<AttachResult> attach;
        try
        {
            attach = _harness.Sessions.AttachAsync(alpha, "server", TimeSpan.FromSeconds(2), false, false, TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(File.Exists(overrideFile));
        }
        finally
        {
            folderLock.Release();
        }

        await RegistryHarness.WaitUntilAsync(() => File.Exists(overrideFile));
        await Assert.ThrowsAsync<SessionException>(() => attach);
    }

    [Fact]
    public async Task AWaitingAttachIsNotAGameRunningOnItsFolder()
    {
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(alpha, "server");

        Assert.Empty(_harness.Sessions.RunningSessionNames(alpha, except: null));
    }
}
