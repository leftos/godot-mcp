using System.Diagnostics;
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

    // Starts a ping that outlives the game and returns its pid.
    private const string LingeringChildScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\treturn OS.create_process(\"ping\", [\"-n\", \"10\", \"127.0.0.1\"])\n";

    // Adds a node whose _exit_tree blocks for 5 s, so the game is still shutting down when the stop's grace ends.
    private const string SlowToQuitScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\tvar script := GDScript.new()\n"
        + "\tscript.source_code = \"extends Node\\n\\n\\nfunc _exit_tree() -> void:\\n\\tOS.delay_msec(5000)\\n\"\n"
        + "\tscript.reload()\n"
        + "\tvar node := Node.new()\n"
        + "\tnode.set_script(script)\n"
        + "\tscene_tree.root.add_child(node)\n"
        + "\treturn true\n";

    // The same, but blocking 1.2 s: the game quits, only just inside the stop's 3 s grace.
    private const string SlowButInTimeScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\tvar script := GDScript.new()\n"
        + "\tscript.source_code = \"extends Node\\n\\n\\nfunc _exit_tree() -> void:\\n\\tOS.delay_msec(1200)\\n\"\n"
        + "\tscript.reload()\n"
        + "\tvar node := Node.new()\n"
        + "\tnode.set_script(script)\n"
        + "\tscene_tree.root.add_child(node)\n"
        + "\treturn true\n";

    private static readonly TimeSpan ChildExitWait = TimeSpan.FromSeconds(5);
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
        Assert.Equal(Installation.FindGodot(), launched.Godot);
        Assert.Equal("InputProbe", stopped.Session);
        Assert.False(stopped.Killed);
        Assert.False(stopped.AlreadyExited);
        Assert.Equal(0, stopped.GameExitCode);
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
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
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
        IReadOnlyList<SessionInfo> listed = _harness.Sessions.List(includeStopped: true);
        StopResult serverStopped = await _harness.Sessions.StopAsync("server", cancellation);
        bool overrideAfterFirstStop = File.Exists(_probe.OverrideFile);
        bool clientAnsweredAlone = await PingAsync("client");
        StopResult clientStopped = await _harness.Sessions.StopAsync("client", cancellation);

        Assert.NotEqual(serverShot, clientShot);
        Assert.True(File.Exists(serverShot) && File.Exists(clientShot));
        Assert.True(serverAnswered && clientAnswered);
        Assert.Equal(["client", "server"], listed.Select(session => session.Name));
        Assert.All(listed, session => Assert.True(session.Live && session.Kind == "run" && session.ProcessId is not null));
        Assert.All(listed, session => Assert.True(session.GameProcessId is not null && session.GameProcessId != session.ProcessId));
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
            _harness.Sessions.AttachAsync(_probe.Directory, "client", TimeSpan.FromSeconds(5), false, false, cancellation)
        );
        bool serverAnswered = await PingAsync("server");

        Assert.Equal(
            $"Sessions on {ProjectPaths.Normalise(_probe.Directory)} run with quiet=true; start this one with the same value, or stop "
                + "them first.",
            refused.Message
        );
        Assert.True(serverAnswered);
        Assert.Equal(["server"], _harness.Sessions.List(includeStopped: true).Select(session => session.Name));
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
        Assert.Equal(["server"], _harness.Sessions.List(includeStopped: true).Select(session => session.Name));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARuntimeToolWithoutANameRefusesWhileSeveralSessionsAreLive()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), "server", cancellation);
        await _harness.Sessions.LaunchAsync(Request(), "client", cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => tools.GetUiElementsAsync(cancellationToken: cancellation));

        Assert.Equal(
            "Several sessions exist (live: client, server); pass session to choose one. Pass the session run_project "
                + "or attach_project returned on every call: another agent's game can start at any time.",
            refused.Message
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ANameIsReusedAfterItsSessionStops()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        LaunchResult first = await _harness.Sessions.LaunchAsync(Request(), "a", cancellation);
        await _harness.Sessions.StopAsync("a", cancellation);

        LaunchResult second = await _harness.Sessions.LaunchAsync(Request(), "a", cancellation);
        bool answered = await PingAsync("a");
        SessionInfo listed = Assert.Single(_harness.Sessions.List(includeStopped: true));

        Assert.NotEqual(first.ProcessId, second.ProcessId);
        Assert.NotNull(listed.GameProcessId);
        Assert.Equal(new SessionInfo("a", ProjectPaths.Normalise(_probe.Directory), "run", true, second.ProcessId, listed.GameProcessId), listed);
        Assert.True(answered);
    }

    // The debugger is a fake that answers yes for every pid: the game itself runs and answers, so the restart and the stop each
    // end it gracefully and warn with the pid of the game they ended.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task StopWithADebuggerAttachedWarns()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        int firstGame = Assert.Single(_harness.Sessions.List(includeStopped: true)).GameProcessId!.Value;
        _harness.Sessions.IsDebuggerAttached = _ => true;

        RestartResult restarted = await _harness.Sessions.RestartAsync(null, prepare: false, cancellation);
        int secondGame = Assert.Single(_harness.Sessions.List(includeStopped: true)).GameProcessId!.Value;
        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        Assert.NotEqual(firstGame, secondGame);
        Assert.Equal($"A debugger was attached to the game (pid {firstGame}); its debug session ended with the game.", restarted.Warning);
        Assert.Equal($"A debugger was attached to the game (pid {secondGame}); its debug session ended with the game.", stopped.Warning);
        Assert.False(stopped.Killed);
        Assert.True(stopped.OverrideRemoved);
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
            () => !_harness.Sessions.List(includeStopped: true).Single(session => session.Name == "client").Live,
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
    public async Task StopAfterTheGameWasKilledOutsideSaysSo()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProjectTools project = new(_harness.Sessions);
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        int gameProcessId = Assert.Single(_harness.Sessions.List(includeStopped: false)).GameProcessId!.Value;

        using (var game = Process.GetProcessById(gameProcessId))
        {
            game.Kill();
        }

        bool ended = await Poll.UntilAsync(() => _harness.Sessions.List(includeStopped: false).Count == 0, TimeSpan.FromSeconds(10), cancellation);
        JsonNode stopped = JsonNode.Parse(await project.StopProjectAsync(cancellationToken: cancellation))!;
        int? gameExitCode = stopped["gameExitCode"]?.GetValue<int>();

        Assert.True(ended);
        Assert.True(stopped["alreadyExited"]?.GetValue<bool>(), stopped.ToJsonString());
        Assert.False(stopped["killed"]!.GetValue<bool>());
        Assert.NotNull(gameExitCode);
        Assert.NotEqual(0, gameExitCode);
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

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AGameThatQuitsWithAChildStillRunningIsNotKilled()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        string started = await tools.RunScriptAsync(LingeringChildScript, 10_000, null, cancellation);
        int pingId = JsonNode.Parse(started)!["value"]!.GetValue<int>();

        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);
        bool pingGone = await ProcessGoneAsync(pingId);

        Assert.False(stopped.Killed);
        Assert.Null(stopped.KillReason);
        Assert.NotNull(stopped.QuitMs);
        Assert.InRange(stopped.QuitMs.Value, 0, 2999);
        Assert.Equal(0, stopped.GameExitCode);
        Assert.True(stopped.OverrideRemoved);
        Assert.NotNull(stopped.LeftRunning);
        string left = Assert.Single(stopped.LeftRunning);
        Assert.Equal($"ping.exe (pid {pingId})", left, ignoreCase: true);
        Assert.True(pingGone, $"ping (pid {pingId}) was still running after the stop");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AGameSlowToQuitIsKilledAndSaysWhy()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        await tools.RunScriptAsync(SlowToQuitScript, 10_000, null, cancellation);

        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        Assert.True(stopped.Killed);
        Assert.Null(stopped.GameExitCode);
        Assert.NotNull(stopped.KillReason);
        Assert.Contains("still shutting down", stopped.KillReason, StringComparison.Ordinal);
        Assert.Null(stopped.QuitMs);
        Assert.Null(stopped.LeftRunning);
        Assert.True(stopped.OverrideRemoved);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStopSaysHowLongTheQuitTook()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        await tools.RunScriptAsync(SlowButInTimeScript, 10_000, null, cancellation);

        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        Assert.False(stopped.Killed);
        Assert.NotNull(stopped.QuitMs);
        Assert.InRange(stopped.QuitMs.Value, 1200, 2999);
        Assert.True(stopped.OverrideRemoved);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARestartReportsWhatTheOldGameLeftRunning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        string started = await tools.RunScriptAsync(LingeringChildScript, 10_000, null, cancellation);
        int pingId = JsonNode.Parse(started)!["value"]!.GetValue<int>();

        RestartResult restarted = await _harness.Sessions.RestartAsync(null, prepare: false, cancellation);
        await _harness.Sessions.StopAsync(null, cancellation);
        bool pingGone = await ProcessGoneAsync(pingId);

        Assert.Null(restarted.PreviousKillReason);
        Assert.NotNull(restarted.PreviousLeftRunning);
        string left = Assert.Single(restarted.PreviousLeftRunning);
        Assert.Equal($"ping.exe (pid {pingId})", left, ignoreCase: true);
        Assert.True(pingGone, $"ping (pid {pingId}) was still running after the restart");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARestartSaysWhyItKilledTheOldGame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        await tools.RunScriptAsync(SlowToQuitScript, 10_000, null, cancellation);

        RestartResult restarted = await _harness.Sessions.RestartAsync(null, prepare: false, cancellation);
        await _harness.Sessions.StopAsync(null, cancellation);

        Assert.NotNull(restarted.PreviousKillReason);
        Assert.Contains("still shutting down", restarted.PreviousKillReason, StringComparison.Ordinal);
        Assert.Null(restarted.PreviousLeftRunning);
        Assert.Null(restarted.PreviousQuitMs);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARestartSaysHowLongTheOldGameTookToQuit()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools tools = new(_harness.Sessions, TestCSharp.Unused());
        await _harness.Sessions.LaunchAsync(Request(), null, cancellation);
        await tools.RunScriptAsync(SlowButInTimeScript, 10_000, null, cancellation);

        RestartResult restarted = await _harness.Sessions.RestartAsync(null, prepare: false, cancellation);
        await _harness.Sessions.StopAsync(null, cancellation);

        Assert.Null(restarted.PreviousKillReason);
        Assert.NotNull(restarted.PreviousQuitMs);
        Assert.InRange(restarted.PreviousQuitMs.Value, 1200, 2999);
    }

    private LaunchRequest Request(string[]? userArgs = null, bool quiet = true, bool shutOutRealGamepads = false) =>
        new(_probe.Directory, null, [], userArgs ?? [], quiet, shutOutRealGamepads, Prepare: true);

    private async Task<bool> PingAsync(string? session)
    {
        JsonNode? pong = await _harness.Sessions.Resolve(session).SendAsync("ping", null, PingTimeout, TestContext.Current.CancellationToken);
        return pong?["pong"]?.GetValue<bool>() == true;
    }

    private static async Task<bool> ProcessGoneAsync(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            return await ProcessExit.WaitUntilGoneAsync(process, ChildExitWait);
        }
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
