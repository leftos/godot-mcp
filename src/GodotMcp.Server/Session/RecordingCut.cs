using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// Cuts a finished recording into its marked clips with ffmpeg, each encoded to H.264 and AAC in an .mp4; with the
/// recording's <see cref="Recording.DropIdle"/>, frames identical to the one before are dropped and so is the audio. A clip
/// whose encode fails is cut by stream copy to an .avi instead: every MJPEG frame is a keyframe and the audio is PCM, so a copy
/// cuts at any frame. The full movie is deleted once every clip is cut; without marks it is the result. A missing ffmpeg or
/// a failed copy keeps the full movie and is reported in the result, never thrown: the run's stop has already succeeded.
/// </summary>
internal static class RecordingCut
{
    public const string LogFileName = "ffmpeg.log";

    public const string MissingFfmpeg =
        "ffmpeg was not found (FFMPEG_PATH is not set and it is not on PATH), so the recording was not cut; the full file is kept. "
        + "Install it with: winget install Gyan.FFmpeg";

    /// <summary>What a recording whose game was killed reports: its movie was never finalised, so it is not cut.</summary>
    public const string KilledMovie = "The game was killed before it quit, so the movie was not finalised (it has no duration); the file is kept.";

    /// <summary>The least time one clip's cut may take.</summary>
    public static readonly TimeSpan MinimumCeiling = TimeSpan.FromSeconds(120);

    /// <summary>How many seconds a cut may take for each second of its clip.</summary>
    private const int CeilingSecondsPerClipSecond = 3;

    /// <summary>yuv420p needs an even width and height; a window of odd size loses its last row or column.</summary>
    private const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    /// <summary>Drops frames identical to the one before, then numbers the rest one 1/60 s frame apart.</summary>
    private static readonly string DropIdleFilter = $"mpdecimate,setpts=N/{GodotCommandLine.MovieFramesPerSecond}/TB,{EvenSize}";

    private static readonly CutStep EncodeStep = new("encoding", "", "");
    private static readonly CutStep CopyStep = new("cutting", "; the full file is kept", " The full file is kept.");

    /// <summary>The n-th clip's encoded file (from 1), beside the full movie: <c>&lt;stem&gt;-clip&lt;n&gt;.mp4</c>.</summary>
    public static string ClipPath(string moviePath, int number) => ClipPathWith(moviePath, number, ".mp4");

    /// <summary>
    /// The n-th clip's file when its encode failed and it was copied instead, in the movie's own container:
    /// <c>&lt;stem&gt;-clip&lt;n&gt;.avi</c>.
    /// </summary>
    public static string FallbackClipPath(string moviePath, int number) => ClipPathWith(moviePath, number, Path.GetExtension(moviePath));

    /// <summary>
    /// How long one clip's cut may take: <see cref="MinimumCeiling"/>, or 3 s per second of clip when longer. A clip with no
    /// stop runs to the movie's frame cap.
    /// </summary>
    public static TimeSpan CeilingFor(ClipSpan span)
    {
        long frames = (span.StopFrame ?? GodotCommandLine.MaxMovieFrames) - span.StartFrame;
        var scaled = TimeSpan.FromSeconds(CeilingSecondsPerClipSecond * (double)frames / GodotCommandLine.MovieFramesPerSecond);
        return scaled > MinimumCeiling ? scaled : MinimumCeiling;
    }

    /// <summary>
    /// ffmpeg's arguments for one clip's encode: the frames from the start frame up to, not including, the stop frame (to the
    /// end without one), as H.264 in an .mp4, with AAC audio, or with idle frames and audio dropped. -ss and -to are input
    /// options, so the filters see only the clip's frames: with -accurate_seek, the default when transcoding, the frames
    /// between the seek point and -ss are decoded and discarded (ffmpeg.html, -ss and -accurate_seek), and -to stops reading
    /// the input there. As output options they would trim the filters' output, whose timestamps idle dropping renumbers.
    /// Each boundary sits half a frame before its frame's timestamp (frame / 60 s), so rounding never moves it onto the
    /// neighbouring frame.
    /// </summary>
    public static List<string> EncodeArguments(string moviePath, ClipSpan span, string clipPath, bool dropIdle)
    {
        List<string> arguments = ["-hide_banner", "-nostdin", "-y", "-ss", BoundarySeconds(span.StartFrame)];
        if (span.StopFrame is { } stop)
        {
            arguments.AddRange(["-to", BoundarySeconds(stop)]);
        }

        string[] audio = dropIdle ? ["-an"] : ["-c:a", "aac"];
        arguments.AddRange(["-i", moviePath, "-vf", dropIdle ? DropIdleFilter : EvenSize]);
        arguments.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", .. audio]);
        arguments.AddRange(["-movflags", "+faststart", clipPath]);
        return arguments;
    }

