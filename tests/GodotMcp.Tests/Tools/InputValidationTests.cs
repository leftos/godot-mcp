using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>The input tools' argument checks, which refuse before anything reaches a game; no Godot runs here.</summary>
public sealed class InputValidationTests : IDisposable
{
    private static readonly InputTarget Point = new(null, 10, 10);
    private static readonly string[] Buttons = ["left", "right", "middle"];
    private static readonly string[] EventTypes =
    [
        "key",
        "mouse_button",
        "mouse_motion",
        "joypad_button",
        "joypad_motion",
        "action",
        "click_element",
        "wait",
    ];
    private static readonly StickPosition Centre = new(0, 0);
    private static readonly string[] MixedCaseButtons = ["a", "Dpad_Down", "paddle4", "TOUCHPAD", "left_shoulder"];
    private static readonly string[] MixedCaseAxes = ["left_x", "Trigger_Right"];
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly GodotSession _session;
    private readonly RuntimeTools _tools;

    public InputValidationTests()
    {
        _session = new GodotSession(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_session);
    }

    public void Dispose()
    {
        _session.Dispose();
        _listener.Dispose();
    }

    [Fact]
    public void KnownButtonsPass() => Assert.All(Buttons, button => Assert.Equal(button, RuntimeTools.CheckButton(button)));

