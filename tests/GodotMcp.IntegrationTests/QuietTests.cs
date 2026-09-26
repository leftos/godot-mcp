using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>run_project's quiet mode, the default: a window created unfocused and off-screen, and the Dummy audio driver.</summary>
public sealed class QuietTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;

    // Whether the window has focus, whether its rect meets any screen's, and the audio driver in use
    // (AudioServer.get_driver_name, servers/audio/audio_server.cpp L1498-1500 in 4.7.2).
    private const string ReadWindowAndAudio =
        "var window := Rect2i(DisplayServer.window_get_position(), DisplayServer.window_get_size())\n\t"
        + "var on_screen := false\n\t"
        + "for screen in DisplayServer.get_screen_count():\n\t\t"
        + "var area := Rect2i(DisplayServer.screen_get_position(screen), DisplayServer.screen_get_size(screen))\n\t\t"
        + "on_screen = on_screen or area.intersects(window)\n\t"
        + "return {\"focused\": DisplayServer.window_is_focused(), \"onScreen\": on_screen, \"driver\": AudioServer.get_driver_name()}";
    private const string ReadText = "return scene_tree.root.get_node(\"Main/TextInput\").text";
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;

    public QuietTests()
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
    public async Task ADefaultRunIsQuiet()
    {
        string launched = await _project.RunProjectAsync(_probe.Directory, cancellationToken: TestContext.Current.CancellationToken);

        JsonNode state = await RunAsync(ReadWindowAndAudio);

        Assert.True(JsonNode.Parse(launched)!["quiet"]!.GetValue<bool>(), launched);
        Assert.False(state["focused"]!.GetValue<bool>(), state.ToJsonString());
        Assert.False(state["onScreen"]!.GetValue<bool>(), state.ToJsonString());
        Assert.Equal("Dummy", state["driver"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ANotQuietRunIsOnScreenAndAudible()
    {
        await LaunchAsync(false, TestContext.Current.CancellationToken);

        JsonNode state = await RunAsync(ReadWindowAndAudio);

        Assert.True(state["onScreen"]!.GetValue<bool>(), state.ToJsonString());
        Assert.NotEqual("Dummy", state["driver"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task InjectedKeysReachTheGuiInAQuietRun()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(true, cancellation);
        await _runtime.ClickAsync(new InputTarget("TextInput"), "left", false, cancellationToken: cancellation);

        await _runtime.TypeTextAsync("a", cancellationToken: cancellation);
        await _runtime.KeyAsync("B", cancellationToken: cancellation);
        JsonObject rawKey = new() { ["type"] = "key", ["key"] = "C" };
        await _runtime.SimulateInputAsync([rawKey], cancellationToken: cancellation);

        // key and simulate_input type a letter key's lower case unless shift is held.
        Assert.Equal("abc", (await RunAsync(ReadText)).GetValue<string>());
    }

    private Task<string> LaunchAsync(bool quiet, CancellationToken cancellation) =>
        _project.RunProjectAsync(_probe.Directory, options: new RunOptions(Quiet: quiet), cancellationToken: cancellation);

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _runtime.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }
}
