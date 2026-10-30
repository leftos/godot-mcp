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
        "{node, property, equals | exists | expression | signal, edge?, timeoutMs?, then?, call?}: a condition laid out as "
            + "wait_for's, of kind exists, property, expression or signal, that the clock waits for: the points count from the "
            + "frame it is met (a signal's: the first check after its emission), where "
            + "start.then runs (call, then timeScale) and then options.call; a start already true at the request, with no "
            + "start.call, starts the clock at the next frame. start.call runs after an edge start's baseline check and before "
            + "the check that can meet the start, so the start sees the change it makes; a change an earlier tool call made may "
            + "already have happened before the start's first check, and an edge start then misses its rise. Its timeoutMs (real "
            + "time, 10000 when left out) is added to the capture's. Met, the result adds start: {met: true, frame, args?, then?, "
            + "call?}, args for a signal; a start that times out answers stopped: true with no frames and start: {met: false, "
            + "last?, call?}, last for every kind but a signal."
    )]
        CaptureStart? Start = null,
    [property: Description(
        "{properties?, expressions?}: values to read with each frame, laid out as watch's property and expression tracks "
            + "(minDelta aside), at most 32 together. Each is read at the frame_post_draw its point's image is grabbed at, after "
            + "the frame's _process and draw, so it is the value that image shows. Each point adds values: {<track>: value}, "
            + "keyed as watch keys a track (its name, else node:property as given); a read that fails gives null and adds "
            + "trackErrors: {<track>: message}, and the capture goes on. A missing node or property, or an expression that does "
            + "not parse, refuses the capture before its clock starts. Signal, monitor and audio tracks are watch's."
    )]
        CaptureTracks? Tracks = null,
    [property: Description(
        "A .png stem to copy the frames to: frame k of the n distinct files lands at <stem>-<k>.png, k zero-padded to the "
            + "width of n (anim.png gives anim-00.png to anim-11.png for 12 files), in the checkout's .tmp folder or the main "
            + "checkout's .tmp folder only, checked as save_screenshot checks its path. Every name the points could give is "
            + "checked before the capture starts, and each file again at its write; folder and files then name the copies and "
            + "captureFolder the capture's folder under .godot/godot-mcp/screenshots/. A copy that fails removes the copies "
            + "made and keeps the result whole: folder and files name the frames under .godot/, and warning says why and names "
            + "that folder, which keeps every frame."
    )]
        string? Path = null,
    [property: Description("Replace existing files at path's names; without it, a capture whose names include an existing file is refused.")]
        bool? Overwrite = null
);

/// <summary>
/// The values capture_frames reads beside each frame: watch's property and expression tracks. Signal, monitor and audio tracks
/// bind so they can be refused by name.
/// </summary>
internal sealed record CaptureTracks(
    [property: Description(
        "Property tracks, each {node, property, name?}: a property of a node, or a path into one as wait_for takes it "
            + "(position:y, modulate:a), read with get_indexed at each point's frame."
    )]
        WatchPropertyTrack[]? Properties = null,
    [property: Description(
        "Expression tracks, each {name, expression, node?}: a Godot Expression run at each point's frame with wait_for's "
            + "inputs (node when given, root, tree, Input, Engine)."
    )]
        WatchExpressionTrack[]? Expressions = null,
    [property: Description("Refused: watch records signals.")] JsonElement? Signals = null,
    [property: Description("Refused: watch reads monitors.")] JsonElement? Monitors = null,
    [property: Description("Refused: watch follows audio.")] JsonElement? Audio = null
);

/// <summary>
/// The condition capture_frames' clock waits for, laid out as wait_for's: exactly one of {node, exists}, {node, property,
/// equals}, {node, signal} or {expression} (node optional), with its edge, timeout, then and the call it makes before the check
/// that can meet it. The other wait kinds bind so they can be refused by name.
/// </summary>
internal sealed record CaptureStart(
    [property: Description("The node, as wait_for's condition.node takes it.")] string? Node = null,
    [property: Description("With node: start once the node is present (true) or absent (false).")] bool? Exists = null,
    [property: Description("With node and equals: the property to read each frame, as wait_for's condition.property.")] string? Property = null,
    [property: JsonPropertyName("equals")]
    [property: JsonConverter(typeof(ExplicitNullConverter))]
    [property: Description("The JSON value the property must equal, as wait_for's condition.equals compares it.")]
        JsonElement? EqualsValue = null,
    [property: Description(
        "With node: start on the node's next emission of this signal, as wait_for's condition.signal: connected at the request "
            + "(or, with start.call, right before the call), and met at the first check after the emission, the frame wait_for's "
            + "signal wait meets on. Refused with edge or timeoutMs 0. start adds args, the emission's arguments."
    )]
        string? Signal = null,
    [property: Description("A Godot Expression evaluated each frame, as wait_for's condition.expression; met when it returns true.")]
        string? Expression = null,
    [property: Description("Refused: options.start takes an exists, property, expression or signal condition.")] JsonElement? UiChanged = null,
    [property: Description("Refused: options.start takes an exists, property, expression or signal condition.")] int? GameMs = null,
    [property: Description("Refused: options.start takes an exists, property, expression or signal condition.")] int? Frames = null,
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
    [property: Description("Refused: options.start takes an exists, property, expression or signal condition.")] string? Text = null,
    [property: Description("Refused: options.start takes an exists, property, expression or signal condition.")] string? Under = null,
    [property: Description("Refused: options.start takes an exists, property, expression or signal condition.")] int? Stable = null,
    [property: Description("Refused: options.start takes an exists, property, expression or signal condition.")] string? State = null,
    [property: Description("Refused: options.start takes an exists, property, expression or signal condition.")] string? Output = null,
    [property: Description("Refused: options.start takes an exists, property, expression or signal condition.")] string? Stream = null,
    [property: Description("Refused: options.start takes an exists, property, expression or signal condition.")] long? After = null
);
