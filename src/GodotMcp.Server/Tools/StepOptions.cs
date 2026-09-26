using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How frame_control's step counts its frames, and whether it captures the last one.</summary>
internal sealed record StepOptions(
    [property: Description(
        "process (the default): count drawn frames, each a whole frame with its physics ticks; physics: count physics ticks, "
            + "whose number per drawn frame varies."
    )]
        string? Unit = null,
    [property: Description(
        "Capture the step's last frame as take_screenshot does (a preview at most 480 px wide); with unit physics, the frame "
            + "drawn after the last tick."
    )]
        bool? Screenshot = null
);
