using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>
/// How record_mark start records: a real-time recording's frame rate, length cap and audio track, which a Movie Maker
/// recording's start mark refuses, and where the finished clip is moved, which both take.
/// </summary>
internal sealed record RecordMarkOptions(
    [property: Description(
        "Frames a second the capture takes, 1 to 60; 30 by default. Each frame is stamped with the time it arrived, so a busy "
            + "machine repeats frames rather than shortening the clip."
    )]
        int? Fps = null,
    [property: Description(
        "The longest the recording runs, in seconds, 1 to 600; 600 by default. It ends there on its own, and record_mark stop then returns the clip."
    )]
        int? MaxSeconds = null,
    [property: Description("Record the game's own sound as the clip's audio track, default true; none when the session is muted or quiet.")]
        bool? Audio = null,
    [property: Description(
        "Where the finished clip is moved: a project-relative, res:// or absolute .mp4 path in the project, its checkout when it "
            + "tracks the project, or from a linked worktree the main checkout's .tmp; it is checked now and again when the clip is "
            + "delivered. Without it the clip stays under .godot/godot-mcp/recordings/."
    )]
        string? Path = null,
    [property: Description("Replace an existing file at path; default false, which refuses one.")] bool Overwrite = false
);
