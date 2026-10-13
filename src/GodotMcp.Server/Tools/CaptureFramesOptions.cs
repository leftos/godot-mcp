using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodotMcp.Server.Tools;

/// <summary>capture_frames' evenly spaced points, its crop, its timeout, the method it calls as its clock starts, and the
/// condition the clock waits for.</summary>
internal sealed record CaptureFramesOptions(
    [property: Description("With for, instead of at: a frame every this many seconds of game time, greater than 0.")] double? Every = null,
    [property: JsonPropertyName("for")]
    [property: Description(
        "With every, instead of at: how many seconds of game time to capture, greater than 0 and at most 120; the points are "
            + "0, every, 2 x every and so on, up to and including for, at most 1000 with the point at 0. The point at 0 is taken "
            + "in the clock's first frame, after options.call has run: one frame into the motion, not the pose before the call."
    )]
        double? For = null,
    [property: Description("A rectangle of each frame to keep, in viewport coordinates, as take_screenshot takes it.")] ScreenshotCrop? Crop = null,
    [property: Description(
        "How long the call may run, in milliseconds, 1 to 600000, load-adjusted; by default the last point's seconds x 1000 + "
            + "10000 + 100 per point. When it passes, the call answers the frames taken so far with stopped and missed."
    )]
        int? TimeoutMs = null,
    [property: Description(
        "{node, method, args}: a method the bridge calls in the frame the capture's clock starts, so the points count from the "
            + "method's entry, with no round trip between; a coroutine is not awaited. Or {tool, args: {...}} for a game tool by "
            + "name with named arguments, as call_game_tool takes them. The result adds call: {value}, the method's return value "
            + "as call_method returns it (null for a coroutine); a game tool's adds tool and type, and pending: true with a null "
            + "value for a Task, which is not awaited. A call refused as call_method or call_game_tool refuses it, or an error "
            + "it raises, fails the capture with no frames. With start, it runs in the frame the start is met, after start.then."
    )]
        MethodCall? Call = null,
    [property: Description(
        "{node, property, equals | exists | expression, edge?, timeoutMs?, then?, call?}: a condition laid out as wait_for's, of "
            + "kind exists, property or expression, that the clock waits for: the points count from the frame it is met, where "
            + "start.then runs (call, then timeScale) and then options.call; a start already true at the request, with no "
            + "start.call, starts the clock at the next frame. start.call runs after an edge start's baseline check and before "
            + "the check that can meet the start, so the start sees the change it makes; a change an earlier tool call made may "
            + "already have happened before the start's first check, and an edge start then misses its rise. Its timeoutMs (real "
            + "time, 10000 when left out) is added to the capture's. Met, the result adds start: {met: true, frame, then?, call?}; "
            + "a start that times out answers stopped: true with no frames and start: {met: false, last, call?}."
    )]
        CaptureStart? Start = null
);

/// <summary>
/// The condition capture_frames' clock waits for, laid out as wait_for's: exactly one of {node, exists}, {node, property,
/// equals} or {expression} (node optional), with its edge, timeout, then and the call it makes before the check that can meet
/// it. The other wait kinds bind so they can be refused by name.
/// </summary>
internal sealed record CaptureStart(
    [property: Description("The node, as wait_for's condition.node takes it.")] string? Node = null,
    [property: Description("With node: start once the node is present (true) or absent (false).")] bool? Exists = null,
    [property: Description("With node and equals: the property to read each frame, as wait_for's condition.property.")] string? Property = null,
    [property: JsonPropertyName("equals")]
    [property: Description("The JSON value the property must equal, as wait_for's condition.equals compares it.")]
        JsonElement? EqualsValue = null,
    [property: Description("Refused: options.start takes an exists, property or expression condition.")] string? Signal = null,
    [property: Description("A Godot Expression evaluated each frame, as wait_for's condition.expression; met when it returns true.")]
        string? Expression = null,
    [property: Description("Refused: options.start takes an exists, property or expression condition.")] bool? UiChanged = null,
    [property: Description("Refused: options.start takes an exists, property or expression condition.")] int? GameMs = null,
    [property: Description("Refused: options.start takes an exists, property or expression condition.")] int? Frames = null,
    [property: Description(
        "How long to wait for the condition, in milliseconds of real time, 0 to 120000; 10000 when left out. 0 checks it " + "once, at the call."
    )]
        int? TimeoutMs = null,
    [property: Description(
        "true: met only on a check that finds the condition true after one that found it false, as wait_for's options.edge; "
            + "refused with timeoutMs 0."
    )]
        bool? Edge = null,
    [property: Description(
        "{call?, timeScale?}, run once in the frame the condition is met, as wait_for's options.then, before options.call; pause is "
            + "refused, as a paused game adds no game time, so the capture would never advance. start adds then: {frame, call?, "
            + "timeScale?}. A refused or failing call fails the capture with no frames."
    )]
        WaitThen? Then = null,
    [property: Description(
        "{node, method, args} or {tool, args}, as options.call takes it: a method the bridge calls in the next frame, after an "
            + "edge start's baseline check and before the check that can meet the start, so the start sees the change it makes; "
            + "a change an earlier tool call made may already have happened before the start's first check. timeoutMs counts "
            + "from after the call, and a start met on the check right after it starts the clock in the call's frame. start adds "
            + "call: {value}, as options.call's. A refused or failing call fails the capture with no frames."
    )]
        MethodCall? Call = null,
    [property: Description("Refused: options.start takes an exists, property or expression condition.")] string? Text = null,
    [property: Description("Refused: options.start takes an exists, property or expression condition.")] string? Under = null,
    [property: Description("Refused: options.start takes an exists, property or expression condition.")] int? Stable = null
);
