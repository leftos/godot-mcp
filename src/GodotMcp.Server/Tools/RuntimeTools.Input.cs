using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Input into the running game. The bridge plays each gesture over frames with new event objects, tracking the held
/// buttons and the pointer itself, and answers once the gesture has ended and two more frames have run. One input call
/// plays at a time on a session (sessions play alongside each other), and the errors the game raised while it played are in
/// its result's errors.
/// </summary>
internal sealed partial class RuntimeTools
{
    private const string ErrorNote =
        " Errors the game's handlers raise while the input plays come back in the result's errors, with file, line and stack; "
        + "the call still succeeds.";
    private const string ElementAim =
        " An element target aims at a Control's centre, or at a 2D or 3D world node's origin (plus offset) through its camera, "
        + "refused off-screen, behind the camera, or where a Control would take the press before physics picking";
    private const string TargetNote =
        ElementAim
        + "; it adds aimedAt {x, y, kind (control, node2d or node3d), path, class, viewport?}: the point in viewport "
        + "coordinates, and the node's viewport when it is not the root.";
    private const string DragTargetNote =
        ElementAim + "; an element at either end adds aimedAt {from, to}, each as click's aimedAt, a point end null.";
    private const int MaxHoverTimeoutMs = 10_000;
    private const int MaxScrollNotches = 100;
    private const double MaxScrollFactor = 10;
    private static readonly TimeSpan InputTimeout = TimeSpan.FromSeconds(10);

    // A generous allowance per character or event on top of InputTimeout: each takes a frame or two.
    private static readonly TimeSpan PerStepAllowance = TimeSpan.FromMilliseconds(100);
    private static readonly string[] Buttons = ["left", "right", "middle"];
    private static readonly string[] Modifiers = ["shift", "ctrl", "alt", "meta"];
    private static readonly string[] ScrollDirections = ["up", "down", "left", "right"];
    private static readonly string[] ScrollVias = ["wheel", "pan"];
    private static readonly string[] EventTypes =
    [
        "key",
        "mouse_button",
        "mouse_motion",
        "pan_gesture",
        "joypad_button",
        "joypad_motion",
        "action",
        "click_element",
        "wait",
    ];

    [McpServerTool(Name = "click", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Clicks in the running game: moves the pointer to the target, presses, and releases a frame later. Points are "
            + "viewport coordinates, as get_ui_elements reports them; the bridge maps them to the window, stretched or letterboxed. "
            + "Returns {pointer, heldButtonMask, pressedOn, releasedOn}: the Controls ({path, class}) under the press and the "
            + "release, a popup's included, null over none; with doubleClick, the second click's."
            + TargetNote
            + ErrorNote
    )]
    public Task<string> ClickAsync(
        [Description("Where to click.")] InputTarget target,
        [Description("left, right or middle.")] string button = "left",
        [Description("Follow the click with a second press marked double_click.")] bool doubleClick = false,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = new()
        {
            ["gesture"] = "click",
            ["target"] = InputTarget.ToBridge(target, "target"),
            ["button"] = CheckButton(button),
            ["doubleClick"] = doubleClick,
        };
        return SendInputAsync(session, "click", parameters, TimeSpan.Zero, cancellationToken);
    }

