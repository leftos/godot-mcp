using System.ComponentModel;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>
/// How run_project sets up the game's window and pads, the session's name, whether the project is prepared first, and which
/// preset of the project's godot-mcp.json it launches, and whether it is recorded. <see cref="Quiet"/> is null when not given.
/// </summary>
internal sealed record RunOptions(
    [property: Description(
        "Starts the game quiet: created unfocused and off-screen, click-through, with the Dummy audio driver, so it takes no "
            + "focus, shows nothing and plays no sound. false gives a normal visible, audible, focusable window. Default true, "
            + "unless the project's godot-mcp.json sets quiet."
    )]
        bool? Quiet = null,
    [property: Description(ProjectTools.ShutOutDescription)] bool ShutOutRealGamepads = false,
    [property: Description(ProjectTools.NewSessionDescription)] string? Session = null,
    [property: Description(RunOptions.PrepareDescription)] string? Prepare = null,
    [property: Description(
        "A launch preset from the project's godot-mcp.json (beside project.godot): its values layer over the file's top-level "
            + "defaults, and run_project's own arguments override both (userArgs and engineArgs append). The file's top level "
            + "applies even with no preset."
    )]
        string? Preset = null,
    [property: Description(
        "Records the run with Godot's Movie Maker from launch: video and audio, frame-perfect, at a fixed 60 fps, to "
            + "<project>/.godot/godot-mcp/recordings/<stamp>-<session>.avi. Game time then advances 1/60 s per frame whatever "
            + "the wall clock does, so millisecond timeouts and gesture durations no longer match game time. Mark the parts to "
            + "keep with record_mark; stop_project finalises the file and cuts them. Capped at 10 minutes of frames. Not with "
            + "--headless. If the server itself exits, a recording game is killed and its movie is not finalised."
    )]
        bool? Record = null
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
