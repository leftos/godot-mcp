using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// capture_input against the InputProbe running in the real Godot: the shared quiet run for sent gestures, the quiet warning
/// and the motion filter, and runs of their own for real input (a run that is not quiet) and a capture outliving a restart.
/// </summary>
public sealed class CaptureTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 60_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string QuietWarning =
        "this session is quiet: its window gets no real input, so only the server's gestures are captured. Run with quiet:false, "
        + "or attach_project without quiet, to capture a person's input.";

    // SmallButton's presses and DropTarget's drops so far.
    private const string ReadCounts =
        "return [scene_tree.root.get_node(\"Main/SmallButton\").press_count, scene_tree.root.get_node(\"Main/DropTarget\").drop_count]";

    // A real B key pressed and released, as the display server would send it: through Input, outside the bridge's _dispatch.
    private const string RealKeyScript =
        "for pressed: bool in [true, false]:\n\t\t"
        + "var key := InputEventKey.new()\n\t\t"
        + "key.keycode = KEY_B\n\t\t"
        + "key.physical_keycode = KEY_B\n\t\t"
        + "key.pressed = pressed\n\t\t"
        + "Input.parse_input_event(key)\n\t\t"
        + "Input.flush_buffered_events()\n\t"
        + "return true";

    // ScrollBox's vertical scroll bar's value; ScrollPoint is inside it, clear of its scroll bars.
    private const string ReadScroll = "return scene_tree.root.get_node(\"Main/ScrollBox\").get_v_scroll_bar().value";

    private static readonly InputTarget ScrollPoint = new(null, 590, 190);
    private static readonly InputTarget SmallButton = new("SmallButton");
    private static readonly InputTarget DragSource = new("DragSource");
    private static readonly InputTarget DropTarget = new("DropTarget");
    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync()
    {
        await _shared.ResetAsync(TestContext.Current.CancellationToken);

        // A capture an earlier test left behind would refuse this test's start; a new start resets the bridge's own.
        _shared.Sessions.Captures.Take(_shared.Sessions.Resolve(null).Name);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SentGesturesReplayToTheSameEffect()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        (int Presses, int Drops) before = await ReadCountsAsync(_tools, cancellation);
        await StartAsync(_tools, new CaptureOptions(Sources: ["sent"]), cancellation);
        await _tools.ClickAsync(SmallButton, cancellationToken: cancellation);
        await _tools.DragAsync(DragSource, DropTarget, 300, "left", cancellationToken: cancellation);
        JsonNode stopped = await StopAsync(_tools, cancellation);
        (int Presses, int Drops) captured = await ReadCountsAsync(_tools, cancellation);

        JsonObject[] events = [.. stopped["events"]!.AsArray().Select(item => item!.DeepClone().AsObject())];
        await _tools.SimulateInputAsync(events, cancellationToken: cancellation);
        (int Presses, int Drops) replayed = await ReadCountsAsync(_tools, cancellation);

        Assert.Equal((1, 1), (captured.Presses - before.Presses, captured.Drops - before.Drops));
        Assert.Equal((1, 1), (replayed.Presses - captured.Presses, replayed.Drops - captured.Drops));
        Assert.Null(stopped["ended"]);
        Assert.False(stopped["truncated"]!.GetValue<bool>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASentScrollReplaysAsWheelEvents()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await StartAsync(_tools, new CaptureOptions(Sources: ["sent"]), cancellation);
        await _tools.ScrollAsync(ScrollPoint, "down", 2, cancellationToken: cancellation);
        JsonNode stopped = await StopAsync(_tools, cancellation);
        double captured = (await RunAsync(_tools, ReadScroll, cancellation)).GetValue<double>();

        JsonObject[] events = [.. stopped["events"]!.AsArray().Select(item => item!.DeepClone().AsObject())];
        await _tools.SimulateInputAsync(events, cancellationToken: cancellation);
        double replayed = (await RunAsync(_tools, ReadScroll, cancellation)).GetValue<double>();

        string[] wheel =
        [
            .. events
                .Where(item => item["type"]!.GetValue<string>() == "mouse_button")
                .Select(item => $"{item["button"]!.GetValue<string>()} {item["pressed"]!.GetValue<bool>()} {item["factor"]!.GetValue<double>()}"),
        ];
        Assert.Equal(["wheel_down True 1", "wheel_down False 1", "wheel_down True 1", "wheel_down False 1"], wheel);
        Assert.True(captured > 0, $"two notches down left ScrollBox at {captured}");
        Assert.True(replayed > captured, $"the replay moved ScrollBox from {captured} to {replayed}");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task RealInputIsCapturedBesideSentInput()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        await using SessionHarness harness = new();
        await harness.Sessions.LaunchAsync(new LaunchRequest(probe.Directory, null, [], [], false, false, Prepare: true), null, cancellation);
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());

        JsonNode started = await StartAsync(tools, null, cancellation);
        await RunAsync(tools, RealKeyScript, cancellation);
        await tools.KeyAsync("A", cancellationToken: cancellation);
        JsonNode stopped = await StopAsync(tools, cancellation);

        Assert.Null(started["warning"]);
        Assert.Equal(["real", "sent"], started["sources"]!.AsArray().Select(source => source!.GetValue<string>()));
        string[] keys =
        [
            .. stopped["events"]!
                .AsArray()
                .Where(item => Type(item) == "key" && item!["key"]!.GetValue<string>() is "A" or "B")
                .Select(item => $"{item!["key"]!.GetValue<string>()}:{item["pressed"]!.GetValue<bool>()}"),
        ];
        Assert.Equal(["B:True", "B:False", "A:True", "A:False"], keys);
        await harness.Sessions.StopAsync(null, cancellation);
        Assert.Equal(string.Empty, Git.Status(probe.Directory));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AQuietRunCapturesSentOnlyAndWarns()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode started = await StartAsync(_tools, null, cancellation);
        await StopAsync(_tools, cancellation);

        Assert.Equal(QuietWarning, started["warning"]?.GetValue<string>());
        Assert.Equal(["sent"], started["sources"]!.AsArray().Select(source => source!.GetValue<string>()));
        Assert.True(started["capturing"]!.GetValue<bool>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ACaptureSurvivesARestart()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        await using SessionHarness harness = new();
        await harness.Sessions.LaunchAsync(new LaunchRequest(probe.Directory, null, [], [], true, false, Prepare: true), null, cancellation);
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());

        await StartAsync(tools, new CaptureOptions(Sources: ["sent"]), cancellation);
        await tools.ClickAsync(SmallButton, cancellationToken: cancellation);
        await harness.Sessions.RestartAsync(null, false, cancellation);
        JsonNode stopped = await StopAsync(tools, cancellation);

        Assert.Equal("restart", stopped["ended"]?.GetValue<string>());
        string[] buttons =
        [
            .. stopped["events"]!
                .AsArray()
                .Where(item => Type(item) == "mouse_button")
                .Select(item => $"{item!["button"]!.GetValue<string>()}:{item["pressed"]!.GetValue<bool>()}"),
        ];
        Assert.Equal(["left:True", "left:False"], buttons);
        await harness.Sessions.StopAsync(null, cancellation);
        Assert.Equal(string.Empty, Git.Status(probe.Directory));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MotionIsOffByDefaultButDragsKeepTheirs()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await StartAsync(_tools, null, cancellation);
        await _tools.DragAsync(DragSource, DropTarget, 300, "left", cancellationToken: cancellation);
        JsonNode stopped = await StopAsync(_tools, cancellation);

        double[] masks =
        [
            .. stopped["events"]!.AsArray().Where(item => Type(item) == "mouse_motion").Select(item => item!["button_mask"]!.GetValue<double>()),
        ];
        Assert.DoesNotContain(0.0, masks);
        Assert.True(masks.Length >= 3, stopped.ToJsonString());
        Assert.All(masks, mask => Assert.Equal(1.0, mask));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task SentPadInputIsCaptured()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        await using SessionHarness harness = new();

        // Real pads are shut out, as in GamepadTests: a real pad's A would jump the probe too.
        await harness.Sessions.LaunchAsync(new LaunchRequest(probe.Directory, null, [], [], true, true, Prepare: true), null, cancellation);
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        int jumpsBefore = (await RunAsync(tools, "return scene_tree.root.get_node(\"Main/PadProbe\").jump_count", cancellation)).GetValue<int>();

        await StartAsync(tools, new CaptureOptions(Sources: ["sent"]), cancellation);
        JsonNode sent = JsonNode.Parse(await tools.GamepadButtonAsync("A", cancellationToken: cancellation))!;
        int device = (int)sent["device"]!.GetValue<double>();
        await tools.GamepadAxisAsync("LEFT_X", 0.5, cancellationToken: cancellation);
        JsonNode stopped = await StopAsync(tools, cancellation);
        (int Jumps, double LeftX) captured = await ReadPadAsync(tools, device, cancellation);
        await tools.GamepadAxisAsync("LEFT_X", 0.0, cancellationToken: cancellation);

        JsonObject[] events = [.. stopped["events"]!.AsArray().Select(item => item!.DeepClone().AsObject())];
        await tools.SimulateInputAsync(events, cancellationToken: cancellation);
        (int Jumps, double LeftX) replayed = await ReadPadAsync(tools, device, cancellation);

        string[] pad =
        [
            .. events
                .Where(item => Type(item) is "joypad_button" or "joypad_motion")
                .Select(item =>
                    Type(item) == "joypad_button"
                        ? $"{item["button"]!.GetValue<string>()}:{item["pressed"]!.GetValue<bool>()}"
                        : $"{item["axis"]!.GetValue<string>()}:{item["value"]!.GetValue<double>()}"
                ),
        ];
        Assert.Equal(["A:True", "A:False", "LEFT_X:0.5"], pad);
        Assert.Equal((1, 0.5), (captured.Jumps - jumpsBefore, captured.LeftX));
        Assert.Equal((1, 0.5), (replayed.Jumps - captured.Jumps, replayed.LeftX));
        await harness.Sessions.StopAsync(null, cancellation);
        Assert.Equal(string.Empty, Git.Status(probe.Directory));
    }

    private static async Task<(int Jumps, double LeftX)> ReadPadAsync(RuntimeTools tools, int device, CancellationToken cancellation)
    {
        JsonNode pad = await RunAsync(
            tools,
            $"return [scene_tree.root.get_node(\"Main/PadProbe\").jump_count, Input.get_joy_axis({device}, JOY_AXIS_LEFT_X)]",
            cancellation
        );
        return (pad[0]!.GetValue<int>(), pad[1]!.GetValue<double>());
    }

    private static string? Type(JsonNode? item) => item?["type"]?.GetValue<string>();

    private static async Task<JsonNode> StartAsync(RuntimeTools tools, CaptureOptions? options, CancellationToken cancellation) =>
        JsonNode.Parse(await tools.CaptureInputAsync("start", options, cancellationToken: cancellation))!;

    private static async Task<JsonNode> StopAsync(RuntimeTools tools, CancellationToken cancellation) =>
        JsonNode.Parse(await tools.CaptureInputAsync("stop", cancellationToken: cancellation))!;

    private static async Task<(int Presses, int Drops)> ReadCountsAsync(RuntimeTools tools, CancellationToken cancellation)
    {
        JsonNode counts = await RunAsync(tools, ReadCounts, cancellation);
        return (counts[0]!.GetValue<int>(), counts[1]!.GetValue<int>());
    }

    private static async Task<JsonNode> RunAsync(RuntimeTools tools, string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
        return JsonNode.Parse(json)!["value"]!;
    }
}
