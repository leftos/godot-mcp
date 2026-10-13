namespace GodotMcp.Server.Session;

/// <summary>
/// A real-time recording's audio track: the game's own process tree through process loopback, read by ffmpeg from a named
/// pipe the capture helper makes. A track is recorded only when one is asked for, the session renders sound and the game's
/// bridge sent its process id; otherwise the clip has none and, when one was asked for, a warning says why.
/// </summary>
internal static class RealtimeAudio
{
    /// <summary>What the capture helper starts each of its stderr lines with.</summary>
    private const string HelperPrefix = "godot-mcp-capture: ";

    /// <summary>Whether a start records the game's sound, from the start's option, the session's mute and the hello's pid.</summary>
    /// <param name="session">The session's name, for the warning.</param>
    /// <param name="requested">options.audio.</param>
    /// <param name="muted">Whether the session renders no sound: options.mute, or an attach's mute.</param>
    /// <param name="gameProcessId">The game's process id from its hello; null when it sent none.</param>
    /// <returns>The process whose sound to record, null for no track; and the warning when a track was asked for and is not recorded.</returns>
    public static (int? AudioPid, string? Warning) Choose(string session, bool requested, bool muted, int? gameProcessId)
    {
        if (!requested)
        {
            return (null, null);
        }

        if (muted)
        {
            return (null, $"no audio: session '{session}' renders no sound (options.mute or a quiet run), so the clip has no audio track");
        }

        return gameProcessId is int pid ? (pid, null) : (null, "no audio: the game's bridge sent no process id, so the clip has no audio track");
    }

    /// <summary>
    /// The warning for a track asked for whose process-loopback audio could not start: the helper's last line, which already
    /// says what could not start, quoted bare without its "godot-mcp-capture: " prefix and its closing periods.
    /// </summary>
    public static string ActivationFailed(string helperLine)
    {
        string said = helperLine.StartsWith(HelperPrefix, StringComparison.Ordinal) ? helperLine[HelperPrefix.Length..] : helperLine;
        return $"no audio: {said.TrimEnd('.')}, so the clip has no audio track";
    }

    /// <summary>A fresh name for the pipe the helper writes the audio to, one per helper started.</summary>
    public static string NewPipeName() => $"godot-mcp-audio-{Guid.NewGuid():N}";

    /// <summary>The path ffmpeg opens the pipe named <paramref name="pipeName"/> by.</summary>
    public static string PipePath(string pipeName) => $@"\\.\pipe\{pipeName}";
}
