using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The gamepad tools against the InputProbe in the real Godot: the pad state Input reports, the probe's pad-bound actions
/// (probe_jump on A, probe_right on LEFT_X+ with deadzone 0.2), and focus moving down its Menu column from MenuA.
/// </summary>
public sealed class GamepadTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string ReadButtonAAndJump = "return [Input.is_joy_button_pressed(0, JOY_BUTTON_A), Input.is_action_pressed(\"probe_jump\")]";
    private const string ReadFocusOwner = "return str(scene_tree.root.gui_get_focus_owner().name)";
    private static readonly StickPosition Down = new(0, 1);
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly RuntimeTools _tools;

    public GamepadTests() => _tools = new RuntimeTools(_harness.Session);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ButtonPressHoldsTheButtonAndItsActionUntilRelease()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchIsolatedAsync();

        await _tools.GamepadButtonAsync("A", "press", 0, cancellation);
        JsonNode held = await RunAsync(ReadButtonAAndJump);
        await _tools.GamepadButtonAsync("a", "release", 0, cancellation);
        JsonNode released = await RunAsync(ReadButtonAAndJump);

        Assert.Equal("[true,true]", held.ToJsonString());
        Assert.Equal("[false,false]", released.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ButtonTapPressesTheActionOnce()
    {
        await LaunchIsolatedAsync();

        await _tools.GamepadButtonAsync("A", "tap", 0, TestContext.Current.CancellationToken);

        JsonNode after = await RunAsync(
            "return [scene_tree.root.get_node(\"Main/PadProbe\").jump_count, Input.is_joy_button_pressed(0, JOY_BUTTON_A)]"
        );
        Assert.Equal("[1,false]", after.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AxisSetsTheRawValueAndTheActionStrengthPastTheDeadzone()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        const string ReadLeftX = "return [Input.get_joy_axis(0, JOY_AXIS_LEFT_X), Input.get_action_strength(\"probe_right\")]";
        await LaunchIsolatedAsync();

        await _tools.GamepadAxisAsync("LEFT_X", 0.6, 0, false, 0, cancellation);
        JsonNode pushed = await RunAsync(ReadLeftX);
        await _tools.GamepadAxisAsync("LEFT_X", 0.6, 0, true, 0, cancellation);
        JsonNode released = await RunAsync(ReadLeftX);

        // inverse_lerp(0.2, 1, 0.6) = 0.5; the axis is stored as a 32-bit float.
        Assert.Equal(0.6, pushed[0]!.GetValue<double>(), 5);
        Assert.Equal(0.5, pushed[1]!.GetValue<double>(), 5);
        Assert.Equal((0.0, 0.0), (released[0]!.GetValue<double>(), released[1]!.GetValue<double>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TriggerSweepTakesItsDurationAndEndsAtTheValue()
    {
        await LaunchIsolatedAsync();

        var sweep = Stopwatch.StartNew();
        await _tools.GamepadAxisAsync("TRIGGER_RIGHT", 1, 300, false, 0, TestContext.Current.CancellationToken);
        sweep.Stop();

        Assert.True(sweep.ElapsedMilliseconds >= 300, $"the sweep took {sweep.ElapsedMilliseconds} ms");
        Assert.Equal(1.0, (await RunAsync("return Input.get_joy_axis(0, JOY_AXIS_TRIGGER_RIGHT)")).GetValue<double>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DpadDownTwiceMovesFocusTwoButtonsDown()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchIsolatedAsync();
        string before = (await RunAsync(ReadFocusOwner)).GetValue<string>();

        await _tools.GamepadButtonAsync("DPAD_DOWN", "tap", 0, cancellation);
        await _tools.GamepadButtonAsync("DPAD_DOWN", "tap", 0, cancellation);

        Assert.Equal(("MenuA", "MenuC"), (before, (await RunAsync(ReadFocusOwner)).GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StickPushedDownAndReleasedTwiceMovesFocusTwoButtonsDown()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchIsolatedAsync();
        string before = (await RunAsync(ReadFocusOwner)).GetValue<string>();

        await _tools.GamepadStickAsync("left", Down, 0, true, 0, cancellation);
        await _tools.GamepadStickAsync("left", Down, 0, true, 0, cancellation);

        Assert.Equal(("MenuA", "MenuC"), (before, (await RunAsync(ReadFocusOwner)).GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APressOnDeviceOneIsNotOnDeviceZero()
    {
        await LaunchIsolatedAsync();

        await _tools.GamepadButtonAsync("A", "press", 1, TestContext.Current.CancellationToken);

        JsonNode pads = await RunAsync("return [Input.is_joy_button_pressed(0, JOY_BUTTON_A), Input.is_joy_button_pressed(1, JOY_BUTTON_A)]");
        Assert.Equal("[false,true]", pads.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SimulateInputPlaysRawJoypadEvents()
    {
        await LaunchIsolatedAsync();
        JsonObject press = new()
        {
            ["type"] = "joypad_button",
            ["button"] = "b",
            ["pressed"] = true,
            ["device"] = 2,
        };
        JsonObject motion = new()
        {
            ["type"] = "joypad_motion",
            ["axis"] = "right_y",
            ["value"] = -0.25,
            ["device"] = 2,
        };

        await _tools.SimulateInputAsync([press, motion], TestContext.Current.CancellationToken);

        JsonNode pad = await RunAsync("return [Input.is_joy_button_pressed(2, JOY_BUTTON_B), Input.get_joy_axis(2, JOY_AXIS_RIGHT_Y)]");
        Assert.True(pad[0]!.GetValue<bool>());
        Assert.Equal(-0.25, pad[1]!.GetValue<double>());
    }

    // The fixture turns ignore_joypad_on_unfocused_application on, with which losing focus clears the pad's buttons, axes
    // and actions (Godot 4.7.2 input.cpp L1600-1623); the injection override.cfg turns it off. The focus loss is the
    // application focus-out notification SceneTree handles (scene_tree.cpp L934-942), sent by hand, since a background
    // window may never have had focus to lose.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task PadStateSurvivesFocusLossInABackgroundRun()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(background: true);

        await _tools.GamepadButtonAsync("A", "press", 0, cancellation);
        await Task.Delay(500, cancellation);

        JsonNode state = await RunAsync(
            "scene_tree.notification(MainLoop.NOTIFICATION_APPLICATION_FOCUS_OUT)\n\treturn [Input.is_joy_button_pressed(0, JOY_BUTTON_A), "
                + "Input.is_action_pressed(\"probe_jump\"), Input.is_ignoring_joypad_on_unfocused_application()]"
        );
        Assert.Equal("[true,true,false]", state.ToJsonString());
    }

    private Task<LaunchResult> LaunchAsync(bool background) =>
        _harness.Session.LaunchAsync(new LaunchRequest(_probe.Directory, null, [], [], background), TestContext.Current.CancellationToken);

    // The machine's own pads feed the same all-device actions and ui_* bindings as the injected one: with four connected,
    // one resting off centre moved focus off MenuA at startup and pressed probe_right mid-test (measured 2026-09-25). With
    // ignore_joypad_on_unfocused_application on and the application focus-out notification sent, Godot drops the real pads'
    // driver input (input.cpp L1652, L1684) and clears what they pressed, while injected events, which go through
    // parse_input_event, pass unfiltered. Focus then goes back to MenuA and the jump count to 0.
    private async Task LaunchIsolatedAsync()
    {
        await LaunchAsync(background: false);
        await RunAsync(
            "Input.set_ignore_joypad_on_unfocused_application(true)\n\t"
                + "scene_tree.notification(MainLoop.NOTIFICATION_APPLICATION_FOCUS_OUT)\n\t"
                + "scene_tree.root.get_node(\"Main/Menu/MenuA\").grab_focus()\n\t"
                + "scene_tree.root.get_node(\"Main/PadProbe\").jump_count = 0\n\t"
                + "return Input.get_connected_joypads()"
        );
    }

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!;
    }
}
