using System.ComponentModel;
using System.Text.Json.Nodes;

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
        WaitCondition? Until = null,
    [property: Description(
        "Riding input: simulate_input's raw events (key, mouse_button, mouse_motion, pan_gesture, joypad_button, joypad_motion, "
            + "action; at most 100), each with frame, the 0-based stepped frame it is sent in (0 when left out). Each is sent at "
            + "its frame's process_frame, before the frame's nodes process, so _input, gui_input and is_action_just_pressed in "
            + "_process see it in that frame; _physics_process sees the press at the next physics tick, one frame later than a "
            + "real event. A key, mouse button or pad button with pressed left out presses at frame and releases at frame + 1, "
            + "inside the step. Events of one frame keep their list order. Process unit only. After an until met early, the "
            + "result adds inputNotSent, the events left unsent (a press-and-release entry counts as two); held {keys, "
            + "mouseButtons, padButtons, actions} lists what is still pressed; a pad event on a connected real pad adds "
            + "simulate_input's warning to warning. An event the game cannot play fails the step at its frame, leaving the "
            + "game paused there; a failed step's error ends with \"; held: \" and the held object when anything is held."
    )]
        JsonObject[]? Input = null
);
