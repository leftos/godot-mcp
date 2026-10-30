using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>Whether wait_for captures the frame its condition was met on and which part of it to keep, the method it calls as
/// its checks start, what it runs in the frame its condition is met, whether only a rise meets it, and the wall time that ends it
/// unmet.</summary>
internal sealed record WaitOptions(
    [property: Description(
        "Capture the frame the condition was met on as take_screenshot does (a preview at most 480 px wide); a timed-out wait "
            + "captures nothing, and a frame that was not drawn (a minimized window) gives a warning instead."
    )]
        bool? Screenshot = null,
    [property: Description(
        "Any wait but uiChanged and output: {node, method, args}, a method the bridge calls in the frame the checks start (a "
            + "signal wait connects before the call, so a signal the call emits at once is caught), with no round trip between: "
            + "a gameMs or frames count runs from the method's entry, and an exists, property or "
            + "expression wait's first check follows the call in its frame, its frames and elapsedMs counted from there (an "
            + "edge wait's baseline check comes before the call, so an effect the call makes at once is a rise); a coroutine is "
            + "not awaited. Or {tool, args: {...}} "
            + "for a game tool by name with named arguments, as call_game_tool takes them. The result adds call: {value}, the "
            + "method's return value as call_method returns it (null for a coroutine); a game tool's adds tool and type, and "
            + "pending: true with a null value for a Task, which is not awaited. A call refused as call_method or call_game_tool "
            + "refuses it, or an error it raises, fails the wait."
    )]
        MethodCall? Call = null,
    [property: Description(
        "Any condition kind: {call?, timeScale?, pause?, record?}, run once in the frame the condition is met, so an effect starts "
            + "there with no round trip. call is a method the bridge calls there, as options.call (a coroutine is not awaited); "
            + "timeScale sets Engine.time_scale right after it; pause: true leaves the game paused on that frame, as frame_control "
            + "pause does, so a later read sees the state the condition did; record {mark, path?, overwrite?} marks a Movie Maker "
            + "recording's clip start or stop at that frame, as record_mark does. The result adds then: {frame, call?: {value}, "
            + "timeScale?, paused?, record?}. A refused or failing call fails the wait and leaves timeScale unset and the game "
            + "running; a timeout runs nothing."
    )]
        WaitThen? Then = null,
    [property: Description(
        "exists, property, expression, text and state waits only: true meets the wait only on a check that finds the condition true after "
            + "a check that found it false, so a condition already true when the wait starts waits for its next rise, and one "
            + "true throughout times out with met: false. A check whose expression fails to run does not count as false. "
            + "Refused with timeoutMs 0."
    )]
        bool? Edge = null,
    [property: Description(
        "Wall-time cap in ms: the wait ends unmet with wallCapped: true once this much real time has passed, however slowly the "
            + "load-adjusted timeout runs. Pass it with every long timeoutMs on a game that may stall."
    )]
        double? MaxWallMs = null,
    [property: Description(
        "With screenshot: true, the part of the met frame to keep, {x, y, width, height} in viewport coordinates as "
            + "take_screenshot's crop; the image block is the crop itself when it is at most 480 px wide, else its preview. A crop "
            + "wholly outside the viewport gives a warning and no screenshot; the wait still meets."
    )]
        ScreenshotCrop? Crop = null,
    [property: Description(
        "With screenshot: true, "
            + RuntimeTools.TmpPathDescription
            + " Checked before the wait starts and written after it "
            + "meets: screenshot.path is then that file and screenshot.capturePath the copy under .godot/godot-mcp/screenshots/. "
            + "A wait that ends with no screenshot (not met, or a crop wholly outside the viewport) writes nothing; a copy "
            + "that fails leaves screenshot.path naming the capture under .godot/ and adds screenshot.warning saying why."
    )]
        string? Path = null,
    [property: Description("Replace an existing file at path; without it, saving over an existing file is refused.")] bool? Overwrite = null
);

/// <summary>What wait_for runs in the frame its condition is met: a method call, an Engine.time_scale, a pause, a record mark, or
/// any mix.</summary>
internal sealed record WaitThen(
    [property: Description(
        "The method to call in the met frame, as options.call: {node, method, args}; a coroutine is not awaited. Or {tool, args: "
            + "{...}} for a game tool by name with named arguments, as call_game_tool takes them. It runs before timeScale, so "
            + "the call sees the scale the wait ran at. then adds call: {value}, the method's return value as call_method returns "
            + "it (null for a coroutine); a game tool's adds tool and type, and pending: true with a null value for a Task, which "
            + "is not awaited. A call refused as call_method or call_game_tool refuses it, or an error it raises, fails the wait."
    )]
        MethodCall? Call = null,
    [property: Description(
        "The Engine.time_scale to set in the met frame, after call, greater than 0 and at most 100; the frames after it run scaled."
    )]
        double? TimeScale = null,
    [property: Description(
        "true: leave the game paused on the met frame, after call and timeScale, as frame_control pause does, so the state the "
            + "condition read holds for a later read; frame_control resume undoes it. A plain wait pauses before the met frame's "
            + "_process, after its physics, so values read are the ones the wait met, but anything the game updates in _process (a "
            + "shader, an animation) may still show the previous frame; screenshot: true pauses after the met frame is drawn, so a "
            + "picture matches the values. capture_frames' start.then refuses it, as a paused game adds no game time, so the capture "
            + "would never advance."
    )]
        bool? Pause = null,
    [property: Description(
        "{mark: start | stop, path?, overwrite?}: marks the session's Movie Maker recording (run_project options.record) at the "
            + "movie frame the condition was met in, as record_mark does there, with no idle movie frames between; a start includes "
            + "the met frame. path and overwrite are a start's, as record_mark start's options take them. The mark, the path and "
            + "the clip state are checked before the wait starts, refused as record_mark refuses them; a real-time recording, or "
            + "none, is refused. then adds record: {mark, mode: movie, frame, seconds, destination?}, as record_mark returns, or "
            + "{mark, error} when the recording refuses the mark once the wait met (a stop on its start's frame); the wait stays "
            + "met. capture_frames' start.then refuses it."
    )]
        ThenRecord? Record = null
);

/// <summary>The record mark wait_for's then places in the met frame of a Movie Maker recording.</summary>
internal sealed record ThenRecord(
    [property: Description("start or stop.")] string Mark,
    [property: Description(
        "On start only, where the finished clip is moved: a project-relative, res:// or absolute .mp4 path in the project, its "
            + "checkout when it tracks the project, or from a linked worktree the main checkout's .tmp, as record_mark start's "
            + "options.path. Without it the clip stays under .godot/godot-mcp/recordings/."
    )]
        string? Path = null,
    [property: Description("On start only: replace an existing file at path; default false, which refuses one.")] bool Overwrite = false
);
