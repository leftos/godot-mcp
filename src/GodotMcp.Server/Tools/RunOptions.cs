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
    [property: Description(RunOptions.PrepareDescription)] string? Prepare = null
)
{
    /// <summary>The prepare option, for run_project's options and restart_project's.</summary>
    internal const string PrepareDescription =
        "auto (the default): before launching, build the project's C# assembly when it is missing or older than its sources "
        + "(dotnet build; log in .godot/godot-mcp/build.log; a failed build refuses the launch with its compiler errors), then "
        + "run a Godot import when imported files are missing (log in .godot/godot-mcp/import.log). never: launch as it is.";

    /// <summary>Whether prepare asks for the prep: true for auto or when left out, false for never.</summary>
    /// <exception cref="McpException">prepare is anything else.</exception>
    internal bool ShouldPrepare() => ParsePrepare(Prepare);

    /// <summary>Whether a prepare value asks for the prep: true for auto or null, false for never.</summary>
    /// <exception cref="McpException">The value is anything else.</exception>
    internal static bool ParsePrepare(string? prepare) =>
        prepare switch
        {
            null or "auto" => true,
            "never" => false,
            _ => throw new McpException($"prepare takes \"auto\" or \"never\"; got \"{prepare}\"."),
        };
}

/// <summary>Whether restart_project prepares the project before the relaunch.</summary>
internal sealed record RestartOptions([property: Description(RunOptions.PrepareDescription)] string? Prepare = null)
{
    /// <summary>Whether prepare asks for the prep: true for auto or when left out, false for never.</summary>
    /// <exception cref="McpException">prepare is anything else.</exception>
    internal bool ShouldPrepare() => RunOptions.ParsePrepare(Prepare);
}
