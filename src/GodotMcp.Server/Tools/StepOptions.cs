using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How frame_control's step counts its frames, whether it captures the last one, and the condition that ends it
/// early.</summary>
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
        bool? Screenshot = null,
    [property: Description(
        "A wait_for condition of any kind but frames: the step stops on the first stepped frame that meets it and stays paused "
            + "there, so every read tool sees that frame; count is then the most frames it runs, 1000 when left out. Checked "
            + "after each stepped frame, never before the first, at the frame's draw (unit process) or as the next physics tick "
            + "starts (unit physics). The result adds met, with value (args for a signal) or last as wait_for reports them; never "
            + "met, the step runs count frames and answers met: false, which is not an error."
    )]
        WaitCondition? Until = null
);
