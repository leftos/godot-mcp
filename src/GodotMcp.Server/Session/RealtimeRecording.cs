using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace GodotMcp.Server.Session;

/// <summary>What record_mark start returns for a real-time recording: the .mp4 it will finish as, the frame size, rate and encoder.</summary>
internal sealed record RealtimeStarted(string Path, int Width, int Height, int Fps, string Encoder, string? Warning)
{
    [JsonPropertyOrder(-2)]
    public string Mark { get; } = Recording.StartMark;

    [JsonPropertyOrder(-1)]
    public string Mode { get; } = RealtimeRecording.Mode;
}

/// <summary>A finished real-time clip: its file, its length from the first frame to the capture's end, and its frames.</summary>
internal sealed record RealtimeClip(string Path, double Seconds, long Frames, double AverageFps, int Width, int Height);

/// <summary>What record_mark stop returns for a real-time recording.</summary>
internal sealed record RealtimeStopped(RealtimeClip Clip, string? Warning)
{
    [JsonPropertyOrder(-2)]
    public string Mark { get; } = Recording.StopMark;

    [JsonPropertyOrder(-1)]
    public string Mode { get; } = RealtimeRecording.Mode;
}

/// <summary>How a real-time recording ended: its clip, or why there is none.</summary>
internal sealed record RealtimeOutcome(RealtimeStopped? Stopped, string? Error);

/// <summary>
/// What a real-time recording captures with: ffmpeg, the capture helper, the window and its client area's crop, and the
/// start's warning.
/// </summary>
internal sealed record RealtimeTarget(string Ffmpeg, string Helper, long Hwnd, PixelRect Crop, string? Warning);

/// <summary>A real-time recording's start: the session, its project folder, the target, the rate, the cap and when it was asked for.</summary>
internal sealed record RealtimeStart(string Session, string ProjectDir, RealtimeTarget Target, int Fps, int MaxSeconds, DateTimeOffset RequestedAt);

/// <summary>
/// A real-time recording's files under <c>&lt;project&gt;/.godot/godot-mcp/recordings/</c>, all named from one stem,
/// <c>&lt;UTC yyyyMMdd-HHmmss-fff&gt;-&lt;session&gt;-realtime</c>: the Matroska file it records into and the .mp4 it is remuxed
/// to, the deadline file the helper reads beside them, the helper's and ffmpeg's logs (<c>-capture.log</c>,
/// <c>-ffmpeg.log</c>), and this start's encoder probe log (<c>-probe-&lt;guid&gt;.log</c>).
/// </summary>
internal sealed record RealtimePaths(string Mkv, string Mp4, string Deadline, string CaptureLog, string FfmpegLog, string ProbeLog)
{
    public string Folder => System.IO.Path.GetDirectoryName(Mkv)!;

    /// <summary>The deadline file's temporary, which a rewrite of it moves over it.</summary>
    public string DeadlineTemporary => Deadline + ".tmp";

    /// <summary>The files a start that does not go ahead deletes: the deadline file, its temporary and the .mkv; the logs stay.</summary>
    public IEnumerable<string> StartFiles => [Deadline, DeadlineTemporary, Mkv];

    public static RealtimePaths For(string projectDir, string session, DateTimeOffset utcNow)
    {
        string folder = System.IO.Path.Combine(projectDir, ".godot", "godot-mcp", "recordings");
        string stem = System.IO.Path.Combine(
            folder,
            $"{utcNow.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}-{session}-realtime"
        );
        return new RealtimePaths(
            stem + ".mkv",
            stem + ".mp4",
            stem + ".deadline",
            stem + "-capture.log",
            stem + "-ffmpeg.log",
            $"{stem}-probe-{Guid.NewGuid():N}.log"
        );
    }
}

/// <summary>
/// A real-time recording of a game's window: the capture helper's raw frames copied into ffmpeg, which encodes them into a
/// Matroska file stamped by the wall clock, until the deadline file the helper reads passes, the window closes or resizes, or
/// the game quits. One task owns the end however it comes: ffmpeg finalises the file, it is remuxed to an .mp4 and the
/// outcome kept, so a stop only moves the deadline to now and waits for it, and no process outlives the recording.
/// </summary>
internal sealed class RealtimeRecording
{
    public const string Mode = "realtime";

    /// <summary>How long a start waits for the first frame, in load-adjusted time; the helper's first deadline allows it too.</summary>
    public static readonly TimeSpan FirstFrameAllowance = TimeSpan.FromSeconds(10);

