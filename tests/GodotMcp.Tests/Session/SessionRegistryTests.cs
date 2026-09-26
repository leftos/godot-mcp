using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The registry's names and rules, without Godot: a session that exists and is live is an attach still waiting for its
/// game, which the test cancels at the end.
/// </summary>
public sealed class SessionRegistryTests : IAsyncDisposable
{
    private const string NameRuleMessage = "a session name is 1 to 64 characters of letters, digits, '.', '_' and '-'";
    private static readonly TimeSpan LongWait = TimeSpan.FromSeconds(60);
    private readonly TempDirectory _temp = new();
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly List<(Task Attach, CancellationTokenSource Cancel)> _waiting = [];

    public SessionRegistryTests() => _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);

    public async ValueTask DisposeAsync()
    {
        foreach ((Task attach, CancellationTokenSource cancel) in _waiting)
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attach);
            cancel.Dispose();
        }

        _sessions.Dispose();
        _listener.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void ADefaultNameIsTheProjectFolderName()
    {
        string projectPath = _temp.Combine("delve") + Path.DirectorySeparatorChar;

        Assert.Equal("delve", SessionRegistry.NameFor(null, projectPath));
        Assert.Equal("client-1", SessionRegistry.NameFor("client-1", projectPath));
    }

    [Fact]
    public void AnInvalidNameIsRefused()
    {
        foreach (string name in new[] { "a b", "", new string('a', 65), "client/1" })
        {
            SessionException refused = Assert.Throws<SessionException>(() => SessionRegistry.NameFor(name, _temp.Path));

            Assert.Equal($"session '{name}' is not a valid name: {NameRuleMessage}.", refused.Message);
        }
    }

    [Fact]
    public void ValidNamesPass()
    {
        foreach (string name in new[] { "server", "client-1", "a.b_C", new string('a', 64) })
        {
            Assert.Equal(name, SessionRegistry.NameFor(name, _temp.Path));
        }
    }

    [Fact]
    public async Task ResolvingWithNoSessionSaysNoneIsRunning()
    {
        SessionException resolved = Assert.Throws<SessionException>(() => _sessions.Resolve(null));
        SessionException stopped = await Assert.ThrowsAsync<SessionException>(() => _sessions.StopAsync(null, TestContext.Current.CancellationToken));
        DebugOutput output = _sessions.GetDebugOutput(null, 10, null);

        Assert.Equal("No Godot session is running; start one with run_project or attach_project.", resolved.Message);
        Assert.Equal("No Godot session has been started, so there is nothing to stop. Start one with run_project.", stopped.Message);
        Assert.Null(output.Session);
        Assert.Empty(_sessions.List());
    }

    [Fact]
    public async Task ResolvingAnUnknownNameListsTheSessions()
    {
        SessionException withNone = Assert.Throws<SessionException>(() => _sessions.Resolve("client"));
        await StartWaitingAttachAsync(Project("alpha"), "server");

        SessionException withOne = Assert.Throws<SessionException>(() => _sessions.Resolve("client"));

        Assert.Equal("No session named 'client'. Sessions: none.", withNone.Message);
        Assert.Equal("No session named 'client'. Sessions: server (live).", withOne.Message);
    }

    [Fact]
    public async Task NamesAreCaseInsensitiveAndTheOnlyLiveSessionNeedsNoName()
    {
        await StartWaitingAttachAsync(Project("alpha"), "Server");

        Assert.Equal("Server", _sessions.Resolve("server").Name);
        Assert.Equal("Server", _sessions.Resolve(null).Name);
    }

    [Fact]
    public async Task SeveralLiveSessionsNeedAName()
    {
        await StartWaitingAttachAsync(Project("alpha"), "server");
        await StartWaitingAttachAsync(Project("beta"), "client");

        SessionException refused = Assert.Throws<SessionException>(() => _sessions.Resolve(null));

        Assert.Equal("Several sessions exist (client (live), server (live)); pass session to choose one.", refused.Message);
    }

    [Fact]
    public async Task ALiveNameIsRefused()
    {
        string alpha = Project("alpha");
        await StartWaitingAttachAsync(alpha, "server");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _sessions.AttachAsync(Project("beta"), "SERVER", LongWait, false, TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"A session named 'server' is live on {alpha}; stop_project or detach_project it, or pass another session name.",
            refused.Message
        );
    }

    [Fact]
    public async Task ASecondAttachOnAFolderWhileOneWaitsIsRefused()
    {
        string alpha = Project("alpha");
        await StartWaitingAttachAsync(alpha, "server");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _sessions.AttachAsync(alpha, "client", LongWait, false, TestContext.Current.CancellationToken)
        );

        Assert.Equal($"Another attach on {alpha} is still waiting for its game; wait for it or let it time out first.", refused.Message);
        Assert.Equal(["server"], _sessions.List().Select(session => session.Name));
    }

    [Fact]
    public async Task ADifferentShutOutOnALiveFolderIsRefused()
    {
        string alpha = Project("alpha");
        await StartWaitingAttachAsync(alpha, "server");
        LaunchRequest request = new(alpha, null, [], [], false, ShutOutRealGamepads: true);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _sessions.LaunchAsync(request, "client", TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"Sessions on {alpha} run with shutOutRealGamepads=false; start this one with the same value, or stop them first.",
            refused.Message
        );
    }

    [Fact]
    public async Task AWaitingAttachIsListedWithoutAProcess()
    {
        string alpha = Project("alpha");
        await StartWaitingAttachAsync(alpha, "server");

        SessionInfo listed = Assert.Single(_sessions.List());

        Assert.Equal(new SessionInfo("server", alpha, "attach", true, null), listed);
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task AnAttachThatTimesOutLeavesNoSessionAndNoOverride()
    {
        string alpha = Project("alpha");

        await Assert.ThrowsAsync<SessionException>(() =>
            _sessions.AttachAsync(alpha, null, TimeSpan.FromSeconds(1), false, TestContext.Current.CancellationToken)
        );

        Assert.Empty(_sessions.List());
        Assert.False(File.Exists(OverrideFile.PathIn(alpha)));
    }

    [Fact]
    public async Task DetachingOneSessionLeavesAnotherAttachsFileInPlace()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string alpha = Project("alpha");
        string attachFile = AttachFile.PathIn(alpha);
        Task<AttachResult> first = _sessions.AttachAsync(alpha, "first", LongWait, false, cancellation);
        await WaitUntilAsync(() => File.Exists(attachFile));
        string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
        using FakeBridge game = await FakeBridge.DialAsync(_listener.Port, token, alpha, cancellation);
        await first;

        await StartWaitingAttachAsync(alpha, "second");
        await WaitUntilAsync(() => File.Exists(attachFile));
        DetachResult detached = await _sessions.DetachAsync("first", cancellation);

        Assert.False(detached.OverrideRemoved);
        Assert.True(File.Exists(attachFile));
        Assert.True(File.Exists(OverrideFile.PathIn(alpha)));
    }

    private string Project(string folder)
    {
        string projectDir = _temp.Combine(folder);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "project.godot"), "config_version=5\n");
        return projectDir;
    }

    private async Task StartWaitingAttachAsync(string projectDir, string name)
    {
        CancellationTokenSource cancel = new();
        Task attach = _sessions.AttachAsync(projectDir, name, LongWait, false, cancel.Token);
        _waiting.Add((attach, cancel));
        await WaitUntilAsync(() => _sessions.List().Any(session => session.Name == name));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition did not hold within 10 s");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
