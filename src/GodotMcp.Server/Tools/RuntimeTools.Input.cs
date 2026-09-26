using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Input into the running game. The bridge plays each gesture over frames with new event objects, tracking the held
/// buttons and the pointer itself, and answers once the gesture has ended and two more frames have run. One input call
/// plays at a time, and script errors the game printed while it played are appended to its result.
/// </summary>
internal sealed partial class RuntimeTools
{
    private const string ErrorNote = " Script errors the game's handlers raise while the input plays are appended to the result.";
    private static readonly TimeSpan InputTimeout = TimeSpan.FromSeconds(10);

    // Static because the SDK may create a tool object per call; one server plays one input call at a time.
    private static readonly SemaphoreSlim InputGate = new(1, 1);

    // A generous allowance per character or event on top of InputTimeout: each takes a frame or two.
    private static readonly TimeSpan PerStepAllowance = TimeSpan.FromMilliseconds(100);
    private static readonly string[] Buttons = ["left", "right", "middle"];
    private static readonly string[] Modifiers = ["shift", "ctrl", "alt", "meta"];
    private static readonly string[] EventTypes = ["key", "mouse_button", "mouse_motion", "action", "click_element", "wait"];

    [McpServerTool(Name = "click")]
    [Description(
        "Clicks in the running game: moves the pointer to the target, presses, and releases a frame later. Points are "
            + "viewport coordinates, as get_ui_elements reports them; the bridge maps them to the window, stretched or letterboxed."
            + ErrorNote
    )]
    public Task<string> ClickAsync(
        [Description("Where to click.")] InputTarget target,
        [Description("left, right or middle.")] string button = "left",
        [Description("Follow the click with a second press marked double_click.")] bool doubleClick = false,
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
        return SendInputAsync("click", parameters, TimeSpan.Zero, cancellationToken);
    }

    [McpServerTool(Name = "drag")]
    [Description(
        "Drags in the running game: presses at from, moves one step a frame in a straight line to to over durationMs (at "
            + "least 3 frames), each motion carrying the held button and its step, and releases at to. Godot starts a drag "
            + "once the path passes gui/common/drag_threshold (10 px by default); a shorter one is a click. While a gesture "
            + "plays, or injected input holds a button, the real mouse's buttons and motions are kept from the game's GUI."
            + ErrorNote
    )]
    public Task<string> DragAsync(
        [Description("Where the drag starts.")] InputTarget from,
        [Description("Where the drag ends and the button is released.")] InputTarget to,
        [Description("How long the moving part takes, in milliseconds.")] int durationMs = 300,
        [Description("left, right or middle.")] string button = "left",
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
        return SendInputAsync("drag", parameters, TimeSpan.FromMilliseconds(durationMs), cancellationToken);
    }

    [McpServerTool(Name = "type_text")]
    [Description(
        "Types text into the focused Control (click a LineEdit first): per character a key press and release with its "
            + "keycode on a US layout, its unicode, and shift where the character needs it, one frame apart. \\n is Enter, \\t Tab."
            + ErrorNote
    )]
    public Task<string> TypeTextAsync([Description("The text, case and symbols kept.")] string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new McpException("text is empty; pass the characters to type.");
        }

        JsonObject parameters = new() { ["gesture"] = "type_text", ["text"] = text };
        return SendInputAsync("type_text", parameters, PerStepAllowance * text.Length, cancellationToken);
    }

    [McpServerTool(Name = "key")]
    [Description(
        "Presses, releases or taps (press, a frame, release) one key in the running game. A printable key also carries "
            + "the character it types unless ctrl, alt or meta is held."
            + ErrorNote
    )]
    public Task<string> KeyAsync(
        [Description("A Godot Key constant without KEY_: Enter, Escape, Space, A, 1, F1, Up, Shift, Ctrl.")] string key,
        [Description("tap, press or release.")] string action = "tap",
        [Description("Modifiers held with the key: any of shift, ctrl, alt, meta.")] string[]? modifiers = null,
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
        return SendInputAsync("key", parameters, TimeSpan.Zero, cancellationToken);
    }

    [McpServerTool(Name = "mouse_button")]
    [Description(
        "Holds or releases a mouse button at a target: moves the pointer there (carrying any buttons already held), then "
            + "presses or releases. The held buttons stay in later motions' button_mask, so press, simulate_input motions "
            + "and release make a drag by hand. While a button is held, the real mouse's buttons and motions are kept from the "
            + "game's GUI."
            + ErrorNote
    )]
    public Task<string> MouseButtonAsync(
        [Description("Where to press or release.")] InputTarget target,
        [Description("left, right or middle.")] string button = "left",
        [Description("press or release.")] string action = "press",
        CancellationToken cancellationToken = default
    )
    {
        if (action is not ("press" or "release"))
        {
            throw new McpException($"action '{action}' is not one of press, release.");
        }

        JsonObject parameters = new()
        {
            ["gesture"] = "mouse_button",
            ["target"] = InputTarget.ToBridge(target, "target"),
            ["button"] = CheckButton(button),
            ["action"] = action,
        };
        return SendInputAsync("mouse_button", parameters, TimeSpan.Zero, cancellationToken);
    }

    [McpServerTool(Name = "simulate_input")]
    [Description(
        "Sends raw events to the running game, one frame apart; x and y are viewport coordinates. Types: "
            + "key {key, pressed?, modifiers?, unicode?}; mouse_button {x, y, button?, pressed?, doubleClick?}; "
            + "mouse_motion {x, y, relative_x?, relative_y?, button_mask?}; action {action, pressed?, strength?}; "
            + "click_element {element, button?, doubleClick?}; wait {ms}. An omitted pressed on key or mouse_button is a press "
            + "and a release a frame apart; a motion's relative defaults to the step from the last pointer position and its "
            + "button_mask to the buttons held now."
            + ErrorNote
    )]
    public Task<string> SimulateInputAsync(
        [Description("The events, each an object with a type and that type's fields.")] JsonObject[] events,
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
        return SendInputAsync("simulate_input", parameters, allowance, cancellationToken);
    }

    /// <summary>
    /// Plays one input call once any earlier one has finished, and appends the script errors Godot printed while it played
    /// (read once, after the stderr settle; the call still succeeds).
    /// </summary>
    private async Task<string> SendInputAsync(string tool, JsonObject parameters, TimeSpan allowance, CancellationToken cancellationToken)
    {
        await InputGate.WaitAsync(cancellationToken);
        try
        {
            long mark = session.MarkStderr();
            JsonNode? reply = await CallBridgeAsync(tool, "input", parameters, InputTimeout + allowance, cancellationToken);
            string result = reply?.ToJsonString() ?? "{}";
            IReadOnlyList<string> errors = await CollectScriptErrorsAsync(mark, StderrSettle, cancellationToken);
            return errors.Count == 0 ? result : $"{result}\nGodot reported errors while the input played:\n{string.Join('\n', errors)}";
        }
        finally
        {
            InputGate.Release();
        }
    }

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
