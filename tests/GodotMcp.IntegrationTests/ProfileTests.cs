using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>run_project with a godot-mcp.json preset: the preset's session name and resolution reach the real game.</summary>
public sealed class ProfileTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string PresetSession = "profiled";
    private const string Profile = """{ "presets": { "small": { "session": "profiled", "resolution": "320x240" } } }""";

    // Godot keeps the engine options it reads out of OS.get_cmdline_args(), so the window size is where --resolution shows.
    private const string ReadWindowSize =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\treturn [DisplayServer.window_get_size().x, DisplayServer.window_get_size().y]\n";

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;

    public ProfileTests()
    {
        _project = new ProjectTools(_harness.Sessions);
        _runtime = new RuntimeTools(_harness.Sessions);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APresetSetsTheSessionAndTheWindowSize()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), Profile, cancellation);

        string launched = await _project.RunProjectAsync(_probe.Directory, options: new RunOptions(Preset: "small"), cancellationToken: cancellation);
        JsonNode listed = Assert.Single(JsonNode.Parse(_project.ListSessions())!["sessions"]!.AsArray())!;
        string script = await _runtime.RunScriptAsync(ReadWindowSize, ScriptTimeoutMs, PresetSession, cancellation);
        JsonNode size = JsonNode.Parse(script)!["value"]!;

        Assert.Equal(PresetSession, JsonNode.Parse(launched)!["session"]!.GetValue<string>());
        Assert.Equal(PresetSession, listed["name"]!.GetValue<string>());
        Assert.Equal([320, 240], [size[0]!.GetValue<int>(), size[1]!.GetValue<int>()]);
    }
}
