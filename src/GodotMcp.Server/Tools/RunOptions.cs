using System.ComponentModel;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>
/// How run_project sets up the game's window and pads, the session's name, whether the project is prepared first, and which
/// preset of the project's godot-mcp.json it launches, whether it is recorded, whether its clips drop idle frames, whether the
/// bridge re-picks the hover every frame, whether a quiet run keeps its sound, and the environment variables the game's process gets.
/// <see cref="Quiet"/>, <see cref="Mute"/> and <see cref="Audible"/> are null when not given.
/// </summary>
internal sealed record RunOptions(
    [property: Description(
        "Starts the game quiet: created unfocused and off-screen, click-through, with the Dummy audio driver, so it takes no "
            + "focus, shows nothing and plays no sound unless audible is true. false gives a normal visible, audible, focusable "
            + "window. Default true, unless the project's godot-mcp.json sets quiet."
    )]
        bool? Quiet = null,
    [property: Description(ProjectTools.MuteEffect + " Default false; false is refused on a quiet run, which is silent unless audible is true.")]
        bool? Mute = null,
    [property: Description(
        "With a quiet run: keeps the real audio driver, so the game is heard while its window stays unfocused and off-screen on "
            + "the hidden desktop. Default false; refused on a run that is not quiet, with mute true or with record. record_mark "
            + "cannot record a quiet window; use record for a clip with sound."
    )]
        bool? Audible = null,
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
            + "the wall clock does, so wait_for's timeoutMs and the gesture durations (drag's durationMs, hover's tooltip wait, a "
            + "gamepad sweep) count clip time, 60 frames a second, and a wait's result adds clipMs. Mark the parts to "
            + "keep with record_mark; stop_project finalises the file and cuts them into .mp4 clips. Capped at 10 minutes of "
            + "frames. Not with "
            + "--headless or options.audible. If the server itself exits, a recording game is killed and its movie is not finalised."
    )]
        bool? Record = null,
    [property: Description(
        "With record: each cut clip keeps only the frames drawn while a tool call ran, a wait_for gameMs hold included, with their "
            + "audio, and drops every frame drawn between calls, so the time between tool calls disappears. A game animation that runs "
            + "only between calls leaves the clip. Refused without record."
    )]
        bool? DropIdle = null,
    [property: Description(
        "Default true; false stops the bridge re-picking the hover at the injected pointer every frame, so only the game's own "
            + "re-picks (which pick at the OS cursor), a scene change and the end of a GUI drag (a drop or a cancel) move the hover."
    )]
        bool? RepickHover = null,
    [property: Description(
        "Environment variables for the game, laid over the server's own (a godot-mcp.json env and a preset's env are laid under these, "
            + "per variable). Cannot set GODOT_MCP_* names. A variable cannot be removed. restart_project keeps them."
    )]
        Dictionary<string, string>? Env = null
)
{
    /// <summary>The prepare option, for run_project's options and restart_project's.</summary>
    internal const string PrepareDescription =
        "auto (the default): before launching, build the project's C# assembly when it is missing or older than its sources "
        + "(dotnet build; log in .godot/godot-mcp/build.log; a failed build refuses the launch with its compiler errors), then "
        + "run a Godot import when imported files are missing, a source asset changed since it was imported, or its import "
        + "settings changed since the server last saw them (log in .godot/godot-mcp/import.log). never: launch as it is.";

    /// <summary>What refuses an explicit mute: false on a run whose quiet resolves true and audible does not.</summary>
    internal const string UnmuteQuietRefusal =
        "options.mute: false cannot unmute a quiet run, which is always silent; pass options.audible: true to hear it off-screen, "
        + "or options.quiet: false to hear it on screen.";

    /// <summary>What refuses options.audible on a run that is not quiet.</summary>
    internal const string AudibleOffQuietRefusal =
        "options.audible: true applies to a quiet run, and this run is not quiet, so it already plays sound (options.mute: true "
        + "silences it); drop options.audible.";

    /// <summary>What refuses options.audible with an explicit mute: true.</summary>
    internal const string AudibleWithMuteRefusal = "options.audible: true and options.mute: true contradict; drop one.";

    /// <summary>What refuses options.audible on a recording run.</summary>
    internal const string AudibleRecordingRefusal =
        "options.audible has no effect on a recording run: Movie Maker mixes on the Dummy driver and writes the sound into the "
        + "clip; drop options.audible.";

    /// <summary>Whether prepare asks for the prep: true for auto or when left out, false for never.</summary>
    /// <exception cref="McpException">prepare is anything else.</exception>
    internal bool ShouldPrepare() => ParsePrepare(Prepare);

    /// <summary>Whether the bridge mutes the run: <see cref="Mute"/> when given, else false.</summary>
    /// <param name="quiet">Whether the run is quiet, as the options and the project's godot-mcp.json resolve it.</param>
    /// <param name="audible">Whether a quiet run keeps the real audio driver, so leaving mute out leaves its sound on.</param>
    /// <exception cref="McpException">mute is false, and the run is quiet and not audible.</exception>
    internal bool MuteFor(bool quiet, bool audible) =>
        Mute == false && quiet && !audible ? throw new McpException(UnmuteQuietRefusal) : Mute ?? false;

    /// <summary>Refuses options.audible: true on a run that is not quiet, with an explicit mute: true, or with record.</summary>
    /// <param name="quiet">Whether the run is quiet, as the options and the project's godot-mcp.json resolve it.</param>
    /// <exception cref="McpException">audible is true and the run is not quiet, is muted, or records.</exception>
    internal void CheckAudible(bool quiet)
    {
        if (Audible != true)
        {
            return;
        }

        if (!quiet)
        {
            throw new McpException(AudibleOffQuietRefusal);
        }

        if (Mute == true)
        {
            throw new McpException(AudibleWithMuteRefusal);
        }

        if (Record == true)
        {
            throw new McpException(AudibleRecordingRefusal);
        }
    }

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

/// <summary>How run_editor opens the editor: quiet or visible, the session's name, and whether the project is prepared first.</summary>
internal sealed record EditorOptions(
    [property: Description(
        "Opens the editor quiet: on a hidden desktop, click-through, with the Dummy audio driver, so it takes no focus, shows "
            + "nothing and plays no sound. false opens a normal visible editor window. Default true."
    )]
        bool Quiet = true,
    [property: Description(ProjectTools.NewSessionDescription)] string? Session = null,
    [property: Description(RunOptions.PrepareDescription)] string? Prepare = null
)
{
    /// <summary>Whether prepare asks for the prep: true for auto or when left out, false for never.</summary>
    /// <exception cref="McpException">prepare is anything else.</exception>
    internal bool ShouldPrepare() => RunOptions.ParsePrepare(Prepare);
}

/// <summary>Whether restart_project prepares the project before the relaunch.</summary>
internal sealed record RestartOptions([property: Description(RunOptions.PrepareDescription)] string? Prepare = null)
{
    /// <summary>Whether prepare asks for the prep: true for auto or when left out, false for never.</summary>
    /// <exception cref="McpException">prepare is anything else.</exception>
    internal bool ShouldPrepare() => RunOptions.ParsePrepare(Prepare);
}
