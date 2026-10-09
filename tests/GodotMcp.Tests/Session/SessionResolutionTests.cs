using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Wire;

namespace GodotMcp.Tests.Session;

/// <summary>How the registry resolves a name to a session and lists what it holds.</summary>
public sealed class SessionResolutionTests : IAsyncDisposable
{
    private readonly RegistryHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task ResolvingWithNoSessionSaysNoneIsRunning()
    {
        SessionException resolved = Assert.Throws<SessionException>(() => _harness.Sessions.Resolve(null));
        SessionException stopped = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.StopAsync(null, TestContext.Current.CancellationToken)
        );
        DebugOutput output = _harness.Sessions.GetDebugOutput(null, 10, null);

        Assert.Equal("No Godot session is running; start one with run_project or attach_project.", resolved.Message);
        Assert.Equal("No Godot session has been started, so there is nothing to stop. Start one with run_project.", stopped.Message);
        Assert.Null(output.Session);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    [Fact]
    public async Task StopWithNeitherFoundIsRefused()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal($"No Godot session or warm headless host runs in {alpha}.", refused.Message);
    }

    [Fact]
    public async Task StopWithBothNamingDifferentFoldersIsRefused()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        string beta = ProjectPaths.Normalise(_harness.Project("beta"));
        await _harness.StartWaitingAttachAsync(alpha, "server");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.StopFolderAsync(beta, "server", TestContext.Current.CancellationToken)
        );

        Assert.Equal($"session 'server' runs {alpha}, not projectPath {beta}; pass one of them.", refused.Message);
        Assert.Equal(["server"], _harness.Sessions.List(includeStopped: false).Select(session => session.Name));
    }

    [Fact]
    public async Task StopWithProjectPathAndSeveralLiveSessionsThereIsRefused()
    {
        string alpha = ProjectPaths.Normalise(_harness.Project("alpha"));
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello());
        await _harness.StartWaitingAttachAsync(alpha, "client");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.StopFolderAsync(alpha, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal($"Several sessions run in {alpha}: client, server; pass session to choose one.", refused.Message);
        Assert.Equal(["client", "server"], _harness.Sessions.List(includeStopped: false).Select(session => session.Name));
    }

    [Fact]
    public async Task ResolvingAnUnknownNameListsTheSessions()
    {
        SessionException withNone = Assert.Throws<SessionException>(() => _harness.Sessions.Resolve("client"));
        await _harness.StartWaitingAttachAsync(_harness.Project("alpha"), "server");

        SessionException withOne = Assert.Throws<SessionException>(() => _harness.Sessions.Resolve("client"));

        Assert.Equal("No session named 'client'. Live sessions: none.", withNone.Message);
        Assert.Equal("No session named 'client'. Live sessions: server.", withOne.Message);
    }

    [Fact]
    public async Task ResolvingAmongOnlyStoppedSessionsCountsThem()
    {
        await _harness.EndAttachedGameAsync(_harness.Project("alpha"), "server");
        await _harness.EndAttachedGameAsync(_harness.Project("beta"), "client");

        SessionException unknown = Assert.Throws<SessionException>(() => _harness.Sessions.Resolve("other"));
        SessionException unnamed = Assert.Throws<SessionException>(() => _harness.Sessions.Resolve(null));

        Assert.Equal(
            "No session named 'other'. Live sessions: none. 2 stopped (list_sessions with includeStopped: true lists them).",
            unknown.Message
        );
        Assert.Equal(
            "Several sessions exist (live: none); pass session to choose one. Pass the session run_project or "
                + "attach_project returned on every call: another agent's game can start at any time. 2 stopped "
                + "(list_sessions with includeStopped: true lists them).",
            unnamed.Message
        );
    }

    [Fact]
    public async Task ListShowsStoppedSessionsOnlyWhenAsked()
    {
        await _harness.EndAttachedGameAsync(_harness.Project("alpha"), "server");
        await _harness.StartWaitingAttachAsync(_harness.Project("beta"), "client");

        IReadOnlyList<SessionInfo> live = _harness.Sessions.List(includeStopped: false);
        IReadOnlyList<SessionInfo> all = _harness.Sessions.List(includeStopped: true);

        Assert.Equal(["client"], live.Select(session => session.Name));
        Assert.Equal([("client", true), ("server", false)], all.Select(session => (session.Name, session.Live)));
    }

    [Fact]
    public async Task NamesAreCaseInsensitiveAndTheOnlyLiveSessionNeedsNoName()
    {
        await _harness.StartWaitingAttachAsync(_harness.Project("alpha"), "Server");

        Assert.Equal("Server", _harness.Sessions.Resolve("server").Name);
        Assert.Equal("Server", _harness.Sessions.Resolve(null).Name);
    }

    [Fact]
    public async Task SeveralLiveSessionsNeedAName()
    {
        await _harness.StartWaitingAttachAsync(_harness.Project("alpha"), "server");
        await _harness.StartWaitingAttachAsync(_harness.Project("beta"), "client");

        SessionException refused = Assert.Throws<SessionException>(() => _harness.Sessions.Resolve(null));

        Assert.Equal(
            "Several sessions exist (live: client, server); pass session to choose one. Pass the session run_project "
                + "or attach_project returned on every call: another agent's game can start at any time.",
            refused.Message
        );
    }

    [Fact]
    public async Task ALiveNameIsRefused()
    {
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(alpha, "server");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(
                new AttachRequest(_harness.Project("beta"), "SERVER", RegistryHarness.LongWait, false, false, null),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(
            $"A session named 'server' is live on {alpha}; stop_project or detach_project it, or pass another session name.",
            refused.Message
        );
    }

    [Fact]
    public async Task ASecondAttachOnAFolderWhileOneWaitsIsRefused()
    {
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(alpha, "server");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(
                new AttachRequest(alpha, "client", RegistryHarness.LongWait, false, false, null),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal($"Another attach on {alpha} is still waiting for its game; wait for it or let it time out first.", refused.Message);
        Assert.Equal(["server"], _harness.Sessions.List(includeStopped: true).Select(session => session.Name));
    }

    [Fact]
    public async Task ADifferentShutOutOnALiveFolderIsRefused()
    {
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(alpha, "server");
        LaunchRequest request = new(alpha, null, [], [], false, ShutOutRealGamepads: true, Prepare: true);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.LaunchAsync(request, "client", TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"Sessions on {alpha} run with shutOutRealGamepads=false; start this one with the same value, or stop them first.",
            refused.Message
        );
    }

    [Fact]
    public async Task AQuietWaitingAttachRefusesANotQuietLaunchBesideIt()
    {
        string alpha = _harness.Project("alpha");
        CancellationTokenSource cancel = new();
        _harness.Waiting.Add(
            (_harness.Sessions.AttachAsync(new AttachRequest(alpha, "server", RegistryHarness.LongWait, false, true, null), cancel.Token), cancel)
        );
        await RegistryHarness.WaitUntilAsync(() => _harness.Sessions.List(includeStopped: true).Any(session => session.Name == "server"));
        LaunchRequest request = new(alpha, null, [], [], Quiet: false, ShutOutRealGamepads: false, Prepare: true);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.LaunchAsync(request, "client", TestContext.Current.CancellationToken)
        );

        Assert.Equal($"Sessions on {alpha} run with quiet=true; start this one with the same value, or stop them first.", refused.Message);
        Assert.Equal(["server"], _harness.Sessions.List(includeStopped: true).Select(session => session.Name));
    }

    // An attach is not quiet unless asked, so a waiting one stands in for a live session with quiet=false.
    [Fact]
    public async Task ADifferentQuietOnALiveFolderIsRefused()
    {
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(alpha, "server");
        LaunchRequest request = new(alpha, null, [], [], Quiet: true, ShutOutRealGamepads: false, Prepare: true);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.LaunchAsync(request, "client", TestContext.Current.CancellationToken)
        );

        Assert.Equal($"Sessions on {alpha} run with quiet=false; start this one with the same value, or stop them first.", refused.Message);
        Assert.Equal(["server"], _harness.Sessions.List(includeStopped: true).Select(session => session.Name));
    }

    [Fact]
    public async Task AWaitingAttachIsListedWithoutAProcess()
    {
        string alpha = _harness.Project("alpha");
        await _harness.StartWaitingAttachAsync(alpha, "server");

        SessionInfo listed = Assert.Single(_harness.Sessions.List(includeStopped: true));

        Assert.Equal(new SessionInfo("server", alpha, "attach", true, null, null), listed);
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task ListSessionsReportsTheGamesOwnPid()
    {
        string alpha = _harness.Project("alpha");
        int pid = _harness.StartOwnedGame().Id;
        using FakeBridge game = await _harness.AttachFakeGameAsync(alpha, "server", new FakeHello { ProcessId = pid });

        SessionInfo listed = Assert.Single(_harness.Sessions.List(includeStopped: true));

        Assert.Equal(new SessionInfo("server", alpha, "attach", true, null, pid), listed);
    }
}