    [McpServerTool(Name = "drag", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Drags in the running game: presses at from, moves one step a frame in a straight line to to over durationMs (at "
            + "least 3 frames), each motion carrying the held button and its step, and releases at to. Godot starts a drag "
            + "once the path passes gui/common/drag_threshold (10 px by default); a shorter one is a click. While a gesture "
            + "plays, or injected input holds a button, the real mouse's buttons and motions are kept from the game's GUI. "
            + "Returns {pointer, heldButtonMask, pressedOn, releasedOn, guiDragStarted, dropAccepted}: the Controls ({path, "
            + "class}) under the press and under the release point, null over none; whether Godot's GUI started a drag; and "
            + "whether a Control accepted its drop."
            + DragTargetNote
            + ErrorNote
    )]
    public Task<string> DragAsync(
        [Description("Where the drag starts.")] InputTarget from,
        [Description("Where the drag ends and the button is released.")] InputTarget to,
        [Description(
            "How long the moving part takes, in milliseconds; in a recording (run_project options.record), clip time: 60 movie "
                + "frames a second, however slowly the game runs."
        )]
            int durationMs = 300,
        [Description("left, right or middle.")] string button = "left",
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        if (durationMs < 0)
        {
            throw new McpException($"durationMs must be 0 or more; got {durationMs}.");
        }

        JsonObject parameters = new()
        {
            ["gesture"] = "drag",
            ["from"] = InputTarget.ToBridge(from, "from"),
            ["to"] = InputTarget.ToBridge(to, "to"),
            ["durationMs"] = durationMs,
            ["button"] = CheckButton(button),
        };
        return SendInputAsync(session, "drag", parameters, TimeSpan.FromMilliseconds(durationMs), cancellationToken);
    }

    [McpServerTool(Name = "type_text", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Types text into the focused Control (click a LineEdit first): per character a key press and release with its "
            + "keycode on a US layout, its unicode, and shift where the character needs it, one frame apart. \\n is Enter, \\t Tab."
            + ErrorNote
    )]
    public Task<string> TypeTextAsync(
        [Description("The text, case and symbols kept.")] string text,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new McpException("text is empty; pass the characters to type.");
        }

        JsonObject parameters = new() { ["gesture"] = "type_text", ["text"] = text };
        return SendInputAsync(session, "type_text", parameters, PerStepAllowance * text.Length, cancellationToken);
    }

