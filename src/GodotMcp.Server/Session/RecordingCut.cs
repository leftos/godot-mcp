using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// Cuts a finished recording into its marked clips with ffmpeg, copying the streams: every MJPEG frame is a keyframe and the
/// audio is PCM, so a stream copy cuts at any frame. The full movie is deleted once every clip is cut; without marks it is the
/// result. A missing ffmpeg or a failed cut keeps the full movie and is reported in the result, never thrown: the run's stop
/// has already succeeded.
/// </summary>
internal static class RecordingCut
{
    public const string LogFileName = "ffmpeg.log";

    public const string MissingFfmpeg =
        "ffmpeg was not found (FFMPEG_PATH is not set and it is not on PATH), so the recording was not cut; the full file is kept. "
        + "Install it with: winget install Gyan.FFmpeg";

    /// <summary>How long one clip's cut may take.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(120);

    /// <summary>The n-th clip's file (from 1), beside the full movie: <c>&lt;stem&gt;-clip&lt;n&gt;.avi</c>.</summary>
    public static string ClipPath(string moviePath, int number) =>
        Path.Combine(Path.GetDirectoryName(moviePath)!, $"{Path.GetFileNameWithoutExtension(moviePath)}-clip{number}{Path.GetExtension(moviePath)}");

    /// <summary>
    /// ffmpeg's arguments for one clip: the streams copied from the start frame up to, not including, the stop frame (to the
    /// end without one). As output options, -ss drops packets before it and -to stops at it; each sits half a frame before
    /// its frame's timestamp (frame / 60 s), so rounding never moves a boundary onto the neighbouring frame.
    /// </summary>
    public static List<string> ClipArguments(string moviePath, ClipSpan span, string clipPath)
    {
        List<string> arguments = ["-hide_banner", "-nostdin", "-y", "-i", moviePath, "-ss", BoundarySeconds(span.StartFrame)];
        if (span.StopFrame is { } stop)
        {
            arguments.AddRange(["-to", BoundarySeconds(stop)]);
        }

        arguments.AddRange(["-c", "copy", clipPath]);
        return arguments;
    }

    /// <summary>What a recording whose game was killed reports: its movie was never finalised, so it is not cut.</summary>
    public const string KilledMovie = "The game was killed before it quit, so the movie was not finalised (it has no duration); the file is kept.";

    /// <summary>
    /// The recording's result once its run has ended: an error when the movie was never written or never finalised (the game
    /// was killed); the full movie when there are no marks; else the clips, cut one by one, and the full movie deleted. A cut
    /// that cannot run or fails stops there and keeps the full movie, with the clips cut so far.
    /// </summary>
    public static async Task<RecordingResult> FinishAsync(Recording recording, ILogger logger, CancellationToken cancellationToken)
    {
        if (Unusable(recording.Path, File.Exists(recording.Path), recording.Killed) is { } unusable)
        {
            return unusable;
        }

        IReadOnlyList<ClipSpan> spans = recording.Clips();
        if (spans.Count == 0)
        {
            return new RecordingResult { Path = recording.Path };
        }

        string? error = FindFfmpegError(out string ffmpeg);
        if (error is not null)
        {
            return new RecordingResult { Path = recording.Path, Error = error };
        }

        List<string> clips = [];
        foreach (ClipSpan span in spans)
        {
            string clip = ClipPath(recording.Path, clips.Count + 1);
            ToolProcessRequest request = new(
                ffmpeg,
                ClipArguments(recording.Path, span, clip),
                Path.GetDirectoryName(recording.Path)!,
                LogPath(recording.Path),
                Ceiling
            )
            {
                AppendToLog = clips.Count > 0,
            };
            error = await CutAsync(request, clip, logger, cancellationToken);
            if (error is not null)
            {
                return new RecordingResult
                {
                    Path = recording.Path,
                    Clips = clips.Count > 0 ? clips : null,
                    Error = error,
                };
            }

            clips.Add(clip);
        }

        return DeleteFullMovie(recording.Path, clips);
    }

    /// <summary>
    /// The result of a movie that cannot be used as it is: one never written has no path, one whose game was killed is kept
    /// unfinalised; null when the movie was written and finalised.
    /// </summary>
    internal static RecordingResult? Unusable(string moviePath, bool exists, bool killed)
    {
        if (!exists)
        {
            return new RecordingResult
            {
                Error = $"The recording {moviePath} was not written (the game may have failed before its first frame), so there is nothing to cut.",
            };
        }

        return killed ? new RecordingResult { Path = moviePath, Error = KilledMovie } : null;
    }

    /// <summary>Why ffmpeg cannot be run; null when it can, with its path.</summary>
    private static string? FindFfmpegError(out string ffmpeg)
    {
        string? found = Installation.FindFfmpeg(out string? configuredButMissing);
        ffmpeg = found ?? string.Empty;
        if (found is not null)
        {
            return null;
        }

        return configuredButMissing is null
            ? MissingFfmpeg
            : $"{Installation.FfmpegPathVariable} is '{configuredButMissing}', which does not exist, so the recording was not cut; the full file "
                + "is kept. Point it at ffmpeg.exe, or install ffmpeg with: winget install Gyan.FFmpeg";
    }

    /// <returns>Why the clip was not cut, or null when it was.</returns>
    private static async Task<string?> CutAsync(ToolProcessRequest request, string clipPath, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            ToolProcessResult result = await ToolProcess.RunAsync(request, logger, cancellationToken);
            return result switch
            {
                { KilledByCeiling: true } =>
                    $"ffmpeg did not cut {clipPath} within {Ceiling.TotalSeconds:0} s; the full file is kept. See {request.LogPath}.",
                { ExitCode: not 0 } =>
                    $"ffmpeg failed (exit code {result.ExitCode}) cutting {clipPath}; the full file is kept. See {request.LogPath}.",
                _ => null,
            };
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return $"ffmpeg could not be run from {request.FileName} to cut {clipPath}: {e.Message} The full file is kept.";
        }
    }

    /// <summary>The ffmpeg log, beside the recordings.</summary>
    private static string LogPath(string moviePath) => Path.Combine(Path.GetDirectoryName(moviePath)!, LogFileName);

    private static RecordingResult DeleteFullMovie(string moviePath, List<string> clips)
    {
        try
        {
            File.Delete(moviePath);
            return new RecordingResult { Clips = clips };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new RecordingResult
            {
                Path = moviePath,
                Clips = clips,
                Error = $"Every clip was cut, but the full file could not be deleted: {e.Message}",
            };
        }
    }

    private static string BoundarySeconds(long frame) =>
        Math.Max(0, (frame - 0.5) / GodotCommandLine.MovieFramesPerSecond).ToString("0.######", CultureInfo.InvariantCulture);
}