    /// <summary>
    /// ffmpeg's arguments for one clip's copy, the fallback when its encode fails: the streams copied from the start frame up
    /// to, not including, the stop frame (to the end without one). As output options, -ss drops packets before it and -to
    /// stops at it; each sits half a frame before its frame's timestamp, as in <see cref="EncodeArguments"/>.
    /// </summary>
    public static List<string> CopyArguments(string moviePath, ClipSpan span, string clipPath)
    {
        List<string> arguments = ["-hide_banner", "-nostdin", "-y", "-i", moviePath, "-ss", BoundarySeconds(span.StartFrame)];
        if (span.StopFrame is { } stop)
        {
            arguments.AddRange(["-to", BoundarySeconds(stop)]);
        }

        arguments.AddRange(["-c", "copy", clipPath]);
        return arguments;
    }

    /// <summary>
    /// The recording's result once its run has ended: an error when the movie was never written or never finalised (the game
    /// was killed); the full movie when there are no marks; else the clips, cut one by one (<see cref="CutClipsAsync"/>).
    /// </summary>
    public static async Task<RecordingResult> FinishAsync(Recording recording, ILogger logger, CancellationToken cancellationToken)
    {
        if (Unusable(recording.Path, File.Exists(recording.Path), recording.Killed) is { } unusable)
        {
            return unusable;
        }

        if (recording.Clips().Count == 0)
        {
            return new RecordingResult { Path = recording.Path };
        }

        string? error = FindFfmpegError(out string ffmpeg);
        if (error is not null)
        {
            return new RecordingResult { Path = recording.Path, Error = error };
        }

        return await CutClipsAsync(recording, ffmpeg, (request, token) => ToolProcess.RunAsync(request, logger, token), cancellationToken);
    }

