using System.Text.Json;
using GodotMcp.IntegrationTests.Fixtures;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>The built server exe over stdio, driven by the MCP SDK's own client, as an agent's host would.</summary>
public sealed class McpServerSmokeTests : IDisposable
{
    private static readonly string[] SmokeArgs = ["--smoke"];
    private readonly ProbeProject _probe = new();

    public void Dispose() => _probe.Dispose();

    [Fact(Timeout = 45_000)]
    public async Task ListsTheThreeToolsAndRunsReadsAndStopsTheProbe()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        StdioClientTransport transport = new(
            new StdioClientTransportOptions
            {
                Name = "godot",
                Command = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "godot-mcp.exe" : "godot-mcp"),
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            }
        );
        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: cancellation);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cancellation);
        CallToolResult run = await CallAsync(client, "run_project", new() { ["projectPath"] = _probe.Directory, ["userArgs"] = SmokeArgs });
        bool sawProbe = await WaitForProbeLineAsync(client, "[probe] ready args=[\"--smoke\"]");
        CallToolResult stop = await CallAsync(client, "stop_project", []);

        Assert.Equal(["get_debug_output", "run_project", "stop_project"], tools.Select(tool => tool.Name).Order());
        Assert.True(run.IsError is not true, Text(run));
        Assert.True(sawProbe);
        Assert.True(stop.IsError is not true, Text(stop));
        Assert.False(JsonDocument.Parse(Text(stop)).RootElement.GetProperty("killed").GetBoolean());
        Assert.False(File.Exists(_probe.OverrideFile));
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
