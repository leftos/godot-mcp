using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>Launching the real Godot on the InputProbe with the bridge injected, several sessions at once, and leaving no trace after.</summary>
public sealed class SessionLifecycleTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 45_000;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    // Quits a moment after returning, so the reply goes out before the game ends.
    private const string QuitSoonScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\tscene_tree.create_timer(0.3).timeout.connect(scene_tree.quit)\n\treturn true\n";
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task LaunchHandshakesPassesUserArgsAndStopsWithoutATrace()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        byte[] projectBefore = File.ReadAllBytes(_probe.ProjectFile);

        LaunchResult launched = await _harness.Sessions.LaunchAsync(Request(userArgs: ["--hello", "a b"]), null, cancellation);
        Assert.True(File.Exists(_probe.OverrideFile));
        Assert.Equal(string.Empty, Git.Status(_probe.Directory));
        Assert.True(await PingAsync(null));
        Assert.True(await StdoutContainsAsync("[probe] ready args=[\"--hello\",\"a b\"]"));
        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        Assert.Equal("InputProbe", launched.Session);
        Assert.Equal("InputProbe", stopped.Session);
        Assert.False(stopped.Killed);
        Assert.True(stopped.OverrideRemoved);
        Assert.False(_harness.Sessions.GetDebugOutput(null, 1, null).Running);
        Assert.False(File.Exists(_probe.OverrideFile));
        Assert.Equal(projectBefore, File.ReadAllBytes(_probe.ProjectFile));
        Assert.Equal(string.Empty, Git.Status(_probe.Directory));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RefusesTheProjectsOwnOverrideAndLeavesItByteIdentical()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        File.WriteAllText(_probe.OverrideFile, "[application]\nconfig/name=\"Mine\"\n");
        byte[] before = File.ReadAllBytes(_probe.OverrideFile);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.LaunchAsync(Request(), null, cancellation));

        Assert.Contains("override.cfg", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_probe.OverrideFile));
        Assert.False(_harness.Sessions.GetDebugOutput(null, 1, null).Running);
        Assert.Empty(_harness.Sessions.List());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ReplacesAStaleMarkedOverrideAndRemovesItAfter()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        File.WriteAllText(_probe.OverrideFile, $"{OverrideFile.Marker}\n[autoload]\n\nGodotMcpBridge=\"*D:/gone/old_bridge.gd\"\n");

        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        string injected = File.ReadAllText(_probe.OverrideFile);
        await _harness.Sessions.StopAsync(null, cancellation);

        Assert.DoesNotContain("old_bridge.gd", injected, StringComparison.Ordinal);
        Assert.Contains(RepoPaths.BridgeScript.Replace('\\', '/'), injected, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_probe.OverrideFile));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASecondLaunchUnderALiveNameIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.LaunchAsync(Request(), null, cancellation));
        bool answered = await PingAsync(null);
        await _harness.Sessions.StopAsync(null, cancellation);

        Assert.Equal(
            $"A session named 'InputProbe' is live on {ProjectPaths.Normalise(_probe.Directory)}; stop_project or detach_project it, "
                + "or pass another session name.",
            refused.Message
        );
        Assert.True(answered);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TwoSessionsOnOneProjectRunTogether()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), "server", cancellation);
        await _harness.Sessions.LaunchAsync(Request(), "client", cancellation);

        string serverShot = await ScreenshotPathAsync(tools, "server");
        string clientShot = await ScreenshotPathAsync(tools, "client");
        bool serverAnswered = await PingAsync("server");
        bool clientAnswered = await PingAsync("client");
        IReadOnlyList<SessionInfo> listed = _harness.Sessions.List();
        StopResult serverStopped = await _harness.Sessions.StopAsync("server", cancellation);
        bool overrideAfterFirstStop = File.Exists(_probe.OverrideFile);
        bool clientAnsweredAlone = await PingAsync("client");
        StopResult clientStopped = await _harness.Sessions.StopAsync("client", cancellation);

        Assert.NotEqual(serverShot, clientShot);
        Assert.True(File.Exists(serverShot) && File.Exists(clientShot));
        Assert.True(serverAnswered && clientAnswered);
        Assert.Equal(["client", "server"], listed.Select(session => session.Name));
        Assert.All(listed, session => Assert.True(session.Live && session.Kind == "run" && session.ProcessId is not null));
        Assert.False(serverStopped.OverrideRemoved);
        Assert.True(overrideAfterFirstStop);
        Assert.True(clientAnsweredAlone);
        Assert.True(clientStopped.OverrideRemoved);
        Assert.False(File.Exists(_probe.OverrideFile));
        Assert.Equal(string.Empty, Git.Status(_probe.Directory));
    }

    // A live run is needed on the folder, so this refusal is tested here rather than in SessionRegistryTests.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnAttachOnAFolderWithAQuietRunIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _harness.Sessions.LaunchAsync(Request(quiet: true), "server", cancellation);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.AttachAsync(_probe.Directory, "client", TimeSpan.FromSeconds(5), false, cancellation)
        );
        bool serverAnswered = await PingAsync("server");

        Assert.Equal(
            $"Sessions on {ProjectPaths.Normalise(_probe.Directory)} run with quiet=true; start this one with the same value, or stop "
                + "them first.",
            refused.Message
        );
        Assert.True(serverAnswered);
        Assert.Equal(["server"], _harness.Sessions.List().Select(session => session.Name));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADifferentGamepadShutOutOnTheSameProjectIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _harness.Sessions.LaunchAsync(Request(), "server", cancellation);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.LaunchAsync(Request(shutOutRealGamepads: true), "client", cancellation)
        );
        bool serverAnswered = await PingAsync("server");

        Assert.Equal(
            $"Sessions on {ProjectPaths.Normalise(_probe.Directory)} run with shutOutRealGamepads=false; start this one with the same "
                + "value, or stop them first.",
            refused.Message
        );
        Assert.True(serverAnswered);
        Assert.Equal(["server"], _harness.Sessions.List().Select(session => session.Name));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARuntimeToolWithoutANameRefusesWhileSeveralSessionsAreLive()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), "server", cancellation);
        await _harness.Sessions.LaunchAsync(Request(), "client", cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => tools.GetUiElementsAsync(cancellationToken: cancellation));

        Assert.Equal("Several sessions exist (client (live), server (live)); pass session to choose one.", refused.Message);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ANameIsReusedAfterItsSessionStops()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        LaunchResult first = await _harness.Sessions.LaunchAsync(Request(), "a", cancellation);
        await _harness.Sessions.StopAsync("a", cancellation);

        LaunchResult second = await _harness.Sessions.LaunchAsync(Request(), "a", cancellation);
        bool answered = await PingAsync("a");
        SessionInfo listed = Assert.Single(_harness.Sessions.List());

        Assert.NotEqual(first.ProcessId, second.ProcessId);
        Assert.Equal(new SessionInfo("a", ProjectPaths.Normalise(_probe.Directory), "run", true, second.ProcessId), listed);
        Assert.True(answered);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AGameThatQuitsLeavesTheOverrideForTheOtherSession()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), "server", cancellation);
        await _harness.Sessions.LaunchAsync(Request(), "client", cancellation);

        await tools.RunScriptAsync(QuitSoonScript, 10_000, "client", cancellation);
        bool clientEnded = await Poll.UntilAsync(
            () => !_harness.Sessions.List().Single(session => session.Name == "client").Live,
            TimeSpan.FromSeconds(10),
            cancellation
        );

        // The exit handler releases the folder a moment after the process ends; give it that moment before looking.
        await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
        bool overrideAfterQuit = File.Exists(_probe.OverrideFile);
        bool serverAnswered = await PingAsync("server");
        StopResult serverStopped = await _harness.Sessions.StopAsync("server", cancellation);

        Assert.True(clientEnded);
        Assert.True(overrideAfterQuit);
        Assert.True(serverAnswered);
        Assert.True(serverStopped.OverrideRemoved);
        Assert.False(File.Exists(_probe.OverrideFile));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AQuietRunStillHandshakes()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        LaunchResult launched = await _harness.Sessions.LaunchAsync(Request(quiet: true), null, cancellation);
        bool answered = await PingAsync(null);
        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        Assert.True(launched.Quiet);
        Assert.True(answered);
        Assert.False(stopped.Killed);
    }

    private LaunchRequest Request(string[]? userArgs = null, bool quiet = true, bool shutOutRealGamepads = false) =>
        new(_probe.Directory, null, [], userArgs ?? [], quiet, shutOutRealGamepads, Prepare: true);

    private async Task<bool> PingAsync(string? session)
    {
        JsonNode? pong = await _harness.Sessions.Resolve(session).SendAsync("ping", null, PingTimeout, TestContext.Current.CancellationToken);
        return pong?["pong"]?.GetValue<bool>() == true;
    }

    private static async Task<string> ScreenshotPathAsync(RuntimeTools tools, string session)
    {
        IEnumerable<ModelContextProtocol.Protocol.ContentBlock> blocks = await tools.TakeScreenshotAsync(
            "path_only",
            session: session,
            cancellationToken: TestContext.Current.CancellationToken
        );
        string text = blocks.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
        return JsonNode.Parse(text)!["path"]!.GetValue<string>();
    }

    private Task<bool> StdoutContainsAsync(string line) =>
        Poll.UntilAsync(
            () => _harness.Sessions.GetDebugOutput(null, GodotRun.OutputCapacity, null).Stdout.Contains(line),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );
}
