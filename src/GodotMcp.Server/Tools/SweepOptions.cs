using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How gamepad_axis and gamepad_stick move to their target.</summary>
internal sealed record SweepOptions(
    [property: Description(
        "How long the sweep to the target (gamepad_axis's value, gamepad_stick's position) takes, in milliseconds; 0 sends it at once. "
            + "In a recording, clip time: 60 movie frames a second."
    )]
        int DurationMs = 0,
    [property: Description("Send 0.0 on the moved axes a frame after the target is reached, as a pad does when let go.")] bool Release = false
);
