using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// Cuts a finished recording into its marked clips with ffmpeg, each encoded to H.264 and AAC in an .mp4; with the
/// recording's <see cref="Recording.DropIdle"/>, the frames drawn between tool calls are dropped and the frames drawn during
/// a call (<see cref="Recording.CallRanges"/>) are kept with their audio, video and audio cut on the same ranges. A clip
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

    /// <summary>How the dropIdle filter scripts are written: UTF-8 without a byte order mark.</summary>
    private static readonly UTF8Encoding ScriptEncoding = new(encoderShouldEmitUTF8Identifier: false);

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
    /// end without one), as H.264 in an .mp4, with AAC audio. With <paramref name="dropIdle"/>, the video and audio filters
    /// are read from its scripts (ffmpeg's -/vf and -/af), which keep only the frames drawn during calls and their audio. -ss
    /// and -to are input options, so the filters see only the clip's frames, counted from its start frame: with
    /// -accurate_seek, the default when transcoding, the frames between the seek point and -ss are decoded and discarded
    /// (ffmpeg.html, -ss and -accurate_seek), and -to stops reading the input there. As output options they would trim the
    /// filters' output, whose timestamps idle dropping renumbers. Each boundary sits a quarter frame before its frame's timestamp
    /// (frame / 60 s), so the microsecond and timebase rounding never moves it onto the neighbouring frame; the clip's last
    /// frame's audio therefore ends a quarter frame early, at the -to.
    /// </summary>
    public static List<string> EncodeArguments(string moviePath, ClipSpan span, string clipPath, DropIdleScripts? dropIdle)
    {
        List<string> arguments = ["-hide_banner", "-nostdin", "-y", "-ss", BoundarySeconds(span.StartFrame)];
        if (span.StopFrame is { } stop)
        {
            arguments.AddRange(["-to", BoundarySeconds(stop)]);
        }

        string[] filters = dropIdle is null ? ["-vf", EvenSize] : ["-/vf", dropIdle.Video, "-/af", dropIdle.Audio];
        arguments.AddRange(["-i", moviePath, .. filters]);
        arguments.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac"]);
        arguments.AddRange(["-movflags", "+faststart", clipPath]);
        return arguments;
    }

    /// <summary>
    /// The n-th clip's dropIdle filter scripts, beside the full movie: <c>&lt;stem&gt;-clip&lt;n&gt;.vf.txt</c> for the video and
    /// <c>&lt;stem&gt;-clip&lt;n&gt;.af.txt</c> for the audio.
    /// </summary>
    public static DropIdleScripts DropIdleScriptsFor(string moviePath, int number) =>
        new(ClipPathWith(moviePath, number, ".vf.txt"), ClipPathWith(moviePath, number, ".af.txt"));

    /// <summary>
    /// The dropIdle filters for a clip from <paramref name="startFrame"/>: the video keeps the frames of
    /// <paramref name="ranges"/>, counted from the clip's first frame, and numbers them one 1/60 s frame apart; the audio keeps
    /// the same ranges as times and renumbers its samples. Movie Maker writes one audio chunk per frame, and after the seek a
    /// quarter frame before the start frame the chunk of frame f starts at (f - seek) / 60 s, with a piece of the frame before
    /// at 0 s: each audio bound sits midway between two chunks' starts, so rounding never moves it onto a chunk. Without ranges
    /// the start frame alone is kept, so the selection is never empty.
    /// </summary>
    internal static (string Video, string Audio) DropIdleFilters(IReadOnlyList<FrameRange> ranges, long startFrame)
    {
        IReadOnlyList<FrameRange> kept = ranges.Count > 0 ? ranges : [new FrameRange(startFrame, startFrame)];
        double seek = SeekFrames(startFrame);
        string frames = string.Join(
            "+",
            kept.Select(range => string.Create(CultureInfo.InvariantCulture, $@"between(n\,{range.From - startFrame}\,{range.To - startFrame})"))
        );
        string times = string.Join(
            "+",
            kept.Select(range => $@"between(t\,{FrameSeconds(AudioFrom(range.From, startFrame, seek))}\,{FrameSeconds(range.To + 0.5 - seek)})")
        );
        return ($"select={frames},setpts=N/{GodotCommandLine.MovieFramesPerSecond}/TB,{EvenSize}", $"aselect={times},asetpts=N/SR/TB");
    }

    /// <summary>
    /// ffmpeg's arguments for one clip's copy, the fallback when its encode fails: the streams copied from the start frame up
    /// to, not including, the stop frame (to the end without one). As output options, -ss drops packets before it and -to
    /// stops at it; each sits a quarter frame before its frame's timestamp, as in <see cref="EncodeArguments"/>.
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
            return new RecordingResult { Mode = Recording.MovieMode, Path = recording.Path };
        }

        string? error = FindFfmpegError(out string ffmpeg);
        if (error is not null)
        {
            return new RecordingResult
            {
                Mode = Recording.MovieMode,
                Path = recording.Path,
                Error = error,
            };
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
        List<string> warnings = [];
        Dictionary<string, int> delivered = new(StringComparer.OrdinalIgnoreCase);
        foreach (ClipSpan span in recording.Clips())
        {
            ClipOutcome outcome = await CutClipAsync(context, span, clips.Count + 1, cancellationToken);
            notes.AddRange(outcome.Notes);
            if (outcome.Clip is not { } clip)
            {
                return new RecordingResult
                {
                    Mode = Recording.MovieMode,
                    Path = recording.Path,
                    Clips = clips.Count > 0 ? clips : null,
                    Warning = JoinWarnings(warnings),
                    Error = string.Join(" ", notes),
                };
            }

            clips.Add(Place(clip, span, clips.Count + 1, warnings, delivered));
        }

        return DeleteFullMovie(recording.Path, clips, notes, warnings);
    }

    /// <summary>
    /// Where a cut clip ends up: moved to its start mark's destination, if any, or kept beside the movie when it has none, when
    /// it fell back to the movie's own container, when the move fails, or when its destination now reaches a file an earlier
    /// clip in <paramref name="delivered"/> went to, each failure adding to <paramref name="warnings"/>; a delivered clip's
    /// final path joins <paramref name="delivered"/> under its 1-based <paramref name="number"/>.
    /// </summary>
    private static string Place(string clip, ClipSpan span, int number, List<string> warnings, Dictionary<string, int> delivered)
    {
        if (span.Destination is not { } destination)
        {
            return clip;
        }

        if (!clip.EndsWith(ClipDelivery.Extension, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"the clip fell back to .avi, so it is kept at {clip} instead of {destination.FullPath}");
            return clip;
        }

        (string placed, string? final, string? warning) = ClipDelivery.Deliver(clip, destination, span.Overwrite, delivered, allowCopy: true);
        if (final is not null)
        {
            delivered.Add(final, number);
        }

        if (warning is not null)
        {
            warnings.Add(warning);
        }

        return placed;
    }

    private static string? JoinWarnings(List<string> warnings) => warnings.Count > 0 ? string.Join("; ", warnings) : null;

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
                Mode = Recording.MovieMode,
                Error = $"The recording {moviePath} was not written (the game may have failed before its first frame), so there is nothing to cut.",
            };
        }

        return killed
            ? new RecordingResult
            {
                Mode = Recording.MovieMode,
                Path = moviePath,
                Error = KilledMovie,
            }
            : null;
    }

    /// <summary>
    /// Encodes the <paramref name="number"/>-th clip, or copies it when the encode fails. With the recording's
    /// <see cref="Recording.DropIdle"/>, its filter scripts are written before the encode and deleted after it.
    /// </summary>
    /// <returns>The clip's file, null when the copy failed too; and what went wrong on the way.</returns>
    private static async Task<ClipOutcome> CutClipAsync(CutContext context, ClipSpan span, int number, CancellationToken cancellationToken)
    {
        Recording recording = context.Recording;
        string encoded = ClipPath(recording.Path, number);
        DropIdleScripts? scripts = recording.DropIdle ? DropIdleScriptsFor(recording.Path, number) : null;
        string? encodeError;
        List<string> leftovers;
        try
        {
            encodeError = await EncodeAsync(context, span, number, scripts, cancellationToken);
        }
        finally
        {
            leftovers = scripts is null ? [] : DeleteScripts(scripts);
        }

        if (encodeError is null)
        {
            return new ClipOutcome(encoded, leftovers);
        }

        List<string> notes = [encodeError, .. leftovers];
        if (DeleteNote(encoded, "partial") is { } partial)
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

    /// <returns>Why ffmpeg did not encode the <paramref name="number"/>-th clip, or null when it did.</returns>
    private static async Task<string?> EncodeAsync(
        CutContext context,
        ClipSpan span,
        int number,
        DropIdleScripts? scripts,
        CancellationToken cancellationToken
    )
    {
        Recording recording = context.Recording;
        string encoded = ClipPath(recording.Path, number);
        if (scripts is not null)
        {
            (string video, string audio) = DropIdleFilters(recording.CallRanges(span), span.StartFrame);
            try
            {
                await File.WriteAllTextAsync(scripts.Video, video, ScriptEncoding, cancellationToken);
                await File.WriteAllTextAsync(scripts.Audio, audio, ScriptEncoding, cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"The dropIdle filters for {encoded} could not be written: {e.Message}";
            }
        }

        List<string> encode = EncodeArguments(recording.Path, span, encoded, scripts);
        return await RunAsync(context, Request(context, encode, span, append: number > 1), encoded, EncodeStep, cancellationToken);
    }

    /// <returns>Why either of the clip's filter scripts could not be deleted; empty when both are gone.</returns>
    private static List<string> DeleteScripts(DropIdleScripts scripts) =>
        [.. new[] { scripts.Video, scripts.Audio }.Select(script => DeleteNote(script, "filter script")).OfType<string>()];

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
                { Killed: not KillReason.None } => $"ffmpeg did not finish {step.Doing} {clipPath} {result.KillPhrase}{step.Consequence}. "
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

    /// <returns>Why the <paramref name="kind"/> file at <paramref name="path"/> could not be deleted, or null when it is gone.</returns>
    private static string? DeleteNote(string path, string kind)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The {kind} {path} could not be deleted: {e.Message}";
        }
    }

    /// <summary>The ffmpeg log, beside the recordings.</summary>
    private static string LogPath(string moviePath) => Path.Combine(Path.GetDirectoryName(moviePath)!, LogFileName);

    private static RecordingResult DeleteFullMovie(string moviePath, List<string> clips, List<string> notes, List<string> warnings)
    {
        string? error = notes.Count > 0 ? string.Join(" ", notes) : null;
        try
        {
            File.Delete(moviePath);
            return new RecordingResult
            {
                Mode = Recording.MovieMode,
                Clips = clips,
                Warning = JoinWarnings(warnings),
                Error = error,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new RecordingResult
            {
                Mode = Recording.MovieMode,
                Path = moviePath,
                Clips = clips,
                Warning = JoinWarnings(warnings),
                Error = string.Join(" ", [.. notes, $"Every clip was cut, but the full file could not be deleted: {e.Message}"]),
            };
        }
    }

    private static string ClipPathWith(string moviePath, int number, string extension) =>
        Path.Combine(Path.GetDirectoryName(moviePath)!, $"{Path.GetFileNameWithoutExtension(moviePath)}-clip{number}{extension}");

    /// <summary>
    /// A seek or stop boundary a quarter frame before its frame's timestamp, so the microsecond and timebase rounding never
    /// moves it onto the neighbouring frame.
    /// </summary>
    private static string BoundarySeconds(long frame) => FrameSeconds(SeekFrames(frame));

    /// <summary>Where a boundary at <paramref name="frame"/> sits, in frames: a quarter frame before it, never before the movie.</summary>
    private static double SeekFrames(long frame) => Math.Max(0, frame - 0.25);

    /// <summary>
    /// The audio time, in frames after the seek, a range from <paramref name="from"/> starts at: midway between the piece of the
    /// frame before the start frame and the start frame's chunk for a range from the start frame, else midway between the
    /// chunks of the frame before and of <paramref name="from"/>.
    /// </summary>
    private static double AudioFrom(long from, long startFrame, double seek) => from == startFrame ? (startFrame - seek) / 2 : from - 0.5 - seek;

    /// <summary>A movie frame's time in seconds, as ffmpeg reads it: up to six decimals, invariant culture.</summary>
    private static string FrameSeconds(double frame) =>
        (frame / GodotCommandLine.MovieFramesPerSecond).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The files a dropIdle encode reads its video and audio filters from.</summary>
    internal sealed record DropIdleScripts(string Video, string Audio);

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
