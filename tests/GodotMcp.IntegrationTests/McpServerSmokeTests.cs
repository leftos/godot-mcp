using System.Diagnostics;
using System.Text.Json;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>The built server exe over stdio, driven by the MCP SDK's own client, as an agent's host would.</summary>
public sealed class McpServerSmokeTests : IDisposable
{
    private const string ChildCountScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\treturn scene_tree.root.get_child_count()\n";
    private const string PressCountScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\treturn scene_tree.root.get_node(\"Main/SmallButton\").press_count\n";
    private const string VersionPattern = @"^\d+\.\d+\.\d+(\+[0-9a-f]{7})?$";
    private static readonly string[] SmokeArgs = ["--smoke"];

    // The probe's user argument naming the file its main scene writes as it leaves the tree (tests/fixtures/InputProbe/main.gd).
    private const string ExitMarkerArg = "--exit-marker=";

    // How long a game may take to quit by itself once its server is gone: one frame to notice, then its exit work.
    private static readonly TimeSpan GameQuitWait = TimeSpan.FromSeconds(10);
    private readonly ProbeProject _probe = new();

    public void Dispose() => _probe.Dispose();

    // xUnit's timeouts are wall time: the headroom lets a run on a busy machine reach the server's own load-adjusted
    // deadlines, which end at 5 x their budget in wall time, before xUnit kills it.
    [Fact(Timeout = 180_000)]
    public async Task ListsTheToolsRunsTheProbeReadsItClicksItAndStopsIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await using McpClient client = await ConnectAsync();

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cancellation);
        CallToolResult run = await CallAsync(client, "run_project", new() { ["projectPath"] = _probe.Directory, ["userArgs"] = SmokeArgs });
        bool sawProbe = await WaitForProbeLineAsync(client, "[probe] ready args=[\"--smoke\"]");
        CallToolResult listed = await CallAsync(client, "list_sessions", []);
        CallToolResult screenshot = await CallAsync(client, "take_screenshot", new() { ["responseMode"] = "preview" });
        CallToolResult script = await CallAsync(client, "run_script", new() { ["script"] = ChildCountScript });
        Dictionary<string, object?> smallButton = new() { ["element"] = "SmallButton" };
        CallToolResult click = await CallAsync(client, "click", new() { ["target"] = smallButton });
        CallToolResult pressCount = await CallAsync(client, "run_script", new() { ["script"] = PressCountScript });
        CallToolResult stop = await CallAsync(client, "stop_project", []);
        CallToolResult listedLiveAfterStop = await CallAsync(client, "list_sessions", []);
        CallToolResult listedAfterStop = await CallAsync(client, "list_sessions", new() { ["includeStopped"] = true });

        Assert.Equal(
            [
                "add_node",
                "arm_project",
                "attach_project",
                "attach_script",
                "batch_drive",
                "batch_scene_operations",
                "call_game_tool",
                "call_method",
                "capture_frames",
                "capture_input",
                "click",
                "compare_screenshot",
                "connect_signal",
                "create_scene",
                "cs_call",
                "cs_get",
                "cs_members",
                "cs_set",
                "delete_nodes",
                "describe_class",
                "detach_project",
                "diff_snapshots",
                "disarm_project",
                "disconnect_signal",
                "drag",
                "duplicate_node",
                "export_mesh_library",
                "frame_control",
                "gamepad_axis",
                "gamepad_button",
                "gamepad_stick",
                "get_debug_output",
                "get_errors",
                "get_game_state",
                "get_node_properties",
                "get_node_signals",
                "get_scene_file_tree",
                "get_scene_tree",
                "get_ui_elements",
                "hover",
                "inspect_node",
                "key",
                "list_game_tools",
                "list_sessions",
                "load_sprite",
                "mouse_button",
                "move_node",
                "preview_scene",
                "record_mark",
                "restart_project",
                "run_csharp",
                "run_project",
                "run_scratches",
                "run_script",
                "save_scene",
                "save_screenshot",
                "save_screenshot_baseline",
                "scroll",
                "set_node_properties",
                "set_property",
                "simulate_action",
                "simulate_input",
                "snapshot_subtree",
                "stop_project",
                "stress_input",
                "take_screenshot",
                "type_text",
                "validate",
                "wait_for",
                "watch",
            ],
            tools.Select(tool => tool.Name).Order()
        );
        Assert.Equal("godot-mcp", client.ServerInfo.Name);
        Assert.Matches(VersionPattern, client.ServerInfo.Version);
        Assert.True(run.IsError is not true, Text(run));
        Assert.Equal(client.ServerInfo.Version, JsonDocument.Parse(Text(run)).RootElement.GetProperty("version").GetString());
        Assert.True(sawProbe);
        JsonElement session = Assert.Single(JsonDocument.Parse(Text(listed)).RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Equal("InputProbe", session.GetProperty("name").GetString());
        Assert.Equal("run", session.GetProperty("kind").GetString());
        Assert.True(session.GetProperty("live").GetBoolean());
        Assert.True(screenshot.IsError is not true, Text(screenshot));
        Assert.Equal("image/png", Assert.Single(screenshot.Content.OfType<ImageContentBlock>()).MimeType);
        Assert.True(script.IsError is not true, Text(script));
        Assert.Equal("""{"value":2}""", Text(script));
        Assert.True(click.IsError is not true, Text(click));
        Assert.Equal("""{"value":1}""", Text(pressCount));
        Assert.True(stop.IsError is not true, Text(stop));
        Assert.False(JsonDocument.Parse(Text(stop)).RootElement.GetProperty("killed").GetBoolean());
        Assert.False(JsonDocument.Parse(Text(stop)).RootElement.GetProperty("alreadyExited").GetBoolean());
        Assert.Empty(JsonDocument.Parse(Text(listedLiveAfterStop)).RootElement.GetProperty("sessions").EnumerateArray());
        JsonElement stopped = Assert.Single(JsonDocument.Parse(Text(listedAfterStop)).RootElement.GetProperty("sessions").EnumerateArray());
        Assert.False(stopped.GetProperty("live").GetBoolean());
        Assert.False(File.Exists(_probe.OverrideFile));
    }

