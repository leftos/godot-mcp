using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>Whether wait_for captures the frame its condition was met on, and the method a gameMs or frames wait calls as it starts.</summary>
internal sealed record WaitOptions(
    [property: Description(
        "Capture the frame the condition was met on as take_screenshot does (a preview at most 480 px wide); a timed-out wait "
            + "captures nothing, and a frame that was not drawn (a minimized window) gives a warning instead."
    )]
        bool? Screenshot = null,
    [property: Description(
        "gameMs and frames waits only: {node, method, args}, a method the bridge calls in the frame the count starts, so the "
            + "count runs from the method's entry, with no round trip between; a coroutine is not awaited. The result adds call: "
            + "{value}, the method's return value as call_method returns it (null for a coroutine). A call refused as call_method "
            + "refuses it, or an error the method raises, fails the wait."
    )]
        MethodCall? Call = null
);
