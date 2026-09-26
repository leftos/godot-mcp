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

    public GamepadTests() => _tools = new RuntimeTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ButtonPressHoldsTheButtonAndItsActionUntilRelease()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(shutOutRealGamepads: true);

        await _tools.GamepadButtonAsync("A", "press", 0, cancellationToken: cancellation);
        JsonNode held = await RunAsync(ReadButtonAAndJump);
        await _tools.GamepadButtonAsync("a", "release", 0, cancellationToken: cancellation);
        JsonNode released = await RunAsync(ReadButtonAAndJump);

        Assert.Equal("[true,true]", held.ToJsonString());
        Assert.Equal("[false,false]", released.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ButtonTapPressesTheActionOnce()
    {
        await LaunchAsync(shutOutRealGamepads: true);

        await _tools.GamepadButtonAsync("A", "tap", 0, cancellationToken: TestContext.Current.CancellationToken);

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
        await LaunchAsync(shutOutRealGamepads: true);

        await _tools.GamepadAxisAsync("LEFT_X", 0.6, 0, null, null, cancellation);
        JsonNode pushed = await RunAsync(ReadLeftX);
        await _tools.GamepadAxisAsync("LEFT_X", 0.6, 0, new SweepOptions(Release: true), null, cancellation);
        JsonNode released = await RunAsync(ReadLeftX);

        // inverse_lerp(0.2, 1, 0.6) = 0.5; the axis is stored as a 32-bit float.
        Assert.Equal(0.6, pushed[0]!.GetValue<double>(), 5);
        Assert.Equal(0.5, pushed[1]!.GetValue<double>(), 5);
        Assert.Equal((0.0, 0.0), (released[0]!.GetValue<double>(), released[1]!.GetValue<double>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TriggerSweepTakesItsDurationAndEndsAtTheValue()
    {
        await LaunchAsync(shutOutRealGamepads: true);

        var sweep = Stopwatch.StartNew();
        await _tools.GamepadAxisAsync("TRIGGER_RIGHT", 1, 0, new SweepOptions(DurationMs: 300), null, TestContext.Current.CancellationToken);
        sweep.Stop();

        Assert.True(sweep.ElapsedMilliseconds >= 300, $"the sweep took {sweep.ElapsedMilliseconds} ms");
        Assert.Equal(1.0, (await RunAsync("return Input.get_joy_axis(0, JOY_AXIS_TRIGGER_RIGHT)")).GetValue<double>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DpadDownTwiceMovesFocusTwoButtonsDown()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(shutOutRealGamepads: true);
        string before = (await RunAsync(ReadFocusOwner)).GetValue<string>();

        await _tools.GamepadButtonAsync("DPAD_DOWN", "tap", 0, cancellationToken: cancellation);
        await _tools.GamepadButtonAsync("DPAD_DOWN", "tap", 0, cancellationToken: cancellation);

        Assert.Equal(("MenuA", "MenuC"), (before, (await RunAsync(ReadFocusOwner)).GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StickPushedDownAndReleasedTwiceMovesFocusTwoButtonsDown()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(shutOutRealGamepads: true);
        string before = (await RunAsync(ReadFocusOwner)).GetValue<string>();

        await _tools.GamepadStickAsync("left", Down, 0, new SweepOptions(Release: true), null, cancellation);
        await _tools.GamepadStickAsync("left", Down, 0, new SweepOptions(Release: true), null, cancellation);

        Assert.Equal(("MenuA", "MenuC"), (before, (await RunAsync(ReadFocusOwner)).GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APressOnDeviceOneIsNotOnDeviceZero()
    {
        await LaunchAsync(shutOutRealGamepads: true);

        await _tools.GamepadButtonAsync("A", "press", 1, cancellationToken: TestContext.Current.CancellationToken);

        JsonNode pads = await RunAsync("return [Input.is_joy_button_pressed(0, JOY_BUTTON_A), Input.is_joy_button_pressed(1, JOY_BUTTON_A)]");
        Assert.Equal("[false,true]", pads.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SimulateInputPlaysRawJoypadEvents()
    {
        await LaunchAsync(shutOutRealGamepads: true);
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

        await _tools.SimulateInputAsync([press, motion], cancellationToken: TestContext.Current.CancellationToken);

        JsonNode pad = await RunAsync("return [Input.is_joy_button_pressed(2, JOY_BUTTON_B), Input.get_joy_axis(2, JOY_AXIS_RIGHT_Y)]");
        Assert.True(pad[0]!.GetValue<bool>());
        Assert.Equal(-0.25, pad[1]!.GetValue<double>());
    }

    // With shutOutRealGamepads, override.cfg turns ignore_joypad_on_unfocused_application on and the bridge marks the
    // application unfocused. Turning the setting on again releases pressed input only while the application is unfocused
    // (input.cpp L1324-1328), so the injected A being cleared by it proves the mark.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task ShuttingOutRealGamepadsMarksTheGameUnfocused()
    {
        await LaunchAsync(shutOutRealGamepads: true);
        await _tools.GamepadButtonAsync("A", "press", 0, cancellationToken: TestContext.Current.CancellationToken);

        JsonNode state = await RunAsync(
            ReadShutOut
                + "\n\tvar held := Input.is_joy_button_pressed(0, JOY_BUTTON_A)\n\t"
                + "Input.set_ignore_joypad_on_unfocused_application(true)\n\t"
                + "return [shut_out, ignoring, held, Input.is_joy_button_pressed(0, JOY_BUTTON_A)]"
        );
        Assert.Equal("[true,true,true,false]", state.ToJsonString());
    }

    // The default leaves the real pads live: the fixture turns ignore_joypad_on_unfocused_application on, override.cfg
    // turns it off, and the bridge sends no focus-out.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADefaultRunSendsNoFocusOutAndTakesTheInjectedPad()
    {
        await LaunchAsync(shutOutRealGamepads: false);

        await _tools.GamepadButtonAsync("A", "tap", 0, cancellationToken: TestContext.Current.CancellationToken);

        JsonNode state = await RunAsync(ReadShutOut + "\n\treturn [shut_out, ignoring, scene_tree.root.get_node(\"Main/PadProbe\").jump_count]");
        Assert.Equal("[false,false,1]", state.ToJsonString());
    }

    // Every focus-out clears pressed pad state while ignore_joypad_on_unfocused_application is on (input.cpp L1600-1623), and
    // a focus-in marks the application focused again (scene_tree.cpp L934-942): the bridge answers each change with a
    // focus-out and sends the injected pads' held buttons and axes again. The notifications are sent by hand, since a
    // quiet window may never have had focus to lose.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task InjectedPadStateSurvivesFocusChanges()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(shutOutRealGamepads: true);

        await _tools.GamepadButtonAsync("A", "press", 0, cancellationToken: cancellation);
        await _tools.GamepadAxisAsync("LEFT_X", 0.6, 0, null, null, cancellation);
        await Task.Delay(500, cancellation);

        JsonNode state = await RunAsync(
            "scene_tree.notification(MainLoop.NOTIFICATION_APPLICATION_FOCUS_OUT)\n\t"
                + "scene_tree.notification(MainLoop.NOTIFICATION_APPLICATION_FOCUS_IN)\n\t"
                + "for frame in 3:\n\t\tawait scene_tree.process_frame\n\t"
                + "var kept := [Input.is_joy_button_pressed(0, JOY_BUTTON_A), Input.get_joy_axis(0, JOY_AXIS_LEFT_X)]\n\t"
                + "Input.set_ignore_joypad_on_unfocused_application(true)\n\t"
                + "return kept + [Input.is_joy_button_pressed(0, JOY_BUTTON_A)]"
        );
        Assert.True(state[0]!.GetValue<bool>(), state.ToJsonString());
        Assert.Equal(0.6, state[1]!.GetValue<double>(), 5);
        Assert.False(state[2]!.GetValue<bool>(), $"the application was focused again after the focus-in: {state.ToJsonString()}");
    }

    private const string ReadShutOut =
        "var shut_out: bool = scene_tree.root.get_node(\"GodotMcpBridge/Gamepad\").real_pads_shut_out\n\t"
        + "var ignoring := Input.is_ignoring_joypad_on_unfocused_application()";

    private Task<LaunchResult> LaunchAsync(bool shutOutRealGamepads) =>
        _harness.Sessions.LaunchAsync(
            new LaunchRequest(_probe.Directory, null, [], [], true, shutOutRealGamepads),
            null,
            TestContext.Current.CancellationToken
        );

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }
}
