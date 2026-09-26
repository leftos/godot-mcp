using System.ComponentModel;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>How run_project sets up the game's window and pads, the session's name, and whether the project is prepared first.</summary>
internal sealed record RunOptions(
    [property: Description(
        "Starts the game quiet: created unfocused and off-screen, click-through, with the Dummy audio driver, so it takes no "
            + "focus, shows nothing and plays no sound. false gives a normal visible, audible, focusable window."
    )]
        bool Quiet = true,
    [property: Description(ProjectTools.ShutOutDescription)] bool ShutOutRealGamepads = false,
    [property: Description(ProjectTools.NewSessionDescription)] string? Session = null,
    [property: Description(
        "auto (the default): before launching, build the project's C# assembly when it is missing or older than its sources "
            + "(dotnet build; log in .godot/godot-mcp/build.log; a failed build refuses the launch with its compiler errors), then "
            + "run a Godot import when imported files are missing (log in .godot/godot-mcp/import.log). never: launch as it is."
    )]
        string? Prepare = null
)
{
    /// <summary>Whether prepare asks for the prep: true for auto or when left out, false for never.</summary>
    /// <exception cref="McpException">prepare is anything else.</exception>
    internal bool ShouldPrepare() =>
        Prepare switch
        {
            null or "auto" => true,
            "never" => false,
            _ => throw new McpException($"prepare takes \"auto\" or \"never\"; got \"{Prepare}\"."),
        };
}