    [McpServerTool(Name = "key", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Presses, releases or taps (press, a frame, release) one key in the running game. A printable key also carries "
            + "the character it types unless ctrl, alt or meta is held."
            + ErrorNote
    )]
    public Task<string> KeyAsync(
        [Description("A Godot Key constant without KEY_: Enter, Escape, Space, A, 1, F1, Up, Shift, Ctrl.")] string key,
        [Description("tap, press or release.")] string action = "tap",
        [Description("Modifiers held with the key: any of shift, ctrl, alt, meta.")] string[]? modifiers = null,
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
            ["gesture"] = "key",
            ["key"] = key,
            ["action"] = action,
            ["modifiers"] = CheckModifiers(modifiers ?? []),
        };
        return SendInputAsync(session, "key", parameters, TimeSpan.Zero, cancellationToken);
    }

    [McpServerTool(Name = "mouse_button", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Holds or releases a mouse button at a target: moves the pointer there (carrying any buttons already held), then "
            + "presses or releases. The held buttons stay in later motions' button_mask, so press, simulate_input motions "
            + "and release make a drag by hand. While a button is held, the real mouse's buttons and motions are kept from the "
            + "game's GUI. move only moves the pointer, pressing and releasing nothing (button is ignored). Returns {pointer, "
            + "heldButtonMask} and pressedOn (a press), releasedOn (a release) or hoveredOn (a move): the Control ({path, "
            + "class}) under the point, null over none. A press or release into a SubViewport whose gui_disable_input is on is "
            + "refused; a move is not."
            + TargetNote
            + ErrorNote
    )]
    public Task<string> MouseButtonAsync(
        [Description("Where to press, release or move to.")] InputTarget target,
        [Description("left, right or middle.")] string button = "left",
        [Description("press, release or move.")] string action = "press",
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        if (action is not ("press" or "release" or "move"))
        {
            throw new McpException($"action '{action}' is not one of press, release, move.");
        }

        JsonObject parameters = new()
        {
            ["gesture"] = "mouse_button",
            ["target"] = InputTarget.ToBridge(target, "target"),
            ["button"] = CheckButton(button),
            ["action"] = action,
        };
        return SendInputAsync(session, "mouse_button", parameters, TimeSpan.Zero, cancellationToken);
    }

    [McpServerTool(Name = "hover", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "The hover gesture: moves the pointer to the target without pressing (carrying any buttons already held), then, when "
            + "Godot has a tooltip to show for the pointer, waits for it to show, up to options.timeoutMs "
            + "(gui/timers/tooltip_delay_sec plus 1 s when left out). That tooltip is the hovered Control's, or else the "
            + "nearest ancestor's, climbing no further than a Control whose mouse filter is Stop or which is top-level. "
            + "Points are viewport coordinates, as get_ui_elements reports them. Returns {pointer, heldButtonMask, hoveredOn, "
            + "tooltip, warning?}: hoveredOn the Control ({path, class}) under the pointer, null over none; tooltip {text, x, "
            + "y, width, height, owner} in viewport coordinates (text null for a custom tooltip without a Label; owner the "
            + "Control ({path, class}) whose tooltip it is), null when none showed, with a warning when one was due. Godot "
            + "starts a tooltip's timer only while the hovered Control can process, so over a pausable Control in a paused "
            + "game hover answers at once with a warning: resume, hover, then pause."
            + TargetNote
            + ErrorNote
    )]
    public Task<string> HoverAsync(
        [Description("Where to hover.")] InputTarget target,
        [Description("{tooltip, timeoutMs}: tooltip false answers right after the move; timeoutMs 0 to 10000.")] HoverOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        HoverOptions checkedOptions = options ?? new HoverOptions();
        int? timeoutMs = CheckHoverTimeout(checkedOptions.TimeoutMs);
        JsonObject parameters = new()
        {
            ["gesture"] = "hover",
            ["target"] = InputTarget.ToBridge(target, "target"),
            ["tooltip"] = checkedOptions.Tooltip,
        };
        if (timeoutMs is not null)
        {
            parameters["timeoutMs"] = timeoutMs;
        }

        return SendInputAsync(session, "hover", parameters, TimeSpan.FromMilliseconds(timeoutMs ?? MaxHoverTimeoutMs), cancellationToken);
    }

    /// <exception cref="McpException">timeoutMs is outside 0 to <see cref="MaxHoverTimeoutMs"/>.</exception>
    internal static int? CheckHoverTimeout(int? timeoutMs) =>
        timeoutMs is null or (>= 0 and <= MaxHoverTimeoutMs)
            ? timeoutMs
            : throw new McpException($"timeoutMs must be 0 to {MaxHoverTimeoutMs}; got {timeoutMs}.");

    [McpServerTool(Name = "scroll", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Scrolls in the running game as a mouse wheel or a trackpad does: closes a showing tooltip, moves the pointer to the "
            + "target (carrying any buttons already held), then plays notches one frame apart. A wheel notch is a press and a "
            + "release of the wheel button in the same frame, carrying factor, as Godot's Windows display server sends one; the "
            + "wheel never joins heldButtonMask. With options.via pan each notch is one InputEventPanGesture instead, its delta "
            + "the direction times factor (down and right positive). A ScrollContainer moves an eighth of its page per notch or "
            + "per delta of 1. Points are viewport coordinates, as get_ui_elements reports them. Returns {pointer, "
            + "heldButtonMask, scrolledOn}: the Control ({path, class}) under the point, null over none."
            + TargetNote
            + ErrorNote
    )]
    public Task<string> ScrollAsync(
        [Description("Where to scroll: the pointer moves there first.")] InputTarget target,
        [Description("up, down, left or right.")] string direction = "down",
        [Description("How many notches, 1 to 100, one frame apart.")] int notches = 1,
        [Description("{factor, via}: factor more than 0 and at most 10 (1 when left out); via wheel (the default) or pan.")]
            ScrollOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        ScrollOptions checkedOptions = options ?? new ScrollOptions();
        JsonObject parameters = new()
        {
            ["gesture"] = "scroll",
            ["target"] = InputTarget.ToBridge(target, "target"),
            ["direction"] = CheckScrollDirection(direction),
            ["notches"] = CheckScrollNotches(notches),
            ["factor"] = CheckScrollFactor(checkedOptions.Factor),
            ["via"] = CheckScrollVia(checkedOptions.Via),
        };
        return SendInputAsync(session, "scroll", parameters, PerStepAllowance * notches, cancellationToken);
    }

    /// <exception cref="McpException">direction is not up, down, left or right.</exception>
    internal static string CheckScrollDirection(string direction) =>
        ScrollDirections.Contains(direction)
            ? direction
            : throw new McpException($"direction '{direction}' is not one of {string.Join(", ", ScrollDirections)}.");

    /// <exception cref="McpException">notches is outside 1 to <see cref="MaxScrollNotches"/>.</exception>
    internal static int CheckScrollNotches(int notches) =>
        notches is >= 1 and <= MaxScrollNotches ? notches : throw new McpException($"notches must be 1 to {MaxScrollNotches}; got {notches}.");

    /// <exception cref="McpException">factor is not more than 0 and at most <see cref="MaxScrollFactor"/> (NaN included).</exception>
    internal static double CheckScrollFactor(double factor) =>
        factor is > 0 and <= MaxScrollFactor
            ? factor
            : throw new McpException(
                $"factor must be more than 0 and at most {MaxScrollFactor}; got {factor.ToString(CultureInfo.InvariantCulture)}."
            );

    /// <exception cref="McpException">via is not wheel or pan.</exception>
    internal static string CheckScrollVia(string via) =>
        ScrollVias.Contains(via) ? via : throw new McpException($"via '{via}' is not one of {string.Join(", ", ScrollVias)}.");

    [McpServerTool(Name = "simulate_input", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Sends raw events to the running game, one frame apart; x and y are viewport coordinates. Types: "
            + "key {key, pressed?, modifiers?, unicode?}; mouse_button {x, y, button?, pressed?, doubleClick?, factor?}; "
            + "mouse_motion {x, y, relative_x?, relative_y?, button_mask?}; pan_gesture {x, y, delta_x?, delta_y?}; "
            + "joypad_button {button, pressed?, device?}; joypad_motion {axis, value, device?}; action {action, pressed?, "
            + "strength?}; click_element {element, button?, doubleClick?}; wait {ms}. mouse_button's button is left, right, "
            + "middle, or a wheel notch: wheel_up, wheel_down, wheel_left, wheel_right, with factor (1 when left out; wheel "
            + "buttons only) for how far it goes. An omitted pressed on key, mouse_button or joypad_button is a press and a "
            + "release a frame apart, on a wheel button both in one frame, as Godot's Windows display server sends a notch; a "
            + "wheel button never joins the held buttons. pan_gesture is a trackpad's pan at the point, its deltas 0 when left "
            + "out (a ScrollContainer scrolls down and right by positive ones). A motion's relative defaults to the step from "
            + "the last pointer position and its button_mask to the buttons held now. Joypad names and ranges are gamepad_button's and "
            + "gamepad_axis's; device omitted: the id the gamepad tools choose (the lowest no connected real pad holds), "
            + "reported as device."
            + PadNote
            + ErrorNote
    )]
    public Task<string> SimulateInputAsync(
        [Description("The events, each an object with a type and that type's fields.")] JsonObject[] events,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        if (events is null || events.Length == 0)
        {
            throw new McpException("events is empty; pass at least one event object with a type.");
        }

        JsonArray list = [];
        double waitMs = 0;
        for (int index = 0; index < events.Length; index++)
        {
            JsonObject item = CheckEvent(events[index], index);
            waitMs += ReadWaitMs(item);
            list.Add(item.DeepClone());
        }

        JsonObject parameters = new() { ["gesture"] = "events", ["events"] = list };
        TimeSpan allowance = (PerStepAllowance * events.Length) + TimeSpan.FromMilliseconds(waitMs);
        return SendInputAsync(session, "simulate_input", parameters, allowance, cancellationToken);
    }

    [McpServerTool(Name = "simulate_action", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Presses, releases or taps (press, a frame, release) an InputMap action in the running game with an InputEventAction, "
            + "as simulate_input's action event does: Input.is_action_pressed and get_action_strength follow it and the game's "
            + "input handlers receive it, whatever keys or buttons the action is bound to. An action missing from the project's "
            + "InputMap is refused. Returns {pointer, heldButtonMask} as simulate_input does."
            + ErrorNote
    )]
    public Task<string> SimulateActionAsync(
        [Description("The action's name as the project's InputMap has it: ui_accept, jump.")] string action,
        [Description("{mode, strength}: mode tap (the default), press or release; strength 0 to 1 (1 when left out), carried by the press.")]
            ActionOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonArray events = BuildActionEvents(action, options);
        JsonObject parameters = new() { ["gesture"] = "events", ["events"] = events };
        return SendInputAsync(session, "simulate_action", parameters, PerStepAllowance * events.Count, cancellationToken);
    }

    /// <summary>
    /// simulate_input's action events for simulate_action: a press, a release, or both a frame apart for a tap, the press
    /// carrying the strength (Godot reads a released InputEventAction's strength as 0, core/input/input_map.cpp L300 in
    /// 4.7.2).
    /// </summary>
    /// <exception cref="McpException">An empty action, an unknown mode, or a strength outside 0 to 1.</exception>
    internal static JsonArray BuildActionEvents(string action, ActionOptions? options)
    {
        CheckName(action, "action", "Pass the name of an action in the project's InputMap, such as ui_accept.");
        ActionOptions checkedOptions = options ?? new ActionOptions();
        string mode = checkedOptions.Mode;
        if (mode is not ("tap" or "press" or "release"))
        {
            throw new McpException($"mode '{mode}' is not one of tap, press, release.");
        }

        double strength = checkedOptions.Strength;
        if (strength is not (>= 0 and <= 1))
        {
            throw new McpException($"strength must be between 0 and 1; got {strength.ToString(CultureInfo.InvariantCulture)}.");
        }

        JsonArray events = [];
        if (mode != "release")
        {
            events.Add(ActionEvent(action, true, strength));
        }

        if (mode != "press")
        {
            events.Add(ActionEvent(action, false, null));
        }

        return events;
    }

    private static JsonObject ActionEvent(string action, bool pressed, double? strength)
    {
        JsonObject item = new()
        {
            ["type"] = "action",
            ["action"] = action,
            ["pressed"] = pressed,
        };
        if (strength is not null)
        {
            item["strength"] = strength;
        }

        return item;
    }

    /// <summary>
    /// Plays one input call on the session once any earlier one on it has finished, and adds the errors the game raised while
    /// it played to the bridge's <c>{pointer, heldButtonMask}</c> (the call still succeeds).
    /// </summary>
    private async Task<string> SendInputAsync(
        string? session,
        string tool,
        JsonObject parameters,
        TimeSpan allowance,
        CancellationToken cancellationToken
    )
    {
        GodotSession target = Find(session);
        await target.InputGate.WaitAsync(cancellationToken);
        try
        {
            BridgeCall call = new(tool, "input", parameters, InputAllowance(allowance, target.ActiveRecording is not null));
            BridgeResult result = await CallWithErrorsAsync(target, call, cancellationToken);
            JsonObject played = result.Reply as JsonObject ?? [];
            return ErrorReport.AddTo(played, result.Errors).ToJsonString();
        }
        finally
        {
            target.InputGate.Release();
        }
    }

    /// <summary>
    /// How long an input call may take: <see cref="InputTimeout"/> plus the call's allowance; in a recording, where the bridge
    /// plays durations in clip time (60 movie frames a second, however slowly the game runs), the step rule's allowance for
    /// the allowance's frames instead.
    /// </summary>
    internal static TimeSpan InputAllowance(TimeSpan allowance, bool recording) =>
        recording ? StepAllowance(ClipFrames((long)Math.Ceiling(allowance.TotalMilliseconds))) : InputTimeout + allowance;

    internal static JsonObject CheckEvent(JsonObject? item, int index)
    {
        JsonObject checkedItem = item ?? throw new McpException($"events[{index}] is null.");
        string? type = checkedItem["type"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        return type is not null && EventTypes.Contains(type)
            ? checkedItem
            : throw new McpException($"events[{index}] has type '{type ?? "null"}'; the types are {string.Join(", ", EventTypes)}.");
    }

    private static double ReadWaitMs(JsonObject item) =>
        item["type"]?.GetValue<string>() == "wait" && item["ms"] is JsonValue ms && ms.TryGetValue(out double wait) ? Math.Max(wait, 0) : 0;

    internal static string CheckButton(string button) =>
        Buttons.Contains(button) ? button : throw new McpException($"button '{button}' is not one of {string.Join(", ", Buttons)}.");

    internal static JsonArray CheckModifiers(string[] modifiers)
    {
        JsonArray checkedModifiers = [];
        foreach (string modifier in modifiers)
        {
            string name = modifier.ToLowerInvariant();
            if (!Modifiers.Contains(name))
            {
                throw new McpException($"modifier '{modifier}' is not one of {string.Join(", ", Modifiers)}.");
            }

            checkedModifiers.Add(name);
        }

        return checkedModifiers;
    }
}
