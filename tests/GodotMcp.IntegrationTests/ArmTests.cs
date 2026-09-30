using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>arm_project, disarm_project and attach_project's join of a dormant game, against InputProbes the test launches itself.</summary>
public sealed class ArmTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 60_000;
    private const int AttachWaitSeconds = 30;
    private static readonly TimeSpan DormantWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FileWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AbsenceWait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);
    private static readonly string[] GodotMcpVariables =
    [
        GodotCommandLine.PortVariable,
        GodotCommandLine.TokenVariable,
        GodotCommandLine.QuietVariable,
        GodotCommandLine.HiddenDesktopVariable,
    ];
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;
    private readonly List<Process> _games = [];

    public ArmTests()
    {
        _project = new ProjectTools(_harness.Sessions);
        _runtime = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    private string ArmFilePath => ArmFile.PathIn(_probe.Directory);

    public async ValueTask DisposeAsync()
    {
        foreach (ArmState armed in _harness.Sessions.ListArmed())
        {
            _harness.Sessions.Disarm(armed.ProjectPath);
        }

        await _harness.DisposeAsync();
        await StopGamesAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ADormantGameIsJoined()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonNode armed = JsonNode.Parse(await _project.ArmProjectAsync(_probe.Directory, cancellationToken: cancellation))!;
        int pid = await StartDormantGameAsync(cancellation);
        JsonNode listed = JsonNode.Parse(_project.ListSessions())!;
        JsonNode attached = JsonNode.Parse(await _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, cancellationToken: cancellation))!;
        bool joinFileLeft = File.Exists(DormantGames.JoinPathIn(_probe.Directory, pid));
        bool dormantFileLeft = File.Exists(DormantFilePath(pid));
        int answeredPid = (await RunAsync("return OS.get_process_id()")).GetValue<int>();

        Assert.Equal(ProjectPaths.Normalise(_probe.Directory), armed["projectPath"]!.GetValue<string>());
        Assert.Empty(armed["dormant"]!.AsArray());
        JsonNode armedFolder = Assert.Single(listed["armed"]!.AsArray())!;
        Assert.Equal(ProjectPaths.Normalise(_probe.Directory), armedFolder["projectPath"]!.GetValue<string>());
        Assert.Equal(pid, Assert.Single(DormantPidsIn(armedFolder)));
        Assert.Equal(pid, attached["joinedPid"]!.GetValue<int>());
        Assert.False(attached["quiet"]!.GetValue<bool>(), attached.ToJsonString());
        Assert.Equal(pid, answeredPid);
        Assert.False(joinFileLeft);
        Assert.False(dormantFileLeft);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ADetachedGameWaitsToBeJoinedAgain()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await _project.ArmProjectAsync(_probe.Directory, cancellationToken: cancellation);
        int pid = await StartDormantGameAsync(cancellation);
        await _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, cancellationToken: cancellation);
        JsonNode detached = JsonNode.Parse(await _project.DetachProjectAsync(cancellationToken: cancellation))!;
        bool dormantAgain = await Poll.UntilAsync(() => File.Exists(DormantFilePath(pid)), FileWait, cancellation);
        bool runningWhileDormant = !_games[^1].HasExited;
        JsonNode rejoined = JsonNode.Parse(await _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, cancellationToken: cancellation))!;
        int answeredPid = (await RunAsync("return OS.get_process_id()")).GetValue<int>();

        Assert.False(detached["overrideRemoved"]!.GetValue<bool>(), detached.ToJsonString());
        Assert.True(File.Exists(_probe.OverrideFile));
        Assert.True(dormantAgain);
        Assert.True(runningWhileDormant);
        Assert.Equal(pid, rejoined["joinedPid"]!.GetValue<int>());
        Assert.Equal(pid, answeredPid);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task StopProjectQuitsAJoinedDormantGame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await _project.ArmProjectAsync(_probe.Directory, cancellationToken: cancellation);
        int pid = await StartDormantGameAsync(cancellation);
        await _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, cancellationToken: cancellation);
        JsonNode stopped = JsonNode.Parse(await _project.StopProjectAsync(cancellationToken: cancellation))!;
        bool exited = _games[^1].HasExited;
        JsonNode listed = JsonNode.Parse(_project.ListSessions())!;

        Assert.False(stopped["killed"]!.GetValue<bool>(), stopped.ToJsonString());
        Assert.True(exited);
        Assert.True(File.Exists(_probe.OverrideFile));
        Assert.DoesNotContain(pid, DormantPidsIn(Assert.Single(listed["armed"]!.AsArray())!));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task SeveralDormantGamesArePickedByPid()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await _project.ArmProjectAsync(_probe.Directory, cancellationToken: cancellation);
        int first = await StartDormantGameAsync(cancellation);
        int second = await StartDormantGameAsync(cancellation);
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, cancellationToken: cancellation)
        );
        JsonNode attached = JsonNode.Parse(
            await _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, new AttachOptions(Pid: second), cancellation)
        )!;
        int answeredPid = (await RunAsync("return OS.get_process_id()")).GetValue<int>();

        Assert.Contains("2 dormant games wait", refused.Message, StringComparison.Ordinal);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"pid {first},"), refused.Message, StringComparison.Ordinal);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"pid {second},"), refused.Message, StringComparison.Ordinal);
        Assert.Equal(second, attached["joinedPid"]!.GetValue<int>());
        Assert.Equal(second, answeredPid);
        Assert.True(File.Exists(DormantFilePath(first)));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ADisarmedGameKeepsRunningAndIsNotJoined()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await _project.ArmProjectAsync(_probe.Directory, cancellationToken: cancellation);
        int pid = await StartDormantGameAsync(cancellation);
        JsonNode disarmed = JsonNode.Parse(await _project.DisarmProjectAsync(_probe.Directory))!;
        bool dormantFileGone = await Poll.UntilAsync(() => !File.Exists(DormantFilePath(pid)), FileWait, cancellation);
        McpException timedOut = await Assert.ThrowsAsync<McpException>(() =>
            _project.AttachProjectAsync(_probe.Directory, 2, cancellationToken: cancellation)
        );

        Assert.True(disarmed["overrideRemoved"]!.GetValue<bool>(), disarmed.ToJsonString());
        Assert.True(dormantFileGone);
        Assert.False(_games[^1].HasExited);
        Assert.False(File.Exists(_probe.OverrideFile));
        Assert.False(File.Exists(ArmFilePath));
        Assert.Empty(_harness.Sessions.ListArmed());
        Assert.Contains("connected within 2 s", timedOut.Message, StringComparison.Ordinal);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGameStartedBeforeTheArmIsNeverListed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        // The engine prints its banner in Main::setup2, after Main::setup has read override.cfg, so a game past its banner
        // has already decided it has no bridge.
        await StartGame().WaitAsync(DormantWait, cancellation);
        JsonNode armed = JsonNode.Parse(await _project.ArmProjectAsync(_probe.Directory, cancellationToken: cancellation))!;
        bool fileAppeared = await Poll.UntilAsync(() => DormantPids().Length > 0, AbsenceWait, cancellation);
        JsonNode listed = JsonNode.Parse(_project.ListSessions())!;

        Assert.Empty(armed["dormant"]!.AsArray());
        Assert.False(fileAppeared);
        Assert.Empty(DormantPidsIn(Assert.Single(listed["armed"]!.AsArray())!));
        Assert.False(_games[^1].HasExited);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AQuietJoinParksTheWindow()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonNode armed = JsonNode.Parse(await _project.ArmProjectAsync(_probe.Directory, new ArmOptions(Quiet: true), cancellation))!;
        int pid = await StartDormantGameAsync(cancellation);
        JsonNode attached = JsonNode.Parse(await _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, cancellationToken: cancellation))!;
        JsonNode state = await RunAsync(
            "return {\"pid\": OS.get_process_id(), \"x\": DisplayServer.window_get_position().x, "
                + "\"focused\": DisplayServer.window_is_focused(), \"maxFps\": Engine.max_fps}"
        );

        Assert.True(armed["quiet"]!.GetValue<bool>(), armed.ToJsonString());
        Assert.Equal(pid, attached["joinedPid"]!.GetValue<int>());
        Assert.True(attached["quiet"]!.GetValue<bool>(), attached.ToJsonString());
        Assert.Equal(pid, state["pid"]!.GetValue<int>());
        Assert.True(state["x"]!.GetValue<int>() <= -9000, state.ToJsonString());
        Assert.False(state["focused"]!.GetValue<bool>(), state.ToJsonString());
        Assert.Equal(60, state["maxFps"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APidThatIsNotDormantIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        int notDormant = Environment.ProcessId;

        await _project.ArmProjectAsync(_probe.Directory, cancellationToken: cancellation);
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _project.AttachProjectAsync(_probe.Directory, AttachWaitSeconds, new AttachOptions(Pid: notDormant), cancellation)
        );

        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"No dormant game with pid {notDormant} waits"),
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("A game started before arm_project", refused.Message, StringComparison.Ordinal);
        Assert.Contains("has no bridge to join", refused.Message, StringComparison.Ordinal);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.False(File.Exists(AttachFile.PathIn(_probe.Directory)));
    }

    private static int[] DormantPidsIn(JsonNode armedFolder) => [.. armedFolder["dormant"]!.AsArray().Select(game => game!["pid"]!.GetValue<int>())];

    private string DormantFilePath(int pid) =>
        Path.Combine(DormantGames.FolderIn(_probe.Directory), pid.ToString(CultureInfo.InvariantCulture) + ".json");

    private int[] DormantPids()
    {
        string folder = DormantGames.FolderIn(_probe.Directory);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return
        [
            .. Directory
                .EnumerateFiles(folder, "*.json")
                .Select(file => int.TryParse(Path.GetFileNameWithoutExtension(file), CultureInfo.InvariantCulture, out int pid) ? pid : 0)
                .Where(pid => pid != 0),
        ];
    }

    // The game's own process is the console wrapper's child, so its pid comes from the dormant file it writes; it is tracked
    // too, so the cleanup waits until it has let go of the probe folder.
    private async Task<int> StartDormantGameAsync(CancellationToken cancellationToken)
    {
        HashSet<int> before = [.. DormantPids()];
        _ = StartGame();
        int pid = 0;
        bool appeared = await Poll.UntilAsync(
            () => (pid = DormantPids().FirstOrDefault(found => !before.Contains(found))) != 0,
            DormantWait,
            cancellationToken
        );
        Assert.True(appeared, $"no dormant file appeared under {DormantGames.FolderIn(_probe.Directory)} within {DormantWait.TotalSeconds:0} s");
        _games.Add(Process.GetProcessById(pid));
        return pid;
    }

    // Godot as a user's script would start it: no GODOT_MCP_* variables, so the bridge can only find the folder's files. The
    // task completes once the engine has printed its banner.
    private Task StartGame()
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

        TaskCompletionSource bannered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Process game = Process.Start(startInfo)!;
        _games.Add(game);
        game.OutputDataReceived += (_, line) =>
        {
            if (line.Data?.Contains("Godot Engine v", StringComparison.Ordinal) == true)
            {
                bannered.TrySetResult();
            }
        };
        game.StandardInput.Close();
        game.BeginOutputReadLine();
        game.BeginErrorReadLine();
        return bannered.Task;
    }

    // Dormant and idle games never exit on their own, so each is killed with its tree and waited for until gone: Windows sets
    // a process's exit code before it closes the process's handles, its current directory among them.
    private async Task StopGamesAsync()
    {
        foreach (Process game in _games)
        {
            game.Kill(entireProcessTree: true);
            if (!await ProcessExit.WaitUntilGoneAsync(game, ExitWait))
            {
                throw new InvalidOperationException(
                    $"the game (pid {game.Id}) still holds its project folder {ExitWait.TotalSeconds:0} s after it was killed"
                );
            }

            game.Dispose();
        }
    }

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _runtime.RunScriptAsync(script, 10_000, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }
}