    [Fact(Timeout = 120_000)]
    public async Task ToolsCarryTheirAnnotations()
    {
        await using McpClient client = await ConnectAsync();

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // (readOnly, destructive, openWorld): every tool sets all three.
        (bool?, bool?, bool?) readsTheGame = (true, false, false);
        (bool?, bool?, bool?) changesTheGame = (false, false, false);
        (bool?, bool?, bool?) destructive = (false, true, false);
        Dictionary<string, (bool?, bool?, bool?)> expected = new()
        {
            ["add_node"] = changesTheGame,
            ["arm_project"] = changesTheGame,
            ["attach_project"] = changesTheGame,
            ["attach_script"] = changesTheGame,
            ["batch_drive"] = destructive,
            ["batch_scene_operations"] = destructive,
            ["call_game_tool"] = destructive,
            ["call_method"] = destructive,
            ["capture_frames"] = readsTheGame,
            ["capture_input"] = changesTheGame,
            ["click"] = changesTheGame,
            ["compare_screenshot"] = readsTheGame,
            ["connect_signal"] = changesTheGame,
            ["create_scene"] = changesTheGame,
            ["cs_call"] = destructive,
            ["cs_get"] = destructive,
            ["cs_members"] = readsTheGame,
            ["cs_set"] = destructive,
            ["delete_nodes"] = destructive,
            ["describe_class"] = readsTheGame,
            ["detach_project"] = changesTheGame,
            ["diff_snapshots"] = destructive,
            ["disarm_project"] = changesTheGame,
            ["disconnect_signal"] = destructive,
            ["drag"] = changesTheGame,
            ["duplicate_node"] = changesTheGame,
            ["export_mesh_library"] = destructive,
            ["frame_control"] = changesTheGame,
            ["gamepad_axis"] = changesTheGame,
            ["gamepad_button"] = changesTheGame,
            ["gamepad_stick"] = changesTheGame,
            ["get_debug_output"] = readsTheGame,
            ["get_errors"] = readsTheGame,
            ["get_game_state"] = destructive,
            ["get_node_properties"] = readsTheGame,
            ["get_node_signals"] = readsTheGame,
            ["get_scene_file_tree"] = readsTheGame,
            ["get_scene_tree"] = readsTheGame,
            ["get_ui_elements"] = readsTheGame,
            ["hover"] = changesTheGame,
            ["inspect_node"] = readsTheGame,
            ["key"] = changesTheGame,
            ["list_game_tools"] = readsTheGame,
            ["list_sessions"] = readsTheGame,
            ["load_sprite"] = changesTheGame,
            ["mouse_button"] = changesTheGame,
            ["move_node"] = changesTheGame,
            ["preview_scene"] = readsTheGame,
            ["record_mark"] = changesTheGame,
            ["restart_project"] = destructive,
            ["run_csharp"] = destructive,
            ["run_project"] = changesTheGame,
            ["run_scratches"] = changesTheGame,
            ["run_script"] = destructive,
            ["save_scene"] = changesTheGame,
            ["save_screenshot"] = changesTheGame,
            ["save_screenshot_baseline"] = changesTheGame,
            ["scroll"] = changesTheGame,
            ["set_node_properties"] = changesTheGame,
            ["set_property"] = changesTheGame,
            ["simulate_action"] = changesTheGame,
            ["simulate_input"] = changesTheGame,
            ["snapshot_subtree"] = readsTheGame,
            ["stop_project"] = destructive,
            ["stress_input"] = changesTheGame,
            ["take_screenshot"] = readsTheGame,
            ["type_text"] = changesTheGame,
            ["validate"] = readsTheGame,
            ["wait_for"] = changesTheGame,
            ["watch"] = destructive,
        };
        Dictionary<string, (bool?, bool?, bool?)> actual = tools.ToDictionary(
            tool => tool.Name,
            tool =>
                (
                    tool.ProtocolTool.Annotations?.ReadOnlyHint,
                    tool.ProtocolTool.Annotations?.DestructiveHint,
                    tool.ProtocolTool.Annotations?.OpenWorldHint
                )
        );

        Assert.Equal(70, actual.Count);
        Assert.Equal(expected.OrderBy(entry => entry.Key), actual.OrderBy(entry => entry.Key));
    }

