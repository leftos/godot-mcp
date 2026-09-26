using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How run_project sets up the game's window and pads, and the session's name.</summary>
internal sealed record RunOptions(
    [property: Description(
        "Starts the game quiet: created unfocused and off-screen, click-through, with the Dummy audio driver, so it takes no "
            + "focus, shows nothing and plays no sound. false gives a normal visible, audible, focusable window."
    )]
        bool Quiet = true,
    [property: Description(ProjectTools.ShutOutDescription)] bool ShutOutRealGamepads = false,
    [property: Description(ProjectTools.NewSessionDescription)] string? Session = null
);
