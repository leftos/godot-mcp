using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // The notification the MCP front reads and never forwards to its client (Session/FrontBusy.cs).
    private const string BusyMethod = "notifications/mcp-front/busy";

    // How long a process a case kills may take to be gone, so its files are released: a cleanup wait, never asserted.
    private static readonly TimeSpan GameQuitWait = TimeSpan.FromSeconds(10);
    private readonly ProbeProject _probe = new();

    public void Dispose() => _probe.Dispose();

    // xUnit's timeouts are wall time: the headroom lets a run on a busy machine reach the server's own load-adjusted
    // deadlines, which end at 5 x their budget in wall time, before xUnit kills it.
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
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
                "crop_screenshot",
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
                "editor",
                "export_mesh_library",
                "frame_control",
                "gamepad_axis",
                "gamepad_button",
                "gamepad_stick",
                "get_debug_output",
                "get_editor_state",
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
                "run_editor",
                "run_process",
                "run_project",
                "run_scratches",
                "run_script",
                "save_scene",
                "save_screenshot",
                "save_screenshot_baseline",
                "scratch_status",
                "scroll",
                "set_node_properties",
                "set_property",
                "simulate_action",
                "simulate_input",
                "snapshot_subtree",
                "stamp_uids",
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
        Assert.True(sawProbe, "the run printed no ready line");
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

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
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
            ["crop_screenshot"] = readsTheGame,
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
            ["editor"] = changesTheGame,
            ["export_mesh_library"] = destructive,
            ["frame_control"] = changesTheGame,
            ["gamepad_axis"] = changesTheGame,
            ["gamepad_button"] = changesTheGame,
            ["gamepad_stick"] = changesTheGame,
            ["get_debug_output"] = readsTheGame,
            ["get_editor_state"] = readsTheGame,
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
            ["run_editor"] = changesTheGame,
            ["run_process"] = changesTheGame,
            ["run_project"] = changesTheGame,
            ["run_scratches"] = changesTheGame,
            ["run_script"] = destructive,
            ["save_scene"] = changesTheGame,
            ["save_screenshot"] = changesTheGame,
            ["save_screenshot_baseline"] = changesTheGame,
            ["scratch_status"] = changesTheGame,
            ["scroll"] = changesTheGame,
            ["set_node_properties"] = changesTheGame,
            ["set_property"] = changesTheGame,
            ["simulate_action"] = changesTheGame,
            ["simulate_input"] = changesTheGame,
            ["snapshot_subtree"] = readsTheGame,
            ["stamp_uids"] = changesTheGame,
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

        Assert.Equal(77, actual.Count);
        Assert.Equal(expected.OrderBy(entry => entry.Key), actual.OrderBy(entry => entry.Key));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AWrongKindArgumentIsRefusedByName()
    {
        await using McpClient client = await ConnectAsync();
        Dictionary<string, object?> options = new() { ["prepare"] = true };

        Dictionary<string, object?> arguments = new() { ["projectPath"] = _probe.Directory, ["options"] = options };

        CallToolResult run = await client.CallToolAsync("run_project", arguments, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.IsError, Text(run));
        Assert.Contains("options.prepare takes a string, not true.", Text(run), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnUnknownOptionKeyIsRefused()
    {
        await using McpClient client = await ConnectAsync();
        Dictionary<string, object?> options = new() { ["prepar"] = "auto" };

        Dictionary<string, object?> arguments = new() { ["projectPath"] = _probe.Directory, ["options"] = options };

        CallToolResult run = await client.CallToolAsync("run_project", arguments, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.IsError, Text(run));
        Assert.Contains("options has no 'prepar'", Text(run), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheFrontIsToldBusyBeforeARunAnswersAndFreeAfterTheStop()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using Process server = StartServer(frontBusy: true);
        WireRecorder wire = new(server.StandardOutput.BaseStream);
        TaskCompletionSource freed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using McpClient client = await McpClient.CreateAsync(
                new StreamClientTransport(server.StandardInput.BaseStream, wire),
                clientOptions: ClientOptions(),
                cancellationToken: cancellation
            );
            await using IAsyncDisposable handler = client.RegisterNotificationHandler(
                BusyMethod,
                (notification, _) =>
                {
                    if (notification.Params is JsonObject parameters && parameters["busy"]?.GetValue<bool>() == false)
                    {
                        freed.TrySetResult();
                    }

                    return ValueTask.CompletedTask;
                }
            );

            CallToolResult run = await CallAsync(client, "run_project", new() { ["projectPath"] = _probe.Directory, ["userArgs"] = SmokeArgs });

            // The SDK client runs a notification's handlers and a response's completion as separate thread-pool items
            // (McpSessionHandler.cs:224-264, ModelContextProtocol 2.2.0), so a handler's effect is unordered against the
            // call's return; the order the front relies on is the wire's, read from the bytes the client has consumed.
            string? beforeRunAnswered = wire.BeforeLastResponse();
            CallToolResult stop = await CallAsync(client, "stop_project", []);

            Assert.True(run.IsError is not true, Text(run));
            Assert.Equal("busy:true", beforeRunAnswered);
            Assert.True(stop.IsError is not true, Text(stop));

            // busy:false precedes the stop's response unless the wrapper's kill wait ran out, which leaves it to
            // FrontBusy's re-check loop; so the case waits on the notification itself, the Timeout its hang guard.
            await freed.Task.WaitAsync(cancellation);
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
            }

            await ProcessExit.WaitUntilGoneAsync(server, GameQuitWait);
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task WithoutTheFrontNoBusyNotificationIsSent()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        List<string> sent = [];
        await using McpClient client = await ConnectAsync();
        await using IAsyncDisposable handler = client.RegisterNotificationHandler(
            BusyMethod,
            (_, _) =>
            {
                lock (sent)
                {
                    sent.Add(BusyMethod);
                }

                return ValueTask.CompletedTask;
            }
        );

        CallToolResult run = await CallAsync(client, "run_project", new() { ["projectPath"] = _probe.Directory, ["userArgs"] = SmokeArgs });
        CallToolResult stop = await CallAsync(client, "stop_project", []);

        cancellation.ThrowIfCancellationRequested();
        Assert.True(run.IsError is not true, Text(run));
        Assert.True(stop.IsError is not true, Text(stop));
        Assert.Empty(Snapshot(sent));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
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
                    clientOptions: ClientOptions(),
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

            using var hangGuard = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            hangGuard.CancelAfter(TestTimeouts.ServerHandshakeMs);
            bool quit;
            try
            {
                await games[0].WaitForExitAsync(hangGuard.Token);
                quit = true;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                quit = false;
            }

            Assert.True(quit, $"the game (pid {games[0].Id}) never exited after its server was killed");
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

    /// <summary>
    /// The client's bounds are hang guards a passing run never reaches: an initialization timeout a starved server can still
    /// pass, and no discovery probe timeout, so load never changes which protocol a case tests.
    /// </summary>
    private static McpClientOptions ClientOptions() =>
        new() { InitializationTimeout = TimeSpan.FromMilliseconds(TestTimeouts.ServerHandshakeMs), DiscoverProbeTimeout = Timeout.InfiniteTimeSpan };

    private static Task<McpClient> ConnectAsync()
    {
        StdioClientTransport transport = new(
            new StdioClientTransportOptions
            {
                Name = "godot",
                Command = ServerPath,
                ShutdownTimeout = TimeSpan.FromSeconds(10),

                // An empty value, not an unset one: the front is off here even when the developer's own shell has it on.
                EnvironmentVariables = new Dictionary<string, string?> { ["MCP_FRONT"] = "" },
            }
        );
        return McpClient.CreateAsync(transport, clientOptions: ClientOptions(), cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Starts the built server over redirected stdio, as a host does, with its stderr read and dropped so it never blocks.</summary>
    /// <param name="frontBusy">Whether a front is told about the server, which turns its busy notifications on.</param>
    private static Process StartServer(bool frontBusy = false)
    {
        ProcessStartInfo start = new(ServerPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // An empty value, not an unset one: the front is off unless the case asks for it.
        start.Environment["MCP_FRONT"] = frontBusy ? "1" : "";
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

    /// <summary>
    /// Waits for <paramref name="line"/> in the run's stdout, on the line itself, with the hang guard as its deadline: a
    /// passing run never reaches it, and a dead server ends the wait at once with the transport's error.
    /// </summary>
    private static async Task<bool> WaitForProbeLineAsync(McpClient client, string line)
    {
        using var hangGuard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        hangGuard.CancelAfter(TestTimeouts.ServerHandshakeMs);
        try
        {
            while (true)
            {
                Dictionary<string, object?> arguments = new() { ["limit"] = 50 };
                CallToolResult output = await client.CallToolAsync("get_debug_output", arguments, cancellationToken: hangGuard.Token);
                JsonElement stdout = JsonDocument.Parse(Text(output)).RootElement.GetProperty("stdout");
                if (stdout.EnumerateArray().Any(entry => entry.GetString() == line))
                {
                    return true;
                }

                await Task.Delay(200, hangGuard.Token);
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static List<string> Snapshot(List<string> events)
    {
        lock (events)
        {
            return [.. events];
        }
    }

    /// <summary>The server's stdout as the client reads it, every byte kept, so a case reads the wire's own order.</summary>
    private sealed class WireRecorder(Stream inner) : Stream
    {
        private readonly List<byte> _read = [];

        /// <summary>
        /// The message just before the last response read so far: busy:true or busy:false for the front's notification,
        /// "response" for another response.
        /// </summary>
        /// <returns>The label, or null before any response has been read.</returns>
        public string? BeforeLastResponse()
        {
            string text;
            lock (_read)
            {
                text = Encoding.UTF8.GetString([.. _read]);
            }

            List<string> labels = [];
            foreach (string line in WholeLines(text))
            {
                if (Label(line) is { } label)
                {
                    labels.Add(label);
                }
            }

            int last = labels.LastIndexOf("response");
            return last > 0 ? labels[last - 1] : null;
        }

        // The lines read so far that are complete: the last one may still be growing, so it is dropped.
        private static string[] WholeLines(string text) => text[..(text.LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // What a line says: busy:true or busy:false for the front's notification, "response" for a response, null for a request.
        private static string? Label(string line)
        {
            JsonObject message = JsonNode.Parse(line)!.AsObject();
            if (message["method"]?.GetValue<string>() == BusyMethod)
            {
                return message["params"]?["busy"]?.GetValue<bool>() == true ? "busy:true" : "busy:false";
            }

            return message["id"] is not null && message["method"] is null ? "response" : null;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Keep(buffer.Span[..read]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Keep(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Keep(ReadOnlySpan<byte> bytes)
        {
            lock (_read)
            {
                _read.AddRange(bytes);
            }

            return bytes.Length;
        }
    }
}
