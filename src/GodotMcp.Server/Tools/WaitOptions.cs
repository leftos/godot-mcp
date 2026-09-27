using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>Whether wait_for captures the frame its condition was met on.</summary>
internal sealed record WaitOptions(
    [property: Description(
        "Capture the frame the condition was met on as take_screenshot does (a preview at most 480 px wide); a timed-out wait "
            + "captures nothing, and a frame that was not drawn (a minimized window) gives a warning instead."
    )]
        bool? Screenshot = null
);