    [Fact(Timeout = 120_000)]
    public async Task AWrongKindArgumentIsRefusedByName()
    {
        await using McpClient client = await ConnectAsync();
        Dictionary<string, object?> options = new() { ["prepare"] = true };

        Dictionary<string, object?> arguments = new() { ["projectPath"] = _probe.Directory, ["options"] = options };

        CallToolResult run = await client.CallToolAsync("run_project", arguments, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.IsError, Text(run));
        Assert.Contains("options.prepare takes a string, not true.", Text(run), StringComparison.Ordinal);
    }

    [Fact(Timeout = 120_000)]
    public async Task AnUnknownOptionKeyIsRefused()
    {
        await using McpClient client = await ConnectAsync();
        Dictionary<string, object?> options = new() { ["prepar"] = "auto" };

        Dictionary<string, object?> arguments = new() { ["projectPath"] = _probe.Directory, ["options"] = options };

        CallToolResult run = await client.CallToolAsync("run_project", arguments, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.IsError, Text(run));
        Assert.Contains("options has no 'prepar'", Text(run), StringComparison.Ordinal);
    }

    [Fact(Timeout = 180_000)]
    public async Task AGameQuitsByItselfWhenItsServerIsKilled()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string marker = Path.Combine(Path.GetDirectoryName(_probe.Directory)!, "exit-marker.txt");
        using Process server = StartServer();
        List<Process> games = [];
        try
        {
            await using (
                McpClient client = await McpClient.CreateAsync(
                    new StreamClientTransport(server.StandardInput.BaseStream, server.StandardOutput.BaseStream),
                    cancellationToken: cancellation
                )
            )
            {
                Dictionary<string, object?> arguments = new() { ["projectPath"] = _probe.Directory, ["userArgs"] = new[] { ExitMarkerArg + marker } };
                CallToolResult run = await CallAsync(client, "run_project", arguments);
                Assert.NotEqual(true, run.IsError);
                CallToolResult listed = await CallAsync(client, "list_sessions", []);
                JsonElement session = Assert.Single(JsonDocument.Parse(Text(listed)).RootElement.GetProperty("sessions").EnumerateArray());
                games.Add(OpenProcess(session.GetProperty("gameProcessId").GetInt32()));
                games.Add(OpenProcess(session.GetProperty("processId").GetInt32()));

                // TerminateProcess, as install's Stop-Process -Force and a crash end it: no shutdown code of the server runs.
                server.Kill();
            }

            bool quit = await ProcessExit.WaitUntilGoneAsync(games[0], GameQuitWait);

            Assert.True(quit, $"the game (pid {games[0].Id}) was still running {GameQuitWait.TotalSeconds:0} s after its server was killed");
            Assert.True(File.Exists(marker), "the game was ended without running its exit work");
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
            }

            foreach (Process game in games)
            {
                await EndAsync(game);
            }
        }
    }

    private static string ServerPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "godot-mcp.exe" : "godot-mcp");

    private static Task<McpClient> ConnectAsync()
    {
        StdioClientTransport transport = new(
            new StdioClientTransportOptions
            {
                Name = "godot",
                Command = ServerPath,
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            }
        );
        return McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Starts the built server over redirected stdio, as a host does, with its stderr read and dropped so it never blocks.</summary>
    private static Process StartServer()
    {
        ProcessStartInfo start = new(ServerPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Process server = Process.Start(start) ?? throw new InvalidOperationException($"the server {ServerPath} did not start");
        server.ErrorDataReceived += (_, _) => { };
        server.BeginErrorReadLine();
        return server;
    }

    /// <summary>A process by id with its handle held, so its pid cannot be reused before the test ends it.</summary>
    private static Process OpenProcess(int processId)
    {
        var process = Process.GetProcessById(processId);
        _ = process.Handle;
        return process;
    }

    /// <summary>Kills a process the test opened if it still runs, with its tree, and waits for it to let go of its files.</summary>
    private static async Task EndAsync(Process process)
    {
        using (process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It has exited; the wait below still waits for its signal.
            }

            await ProcessExit.WaitUntilGoneAsync(process, GameQuitWait);
        }
    }

    private static Task<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments) =>
        client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken).AsTask();

    private static async Task<bool> WaitForProbeLineAsync(McpClient client, string line)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            CallToolResult output = await CallAsync(client, "get_debug_output", new() { ["limit"] = 50 });
            JsonElement stdout = JsonDocument.Parse(Text(output)).RootElement.GetProperty("stdout");
            if (stdout.EnumerateArray().Any(entry => entry.GetString() == line))
            {
                return true;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
