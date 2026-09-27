using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The gamepad tools against the InputProbe in the real Godot: the pad state Input reports, the probe's pad-bound actions
/// (probe_jump on A, probe_right on LEFT_X+ with deadzone 0.2), and focus moving down its Menu column from MenuA. One shared
/// run with the real pads shut out, reset before each test, except for the test of a default run, which launches its own.
/// </summary>
public sealed class GamepadTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string ReadButtonAAndJump = "return [Input.is_joy_button_pressed(0, JOY_BUTTON_A), Input.is_action_pressed(\"probe_jump\")]";
    private const string ReadFocusOwner = "return str(scene_tree.root.gui_get_focus_owner().name)";
    private static readonly StickPosition Down = new(0, 1);
    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ButtonPressHoldsTheButtonAndItsActionUntilRelease()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

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
        string before = (await RunAsync(ReadFocusOwner)).GetValue<string>();

        await _tools.GamepadButtonAsync("DPAD_DOWN", "tap", 0, cancellationToken: cancellation);
        await _tools.GamepadButtonAsync("DPAD_DOWN", "tap", 0, cancellationToken: cancellation);

        Assert.Equal(("MenuA", "MenuC"), (before, (await RunAsync(ReadFocusOwner)).GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StickPushedDownAndReleasedTwiceMovesFocusTwoButtonsDown()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string before = (await RunAsync(ReadFocusOwner)).GetValue<string>();

        await _tools.GamepadStickAsync("left", Down, 0, new SweepOptions(Release: true), null, cancellation);
        await _tools.GamepadStickAsync("left", Down, 0, new SweepOptions(Release: true), null, cancellation);

        Assert.Equal(("MenuA", "MenuC"), (before, (await RunAsync(ReadFocusOwner)).GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APressOnDeviceOneIsNotOnDeviceZero()
    {
        await _tools.GamepadButtonAsync("A", "press", 1, cancellationToken: TestContext.Current.CancellationToken);

        JsonNode pads = await RunAsync("return [Input.is_joy_button_pressed(0, JOY_BUTTON_A), Input.is_joy_button_pressed(1, JOY_BUTTON_A)]");
        Assert.Equal("[false,true]", pads.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SimulateInputPlaysRawJoypadEvents()
    {
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
        // The shared run shuts the real pads out, so the default run is a run of its own.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        await using SessionHarness harness = new();
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        await harness.Sessions.LaunchAsync(new LaunchRequest(probe.Directory, null, [], [], true, false, Prepare: true), null, cancellation);

        await tools.GamepadButtonAsync("A", "tap", 0, cancellationToken: cancellation);

        JsonNode state = await RunAsync(
            tools,
            ReadShutOut + "\n\treturn [shut_out, ignoring, scene_tree.root.get_node(\"Main/PadProbe\").jump_count]"
        );
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

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADefaultedPadCallInjectsOnTheLowestIdNoRealPadHolds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        int[] connected = await ConnectedPadsAsync();
        int expected = Enumerable.Range(0, 16).First(id => !connected.Contains(id));

        JsonNode pressed = JsonNode.Parse(await _tools.GamepadButtonAsync("A", "press", cancellationToken: cancellation))!;
        bool held = (await RunAsync($"return Input.is_joy_button_pressed({expected}, JOY_BUTTON_A)")).GetValue<bool>();
        await _tools.GamepadButtonAsync("A", "release", cancellationToken: cancellation);

        Assert.Equal(expected, Device(pressed));
        Assert.True(held, $"A is not held on device {expected}; the real pads hold [{string.Join(", ", connected)}]");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TheDefaultedIdIsKeptAcrossCalls()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonNode first = JsonNode.Parse(await _tools.GamepadButtonAsync("B", "tap", cancellationToken: cancellation))!;
        JsonNode second = JsonNode.Parse(await _tools.GamepadAxisAsync("LEFT_Y", 0.5, cancellationToken: cancellation))!;

        Assert.Equal(Device(first), Device(second));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARawJoypadEventWithoutDeviceUsesTheChosenId()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject motion = new()
        {
            ["type"] = "joypad_motion",
            ["axis"] = "right_x",
            ["value"] = 0.75,
        };

        JsonNode axis = JsonNode.Parse(await _tools.GamepadAxisAsync("LEFT_X", 0.0, cancellationToken: cancellation))!;
        JsonNode raw = JsonNode.Parse(await _tools.SimulateInputAsync([motion], cancellationToken: cancellation))!;
        int device = Device(raw);
        double moved = (await RunAsync($"return Input.get_joy_axis({device}, JOY_AXIS_RIGHT_X)")).GetValue<double>();

        Assert.Equal(Device(axis), device);
        Assert.Equal(0.75, moved, 5);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnExplicitRealPadIdWarns()
    {
        int[] connected = await ConnectedPadsAsync();
        Assert.SkipWhen(connected.Length == 0, "no real pad is connected");
        int id = connected[0];

        JsonNode tapped = JsonNode.Parse(await _tools.GamepadButtonAsync("A", "tap", id, cancellationToken: TestContext.Current.CancellationToken))!;

        string warning = tapped["warning"]?.GetValue<string>() ?? string.Empty;
        Assert.StartsWith($"Device {id} is a connected real pad (", warning, StringComparison.Ordinal);
        Assert.EndsWith("its own input mixes with what is injected there.", warning, StringComparison.Ordinal);
    }

    private const string ReadShutOut =
        "var shut_out: bool = scene_tree.root.get_node(\"GodotMcpBridge/Gamepad\").real_pads_shut_out\n\t"
        + "var ignoring := Input.is_ignoring_joypad_on_unfocused_application()";

    private Task<JsonNode> RunAsync(string body) => RunAsync(_tools, body);

    /// <summary>The ids of the real pads the platform's driver connected, as the game sees them.</summary>
    private async Task<int[]> ConnectedPadsAsync() =>
        [.. (await RunAsync("return Input.get_connected_joypads()")).AsArray().Select(id => (int)id!.GetValue<double>())];

    /// <summary>The device id a pad gesture's result reports.</summary>
    private static int Device(JsonNode result)
    {
        JsonNode? device = result["device"];
        Assert.True(device is JsonValue, $"no device in {result.ToJsonString()}");
        return (int)device!.GetValue<double>();
    }

    private static async Task<JsonNode> RunAsync(RuntimeTools tools, string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }
}
