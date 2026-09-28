using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_project's quiet mode, the default: a window created unfocused, out of sight (at (0, 0) on the server's hidden desktop on
/// Windows, off-screen elsewhere), and the Dummy audio driver.
/// </summary>
public sealed class QuietTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;

    // How long the window watch goes on once the game answers: its window is created and shown before the bridge's _ready.
    private const int WatchAfterLaunchMs = 500;

    // Whether the window has focus, whether its rect meets any screen's, where it is, whether the server started the game on
    // its hidden desktop, and the audio driver in use (AudioServer.get_driver_name, servers/audio/audio_server.cpp L1498-1500
    // in 4.7.2).
    private const string ReadWindowAndAudio =
        "var window := Rect2i(DisplayServer.window_get_position(), DisplayServer.window_get_size())\n\t"
        + "var on_screen := false\n\t"
        + "for screen in DisplayServer.get_screen_count():\n\t\t"
        + "var area := Rect2i(DisplayServer.screen_get_position(screen), DisplayServer.screen_get_size(screen))\n\t\t"
        + "on_screen = on_screen or area.intersects(window)\n\t"
        + "return {\"focused\": DisplayServer.window_is_focused(), \"onScreen\": on_screen, \"x\": window.position.x, "
        + "\"y\": window.position.y, \"hiddenDesktop\": OS.get_environment(\""
        + GodotCommandLine.HiddenDesktopVariable
        + "\") == \"1\", "
        + "\"driver\": AudioServer.get_driver_name()}";
    private const string ReadText = "return scene_tree.root.get_node(\"Main/TextInput\").text";
    private const string ReadMaxFps = "return Engine.max_fps";
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;

    public QuietTests()
    {
        _project = new ProjectTools(_harness.Sessions);
        _runtime = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
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
        if (OperatingSystem.IsWindows())
        {
            // Out of sight on the server's hidden desktop, where the window stays at (0, 0).
            Assert.True(state["hiddenDesktop"]!.GetValue<bool>(), state.ToJsonString());
            Assert.Equal((0, 0), (state["x"]!.GetValue<int>(), state["y"]!.GetValue<int>()));
        }
        else
        {
            Assert.False(state["onScreen"]!.GetValue<bool>(), state.ToJsonString());
        }

        Assert.Equal("Dummy", state["driver"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ANotQuietRunIsOnScreenAndAudible()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(false, cancellation);

        JsonNode state = await RunAsync(ReadWindowAndAudio);

        Assert.True(state["onScreen"]!.GetValue<bool>(), state.ToJsonString());
        Assert.NotEqual("Dummy", state["driver"]!.GetValue<string>());
        if (OperatingSystem.IsWindows())
        {
            // The window check of the quiet test below can see a Godot window: a not-quiet one is on the caller's desktop.
            int game = _harness.Sessions.Resolve(null).GameProcessId!.Value;
            Assert.True(await Poll.UntilAsync(() => WindowOwners(visibleOnly: true).Contains(game), TimeSpan.FromSeconds(5), cancellation));
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AQuietRunShowsNoWindowOnTheCallersDesktop()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Desktops are a Windows feature; elsewhere a quiet window is parked off-screen.");
        // A test process already on a non-interactive desktop cannot tell the paths apart; this was proven red on an interactive one.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using CancellationTokenSource stopWatching = new();
        Task<HashSet<int>> watching = Task.Run(() => WatchShownWindows(stopWatching.Token), cancellation);

        await LaunchAsync(true, cancellation);
        await RunAsync("return 0");
        await Task.Delay(WatchAfterLaunchMs, cancellation);
        await stopWatching.CancelAsync();
        HashSet<int> shownDuringLaunch = await watching;

        GodotSession session = _harness.Sessions.Resolve(null);
        int[] godot = [session.ProcessId!.Value, session.GameProcessId!.Value];
        Assert.Empty(shownDuringLaunch.Intersect(godot));
        Assert.Empty(WindowOwners(visibleOnly: false).Intersect(godot));
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

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AQuietRunCapsItsFrameRateAt60()
    {
        await _project.RunProjectAsync(_probe.Directory, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(60, (await RunAsync(ReadMaxFps)).GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ANotQuietRunKeepsTheEngineFrameRate()
    {
        await LaunchAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(0, (await RunAsync(ReadMaxFps)).GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AQuietRunKeepsTheProjectsOwnFrameRate()
    {
        string settings = File.ReadAllText(_probe.ProjectFile);
        File.WriteAllText(_probe.ProjectFile, settings.Replace("[application]", "[application]\n\nrun/max_fps=30", StringComparison.Ordinal));
        await LaunchAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(30, (await RunAsync(ReadMaxFps)).GetValue<int>());
    }

    private Task<string> LaunchAsync(bool quiet, CancellationToken cancellation) =>
        _project.RunProjectAsync(_probe.Directory, options: new RunOptions(Quiet: quiet), cancellationToken: cancellation);

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _runtime.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }

    /// <summary>Every process that showed a visible top-level window on this thread's desktop, sampled every 10 ms until stopped.</summary>
    private static HashSet<int> WatchShownWindows(CancellationToken stop)
    {
        HashSet<int> owners = [];
        while (!stop.IsCancellationRequested)
        {
            owners.UnionWith(WindowOwners(visibleOnly: true));
            Thread.Sleep(10);
        }

        return owners;
    }

    /// <summary>The processes owning a top-level window on the calling thread's desktop (EnumWindows).</summary>
    private static HashSet<int> WindowOwners(bool visibleOnly)
    {
        HashSet<int> owners = [];
        EnumWindows(
            (window, parameter) =>
            {
                if (!visibleOnly || IsWindowVisible(window))
                {
                    _ = GetWindowThreadProcessId(window, out int processId);
                    owners.Add(processId);
                }

                return true;
            },
            0
        );
        return owners;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);
}