    /// <summary>
    /// Cuts every clip with <paramref name="run"/> running ffmpeg, then deletes the full movie. A clip whose encode fails is
    /// copied instead and the failure reported in the result's error; a copy that fails too stops there and keeps the full
    /// movie, with the clips cut so far.
    /// </summary>
    internal static async Task<RecordingResult> CutClipsAsync(
        Recording recording,
        string ffmpeg,
        Func<ToolProcessRequest, CancellationToken, Task<ToolProcessResult>> run,
        CancellationToken cancellationToken
    )
    {
        CutContext context = new(recording, ffmpeg, run);
        List<string> clips = [];
        List<string> notes = [];
        foreach (ClipSpan span in recording.Clips())
        {
            ClipOutcome outcome = await CutClipAsync(context, span, clips.Count + 1, cancellationToken);
            notes.AddRange(outcome.Notes);
            if (outcome.Clip is not { } clip)
            {
                return new RecordingResult
                {
                    Path = recording.Path,
                    Clips = clips.Count > 0 ? clips : null,
                    Error = string.Join(" ", notes),
                };
            }

            clips.Add(clip);
        }

        return DeleteFullMovie(recording.Path, clips, notes);
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

    /// <summary>
    /// Encodes the <paramref name="number"/>-th clip, or copies it when the encode fails.
    /// </summary>
    /// <returns>The clip's file, null when the copy failed too; and what went wrong on the way.</returns>
    private static async Task<ClipOutcome> CutClipAsync(CutContext context, ClipSpan span, int number, CancellationToken cancellationToken)
    {
        Recording recording = context.Recording;
        string encoded = ClipPath(recording.Path, number);
        List<string> encode = EncodeArguments(recording.Path, span, encoded, recording.DropIdle);
        string? encodeError = await RunAsync(context, Request(context, encode, span, append: number > 1), encoded, EncodeStep, cancellationToken);
        if (encodeError is null)
        {
            return new ClipOutcome(encoded, []);
        }

        List<string> notes = [encodeError];
        if (DeletePartial(encoded) is { } partial)
        {
            notes.Add(partial);
        }

        string copied = FallbackClipPath(recording.Path, number);
        List<string> copy = CopyArguments(recording.Path, span, copied);
        string? copyError = await RunAsync(context, Request(context, copy, span, append: true), copied, CopyStep, cancellationToken);
        if (copyError is not null)
        {
            return new ClipOutcome(null, [.. notes, copyError]);
        }

        string idle = recording.DropIdle ? ", its idle frames kept," : "";
        return new ClipOutcome(copied, [.. notes, $"Clip {number} was copied to {copied}{idle} as recorded instead."]);
    }

    private static ToolProcessRequest Request(CutContext context, List<string> arguments, ClipSpan span, bool append) =>
        new(context.Ffmpeg, arguments, Path.GetDirectoryName(context.Recording.Path)!, LogPath(context.Recording.Path), CeilingFor(span))
        {
            AppendToLog = append,
        };

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

    /// <returns>Why ffmpeg did not make <paramref name="clipPath"/>, or null when it did.</returns>
    private static async Task<string?> RunAsync(
        CutContext context,
        ToolProcessRequest request,
        string clipPath,
        CutStep step,
        CancellationToken cancellationToken
    )
    {
        try
        {
            ToolProcessResult result = await context.Run(request, cancellationToken);
            return result switch
            {
                { KilledByCeiling: true } =>
                    $"ffmpeg did not finish {step.Doing} {clipPath} within {request.Ceiling.TotalSeconds:0} s{step.Consequence}. "
                        + $"See {request.LogPath}.",
                { ExitCode: not 0 } =>
                    $"ffmpeg failed (exit code {result.ExitCode}) {step.Doing} {clipPath}{step.Consequence}. See {request.LogPath}.",
                _ => null,
            };
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return $"ffmpeg could not be run from {request.FileName} for {step.Doing} {clipPath}: {e.Message}{step.AfterException}";
        }
    }

    /// <returns>Why a failed encode's partial file could not be deleted, or null when it is gone.</returns>
    private static string? DeletePartial(string clipPath)
    {
        try
        {
            File.Delete(clipPath);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The partial {clipPath} could not be deleted: {e.Message}";
        }
    }

    /// <summary>The ffmpeg log, beside the recordings.</summary>
    private static string LogPath(string moviePath) => Path.Combine(Path.GetDirectoryName(moviePath)!, LogFileName);

    private static RecordingResult DeleteFullMovie(string moviePath, List<string> clips, List<string> notes)
    {
        string? error = notes.Count > 0 ? string.Join(" ", notes) : null;
        try
        {
            File.Delete(moviePath);
            return new RecordingResult { Clips = clips, Error = error };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new RecordingResult
            {
                Path = moviePath,
                Clips = clips,
                Error = string.Join(" ", [.. notes, $"Every clip was cut, but the full file could not be deleted: {e.Message}"]),
            };
        }
    }

    private static string ClipPathWith(string moviePath, int number, string extension) =>
        Path.Combine(Path.GetDirectoryName(moviePath)!, $"{Path.GetFileNameWithoutExtension(moviePath)}-clip{number}{extension}");

    private static string BoundarySeconds(long frame) =>
        Math.Max(0, (frame - 0.5) / GodotCommandLine.MovieFramesPerSecond).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// What a failed step was doing, and what its failure message adds: <see cref="Consequence"/> before the log's path,
    /// <see cref="AfterException"/> after the exception's message.
    /// </summary>
    private sealed record CutStep(string Doing, string Consequence, string AfterException);

    /// <summary>The recording being cut, the ffmpeg that cuts it, and how a cut is run.</summary>
    private sealed record CutContext(Recording Recording, string Ffmpeg, Func<ToolProcessRequest, CancellationToken, Task<ToolProcessResult>> Run);

    /// <summary>One clip's file, null when it could not be cut; and the failures on the way.</summary>
    private sealed record ClipOutcome(string? Clip, IReadOnlyList<string> Notes);
}
