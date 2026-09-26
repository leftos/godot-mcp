using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How run_project sets up the game's window and pads, and the session's name.</summary>
internal sealed record RunOptions(
    [property: Description("Park the window off-screen, unfocusable and click-through, so it never takes the user's mouse or keyboard.")]
        bool Background = false,
    [property: Description(ProjectTools.ShutOutDescription)] bool ShutOutRealGamepads = false,
    [property: Description(ProjectTools.NewSessionDescription)] string? Session = null
);
