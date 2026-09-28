using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>attach_project and detach_project against an InputProbe the test launches itself, with no godot-mcp environment.</summary>
public sealed class AttachTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 60_000;
    private const int AttachWaitSeconds = 30;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);
    private static readonly string[] GodotMcpVariables =
    [
        GodotCommandLine.PortVariable,
        GodotCommandLine.TokenVariable,
        GodotCommandLine.QuietVariable,
    ];
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;
    private readonly List<Process> _games = [];

    public AttachTests()
    {
        _project = new ProjectTools(_harness.Sessions);
        _runtime = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    private string AttachFilePath => AttachFile.PathIn(_probe.Directory);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await StopGamesAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachedGameAnswersRefusesStopAndOutputAndKeepsRunningAfterDetach()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        Task<string> attach = _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, false, cancellationToken: cancellation);
        Assert.True(await Poll.UntilAsync(() => File.Exists(AttachFilePath), TimeSpan.FromSeconds(10), cancellation));
        StartGame();
        JsonNode attached = JsonNode.Parse(await attach)!;
        bool attachFileLeft = File.Exists(AttachFilePath);
        bool overrideWhileAttached = File.Exists(_probe.OverrideFile);
        JsonNode? pong = await _harness.Sessions.Resolve(null).SendAsync("ping", null, PingTimeout, cancellation);
        int gameProcessId = (await RunAsync("return OS.get_process_id()")).GetValue<int>();
        _games.Add(Process.GetProcessById(gameProcessId));
        int? listedGameProcessId = Assert.Single(_harness.Sessions.List(includeStopped: true)).GameProcessId;
        McpException output = Assert.Throws<McpException>(() => _project.GetDebugOutput(10));
        McpException stop = await Assert.ThrowsAsync<McpException>(() => _project.StopProjectAsync(cancellationToken: cancellation));
        JsonNode detached = JsonNode.Parse(await _project.DetachProjectAsync(cancellationToken: cancellation))!;
        await Task.Delay(TimeSpan.FromSeconds(1), cancellation);

        Assert.Equal(ProjectPaths.Normalise(_probe.Directory), attached["projectPath"]!.GetValue<string>());
        Assert.False(attached["quiet"]!.GetValue<bool>());
        Assert.Matches(@"^\d+\.\d+\.\d+(\+[0-9a-f]{7})?$", attached["version"]?.GetValue<string>());
        Assert.False(attachFileLeft);
        Assert.True(overrideWhileAttached);
        Assert.True(pong?["pong"]?.GetValue<bool>());
        Assert.Equal(gameProcessId, listedGameProcessId);
        Assert.Contains("attached sessions have no captured output", output.Message, StringComparison.Ordinal);
        Assert.Contains("use detach_project", stop.Message, StringComparison.Ordinal);
        Assert.True(detached["overrideRemoved"]!.GetValue<bool>());
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.False(_games[^1].HasExited);
        Assert.False(File.Exists(_probe.OverrideFile));
        Assert.False(File.Exists(AttachFilePath));
        Assert.Equal(string.Empty, Git.Status(_probe.Directory));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AQuietAttachParksTheWindow()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        Task<string> attach = _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, false, quiet: true, cancellationToken: cancellation);
        Assert.True(await Poll.UntilAsync(() => File.Exists(AttachFilePath), TimeSpan.FromSeconds(10), cancellation));
        StartGame();
        JsonNode attached = JsonNode.Parse(await attach)!;
        JsonNode state = await RunAsync(
            "return {\"pid\": OS.get_process_id(), \"x\": DisplayServer.window_get_position().x, "
                + "\"focused\": DisplayServer.window_is_focused(), \"maxFps\": Engine.max_fps}"
        );
        _games.Add(Process.GetProcessById(state["pid"]!.GetValue<int>()));

        Assert.True(attached["quiet"]!.GetValue<bool>(), attached.ToJsonString());
        Assert.True(state["x"]!.GetValue<int>() <= -9000, state.ToJsonString());
        Assert.False(state["focused"]!.GetValue<bool>(), state.ToJsonString());
        Assert.Equal(60, state["maxFps"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachWithoutALaunchTimesOutAndLeavesNoFiles()
    {
        McpException timedOut = await Assert.ThrowsAsync<McpException>(() =>
            _project.AttachProjectAsync(_probe.Directory, 1, false, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Contains("connected within 1 s", timedOut.Message, StringComparison.Ordinal);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.False(File.Exists(_probe.OverrideFile));
        Assert.False(File.Exists(AttachFilePath));
        Assert.Equal(string.Empty, Git.Status(_probe.Directory));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnAttachedGameReportsErrors()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        Task<string> attach = _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, false, cancellationToken: cancellation);
        Assert.True(await Poll.UntilAsync(() => File.Exists(AttachFilePath), TimeSpan.FromSeconds(10), cancellation));
        StartGame();
        await attach;
        JsonNode result = await RunForResultAsync("push_error(\"attached probe error\")\n\treturn OS.get_process_id()");
        _games.Add(Process.GetProcessById(result["value"]!.GetValue<int>()));

        JsonNode error = Assert.Single(result["errors"]!.AsArray())!;
        Assert.Equal("attached probe error", error["message"]!.GetValue<string>());
        Assert.Equal(5, error["line"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnAttachedGameThatQuitsTakesItsSnapshotsWithIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        Task<string> attach = _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, false, cancellationToken: cancellation);
        Assert.True(await Poll.UntilAsync(() => File.Exists(AttachFilePath), TimeSpan.FromSeconds(10), cancellation));
        StartGame();
        await attach;
        var game = Process.GetProcessById((await RunAsync("return OS.get_process_id()")).GetValue<int>());
        _games.Add(game);
        string before = await SnapshotIdAsync(cancellation);
        string after = await SnapshotIdAsync(cancellation);
        GodotSession session = _harness.Sessions.Resolve(null);
        game.Kill();
        await game.WaitForExitAsync(cancellation);
        // The store is cleared just after the session sees the connection end, so the wait is for both.
        _ = await Poll.UntilAsync(() => !session.HasGame && session.Snapshots.Find(before) is null, TimeSpan.FromSeconds(10), cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _runtime.DiffSnapshotsAsync(before, after, cancellationToken: cancellation)
        );

        Assert.Contains($"snapshot {before} is not held", refused.Message, StringComparison.Ordinal);
    }

    // Godot as a user's script would start it: no GODOT_MCP_* variables, so the bridge can only find the attach file.
    private void StartGame()
    {
        ProcessStartInfo startInfo = new(Installation.FindGodot())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(_probe.Directory);
        foreach (string variable in GodotMcpVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        Process game = Process.Start(startInfo)!;
        _games.Add(game);
        game.StandardInput.Close();
        game.BeginOutputReadLine();
        game.BeginErrorReadLine();
    }

    // The harness kills and waits for the games its registry still knows, but detaching drops the session from it and these
    // games keep running after the detach, so they are killed and waited for here: Godot_console.exe exits before the
    // Godot.exe it wraps lets go of the probe folder.
    private async Task StopGamesAsync()
    {
        foreach (Process game in _games)
        {
            if (!game.HasExited)
            {
                game.Kill(entireProcessTree: true);
            }

            using CancellationTokenSource wait = new(ExitWait);
            await game.WaitForExitAsync(wait.Token);
            game.Dispose();
        }
    }

    private async Task<JsonNode> RunAsync(string body) => (await RunForResultAsync(body))["value"]!;

    private async Task<string> SnapshotIdAsync(CancellationToken cancellationToken) =>
        JsonNode.Parse(await _runtime.SnapshotSubtreeAsync(cancellationToken: cancellationToken))!["snapshotId"]!.GetValue<string>();

    private async Task<JsonNode> RunForResultAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _runtime.RunScriptAsync(script, 10_000, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!;
    }
}
