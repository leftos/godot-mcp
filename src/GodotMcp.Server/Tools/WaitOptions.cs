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
        "Any condition kind: {call?, timeScale?, pause?}, run once in the frame the condition is met, so an effect starts there "
            + "with no round trip. call is a method the bridge calls there, as options.call (a coroutine is not awaited); timeScale "
            + "sets Engine.time_scale right after it; pause: true leaves the game paused on that frame, as frame_control pause does, "
            + "so a later read sees the state the condition did. The result adds then: {frame, call?: {value}, timeScale?, paused?}. "
            + "A refused or failing call fails the wait and leaves timeScale unset and the game running; a timeout runs nothing."
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
        ScreenshotCrop? Crop = null
);

/// <summary>What wait_for runs in the frame its condition is met: a method call, an Engine.time_scale, a pause, or any mix.</summary>
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
        bool? Pause = null
);