    /// <summary>The helper's exit status when the window changed size: the clip ends there and is kept.</summary>
    private const int ResizeExit = 3;

    /// <summary>The share of the asked rate below which the clip is warned to repeat frames.</summary>
    private const double LowRateShare = 0.9;

    private const int LastLineCount = 3;

    /// <summary>How long past their deadline the helper and ffmpeg may run before their ceiling kills them.</summary>
    private static readonly TimeSpan FinishMargin = TimeSpan.FromSeconds(60);

    /// <summary>How long the helper's probe of the window, which gives the frame size, may take.</summary>
    private static readonly TimeSpan HelperProbeCeiling = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MinimumRemuxCeiling = TimeSpan.FromSeconds(60);

    private readonly RealtimeEnvironment _environment;
    private readonly RealtimeStart _start;
    private readonly RealtimePipeline _pipeline;
    private readonly long _frameBytes;
    private readonly Lock _lock = new();
    private bool _helperGone;

    // Whether the server killed the helper itself, when the deadline could not be moved, so its exit is no failure; under _lock.
    private bool _helperKilled;
    private string? _endNote;

    private RealtimeRecording(StartContext context, RealtimePipeline pipeline, RealtimeStarted started)
    {
        _environment = context.Environment;
        _start = context.Start;
        _frameBytes = context.FrameBytes;
        _pipeline = pipeline;
        Paths = context.Paths;
        Started = started;
        Finished = FinishAsync();
    }

    public RealtimePaths Paths { get; }

    /// <summary>What the start returned.</summary>
    public RealtimeStarted Started { get; }

    /// <summary>When the start was asked for.</summary>
    public DateTimeOffset StartedAt => _start.RequestedAt;

    /// <summary>Whether the capture, the encode or the remux is still going on.</summary>
    public bool IsRunning => !Finished.IsCompleted;

    /// <summary>The recording's end, however it came; it never throws.</summary>
    public Task<RealtimeOutcome> Finished { get; }

    /// <summary>
    /// The capture's target for the game window <paramref name="hwnd"/>: ffmpeg and the helper found, the window read (so a
    /// window that has closed is refused as gone), top-level and not minimized, its client area's crop, and a warning when the
    /// client area lies partly off the desktop.
    /// </summary>
    /// <exception cref="SessionException">ffmpeg or the helper is missing, or the window is gone, unreadable, embedded or minimized.</exception>
    public static RealtimeTarget Locate(RealtimeEnvironment environment, string session, long hwnd)
    {
        string ffmpeg = FindFfmpeg(environment.FindFfmpeg());
        string helper =
            environment.FindCaptureHelper()
            ?? throw new SessionException(
                "A real-time recording needs Windows 10 1903 or later and capture/godot-mcp-capture.exe beside the server "
                    + $"({environment.ExpectedCaptureHelper}); reinstall godot-mcp."
            );
        WindowReading reading = environment.ReadWindow(hwnd);
        if (!environment.IsTopLevel(hwnd))
        {
            throw new SessionException(
                $"The game's window in session '{session}' is embedded in another window, so a real-time recording cannot capture it "
                    + "alone; run the game in its own window."
            );
        }

        PixelRect crop = WindowRect.Crop(reading);
        PixelRect client = new(reading.ClientOrigin.X, reading.ClientOrigin.Y, reading.ClientSize.Width, reading.ClientSize.Height);
        return new RealtimeTarget(ffmpeg, helper, hwnd, crop, WindowRect.DescribeOffDesktop(client, reading.Desktop));
    }

