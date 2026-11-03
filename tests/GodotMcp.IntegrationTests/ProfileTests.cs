using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>run_project with a godot-mcp.json preset: the preset's session name and resolution reach the real game.</summary>
public sealed class ProfileTests : IAsyncDisposable
{
    private const int ScriptTimeoutMs = 10_000;
    private const string PresetSession = "profiled";
    private const string Profile = """{ "presets": { "small": { "session": "profiled", "resolution": "320x240" } } }""";
    private const string EnvProfile = """{ "presets": { "withEnv": { "env": { "GMCP_TEST_VAR": "from-preset" } } } }""";
    private const string ReadTestVariable = "return OS.get_environment(\"GMCP_TEST_VAR\")";

    // Godot keeps the engine options it reads out of OS.get_cmdline_args(), so the window size is where --resolution shows.
    private const string ReadWindowSize =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\treturn [DisplayServer.window_get_size().x, DisplayServer.window_get_size().y]\n";

    // The widest and the tallest of the machine's screens, which may be two different screens.
    private const string ReadLargestScreenSize =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\tvar largest := Vector2i.ZERO\n"
        + "\tfor screen: int in DisplayServer.get_screen_count():\n"
        + "\t\tlargest = largest.max(DisplayServer.screen_get_size(screen))\n"
        + "\treturn [largest.x, largest.y]\n";

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;

    public ProfileTests()
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

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AResolutionLargerThanTheScreenIsGivenExactly()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        string launched = await _project.RunProjectAsync(
            _probe.Directory,
            engineArgs: ["--resolution", "7680x4320"],
            cancellationToken: cancellation
        );
        JsonNode screens = JsonNode.Parse(await _runtime.RunScriptAsync(ReadLargestScreenSize, ScriptTimeoutMs, null, cancellation))!["value"]!;
        JsonNode size = JsonNode.Parse(await _runtime.RunScriptAsync(ReadWindowSize, ScriptTimeoutMs, null, cancellation))!["value"]!;
        JsonNode result = JsonNode.Parse(launched)!;

        // Every screen is smaller than the size asked for on one side at least, so Windows holds the created window to it.
        Assert.True(screens[0]!.GetValue<int>() < 7680 || screens[1]!.GetValue<int>() < 4320, $"a screen here is {screens.ToJsonString()}");
        Assert.Equal([7680, 4320], [size[0]!.GetValue<int>(), size[1]!.GetValue<int>()]);
        Assert.Equal(7680, result["window"]!["width"]!.GetValue<int>());
        Assert.Equal(4320, result["window"]!["height"]!.GetValue<int>());
        Assert.Null(result["warning"]);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task APresetEnvReachesTheGame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), EnvProfile, cancellation);

        // The quiet run and the watched one take the hidden desktop and the caller's desktop paths; both give the game the variable.
        string quiet = await ReadEnvAsync(quiet: true, session: "envQuiet", cancellation);
        await _project.StopProjectAsync("envQuiet", cancellationToken: cancellation);
        string watched = await ReadEnvAsync(quiet: false, session: "envWatched", cancellation);

        Assert.Equal(("from-preset", "from-preset"), (quiet, watched));
    }

    /// <summary>Launches the probe with the preset's <c>env</c> and reads the variable back from inside the game.</summary>
    private async Task<string> ReadEnvAsync(bool quiet, string session, CancellationToken cancellation)
    {
        await _project.RunProjectAsync(
            _probe.Directory,
            options: new RunOptions(Quiet: quiet, Session: session, Preset: "withEnv"),
            cancellationToken: cancellation
        );
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{ReadTestVariable}\n";
        string json = await _runtime.RunScriptAsync(script, ScriptTimeoutMs, session, cancellation);
        return JsonNode.Parse(json)!["value"]!.GetValue<string>();
    }
}
