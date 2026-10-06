using System.ComponentModel;
using System.Text.Json.Serialization;

namespace GodotMcp.Server.Tools;

/// <summary>capture_frames' evenly spaced points, its crop, its timeout and the method it calls as its clock starts.</summary>
internal sealed record CaptureFramesOptions(
    [property: Description("With for, instead of at: a frame every this many seconds of game time, greater than 0.")] double? Every = null,
    [property: JsonPropertyName("for")]
    [property: Description(
        "With every, instead of at: how many seconds of game time to capture, greater than 0 and at most 120; the points are "
            + "every, 2 x every and so on, up to and including for."
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
            + "it raises, fails the capture with no frames."
    )]
        MethodCall? Call = null
);
