using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Gamepad input into the running game, sent as a pad's driver sends it: joypad button and motion events, which update
/// Input's pad state (is_joy_button_pressed, get_joy_axis), press the InputMap's actions bound to the pad, and move GUI
/// focus through the default ui_up/down/left/right bindings. The bridge tracks each injected axis's value per device.
/// </summary>
internal sealed partial class RuntimeTools
{
    private const int MaxDevice = 15;
    private const string SweepDescription =
        "{durationMs, release}: sweep over durationMs instead of at once, then let go; 0 and false when left out.";
    private const string PadNote =
        " The injected pad never shows as connected: Input.get_connected_joypads, is_joy_known and get_joy_name are filled only "
        + "by the platform's pad driver, so a game that waits for a connected pad needs an OS-level virtual pad.";

    private static readonly string[] JoyButtons =
    [
        "A",
        "B",
        "X",
        "Y",
        "BACK",
        "GUIDE",
        "START",
        "LEFT_STICK",
        "RIGHT_STICK",
        "LEFT_SHOULDER",
        "RIGHT_SHOULDER",
        "DPAD_UP",
        "DPAD_DOWN",
        "DPAD_LEFT",
        "DPAD_RIGHT",
        "MISC1",
        "PADDLE1",
        "PADDLE2",
        "PADDLE3",
        "PADDLE4",
        "TOUCHPAD",
    ];

    private static readonly string[] StickAxes = ["LEFT_X", "LEFT_Y", "RIGHT_X", "RIGHT_Y"];
    private static readonly string[] TriggerAxes = ["TRIGGER_LEFT", "TRIGGER_RIGHT"];

    [McpServerTool(Name = "gamepad_button")]
    [Description(
        "Presses, releases or taps (press, a frame, release) one gamepad button in the running game, as a pad's driver "
            + "would: Input.is_joy_button_pressed and the actions bound to the button follow it, and the d-pad moves GUI focus."
            + PadNote
            + ErrorNote
    )]
    public Task<string> GamepadButtonAsync(
        [Description(
            "A Godot JoyButton without JOY_BUTTON_, any case: A, B, X, Y, BACK, GUIDE, START, LEFT_STICK, RIGHT_STICK, "
                + "LEFT_SHOULDER, RIGHT_SHOULDER, DPAD_UP, DPAD_DOWN, DPAD_LEFT, DPAD_RIGHT, MISC1, PADDLE1-4, TOUCHPAD."
        )]
            string button,
        [Description("tap, press or release.")] string action = "tap",
        [Description("The pad's device id, 0 (the first pad) to 15.")] int device = 0,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        if (action is not ("tap" or "press" or "release"))
        {
            throw new McpException($"action '{action}' is not one of tap, press, release.");
        }

        JsonObject parameters = new()
        {
            ["gesture"] = "gamepad_button",
            ["button"] = CheckJoyButton(button),
            ["action"] = action,
            ["device"] = CheckDevice(device),
        };
        return SendInputAsync(session, "gamepad_button", parameters, TimeSpan.Zero, cancellationToken);
    }

    [McpServerTool(Name = "gamepad_axis")]
    [Description(
        "Moves one gamepad axis in the running game: Input.get_joy_axis reads the raw value, and an action bound to the axis "
            + "is pressed past its deadzone with strength inverse_lerp(deadzone, 1, |value|). With durationMs the axis sweeps "
            + "from the value it holds to value, one event a frame; with release a 0.0 follows a frame after it arrives (both in options)."
            + PadNote
            + ErrorNote
    )]
    public Task<string> GamepadAxisAsync(
        [Description("A Godot JoyAxis without JOY_AXIS_, any case: LEFT_X, LEFT_Y, RIGHT_X, RIGHT_Y, TRIGGER_LEFT, TRIGGER_RIGHT.")] string axis,
        [Description("-1 to 1 for a stick axis (Y is down-positive), 0 to 1 for a trigger.")] double value,
        [Description("The pad's device id, 0 (the first pad) to 15.")] int device = 0,
        [Description(SweepDescription)] SweepOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        string name = CheckJoyAxis(axis);
        JsonArray axes = [AxisTarget(name, CheckAxisValue(name, value))];
        SweepOptions sweep = CheckSweep(options);
        return SendInputAsync(
            session,
            "gamepad_axis",
            AxesParameters(axes, device, sweep),
            TimeSpan.FromMilliseconds(sweep.DurationMs),
            cancellationToken
        );
    }