    [Fact]
    public void AnUnknownButtonIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckButton("Left"));

        Assert.Equal("button 'Left' is not one of left, right, middle.", refused.Message);
    }

    [Fact]
    public void ModifiersAreLowerCased() =>
        Assert.Equal("[\"shift\",\"ctrl\",\"alt\",\"meta\"]", RuntimeTools.CheckModifiers(["Shift", "CTRL", "alt", "Meta"]).ToJsonString());

    [Fact]
    public void AnUnknownModifierIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckModifiers(["shift", "super"]));

        Assert.Equal("modifier 'super' is not one of shift, ctrl, alt, meta.", refused.Message);
    }

    [Fact]
    public void EveryEventTypePasses() =>
        Assert.All(
            EventTypes,
            type =>
            {
                JsonObject item = Event(type);
                Assert.Same(item, RuntimeTools.CheckEvent(item, 0));
            }
        );

    [Fact]
    public void ANullEventIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckEvent(null, 3));

        Assert.Equal("events[3] is null.", refused.Message);
    }

    [Fact]
    public void AnEventWithoutATypeIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckEvent(new JsonObject { ["x"] = 1 }, 0));

        Assert.StartsWith("events[0] has type 'null'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventOfAnUnknownTypeIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckEvent(Event("scroll"), 1));

        Assert.Equal(
            "events[1] has type 'scroll'; the types are key, mouse_button, mouse_motion, joypad_button, joypad_motion, action, "
                + "click_element, wait.",
            refused.Message
        );
    }

    [Fact]
    public void GamepadButtonNamesPassInAnyCase() =>
        Assert.Equal(["A", "DPAD_DOWN", "PADDLE4", "TOUCHPAD", "LEFT_SHOULDER"], MixedCaseButtons.Select(RuntimeTools.CheckJoyButton));

    [Fact]
    public void AnUnknownGamepadButtonIsRefusedWithTheNames()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckJoyButton("JOY_BUTTON_A"));

        Assert.StartsWith(
            "button 'JOY_BUTTON_A' is not a gamepad button; the buttons are A, B, X, Y, BACK,",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.EndsWith("PADDLE4, TOUCHPAD.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GamepadAxisNamesPassInAnyCase() => Assert.Equal(["LEFT_X", "TRIGGER_RIGHT"], MixedCaseAxes.Select(RuntimeTools.CheckJoyAxis));

    [Fact]
    public void AnUnknownGamepadAxisIsRefusedWithTheNames()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckJoyAxis("LEFT_Z"));

        Assert.Equal(
            "axis 'LEFT_Z' is not a gamepad axis; the axes are LEFT_X, LEFT_Y, RIGHT_X, RIGHT_Y, TRIGGER_LEFT, TRIGGER_RIGHT.",
            refused.Message
        );
    }

    [Fact]
    public void AStickAxisTakesMinusOneToOne()
    {
        Assert.Equal((-1.0, 1.0), (RuntimeTools.CheckAxisValue("LEFT_Y", -1), RuntimeTools.CheckAxisValue("RIGHT_X", 1)));
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckAxisValue("LEFT_X", 1.5));
        Assert.Equal("LEFT_X takes values from -1 to 1; got 1.5.", refused.Message);
        Assert.Throws<McpException>(() => RuntimeTools.CheckAxisValue("LEFT_X", -1.01));
        Assert.Throws<McpException>(() => RuntimeTools.CheckAxisValue("LEFT_X", double.NaN));
    }

    [Fact]
    public void ATriggerTakesZeroToOne()
    {
        Assert.Equal((0.0, 1.0), (RuntimeTools.CheckAxisValue("TRIGGER_LEFT", 0), RuntimeTools.CheckAxisValue("TRIGGER_RIGHT", 1)));
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckAxisValue("TRIGGER_LEFT", -0.5));
        Assert.Equal("TRIGGER_LEFT takes values from 0 to 1; got -0.5.", refused.Message);
        Assert.Throws<McpException>(() => RuntimeTools.CheckAxisValue("TRIGGER_RIGHT", 1.2));
    }

    [Fact]
    public void DevicesAreTheJoypadIds()
    {
        Assert.Equal((0, 15), (RuntimeTools.CheckDevice(0), RuntimeTools.CheckDevice(15)));
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckDevice(16));
        Assert.Equal("device 16 is not a joypad id; Godot's pads are 0 (the first) to 15.", refused.Message);
        Assert.Throws<McpException>(() => RuntimeTools.CheckDevice(-1));
    }

    [Fact]
    public async Task GamepadButtonRefusesAnUnknownAction()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.GamepadButtonAsync("A", "hold", 0, TestContext.Current.CancellationToken)
        );

        Assert.Equal("action 'hold' is not one of tap, press, release.", refused.Message);
    }

    [Fact]
    public async Task GamepadStickRefusesAnUnknownStick()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.GamepadStickAsync("middle", Centre, 0, false, 0, TestContext.Current.CancellationToken)
        );

        Assert.Equal("stick 'middle' is not one of left, right.", refused.Message);
    }

    [Fact]
    public async Task GamepadStickRefusesAPositionOffTheStick() =>
        await Assert.ThrowsAsync<McpException>(() =>
            _tools.GamepadStickAsync("left", new StickPosition(0, 2), 0, false, 0, TestContext.Current.CancellationToken)
        );

    [Fact]
    public async Task GamepadAxisRefusesANegativeDuration()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.GamepadAxisAsync("LEFT_X", 0.5, -1, false, 0, TestContext.Current.CancellationToken)
        );

        Assert.Equal("durationMs must be 0 or more; got -1.", refused.Message);
    }

    [Fact]
    public async Task AValidGamepadCallWithoutASessionSaysNoneIsRunning()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.GamepadAxisAsync("TRIGGER_LEFT", 0.5, 0, true, 3, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventWhoseTypeIsNotAStringIsRefused() =>
        Assert.Throws<McpException>(() => RuntimeTools.CheckEvent(new JsonObject { ["type"] = 5 }, 0));

    [Fact]
    public async Task SimulateInputRefusesAnEmptyList()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.SimulateInputAsync([], TestContext.Current.CancellationToken));

        Assert.StartsWith("events is empty", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyRefusesAnUnknownAction()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.KeyAsync("A", "hold", null, TestContext.Current.CancellationToken)
        );

        Assert.Equal("action 'hold' is not one of tap, press, release.", refused.Message);
    }

    [Fact]
    public async Task MouseButtonRefusesTap()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.MouseButtonAsync(Point, "left", "tap", TestContext.Current.CancellationToken)
        );

        Assert.Equal("action 'tap' is not one of press, release.", refused.Message);
    }

    [Fact]
    public async Task DragRefusesANegativeDuration()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DragAsync(Point, Point, -1, "left", TestContext.Current.CancellationToken)
        );

        Assert.Equal("durationMs must be 0 or more; got -1.", refused.Message);
    }

    [Fact]
    public async Task TypeTextRefusesEmptyText() =>
        await Assert.ThrowsAsync<McpException>(() => _tools.TypeTextAsync(string.Empty, TestContext.Current.CancellationToken));

    [Fact]
    public async Task AValidCallWithoutASessionSaysNoneIsRunning()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ClickAsync(Point, "left", false, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    private static JsonObject Event(string type) => new() { ["type"] = type };
}
