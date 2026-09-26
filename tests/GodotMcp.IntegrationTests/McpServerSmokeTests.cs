using System.Text.Json;
using GodotMcp.IntegrationTests.Fixtures;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>The built server exe over stdio, driven by the MCP SDK's own client, as an agent's host would.</summary>
public sealed class McpServerSmokeTests : IDisposable
{
    private const string ChildCountScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\treturn scene_tree.root.get_child_count()\n";
    private const string PressCountScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\treturn scene_tree.root.get_node(\"Main/SmallButton\").press_count\n";
    private static readonly string[] SmokeArgs = ["--smoke"];
    private readonly ProbeProject _probe = new();

    public void Dispose() => _probe.Dispose();

    [Fact(Timeout = 45_000)]
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
        CallToolResult listedAfterStop = await CallAsync(client, "list_sessions", []);

        Assert.Equal(
            [
                "attach_project",
                "click",
                "detach_project",
                "drag",
                "gamepad_axis",
                "gamepad_button",
                "gamepad_stick",
                "get_debug_output",
                "get_errors",
                "get_ui_elements",
                "key",
                "list_sessions",
                "mouse_button",
                "run_project",
                "run_script",
                "simulate_input",
                "stop_project",
                "take_screenshot",
                "type_text",
            ],
            tools.Select(tool => tool.Name).Order()
        );
        Assert.True(run.IsError is not true, Text(run));
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
        JsonElement stopped = Assert.Single(JsonDocument.Parse(Text(listedAfterStop)).RootElement.GetProperty("sessions").EnumerateArray());
        Assert.False(stopped.GetProperty("live").GetBoolean());
        Assert.False(File.Exists(_probe.OverrideFile));
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolsCarryTheirAnnotations()
    {
        await using McpClient client = await ConnectAsync();

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // (readOnly, destructive, openWorld) as each tool sets them; null is a hint left unset.
        (bool?, bool?, bool?) readOnly = (true, null, false);
        (bool?, bool?, bool?) changesTheGame = (null, false, false);
        (bool?, bool?, bool?) destructive = (null, true, false);
        Dictionary<string, (bool?, bool?, bool?)> expected = new()
        {
            ["attach_project"] = changesTheGame,
            ["click"] = changesTheGame,
            ["detach_project"] = changesTheGame,
            ["drag"] = changesTheGame,
            ["gamepad_axis"] = changesTheGame,
            ["gamepad_button"] = changesTheGame,
            ["gamepad_stick"] = changesTheGame,
            ["get_debug_output"] = readOnly,
            ["get_errors"] = readOnly,
            ["get_ui_elements"] = readOnly,
            ["key"] = changesTheGame,
            ["list_sessions"] = readOnly,
            ["mouse_button"] = changesTheGame,
            ["run_project"] = changesTheGame,
            ["run_script"] = destructive,
            ["simulate_input"] = changesTheGame,
            ["stop_project"] = destructive,
            ["take_screenshot"] = readOnly,
            ["type_text"] = changesTheGame,
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

        Assert.Equal(19, actual.Count);
        Assert.Equal(expected.OrderBy(entry => entry.Key), actual.OrderBy(entry => entry.Key));
    }

    private static Task<McpClient> ConnectAsync()
    {
        StdioClientTransport transport = new(
            new StdioClientTransportOptions
            {
                Name = "godot",
                Command = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "godot-mcp.exe" : "godot-mcp"),
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            }
        );
        return McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
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
