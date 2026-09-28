using System.ComponentModel;
using System.Text.Json.Serialization;

namespace GodotMcp.Server.Tools;

/// <summary>capture_frames' evenly spaced points, its crop and its timeout.</summary>
internal sealed record CaptureFramesOptions(
    [property: Description("With for, instead of at: a frame every this many seconds of game time, greater than 0.")] double? Every = null,
    [property: JsonPropertyName("for")]
    [property: Description(
        "With every, instead of at: how many seconds of game time to capture, greater than 0 and at most 120; the points are "
            + "every, 2 x every and so on, up to and including for."
    )]
        double? For = null,
    [property: Description("A rectangle of each frame to keep, as take_screenshot takes it.")] ScreenshotCrop? Crop = null,
    [property: Description(
        "How long the call may run, in milliseconds, 1 to 600000, load-adjusted; by default the last point's seconds x 1000 + "
            + "10000 + 100 per point. When it passes, the call answers the frames taken so far with stopped and missed."
    )]
        int? TimeoutMs = null
);
