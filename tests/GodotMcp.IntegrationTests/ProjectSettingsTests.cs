using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_project's options.projectSettings: the values are written into the bridge's override.cfg, reach the game as project
/// settings, survive a restart, and leave with the file when the session stops, project.godot untouched.
/// </summary>
public sealed class ProjectSettingsTests : IAsyncDisposable
{
    private const int ScriptTimeoutMs = 10_000;

    /// <summary>The setting the run writes, and the engine frame rate Godot reads from the same block.</summary>
    private const string ReadSettings = "return [ProjectSettings.get_setting(\"gmcp/test/value\"), Engine.max_fps]";

    /// <summary>The run's block as a refusal summarises it: the sections in ordinal order, then their keys.</summary>
    private const string Summarised = "application/run/max_fps=30, gmcp/test/value=\"Seen\"";

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;

    public ProjectSettingsTests()
    {
        _project = new ProjectTools(_harness.Sessions);
        _runtime = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARunsProjectSettingsReachTheGameAndLeaveWithItsOverride()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        byte[] project = File.ReadAllBytes(_probe.ProjectFile);

        await _project.RunProjectAsync(
            _probe.Directory,
            options: new RunOptions(
                ProjectSettings: new()
                {
                    ["gmcp/test/value"] = JsonSerializer.SerializeToElement("Seen"),
                    ["application/run/max_fps"] = JsonSerializer.SerializeToElement(30),
                }
            ),
            cancellationToken: cancellation
        );

        JsonNode first = await RunAsync(ReadSettings);
        McpException armed = await Assert.ThrowsAsync<McpException>(() =>
            _project.ArmProjectAsync(_probe.Directory, new ArmOptions(Quiet: true), cancellation)
        );
        await _project.RestartProjectAsync(session: null, cancellationToken: cancellation);
        JsonNode restarted = await RunAsync(ReadSettings);
        string written = File.ReadAllText(_probe.OverrideFile);

        await _project.StopProjectAsync(session: null, cancellationToken: cancellation);

        Assert.Equal("Seen", first[0]!.GetValue<string>());
        Assert.Equal(30, first[1]!.GetValue<int>());
        Assert.Equal(
            $"Sessions on {ProjectPaths.Normalise(_probe.Directory)} run with projectSettings={Summarised}; arm_project takes no "
                + "projectSettings, so stop them first.",
            armed.Message
        );
        Assert.Equal("Seen", restarted[0]!.GetValue<string>());
        Assert.Equal(30, restarted[1]!.GetValue<int>());
        Assert.Contains("; godot-mcp project settings", written, StringComparison.Ordinal);
        Assert.Contains("value=\"Seen\"", written, StringComparison.Ordinal);
        Assert.False(File.Exists(_probe.OverrideFile));
        Assert.Equal(project, File.ReadAllBytes(_probe.ProjectFile));
    }

    /// <summary>Runs a script in the game and returns the value it returns.</summary>
    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _runtime.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }
}
