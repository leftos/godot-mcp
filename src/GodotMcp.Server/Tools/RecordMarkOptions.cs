using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How record_mark start records a session's window in real time; a Movie Maker recording's marks take none.</summary>
internal sealed record RecordMarkOptions(
    [property: Description(
        "Frames a second the capture takes, 1 to 60; 30 by default. Each frame is stamped with the time it arrived, so a busy "
            + "machine repeats frames rather than shortening the clip."
    )]
        int Fps = 30,
    [property: Description(
        "The longest the recording runs, in seconds, 1 to 600; 600 by default. It ends there on its own, and record_mark stop then returns the clip."
    )]
        int MaxSeconds = 600
);
