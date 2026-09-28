using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// restart_project against the real dotnet and Godot: a changed C# method rebuilt and relaunched under the same session, a
/// broken build leaving the old game running, the scene and arguments kept, the override kept, a game that quit started
/// again, and an attached session refused.
/// </summary>
public sealed class RestartTests : IAsyncDisposable
{
    private const int BuildTestTimeoutMs = 240_000;
    private const int TestTimeoutMs = 90_000;
    private const int AttachWaitSeconds = 30;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    // Quits a moment after returning, so the reply goes out before the game ends.
    private const string QuitSoonScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\tscene_tree.create_timer(0.3).timeout.connect(scene_tree.quit)\n\treturn true\n";

    // The engine arguments show as the window size: Godot keeps the engine options it reads out of OS.get_cmdline_args().
    private const string ArgumentsBody =
        "return [scene_tree.current_scene.scene_file_path, OS.get_cmdline_user_args(), DisplayServer.window_get_size().x, "
        + "DisplayServer.window_get_size().y]";

    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;
    private readonly List<IDisposable> _projects = [];

    public RestartTests()
    {
        _project = new ProjectTools(_harness.Sessions);
        _runtime = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (IDisposable project in _projects)
        {
            project.Dispose();
        }
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task AnEditedMethodIsRebuiltUnderTheSameSessionAndItsErrorsKeepCounting()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        LaunchResult launched = await LaunchAsync(csProbe.Directory, cancellation);
        long seqBefore = await PushErrorAsync("before the restart");
        string source = File.ReadAllText(csProbe.SourcePath("CsProbeNode.cs"));
        csProbe.WriteSource("CsProbeNode.cs", source.Replace("n + 1", "n + 2", StringComparison.Ordinal));

        JsonNode restarted = await RestartAsync(null, cancellation);
        JsonNode called = await CallAsync("CsProbe", "PlayStep", "4");
        long seqAfter = await PushErrorAsync("after the restart");
        JsonNode unchanged = await RestartAsync(null, cancellation);

        Assert.Equal(launched.Session, restarted["session"]!.GetValue<string>());
        Assert.NotEqual(launched.ProcessId, restarted["processId"]!.GetValue<int>());
        Assert.Equal(launched.ProcessId, restarted["previousProcessId"]!.GetValue<int>());

        // The old game answered and quit on the shutdown command, so its exit code is known and reported.
        Assert.Equal(0, restarted["previousExitCode"]!.GetValue<int>());
        Assert.False(restarted["previousAlreadyExited"]!.GetValue<bool>());
        Assert.Equal("built", restarted["prep"]!["build"]!.GetValue<string>());
        Assert.Equal(6, called["value"]!.GetValue<int>());
        Assert.True(seqAfter > seqBefore, $"the error after the restart has seq {seqAfter}, not above {seqBefore}");
        Assert.Equal("up-to-date", unchanged["prep"]!["build"]!.GetValue<string>());
        Assert.Equal(launched.Session, Assert.Single(_harness.Sessions.List(includeStopped: true)).Name);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task ABrokenSourceFailsTheRestartAndLeavesTheOldGameRunning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        LaunchResult launched = await LaunchAsync(csProbe.Directory, cancellation);
        string source = File.ReadAllText(csProbe.SourcePath("CsProbeNode.cs"));
        csProbe.WriteSource("CsProbeNode.cs", source.Replace("\"hidden\";", "\"hidden\"", StringComparison.Ordinal));

        McpException refused = await Assert.ThrowsAsync<McpException>(() => RestartAsync(null, cancellation));
        JsonNode called = await CallAsync("CsProbe", "PlayStep", "4");
        JsonNode listed = Assert.Single(JsonNode.Parse(_project.ListSessions())!["sessions"]!.AsArray())!;

        Assert.Contains("CsProbeNode.cs:10: CS1002", refused.Message, StringComparison.Ordinal);
        Assert.Equal(5, called["value"]!.GetValue<int>());
        Assert.Equal(launched.ProcessId, listed["processId"]!.GetValue<int>());
        Assert.True(listed["live"]!.GetValue<bool>());
        Assert.True(OverrideFile.IsOurs(OverrideFile.PathIn(csProbe.Directory)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARestartsImportIsRefusedOnlyForAnotherSessionsGame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        string target = await PrepTests.ImportIconThenDeleteGodotFolderAsync(probe.Directory, cancellation);
        LaunchRequest unprepared = new(probe.Directory, null, [], [], true, false, Prepare: false);
        LaunchResult first = await _harness.Sessions.LaunchAsync(unprepared, "first", cancellation);
        await _harness.Sessions.LaunchAsync(unprepared, "second", cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => RestartAsync("first", cancellation));
        bool firstAnswered = await PingAsync("first", cancellation);
        int? firstProcessId = _harness.Sessions.List(includeStopped: true).Single(session => session.Name == "first").ProcessId;
        await _project.StopProjectAsync("second", cancellation);
        JsonNode restarted = await RestartAsync("first", cancellation);

        Assert.Equal(
            $"the project at {ProjectPaths.Normalise(probe.Directory)} needs a Godot import, but session(s) second are running on it; "
                + "stop them first, or pass options.prepare: \"never\" to launch without importing.",
            refused.Message
        );
        Assert.True(firstAnswered);
        Assert.Equal(first.ProcessId, firstProcessId);
        Assert.Equal("done", restarted["prep"]!["import"]!.GetValue<string>());
        Assert.True(File.Exists(target), $"{target} was not imported");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARestartKeepsTheSceneAndArgumentsAndMarksTheOutput()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        LaunchRequest request = new(probe.Directory, "res://inspect_probe.tscn", ["--resolution", "1000x900"], ["--hello", "a b"], true, false, true);
        LaunchResult launched = await _harness.Sessions.LaunchAsync(request, null, cancellation);
        JsonNode argumentsBefore = await RunValueAsync(ArgumentsBody);

        JsonNode restarted = await RestartAsync(null, cancellation);
        JsonNode argumentsAfter = await RunValueAsync(ArgumentsBody);
        JsonNode output = JsonNode.Parse(_project.GetDebugOutput(GodotRun.OutputCapacity))!;
        string[] stdout = Lines(output, "stdout");
        string[] stderr = Lines(output, "stderr");

        string marker =
            $"[godot-mcp] restarted: the previous game (pid {launched.ProcessId}) was stopped; output below is from pid "
            + $"{restarted["processId"]!.GetValue<int>()}.";
        Assert.Equal("res://inspect_probe.tscn", argumentsBefore[0]!.GetValue<string>());
        Assert.Equal(["--hello", "a b"], argumentsBefore[1]!.AsArray().Select(arg => arg!.GetValue<string>()));
        Assert.Equal(1000, argumentsBefore[2]!.GetValue<int>());
        Assert.Equal(900, argumentsBefore[3]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(argumentsBefore, argumentsAfter), $"before: {argumentsBefore}\nafter: {argumentsAfter}");
        Assert.True(Array.IndexOf(stdout, marker) > 0, $"no marker after older lines in stdout:\n{string.Join('\n', stdout)}");
        Assert.Contains(marker, stderr);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TheOverrideSurvivesTheRestartAndGoesWithTheStop()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        await LaunchAsync(probe.Directory, cancellation);

        await RestartAsync(null, cancellation);

        // The old game's exit handler runs once the restart lets go of the session; give it that moment before looking.
        await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
        bool present = File.Exists(probe.OverrideFile);
        bool marked = present && OverrideFile.IsOurs(probe.OverrideFile);
        bool answered = await PingAsync(null, cancellation);
        JsonNode stopped = JsonNode.Parse(await _project.StopProjectAsync(cancellationToken: cancellation))!;

        Assert.True(present);
        Assert.True(marked);
        Assert.True(answered);
        Assert.True(stopped["overrideRemoved"]!.GetValue<bool>());
        Assert.False(File.Exists(probe.OverrideFile));
        Assert.Equal(string.Empty, Git.Status(probe.Directory));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AGameThatQuitItselfIsStartedAgain()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        LaunchResult launched = await LaunchAsync(probe.Directory, cancellation);
        await _runtime.RunScriptAsync(QuitSoonScript, 10_000, cancellationToken: cancellation);
        bool ended = await Poll.UntilAsync(
            () => !Assert.Single(_harness.Sessions.List(includeStopped: true)).Live,
            TimeSpan.FromSeconds(10),
            cancellation
        );
        await Task.Delay(TimeSpan.FromSeconds(1), cancellation);

        JsonNode restarted = await RestartAsync(null, cancellation);
        bool answered = await PingAsync(null, cancellation);
        SessionInfo listed = Assert.Single(_harness.Sessions.List(includeStopped: true));

        Assert.True(ended);
        Assert.NotEqual(launched.ProcessId, restarted["processId"]!.GetValue<int>());
        Assert.NotNull(restarted["previousExitCode"]);
        Assert.True(restarted["previousAlreadyExited"]!.GetValue<bool>());
        Assert.Equal(0, restarted["previousGameExitCode"]!.GetValue<int>());
        Assert.True(answered);
        Assert.True(listed.Live);
        Assert.True(File.Exists(probe.OverrideFile));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnAttachedSessionIsNotRestarted()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        Task<string> attach = _project.AttachProjectAsync(probe.Directory, AttachWaitSeconds, false, cancellationToken: cancellation);
        Assert.True(await Poll.UntilAsync(() => File.Exists(AttachFile.PathIn(probe.Directory)), TimeSpan.FromSeconds(10), cancellation));
        StartGame(probe.Directory);
        await attach;

        McpException refused = await Assert.ThrowsAsync<McpException>(() => RestartAsync(null, cancellation));
        bool answered = await PingAsync(null, cancellation);

        Assert.Equal(
            "session 'InputProbe' is attached, not started by run_project, so it cannot be restarted; detach_project, then start "
                + "the game again yourself.",
            refused.Message
        );
        Assert.True(answered);
    }

    private T Track<T>(T project)
        where T : IDisposable
    {
        _projects.Add(project);
        return project;
    }

    private Task<LaunchResult> LaunchAsync(string directory, CancellationToken cancellation) =>
        _harness.Sessions.LaunchAsync(new LaunchRequest(directory, null, [], [], true, false, true), null, cancellation);

    private async Task<JsonNode> RestartAsync(string? session, CancellationToken cancellation) =>
        JsonNode.Parse(await _project.RestartProjectAsync(session: session, cancellationToken: cancellation))!;

    private async Task<bool> PingAsync(string? session, CancellationToken cancellation)
    {
        JsonNode? pong = await _harness.Sessions.Resolve(session).SendAsync("ping", null, PingTimeout, cancellation);
        return pong?["pong"]?.GetValue<bool>() == true;
    }

    /// <summary>Pushes an error from the game and returns its seq.</summary>
    private async Task<long> PushErrorAsync(string message)
    {
        JsonNode result = await RunForResultAsync($"push_error(\"{message}\")\n\treturn true");
        return Assert.Single(result["errors"]!.AsArray())!["seq"]!.GetValue<long>();
    }

    private async Task<JsonNode> RunValueAsync(string body) => (await RunForResultAsync(body))["value"]!;

    private async Task<JsonNode> RunForResultAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _runtime.RunScriptAsync(script, 10_000, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!;
    }

    private async Task<JsonNode> CallAsync(string node, string method, params string[] argsJson)
    {
        JsonElement[] args = [.. argsJson.Select(arg => JsonSerializer.Deserialize<JsonElement>(arg))];
        string json = await _runtime.CallMethodAsync(node, method, args, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!;
    }

    private static string[] Lines(JsonNode output, string stream) => [.. output[stream]!.AsArray().Select(line => line!.GetValue<string>())];

    // Godot as a user's script would start it: no GODOT_MCP_* variables, so the bridge can only find the attach file.
    private static void StartGame(string projectDir)
    {
        ProcessStartInfo startInfo = new(Installation.FindGodot())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(projectDir);
        string[] variables =
        [
            GodotCommandLine.PortVariable,
            GodotCommandLine.TokenVariable,
            GodotCommandLine.QuietVariable,
            GodotCommandLine.HiddenDesktopVariable,
        ];
        foreach (string variable in variables)
        {
            startInfo.Environment.Remove(variable);
        }

        Process game = Process.Start(startInfo)!;
        game.StandardInput.Close();
        game.BeginOutputReadLine();
        game.BeginErrorReadLine();
    }
}