    [McpServerTool(Name = "gamepad_stick")]
    [Description(
        "Pushes a gamepad stick in the running game, sending both of its axes each frame. A push moves GUI focus once, on the "
            + "change from released to pressed, so the next move needs a release first (options.release: true, or a push to 0, 0)."
            + PadNote
            + ErrorNote
    )]
    public Task<string> GamepadStickAsync(
        [Description("left or right.")] string stick,
        [Description("The stick's position, {x, y}, each -1 to 1; y is down-positive.")] StickPosition position,
        [Description("The pad's device id, 0 (the first pad) to 15.")] int device = 0,
        [Description(SweepDescription)] SweepOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        (string xAxis, string yAxis) = stick.ToLowerInvariant() switch
        {
            "left" => ("LEFT_X", "LEFT_Y"),
            "right" => ("RIGHT_X", "RIGHT_Y"),
            _ => throw new McpException($"stick '{stick}' is not one of left, right."),
        };
        StickPosition checkedPosition = position ?? throw new McpException("position needs x and y, each -1 to 1.");
        JsonArray axes = [AxisTarget(xAxis, CheckAxisValue(xAxis, checkedPosition.X)), AxisTarget(yAxis, CheckAxisValue(yAxis, checkedPosition.Y))];
        SweepOptions sweep = CheckSweep(options);
        return SendInputAsync(
            session,
            "gamepad_stick",
            AxesParameters(axes, device, sweep),
            TimeSpan.FromMilliseconds(sweep.DurationMs),
            cancellationToken
        );
    }

    /// <exception cref="McpException">The sweep's duration is negative.</exception>
    private static SweepOptions CheckSweep(SweepOptions? options)
    {
        SweepOptions sweep = options ?? new SweepOptions();
        return sweep.DurationMs >= 0 ? sweep : throw new McpException($"durationMs must be 0 or more; got {sweep.DurationMs}.");
    }

    private static JsonObject AxesParameters(JsonArray axes, int device, SweepOptions sweep) =>
        new()
        {
            ["gesture"] = "gamepad_axes",
            ["axes"] = axes,
            ["durationMs"] = sweep.DurationMs,
            ["release"] = sweep.Release,
            ["device"] = CheckDevice(device),
        };

    private static JsonObject AxisTarget(string axis, double value) => new() { ["axis"] = axis, ["value"] = value };

    internal static string CheckJoyButton(string button)
    {
        string name = (button ?? string.Empty).ToUpperInvariant();
        return JoyButtons.Contains(name)
            ? name
            : throw new McpException($"button '{button}' is not a gamepad button; the buttons are {string.Join(", ", JoyButtons)}.");
    }

    internal static string CheckJoyAxis(string axis)
    {
        string name = (axis ?? string.Empty).ToUpperInvariant();
        return StickAxes.Contains(name) || TriggerAxes.Contains(name)
            ? name
            : throw new McpException(
                $"axis '{axis}' is not a gamepad axis; the axes are {string.Join(", ", StickAxes)}, {string.Join(", ", TriggerAxes)}."
            );
    }

    /// <summary>Refuses a value outside the axis's range: -1 to 1 for a stick axis, 0 to 1 for a trigger.</summary>
    /// <param name="axis">A name <see cref="CheckJoyAxis"/> returned.</param>
    /// <param name="value">The axis value.</param>
    internal static double CheckAxisValue(string axis, double value)
    {
        double low = TriggerAxes.Contains(axis) ? 0 : -1;
        return value >= low && value <= 1
            ? value
            : throw new McpException(
                $"{axis} takes values from {low.ToString(CultureInfo.InvariantCulture)} to 1; got {value.ToString(CultureInfo.InvariantCulture)}."
            );
    }

    internal static int CheckDevice(int device) =>
        device is >= 0 and <= MaxDevice
            ? device
            : throw new McpException($"device {device} is not a joypad id; Godot's pads are 0 (the first) to {MaxDevice}.");
}
