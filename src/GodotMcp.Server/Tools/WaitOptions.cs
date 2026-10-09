using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>Whether wait_for captures the frame its condition was met on, the method a gameMs or frames wait calls as it
/// starts, and what it runs in the frame its condition is met.</summary>
internal sealed record WaitOptions(
    [property: Description(
        "Capture the frame the condition was met on as take_screenshot does (a preview at most 480 px wide); a timed-out wait "
            + "captures nothing, and a frame that was not drawn (a minimized window) gives a warning instead."
    )]
        bool? Screenshot = null,
    [property: Description(
        "gameMs and frames waits only: {node, method, args}, a method the bridge calls in the frame the count starts, so the "
            + "count runs from the method's entry, with no round trip between; a coroutine is not awaited. Or {tool, args: {...}} "
            + "for a game tool by name with named arguments, as call_game_tool takes them. The result adds call: {value}, the "
            + "method's return value as call_method returns it (null for a coroutine); a game tool's adds tool and type, and "
            + "pending: true with a null value for a Task, which is not awaited. A call refused as call_method or call_game_tool "
            + "refuses it, or an error it raises, fails the wait."
    )]
        MethodCall? Call = null,
    [property: Description(
        "Any condition kind: {call?, timeScale?}, run once in the frame the condition is met, so an effect starts there with no "
            + "round trip. call is a method the bridge calls there, as options.call (a coroutine is not awaited); timeScale sets "
            + "Engine.time_scale right after it. The result adds then: {frame, call?: {value}, timeScale?}. A refused or failing "
            + "call fails the wait and leaves timeScale unset; a timeout runs nothing."
    )]
        WaitThen? Then = null,
    [property: Description(
        "exists, property and expression waits only: true meets the wait only on a check that finds the condition true after "
            + "a check that found it false, so a condition already true when the wait starts waits for its next rise, and one "
            + "true throughout times out with met: false. A check whose expression fails to run does not count as false. "
            + "Refused with timeoutMs 0."
    )]
        bool? Edge = null
);

/// <summary>What wait_for runs in the frame its condition is met: a method call, an Engine.time_scale, or both.</summary>
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
        double? TimeScale = null
);