    /// <summary>
    /// Starts the recording and returns once ffmpeg has opened its encoder on the first frame: the helper's probe gives the
    /// frame size, the encoder is chosen, then the deadline file is written, ffmpeg and the helper started and the encoder's
    /// opening waited for. An encoder that fails at its start is marked unusable and followed by the next usable one in
    /// <see cref="RealtimeEncode.Preference"/> order. A start that does not go ahead, refused, failed or cancelled, deletes the
    /// deadline file and the .mkv and keeps its logs.
    /// </summary>
    /// <exception cref="SessionException">
    /// The recordings folder cannot be made, the probe refused the window, ffmpeg cannot be run or no encoder works, or the
    /// encoder did not open.
    /// </exception>
    /// <exception cref="OperationCanceledException">The start was cancelled; nothing is left running.</exception>
    public static async Task<RealtimeRecording> StartAsync(RealtimeEnvironment environment, RealtimeStart start, CancellationToken cancellationToken)
    {
        var paths = RealtimePaths.For(start.ProjectDir, start.Session, environment.Time.GetUtcNow());
        CreateFolder(paths.Folder);
        try
        {
            return await StartInFolderAsync(environment, start, paths, cancellationToken);
        }
        catch (SessionException e)
        {
            string? left = DeleteFiles(paths.StartFiles);
            throw left is null ? e : new SessionException($"{e.Message} {left}", e);
        }
        catch
        {
            // A cancelled start answers no one, so a file it cannot delete goes unreported, as the cancel's own exception goes on.
            DeleteFiles(paths.StartFiles);
            throw;
        }
    }

    /// <summary>Ends the capture now, if it still runs, and waits for the clip.</summary>
    /// <exception cref="SessionException">The capture, the encode or the remux failed; the message names the files kept.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled; the recording still finishes on its own.</exception>
    public async Task<RealtimeStopped> StopAsync(CancellationToken cancellationToken)
    {
        await EndCaptureAsync();
        RealtimeOutcome outcome = await Finished.WaitAsync(cancellationToken);
        return outcome.Stopped ?? throw new SessionException(outcome.Error ?? "The real-time recording ended without a clip.");
    }

    /// <summary>
    /// Moves the deadline to now, so the helper ends within its next read of it, unless it has already ended. A deadline that
    /// cannot be written kills the helper instead, which the clip's warning says; the clip is still finished and remuxed.
    /// </summary>
    public async Task EndCaptureAsync()
    {
        lock (_lock)
        {
            if (_helperGone || _helperKilled)
            {
                return;
            }

            try
            {
                WriteDeadline(Paths.Deadline, _environment.Time.GetUtcNow());
                return;
            }
            catch (SessionException e)
            {
                _helperKilled = true;
                _endNote = $"{e.Message} The capture helper was killed instead, so the clip ends there.";
            }
        }

        await _pipeline.KillHelperAsync();
    }

    /// <summary>The last lines of a log, joined by " | ", for a message; what went wrong when it cannot be read.</summary>
    internal static string LastLines(string logPath)
    {
        try
        {
            using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.UTF8);
            string[] lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length == 0 ? "(it wrote nothing)" : string.Join(" | ", lines[^Math.Min(LastLineCount, lines.Length)..]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"(its log could not be read: {e.Message})";
        }
    }

    /// <summary>
    /// Writes the deadline beside its file and moves it over, so the helper never reads half a line; a temporary that cannot
    /// be moved is deleted.
    /// </summary>
    /// <exception cref="SessionException">The file could not be written.</exception>
    private static void WriteDeadline(string path, DateTimeOffset end)
    {
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, end.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) + "\n");
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            string written = $"The real-time recording's deadline file {path} could not be written: {e.Message}";
            throw new SessionException(Join(" ", [written, DeleteFiles([temporary])])!, e);
        }
    }

    /// <summary>Creates the recordings folder.</summary>
    /// <exception cref="SessionException">It could not be created.</exception>
    private static void CreateFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SessionException(
                $"The real-time recordings folder {folder} could not be created: {e.Message} Check that the project folder is writable.",
                e
            );
        }
    }

    /// <summary>
    /// The start in its recordings folder: the helper's probe gives the frame size, which must be the crop's, the encoder is
    /// chosen, then the encode and the capture are started.
    /// </summary>
    private static async Task<RealtimeRecording> StartInFolderAsync(
        RealtimeEnvironment environment,
        RealtimeStart start,
        RealtimePaths paths,
        CancellationToken cancellationToken
    )
    {
        RealtimeTarget target = start.Target;
        (int width, int height) = await ProbeSizeAsync(environment, target, paths.CaptureLog, cancellationToken);
        (int cropWidth, int cropHeight) = WindowRect.OutputSize(target.Crop);
        if ((width, height) != (cropWidth, cropHeight))
        {
            throw new SessionException(
                FormattableString.Invariant(
                    $"The capture helper measured the game's window at {width}x{height}, but its client area read {cropWidth}x{cropHeight}; "
                ) + "the window may have changed size as the recording started. record_mark start again."
            );
        }

        string chosen = await RunEncoderProbesAsync(
            target.Ffmpeg,
            paths.ProbeLog,
            () => environment.Encoders.ChooseAsync(target.Ffmpeg, paths.ProbeLog, target.Crop, environment.Run, cancellationToken)
        );
        StartContext context = new(environment, start, paths, (long)width * height * 4);
        (RealtimePipeline pipeline, string encoder) = await StartWithFallbackAsync(context, chosen, cancellationToken);
        return new RealtimeRecording(context, pipeline, new RealtimeStarted(paths.Mp4, width, height, start.Fps, encoder, target.Warning));
    }

    /// <summary>Runs encoder probes, an ffmpeg that cannot be run or a probe log that cannot be opened named as the start's refusal.</summary>
    /// <exception cref="SessionException">ffmpeg could not be run, or no encoder works.</exception>
    private static async Task<T> RunEncoderProbesAsync<T>(string ffmpeg, string logPath, Func<Task<T>> probes)
    {
        try
        {
            return await probes();
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new SessionException(
                $"ffmpeg at {ffmpeg} could not be run to test its encoders (log {logPath}): {e.Message} Point FFMPEG_PATH at a working "
                    + "ffmpeg.exe, or install it with winget install Gyan.FFmpeg.",
                e
            );
        }
    }

    private static string FindFfmpeg(FfmpegLookup lookup)
    {
        if (lookup.Path is { } path)
        {
            return path;
        }

        throw new SessionException(
            lookup.ConfiguredButMissing is { } missing
                ? $"A real-time recording needs ffmpeg, and {Installation.FfmpegPathVariable} is '{missing}', which does not exist: point it at "
                    + "ffmpeg.exe, or install ffmpeg with winget install Gyan.FFmpeg."
                : "A real-time recording needs ffmpeg: set FFMPEG_PATH to ffmpeg.exe, or install it with winget install Gyan.FFmpeg."
        );
    }

    /// <summary>The frame size the helper writes for the target's crop, from its probe, which refuses a window it cannot capture.</summary>
    /// <exception cref="SessionException">The probe refused the window or said no size; the message ends with its last line.</exception>
    private static async Task<(int Width, int Height)> ProbeSizeAsync(
        RealtimeEnvironment environment,
        RealtimeTarget target,
        string logPath,
        CancellationToken cancellationToken
    )
    {
        ToolProcessRequest request = new(
            target.Helper,
            [.. WindowArguments(target), "--probe"],
            Path.GetDirectoryName(logPath)!,
            logPath,
            HelperProbeCeiling
        )
        {
            PipeStandardOutput = true,
            FlushEachLine = true,
            Clock = environment.Clock,
        };
        using IRealtimeProcess probe = RealtimePipeline.StartProcess(environment, request, "The capture helper");
        Task<ToolProcessResult> exited = probe.WaitAsync(cancellationToken);
        string output;
        using (StreamReader reader = new(probe.StandardOutput, Encoding.UTF8))
        {
            // Read to the end whatever happens: a cancel kills the probe, which ends its output, and the wait then throws.
            output = await reader.ReadToEndAsync(CancellationToken.None);
        }

        ToolProcessResult result = await exited;
        if (result is { WasKilled: false, ExitCode: 0 } && TryParseSize(output, out (int Width, int Height) size))
        {
            return size;
        }

        string said = result.WasKilled ? $"its probe did not finish {result.KillPhrase}" : LastLines(logPath).Split(" | ")[^1];
        throw new SessionException($"The capture helper cannot record the game's window: {said} (see {logPath}).");
    }

    private static bool TryParseSize(string output, out (int Width, int Height) size)
    {
        string[] sides = output.Trim().Split('x');
        size = default;
        if (
            sides.Length != 2
            || !int.TryParse(sides[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(sides[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height)
        )
        {
            return false;
        }

        size = (width, height);
        return width > 0 && height > 0;
    }

    /// <summary>
    /// Tries the chosen encoder, then each later one in preference order that probes usable, until one gets past its start;
    /// each that fails at its start is marked unusable in the server's probe cache, so later starts skip it.
    /// </summary>
    /// <exception cref="SessionException">
    /// The helper failed, no frame came, ffmpeg could not be run, or every encoder failed at its start.
    /// </exception>
    private static async Task<(RealtimePipeline Pipeline, string Encoder)> StartWithFallbackAsync(
        StartContext context,
        string chosen,
        CancellationToken cancellationToken
    )
    {
        RealtimeTarget target = context.Start.Target;
        EncoderProbeCache encoders = context.Environment.Encoders;
        string probeLog = context.Paths.ProbeLog;
        List<string> failed = [];
        foreach (string encoder in RealtimeEncode.Preference(target.Crop).SkipWhile(candidate => candidate != chosen))
        {
            bool usable =
                failed.Count == 0
                || await RunEncoderProbesAsync(
                    target.Ffmpeg,
                    probeLog,
                    () => encoders.IsUsableAsync(target.Ffmpeg, probeLog, encoder, context.Environment.Run, cancellationToken)
                );
            if (!usable)
            {
                continue;
            }

            if (await TryStartAsync(context, encoder, append: failed.Count > 0, cancellationToken) is { } pipeline)
            {
                return (pipeline, encoder);
            }

            encoders.MarkUnusable(target.Ffmpeg, encoder, context.Paths.FfmpegLog);
            failed.Add(encoder);
        }

        throw new SessionException(
            $"ffmpeg ended before the first frame with every encoder it could run ({string.Join(", ", failed)}), so the recording did "
                + $"not start: {LastLines(context.Paths.FfmpegLog)}. See {context.Paths.FfmpegLog}."
        );
    }

    /// <summary>
    /// One start with <paramref name="encoder"/>: the deadline at now plus the first-frame allowance plus the cap, ffmpeg, then
    /// the helper; once ffmpeg has opened its encoder on the first frame, the deadline moves to the first bytes' time plus the
    /// cap. ffmpeg ending, or refusing frames, before that is the encoder failing at its start.
    /// </summary>
    /// <returns>The running pipeline; null when the encoder failed at its start, both processes gone.</returns>
    /// <exception cref="SessionException">The helper ended or no frame came; both processes are gone.</exception>
    private static async Task<RealtimePipeline?> TryStartAsync(StartContext context, string encoder, bool append, CancellationToken cancellationToken)
    {
        var cap = TimeSpan.FromSeconds(context.Start.MaxSeconds);
        WriteDeadline(context.Paths.Deadline, context.Environment.Time.GetUtcNow() + FirstFrameAllowance + cap);
        RealtimePipeline pipeline = await RealtimePipeline.StartAsync(
            context.Environment,
            EncodeRequest(context, encoder, append),
            CaptureRequest(context)
        );
        try
        {
            FirstFrameEnd end = await WaitForEncoderAsync(context.Environment.Clock, pipeline, cancellationToken);
            if (end == FirstFrameEnd.Opened)
            {
                WriteDeadline(context.Paths.Deadline, pipeline.Pump.FirstBytesUtc + cap);
                return pipeline;
            }

            (ToolProcessResult helper, _) = await pipeline.AbandonAsync();
            return end == FirstFrameEnd.EncoderFailed
                ? null
                : throw new SessionException(DescribeNoStart(end, helper, context.Paths, framesCame: pipeline.Pump.Bytes > 0));
        }
        catch (Exception e) when (e is OperationCanceledException or SessionException)
        {
            await pipeline.AbandonAsync();
            throw;
        }
    }

    /// <summary>Waits for ffmpeg to open its encoder, the end of either process, or the allowance, whichever comes first.</summary>
    /// <exception cref="OperationCanceledException">The start was cancelled.</exception>
    private static async Task<FirstFrameEnd> WaitForEncoderAsync(LoadClock clock, RealtimePipeline pipeline, CancellationToken cancellationToken)
    {
        using LoadDeadline allowance = clock.Start(FirstFrameAllowance, cancellationToken);
        var expired = Task.Delay(Timeout.InfiniteTimeSpan, allowance.Token);
        await Task.WhenAny(pipeline.EncoderOpened, pipeline.FfmpegExit, pipeline.HelperExit, pipeline.Copy, expired);
        cancellationToken.ThrowIfCancellationRequested();
        return pipeline.Classify();
    }

    /// <summary>Why a start did not go ahead: the helper ended first, or nothing opened the encoder within the allowance.</summary>
    /// <param name="framesCame">Whether frames went into ffmpeg, so its encoder, not the helper, kept the start waiting.</param>
    private static string DescribeNoStart(FirstFrameEnd end, ToolProcessResult helper, RealtimePaths paths, bool framesCame)
    {
        if (end == FirstFrameEnd.HelperEnded)
        {
            return $"The capture helper {Describe(helper)} before its first frame, so the recording did not start: "
                + $"{LastLines(paths.CaptureLog)}. See {paths.CaptureLog}.";
        }

        string waited =
            $"within {FirstFrameAllowance.TotalSeconds:0} s of load-adjusted time, so the recording did not start; the helper and "
            + "ffmpeg were stopped.";
        string said =
            $"The helper said: {LastLines(paths.CaptureLog)}; ffmpeg said: {LastLines(paths.FfmpegLog)}. See {paths.CaptureLog} and "
            + $"{paths.FfmpegLog}.";
        return framesCame ? $"ffmpeg did not open its encoder {waited} {said}" : $"The capture helper sent no frame {waited} {said}";
    }

    private static string Describe(ToolProcessResult result) =>
        result.WasKilled ? $"did not finish {result.KillPhrase}" : FormattableString.Invariant($"exited with code {result.ExitCode}");

    /// <summary>ffmpeg's encode, its progress reports on stdout for the start to see the encoder open, its errors to its log.</summary>
    private static ToolProcessRequest EncodeRequest(StartContext context, string encoder, bool append) =>
        new(
            context.Start.Target.Ffmpeg,
            [
                .. RealtimePipeline.ProgressArguments,
                .. RealtimeEncode.EncodeArguments(encoder, context.Start.Target.Crop, context.Start.Fps, context.Paths.Mkv),
            ],
            context.Paths.Folder,
            context.Paths.FfmpegLog,
            RunCeiling(context.Start)
        )
        {
            KeepStandardInput = true,
            PipeStandardOutput = true,
            AppendToLog = append,
            FlushEachLine = true,
            Clock = context.Environment.Clock,
        };

    private static ToolProcessRequest CaptureRequest(StartContext context) =>
        new(
            context.Start.Target.Helper,
            [
                .. WindowArguments(context.Start.Target),
                "--fps",
                context.Start.Fps.ToString(CultureInfo.InvariantCulture),
                "--until-file",
                context.Paths.Deadline,
            ],
            context.Paths.Folder,
            context.Paths.CaptureLog,
            RunCeiling(context.Start)
        )
        {
            PipeStandardOutput = true,
            AppendToLog = true,
            FlushEachLine = true,
            Clock = context.Environment.Clock,
        };

    private static string[] WindowArguments(RealtimeTarget target) =>
        [
            "--hwnd",
            target.Hwnd.ToString(CultureInfo.InvariantCulture),
            "--crop",
            FormattableString.Invariant($"{target.Crop.X},{target.Crop.Y},{target.Crop.Width},{target.Crop.Height}"),
        ];

    /// <summary>How long the helper and ffmpeg may run: the first-frame allowance, the cap and a margin to finish in.</summary>
    private static TimeSpan RunCeiling(RealtimeStart start) => FirstFrameAllowance + TimeSpan.FromSeconds(start.MaxSeconds) + FinishMargin;

    /// <summary>How long the remux may take: 60 s, or 1 s per 10 s of clip when longer.</summary>
    private static TimeSpan RemuxCeiling(TimeSpan clip) => clip / 10 > MinimumRemuxCeiling ? clip / 10 : MinimumRemuxCeiling;

    /// <summary>Deletes each file that exists; null when all are gone, else what could not be deleted.</summary>
    private static string? DeleteFiles(IEnumerable<string> files)
    {
        List<string> left = [];
        foreach (string file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                left.Add($"{file} could not be deleted: {e.Message}");
            }
        }

        return left.Count == 0 ? null : string.Join(" ", left);
    }

    /// <summary>The end, however it came: both processes waited for and let go of, then the clip judged and remuxed.</summary>
    private async Task<RealtimeOutcome> FinishAsync()
    {
        (ToolProcessResult helper, ToolProcessResult ffmpeg) = await _pipeline.FinishAsync();
        TimeSpan length = _environment.Time.GetElapsedTime(_pipeline.Pump.FirstBytesTimestamp, await _pipeline.HelperEndedAt);
        string? deadlineLeft;
        lock (_lock)
        {
            _helperGone = true;
            deadlineLeft = DeleteFiles([Paths.Deadline]);
        }

        if (Failure(helper, ffmpeg) is { } failure)
        {
            return new RealtimeOutcome(null, Join(" ", [failure, _endNote, deadlineLeft]));
        }

        return await RemuxAsync(helper, length, deadlineLeft);
    }

    /// <summary>
    /// Why the capture or the encode failed, ffmpeg first since a helper whose reader died follows it; null when neither did.
    /// A helper the server killed itself, since its deadline could not be moved, ended as asked.
    /// </summary>
    private string? Failure(ToolProcessResult helper, ToolProcessResult ffmpeg)
    {
        if (ffmpeg.WasKilled || ffmpeg.ExitCode != 0)
        {
            return $"ffmpeg {Describe(ffmpeg)} while it encoded the recording: {LastLines(Paths.FfmpegLog)}. The file is kept as ffmpeg left "
                + $"it at {Paths.Mkv}; see {Paths.FfmpegLog}.";
        }

        bool killedOnPurpose;
        lock (_lock)
        {
            killedOnPurpose = _helperKilled;
        }

        if (!killedOnPurpose && (helper.WasKilled || helper.ExitCode is not (0 or ResizeExit)))
        {
            return $"The capture helper {Describe(helper)} during the recording: {LastLines(Paths.CaptureLog)}. The frames recorded before it "
                + $"are kept in {Paths.Mkv}; see {Paths.CaptureLog}.";
        }

        return null;
    }

    /// <summary>Remuxes the finished .mkv to the .mp4 and deletes the .mkv; a failed remux keeps the .mkv.</summary>
    private async Task<RealtimeOutcome> RemuxAsync(ToolProcessResult helper, TimeSpan length, string? deadlineLeft)
    {
        ToolProcessRequest request = new(
            _start.Target.Ffmpeg,
            RealtimeEncode.RemuxArguments(Paths.Mkv, Paths.Mp4, Started.Encoder == RealtimeEncode.HevcNvenc),
            Paths.Folder,
            Paths.FfmpegLog,
            RemuxCeiling(length)
        )
        {
            AppendToLog = true,
            Clock = _environment.Clock,
        };
        string? failed = await RunRemuxAsync(request);
        if (failed is not null)
        {
            return new RealtimeOutcome(null, Join(" ", [failed, DeleteFiles([Paths.Mp4]), _endNote, deadlineLeft]));
        }

        RealtimeClip clip = Clip(length);
        return new RealtimeOutcome(new RealtimeStopped(clip, Warnings(helper, clip, [_endNote, deadlineLeft, DeleteFiles([Paths.Mkv])])), null);
    }

    /// <returns>Why the remux did not make the .mp4; null when it did.</returns>
    private async Task<string?> RunRemuxAsync(ToolProcessRequest request)
    {
        try
        {
            ToolProcessResult result = await _environment.Run(request, CancellationToken.None);
            return result is { WasKilled: false, ExitCode: 0 }
                ? null
                : $"The remux of {Paths.Mkv} to {Paths.Mp4} {Describe(result)}; the .mkv is kept. See {Paths.FfmpegLog}.";
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return $"ffmpeg could not be run from {request.FileName} for the remux of {Paths.Mkv}: {e.Message}; the .mkv is kept.";
        }
    }

    private RealtimeClip Clip(TimeSpan length)
    {
        long frames = _pipeline.Pump.Bytes / _frameBytes;
        double seconds = length.TotalSeconds;
        double averageFps = seconds > 0 ? frames / seconds : 0;
        return new RealtimeClip(Paths.Mp4, Math.Round(seconds, 3), frames, Math.Round(averageFps, 1), Started.Width, Started.Height);
    }

    private string? Warnings(ToolProcessResult helper, RealtimeClip clip, IEnumerable<string?> notes)
    {
        List<string?> warnings = [];
        if (helper.ExitCode == ResizeExit)
        {
            warnings.Add(FormattableString.Invariant($"the window changed size at {clip.Seconds:0.0} s; the clip ends there"));
        }

        if (clip.AverageFps < LowRateShare * _start.Fps)
        {
            warnings.Add(
                FormattableString.Invariant(
                    $"the capture got {clip.AverageFps:0.0} fps of {_start.Fps}: the machine was busy, so the clip repeats frames"
                )
            );
        }

        return Join("; ", [.. warnings, .. notes]);
    }

    private static string? Join(string separator, IEnumerable<string?> parts)
    {
        string[] present = [.. parts.OfType<string>()];
        return present.Length == 0 ? null : string.Join(separator, present);
    }

    /// <summary>One start's environment, request, files and frame size in bytes.</summary>
    private sealed record StartContext(RealtimeEnvironment Environment, RealtimeStart Start, RealtimePaths Paths, long FrameBytes);
}
