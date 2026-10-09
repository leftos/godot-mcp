using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A real-time recording's processes through fakes standing in for the capture helper and ffmpeg: the order things start
/// in, the deadline file the helper reads, the encoder fallback, the end however it comes and the clip's numbers. No window
/// is captured and no ffmpeg runs here.
/// </summary>
public sealed partial class RealtimeRecordingTests : IDisposable
{
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    private readonly FakeRealtime _fake = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _fake.Dispose();

    [Fact]
    public async Task StartRunsFfmpegBeforeTheHelperAndWritesTheDeadlineAheadOfMaxSeconds()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        RealtimeRecording recording = await _fake.StartAsync(maxSeconds: 600);
        DateTimeOffset after = DateTimeOffset.UtcNow;
        await recording.StopAsync(Token);

        List<string> events = [.. _fake.Events];
        Assert.True(
            events.IndexOf("start:ffmpeg:h264_nvenc") < events.IndexOf("start:helper"),
            $"ffmpeg did not start before the helper: {string.Join(", ", events)}"
        );
        DateTimeOffset deadline = _fake.DeadlineAtHelperStart ?? throw new InvalidOperationException("the helper read no deadline");
        TimeSpan ahead = RealtimeRecording.FirstFrameAllowance + TimeSpan.FromSeconds(600);
        Assert.InRange(deadline, before + ahead - Slack, after + ahead + Slack);
    }

    [Fact]
    public async Task TheEncoderIsChosenBeforeTheFirstFrameClockStarts()
    {
        _fake.UnusableEncoders.Add("h264_nvenc");

        RealtimeRecording recording = await _fake.StartAsync();
        await recording.StopAsync(Token);

        List<string> events = [.. _fake.Events];
        Assert.Equal("libx264", recording.Started.Encoder);
        int lastProbe = events.FindLastIndex(entry => entry.StartsWith("run:probe:", StringComparison.Ordinal));
        Assert.True(lastProbe >= 0 && lastProbe < events.IndexOf("start:ffmpeg:libx264"), string.Join(", ", events));
        Assert.False(_fake.DeadlineExistedAtProbe, "the deadline file, which starts the first-frame clock, existed during a probe");
    }

    [Fact]
    public async Task AnEncoderThatFailsAtStartFallsBackToTheNext()
    {
        _fake.EncodersFailingAtStart.Add("h264_nvenc");

        RealtimeRecording recording = await _fake.StartAsync();
        RealtimeStopped stopped = await recording.StopAsync(Token);

        Assert.Equal("libx264", recording.Started.Encoder);
        Assert.Equal(
            ["start:ffmpeg:h264_nvenc", "start:ffmpeg:libx264"],
            _fake.Events.Where(entry => entry.StartsWith("start:ffmpeg", StringComparison.Ordinal))
        );
        Assert.True(File.Exists(stopped.Clip.Path));
        Assert.Contains("OpenEncodeSessionEx failed", File.ReadAllText(recording.Paths.FfmpegLog), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeadlineMovesToFirstFramePlusMaxSecondsOnTheFirstBytes()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        RealtimeRecording recording = await _fake.StartAsync(maxSeconds: 300);
        DateTimeOffset after = DateTimeOffset.UtcNow;
        DateTimeOffset moved = FakeRealtime.ReadDeadline(recording.Paths.Deadline);
        await recording.StopAsync(Token);

        var cap = TimeSpan.FromSeconds(300);
        Assert.InRange(moved, before + cap, after + cap);
        Assert.True(moved < _fake.DeadlineAtHelperStart, "the deadline did not move earlier on the first bytes");
    }

    [Fact]
    public async Task StopMovesTheDeadlineToNowAndRemuxes()
    {
        RealtimeRecording recording = await _fake.StartAsync();
        await Task.Delay(100, Token);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        RealtimeStopped stopped = await recording.StopAsync(Token);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.InRange(_fake.DeadlineAtHelperEnd ?? DateTimeOffset.MaxValue, before - Slack, after);
        ToolProcessRequest remux = Assert.Single(_fake.Runs, run => run.Arguments.Contains("-movflags"));
        Assert.Equal(RealtimeEncode.RemuxArguments(recording.Paths.Mkv, recording.Paths.Mp4, isHevc: false), remux.Arguments);
        Assert.Equal(recording.Paths.Mp4, stopped.Clip.Path);
        Assert.True(File.Exists(recording.Paths.Mp4), "the .mp4 was not written");
        Assert.False(File.Exists(recording.Paths.Mkv), "the .mkv was not deleted after a good remux");
        Assert.False(File.Exists(recording.Paths.Deadline), "the deadline file was left behind");
        Assert.Equal("stop", stopped.Mark);
        Assert.Equal("realtime", stopped.Mode);
    }

    [Fact]
    public async Task TheClipCountsFramesFromBytes()
    {
        RealtimeRecording recording = await _fake.StartAsync();
        await Task.Delay(300, Token);

        RealtimeStopped stopped = await recording.StopAsync(Token);

        Assert.True(stopped.Clip.Frames > 1, $"only {stopped.Clip.Frames} frames");
        Assert.Equal(_fake.HelperFramesWritten, stopped.Clip.Frames);
        Assert.Equal(_fake.FfmpegBytes / (FakeRealtime.Width * FakeRealtime.Height * 4), stopped.Clip.Frames);
        Assert.Equal(FakeRealtime.Width, stopped.Clip.Width);
        Assert.Equal(FakeRealtime.Height, stopped.Clip.Height);
        Assert.Equal(Math.Round(stopped.Clip.Frames / stopped.Clip.Seconds, 1), stopped.Clip.AverageFps, 0.2);
    }

    [Fact]
    public async Task ANaturalEndFinishesTheClipWithoutAStop()
    {
        RealtimeRecording recording = await _fake.StartAsync(maxSeconds: 1);

        RealtimeOutcome outcome = await recording.Finished.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Null(outcome.Error);
        Assert.True(File.Exists(recording.Paths.Mp4), "the .mp4 was not written at the natural end");
        Assert.False(recording.IsRunning);
        RealtimeStopped stopped = await recording.StopAsync(Token);
        Assert.Equal(outcome.Stopped, stopped);
        // Only the lower bound is the cap's: a busy machine may let the helper read its passed deadline late.
        Assert.True(stopped.Clip.Seconds >= 0.8, $"the clip lasts {stopped.Clip.Seconds} s, short of the 1 s cap");
    }

    [Fact]
    public async Task ANonZeroHelperExitIsAnErrorQuotingItsLastLines()
    {
        _fake.HelperFrameLimit = 3;
        _fake.HelperExit = 5;
        _fake.HelperLines = ["capturing", "the game's audio could not be read: the stream was never opened."];
        RealtimeRecording recording = await _fake.StartAsync();
        await recording.Finished.WaitAsync(TimeSpan.FromSeconds(20), Token);

        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => recording.StopAsync(Token));

        Assert.StartsWith("The capture helper exited with code 5 during the recording: ", failed.Message, StringComparison.Ordinal);
        Assert.Contains("the game's audio could not be read: the stream was never opened.", failed.Message, StringComparison.Ordinal);
        Assert.Contains(recording.Paths.CaptureLog, failed.Message, StringComparison.Ordinal);
        Assert.Contains(recording.Paths.Mkv, failed.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(recording.Paths.Mkv), "the .mkv was not kept");
    }

    [Fact]
    public async Task AResizeExitIsAWarningAndKeepsTheClip()
    {
        _fake.FrameInterval = TimeSpan.FromMilliseconds(5);
        _fake.HelperFrameLimit = 5;
        _fake.HelperExit = 3;
        RealtimeRecording recording = await _fake.StartAsync();
        await recording.Finished.WaitAsync(TimeSpan.FromSeconds(20), Token);

        RealtimeStopped stopped = await recording.StopAsync(Token);

        Assert.True(File.Exists(stopped.Clip.Path), "the clip was not kept");
        Assert.Equal(5, stopped.Clip.Frames);
        Assert.Matches(ResizeWarning(), stopped.Warning ?? "");
    }

    [Fact]
    public async Task ALowRateIsWarned()
    {
        _fake.FrameInterval = TimeSpan.FromMilliseconds(100);
        RealtimeRecording recording = await _fake.StartAsync(fps: 60);
        await Task.Delay(600, Token);

        RealtimeStopped stopped = await recording.StopAsync(Token);

        Assert.Matches(LowRateWarning(), stopped.Warning ?? "");
        Assert.True(stopped.Clip.AverageFps < 54, $"{stopped.Clip.AverageFps} fps");
    }

    [Fact]
    public async Task AnFfmpegFailureKeepsTheMkvAndNamesTheLog()
    {
        _fake.FfmpegExit = 1;
        RealtimeRecording recording = await _fake.StartAsync();

        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => recording.StopAsync(Token));

        Assert.StartsWith("ffmpeg exited with code 1 while it encoded the recording: ", failed.Message, StringComparison.Ordinal);
        Assert.Contains("Error writing trailer", failed.Message, StringComparison.Ordinal);
        Assert.Contains(recording.Paths.Mkv, failed.Message, StringComparison.Ordinal);
        Assert.Contains(recording.Paths.FfmpegLog, failed.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(recording.Paths.Mkv), "the .mkv was not kept");
        Assert.DoesNotContain(_fake.Runs, run => run.Arguments.Contains("-movflags"));
    }

    [Fact]
    public async Task ConcurrentProbesUseTheirOwnLogs()
    {
        TaskCompletionSource bothProbing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentBag<string> probeLogs = [];
        _fake.BeforeProbe = async request =>
        {
            probeLogs.Add(request.LogPath);
            if (probeLogs.Count >= 2)
            {
                bothProbing.TrySetResult();
            }

            await bothProbing.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        };

        RealtimeRecording[] recordings = await Task.WhenAll(
            _fake.StartAsync(session: "first", ffmpeg: @"C:\tools\a\ffmpeg.exe"),
            _fake.StartAsync(session: "second", ffmpeg: @"C:\tools\b\ffmpeg.exe")
        );
        foreach (RealtimeRecording recording in recordings)
        {
            await recording.StopAsync(Token);
        }

        Assert.Equal(2, probeLogs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(probeLogs, log => recordings.Any(recording => recording.Paths.FfmpegLog == log || recording.Paths.CaptureLog == log));
    }

    [Fact]
    public async Task AnEncoderThatFailedAtStartIsNotTriedFirstAgain()
    {
        _fake.EncodersFailingAtStart.Add("h264_nvenc");
        RealtimeRecording first = await _fake.StartAsync(session: "first");
        await first.StopAsync(Token);

        RealtimeRecording second = await _fake.StartAsync(session: "second");
        await second.StopAsync(Token);

        Assert.Equal("libx264", second.Started.Encoder);
        Assert.Single(_fake.Events, entry => entry == "start:ffmpeg:h264_nvenc");
    }

    [Fact]
    public async Task ACancelledStartLeavesNoFiles()
    {
        _fake.HelperSilent = true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<RealtimeRecording> start = _fake.StartAsync(cancellationToken: cancel.Token);
        await RegistryHarness.WaitUntilAsync(() => _fake.Events.Contains("start:helper"));

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Empty(_fake.StartFiles());
    }

    [Fact]
    public async Task AnUnwritableDeadlineAtStopStillFinishesTheClip()
    {
        RealtimeRecording recording = await _fake.StartAsync();
        RealtimeStopped stopped;
        // Held without delete sharing, the deadline file cannot be replaced by the stop's move.
        using (new FileStream(recording.Paths.Deadline, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stopped = await recording.StopAsync(Token);
        }

        string warning = stopped.Warning ?? "";
        Assert.True(File.Exists(stopped.Clip.Path), "the clip was not finished");
        Assert.Contains("could not be written", warning, StringComparison.Ordinal);
        Assert.Contains("The capture helper was killed instead, so the clip ends there.", warning, StringComparison.Ordinal);
        Assert.False(File.Exists(recording.Paths.Deadline + ".tmp"), "the deadline's temporary file was left behind");
    }

    [Fact]
    public async Task AKillAfterTheEndDoesNotThrow()
    {
        string deadline = Path.Combine(_fake.ProjectDir, "ended.deadline");
        File.WriteAllText(deadline, DateTimeOffset.UtcNow.AddSeconds(-1).UtcDateTime.ToString("o", CultureInfo.InvariantCulture) + "\n");
        RealtimePipeline pipeline = await RealtimePipeline.StartAsync(
            _fake.Environment,
            _fake.EncodeRequest(Path.Combine(_fake.ProjectDir, "ended.mkv")),
            _fake.CaptureRequest(deadline)
        );
        await pipeline.FinishAsync();

        await pipeline.KillHelperAsync();
    }

    [Fact]
    public async Task AnUnrunnableFfmpegIsRefusedWithTheFix()
    {
        _fake.FfmpegRunFailure = new Win32Exception(193, "%1 is not a valid Win32 application.");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _fake.StartAsync());

        Assert.StartsWith(@"ffmpeg at C:\tools\ffmpeg.exe could not be run to test its encoders (log ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("): %1 is not a valid Win32 application. ", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            "Point FFMPEG_PATH at a working ffmpeg.exe, or install it with winget install Gyan.FFmpeg.",
            refused.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task AnUnwritableRecordingsFolderIsRefusedWithThePath()
    {
        File.WriteAllText(Path.Combine(_fake.ProjectDir, ".godot"), "a file where the folder belongs");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _fake.StartAsync());

        Assert.StartsWith($"The real-time recordings folder {_fake.Recordings} could not be created: ", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("Check that the project folder is writable.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNoEncoderRefusalNamesTheLogItsProbesWrote()
    {
        _fake.UnusableEncoders.UnionWith(["h264_nvenc", "libx264", "h264_mf"]);

        SessionException first = await Assert.ThrowsAsync<SessionException>(() => _fake.StartAsync(session: "first"));
        SessionException second = await Assert.ThrowsAsync<SessionException>(() => _fake.StartAsync(session: "second"));

        string log = LogNamedIn(first.Message);
        Assert.Equal(log, LogNamedIn(second.Message));
        Assert.True(File.Exists(log), $"the refusal names {log}, which was never written");
    }

    [Fact]
    public async Task AStartWithNoFrameWithinTheAllowanceIsRefused()
    {
        FakeTimeProvider clockTime = new();
        using FakeRealtime fake = new(clockTime) { HelperSilent = true };
        Task<RealtimeRecording> start = fake.StartAsync();
        await RegistryHarness.WaitUntilAsync(() => fake.Events.Contains("start:helper"));

        // Advanced until the start ends, bounded in wall time, so a busy machine slow to set the allowance's timer still sees it pass.
        var wall = System.Diagnostics.Stopwatch.StartNew();
        while (!start.IsCompleted && wall.Elapsed < TimeSpan.FromSeconds(30))
        {
            clockTime.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(10, Token);
        }

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => start);
        Assert.StartsWith(
            "The capture helper sent no frame within 10 s of load-adjusted time, so the recording did not start; the helper and ffmpeg "
                + "were stopped. ",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Empty(fake.StartFiles());
    }

    [Fact]
    public async Task AHelperEndingBeforeItsFirstFrameIsRefusedWithItsLastLines()
    {
        _fake.HelperFrameLimit = 0;
        _fake.HelperExit = 5;
        _fake.HelperLines = ["godot-mcp-capture: the window 4242 closed."];

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _fake.StartAsync());

        Assert.StartsWith(
            "The capture helper exited with code 5 before its first frame, so the recording did not start: ",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("godot-mcp-capture: the window 4242 closed.", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("-capture.log.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedStartDeletesItsDeadlineAndMkvAndKeepsItsLogs()
    {
        _fake.HelperFrameLimit = 0;
        _fake.HelperExit = 5;

        await Assert.ThrowsAsync<SessionException>(() => _fake.StartAsync());

        Assert.Empty(_fake.StartFiles());
        List<string> files = [.. Directory.EnumerateFiles(_fake.Recordings)];
        Assert.Contains(files, file => file.EndsWith("-capture.log", StringComparison.Ordinal));
        Assert.Contains(files, file => file.EndsWith("-ffmpeg.log", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProbeRefusalEndsWithTheHelpersLastLine()
    {
        _fake.ProbeExit = 2;
        _fake.ProbeLines = ["godot-mcp-capture: probing window 4242", "godot-mcp-capture: window 4242 is cloaked, so it cannot be captured."];

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _fake.StartAsync());

        Assert.StartsWith(
            "The capture helper cannot record the game's window: godot-mcp-capture: window 4242 is cloaked, so it cannot be captured. (see ",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(_fake.Events, entry => entry.StartsWith("start:ffmpeg", StringComparison.Ordinal));
    }

    private static string LogNamedIn(string message) =>
        SeeLog().Match(message) is { Success: true } match
            ? match.Groups[1].Value
            : throw new InvalidOperationException($"no log named in: {message}");

    [GeneratedRegex(@"; see (.+)\.$")]
    private static partial Regex SeeLog();

    // Each matches its warning as one whole part of the "; "-joined warnings, so another a busy machine adds beside it does
    // not fail the match; the resize warning holds a "; " of its own, so the warnings are not split.
    [GeneratedRegex(@"(?:^|; )the window changed size at \d+\.\d s; the clip ends there(?:; |$)")]
    private static partial Regex ResizeWarning();

    [GeneratedRegex(@"(?:^|; )the capture got \d+\.\d fps of 60: the machine was busy, so the clip repeats frames(?:; |$)")]
    private static partial Regex LowRateWarning();
}

/// <summary>
/// A real-time recording's environment of fakes: a helper that writes blank frames into a pipe until its deadline file says
/// stop (or a frame limit, with an exit status of the test's choosing), an ffmpeg that counts what it is given and makes its
/// output files, probes and a remux that answer as told, and a window that reads as asked.
/// </summary>
internal sealed class FakeRealtime : IDisposable
{
    public const int Width = 64;
    public const int Height = 32;
    public const long Hwnd = 4242;
    public const long FrameBytes = Width * Height * 4;
    public const string ExpectedHelper = @"C:\server\capture\godot-mcp-capture.exe";

    private static readonly Lock LogLock = new();

    private readonly TempDirectory _temp = new();
    private readonly LoadClock _clock;
    private readonly List<FakeHelper> _helpers = [];
    private long _helperFrames;
    private long _ffmpegBytes;
    private int _windowReads;

    /// <param name="clockTime">The time the processes' ceilings and the wait for the first frame run on; the system's when null.</param>
    public FakeRealtime(TimeProvider? clockTime = null)
    {
        _clock = new LoadClock(clockTime ?? TimeProvider.System, new NoLoadSource());
        Environment = new RealtimeEnvironment
        {
            FindFfmpeg = () => Ffmpeg,
            FindCaptureHelper = () => Helper,
            ExpectedCaptureHelper = ExpectedHelper,
            IsTopLevel = _ => TopLevel,
            ReadWindow = _ =>
            {
                Interlocked.Increment(ref _windowReads);
                return Reading;
            },
            Time = TimeProvider.System,
            Clock = _clock,
            Start = StartProcess,
            Run = RunAsync,
        };
    }

    public RealtimeEnvironment Environment { get; }

    public string ProjectDir => _temp.Path;

    public string Recordings => Path.Combine(ProjectDir, ".godot", "godot-mcp", "recordings");

    public FfmpegLookup Ffmpeg { get; set; } = new(@"C:\tools\ffmpeg.exe", null);

    public string? Helper { get; set; } = @"C:\server\capture\godot-mcp-capture.exe";

    public bool TopLevel { get; set; } = true;

    /// <summary>A 64x32 client area inside its frame, on the desktop.</summary>
    public WindowReading Reading { get; set; } =
        new(new PixelRect(100, 100, 66, 63), (101, 131), (Width, Height), false, new PixelRect(0, 0, 1920, 1080));

    public int WindowReads => Volatile.Read(ref _windowReads);

    public HashSet<string> UnusableEncoders { get; } = [];

    public HashSet<string> EncodersFailingAtStart { get; } = [];

    public int FfmpegExit { get; set; }

    /// <summary>The frames the helper writes before it exits with <see cref="HelperExit"/>; null, it runs to its deadline and exits 0.</summary>
    public int? HelperFrameLimit { get; set; }

    public int HelperExit { get; set; }

    public string[] HelperLines { get; set; } = ["fake capture"];

    /// <summary>The helper writes no frame, running until its deadline passes or it is killed.</summary>
    public bool HelperSilent { get; set; }

    /// <summary>The helper's probe's exit status; any but 0 writes no size and logs <see cref="ProbeLines"/>.</summary>
    public int ProbeExit { get; set; }

    public string[] ProbeLines { get; set; } = ["godot-mcp-capture: the window cannot be captured."];

    /// <summary>What an encoder probe's run throws, as an ffmpeg that cannot be started does.</summary>
    public Exception? FfmpegRunFailure { get; set; }

    public TimeSpan FrameInterval { get; set; } = TimeSpan.FromMilliseconds(20);

    public Func<ToolProcessRequest, Task>? BeforeProbe { get; set; }

    public ConcurrentQueue<string> Events { get; } = new();

    public ConcurrentQueue<ToolProcessRequest> Runs { get; } = new();

    public bool DeadlineExistedAtProbe { get; private set; }

    public DateTimeOffset? DeadlineAtHelperStart { get; set; }

    public DateTimeOffset? DeadlineAtHelperEnd { get; set; }

    public long HelperFramesWritten => Interlocked.Read(ref _helperFrames);

    public long FfmpegBytes => Interlocked.Read(ref _ffmpegBytes);

    public Task<RealtimeRecording> StartAsync(
        int fps = 30,
        int maxSeconds = 600,
        string session = "game",
        string? ffmpeg = null,
        CancellationToken? cancellationToken = null
    ) =>
        RealtimeRecording.StartAsync(
            Environment,
            new RealtimeStart(session, ProjectDir, Target(ffmpeg ?? Ffmpeg.Path!), fps, maxSeconds, DateTimeOffset.UtcNow),
            cancellationToken ?? TestContext.Current.CancellationToken
        );

    public static RealtimeTarget Target(string ffmpeg) =>
        new(ffmpeg, @"C:\server\capture\godot-mcp-capture.exe", Hwnd, new PixelRect(1, 31, Width, Height), null);

    /// <summary>The files a start that did not go ahead must not leave: the .mkv, the deadline file and its temporary.</summary>
    public IEnumerable<string> StartFiles() =>
        Directory.Exists(Recordings)
            ? Directory
                .EnumerateFiles(Recordings)
                .Where(file =>
                    file.EndsWith(".mkv", StringComparison.Ordinal)
                    || file.EndsWith(".deadline", StringComparison.Ordinal)
                    || file.EndsWith(".deadline.tmp", StringComparison.Ordinal)
                )
            : [];

    /// <summary>An encode of the fake window's frames into <paramref name="mkv"/>, as a recording asks ffmpeg for one.</summary>
    public ToolProcessRequest EncodeRequest(string mkv) =>
        new(
            Ffmpeg.Path!,
            RealtimeEncode.EncodeArguments(RealtimeEncode.Libx264, Target(Ffmpeg.Path!).Crop, 30, mkv),
            ProjectDir,
            Path.Combine(ProjectDir, "ffmpeg.log"),
            TimeSpan.FromMinutes(1)
        )
        {
            KeepStandardInput = true,
            PipeStandardOutput = true,
        };

    /// <summary>A capture of the fake window until <paramref name="deadline"/> says stop, as a recording asks the helper for one.</summary>
    public ToolProcessRequest CaptureRequest(string deadline) =>
        new(
            Helper!,
            ["--hwnd", "4242", "--crop", "1,31,64,32", "--fps", "30", "--until-file", deadline],
            ProjectDir,
            Path.Combine(ProjectDir, "capture.log"),
            TimeSpan.FromMinutes(1)
        )
        {
            PipeStandardOutput = true,
        };

    public static DateTimeOffset ReadDeadline(string path) =>
        TryReadDeadline(path, out DateTimeOffset deadline) ? deadline : throw new InvalidOperationException($"{path} holds no deadline");

    /// <summary>Reads a deadline file as the helper does: shared with a writer that replaces it by a move.</summary>
    public static bool TryReadDeadline(string path, out DateTimeOffset deadline)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            return DateTimeOffset.TryParse(reader.ReadToEnd().Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out deadline);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            deadline = default;
            return false;
        }
    }

    public void Dispose()
    {
        foreach (FakeHelper helper in _helpers)
        {
            helper.Dispose();
        }

        _clock.Dispose();
        _temp.Dispose();
    }

    internal void CountHelperFrame() => Interlocked.Increment(ref _helperFrames);

    internal void CountFfmpegBytes(int count) => Interlocked.Add(ref _ffmpegBytes, count);

    internal static void Log(ToolProcessRequest request, IEnumerable<string> lines)
    {
        lock (LogLock)
        {
            File.AppendAllLines(request.LogPath, lines);
        }
    }

    internal static ToolProcessResult Exit(int code) => new(code, TimeSpan.Zero, KillReason.None);

    private IRealtimeProcess StartProcess(ToolProcessRequest request)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.LogPath)!);
        if (!request.AppendToLog)
        {
            File.WriteAllText(request.LogPath, "");
        }

        if (!request.Arguments.Contains("--hwnd"))
        {
            string encoder = request.Arguments[request.Arguments.ToList().IndexOf("-c:v") + 1];
            Events.Enqueue($"start:ffmpeg:{encoder}");
            return new FakeFfmpeg(this, request, EncodersFailingAtStart.Contains(encoder));
        }

        if (request.Arguments.Contains("--probe"))
        {
            Events.Enqueue("start:probe");
            Log(request, ProbeExit == 0 ? ["item 66x63, crop 1,31,64,32"] : ProbeLines);
            return new FakeProbe(ProbeExit == 0 ? FormattableString.Invariant($"{Width}x{Height}\n") : "", ProbeExit);
        }

        Events.Enqueue("start:helper");
        FakeHelper helper = new(this, request);
        lock (_helpers)
        {
            _helpers.Add(helper);
        }

        return helper;
    }

    private async Task<ToolProcessResult> RunAsync(ToolProcessRequest request, CancellationToken cancellationToken)
    {
        Runs.Enqueue(request);
        List<string> arguments = [.. request.Arguments];
        if (arguments.Contains("lavfi"))
        {
            string encoder = arguments[arguments.IndexOf("-c:v") + 1];
            Events.Enqueue($"run:probe:{encoder}");
            DeadlineExistedAtProbe |= Directory.EnumerateFiles(request.WorkingDirectory, "*.deadline").Any();
            if (FfmpegRunFailure is { } failure)
            {
                throw failure;
            }

            if (BeforeProbe is { } hook)
            {
                await hook(request);
            }

            bool usable = !UnusableEncoders.Contains(encoder);
            Log(request, [usable ? $"[{encoder}] encoded one frame" : $"[{encoder} @ 0x1] Error while opening encoder"]);
            return Exit(usable ? 0 : 1);
        }

        Events.Enqueue("run:remux");
        File.Copy(arguments[arguments.IndexOf("-i") + 1], arguments[^1], overwrite: true);
        return Exit(0);
    }

    /// <summary>The helper's probe: its size on stdout and its exit status.</summary>
    private sealed class FakeProbe(string size, int exitCode) : IRealtimeProcess
    {
        public Stream StandardInput => throw new InvalidOperationException("the probe's stdin is closed");

        public Stream StandardOutput { get; } = new MemoryStream(Encoding.UTF8.GetBytes(size));

        public Task<ToolProcessResult> WaitAsync(CancellationToken cancellationToken) => Task.FromResult(Exit(exitCode));

        public Task KillAsync() => Task.CompletedTask;

        public void Dispose() => StandardOutput.Dispose();
    }
}

/// <summary>
/// ffmpeg's stand-in: it makes its output file, as ffmpeg opens it before its encoder, counts the bytes it is given, opens its
/// encoder once a whole first frame is in and says so on its progress output, and exits once its stdin closes. An encoder that
/// fails at its start takes the first frame, then exits 1 with a progress report that ends the run, refusing what follows as
/// a pipe ffmpeg has closed.
/// </summary>
internal sealed class FakeFfmpeg : IRealtimeProcess
{
    private readonly TaskCompletionSource<ToolProcessResult> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AnonymousPipeServerStream _progressReader = new(PipeDirection.In);
    private readonly AnonymousPipeClientStream _progress;
    private readonly Lock _progressLock = new();
    private bool _progressClosed;

    public FakeFfmpeg(FakeRealtime owner, ToolProcessRequest request, bool failAtStart)
    {
        _progress = new AnonymousPipeClientStream(PipeDirection.Out, _progressReader.ClientSafePipeHandle);
        List<string> arguments = [.. request.Arguments];
        string[] size = arguments[arguments.IndexOf("-video_size") + 1].Split('x');
        long frameBytes = long.Parse(size[0], CultureInfo.InvariantCulture) * long.Parse(size[1], CultureInfo.InvariantCulture) * 4;
        File.WriteAllText(request.Arguments[^1], "");
        StandardInput = new CountingSink(
            frameBytes,
            () => failAtStart ? FailAtStart(request) : OpenEncoder(request),
            owner.CountFfmpegBytes,
            () =>
            {
                FakeRealtime.Log(request, owner.FfmpegExit == 0 ? [] : ["[matroska @ 0x1] Error writing trailer: Invalid argument"]);
                End(owner.FfmpegExit, "progress=end");
            }
        );
    }

    public Stream StandardInput { get; }

    public Stream StandardOutput => _progressReader;

    public Task<ToolProcessResult> WaitAsync(CancellationToken cancellationToken) => _exit.Task;

    public Task KillAsync()
    {
        End(-1);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        CloseProgress();
        _progressReader.Dispose();
    }

    private bool OpenEncoder(ToolProcessRequest request)
    {
        File.WriteAllText(request.Arguments[^1], "matroska");
        Report("frame=0", "progress=continue");
        return true;
    }

    private bool FailAtStart(ToolProcessRequest request)
    {
        FakeRealtime.Log(request, ["[h264_nvenc @ 0x1] OpenEncodeSessionEx failed: incompatible client key (21)"]);
        End(1, "frame=0", "progress=end");
        return false;
    }

    private void End(int exitCode, params string[] lastReport)
    {
        Report(lastReport);
        CloseProgress();
        _exit.TrySetResult(FakeRealtime.Exit(exitCode));
    }

    private void Report(params string[] lines)
    {
        lock (_progressLock)
        {
            if (_progressClosed)
            {
                return;
            }

            byte[] report = Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n")));
            try
            {
                _progress.Write(report);
            }
            catch (IOException)
            {
                // The reader let go of the progress pipe: nobody reads the report.
            }
        }
    }

    private void CloseProgress()
    {
        lock (_progressLock)
        {
            _progressClosed = true;
            _progress.Dispose();
        }
    }

    /// <summary>
    /// ffmpeg's stdin: counts what is written, calls the first-frame handler once a whole frame is in, and from then on
    /// refuses what is written as a pipe ffmpeg has closed when the handler says the encoder failed; closing it ends ffmpeg.
    /// </summary>
    private sealed class CountingSink(long frameBytes, Func<bool> firstFrame, Action<int> counted, Action closed) : Stream
    {
        private int _closed;
        private long _total;
        private bool _broken;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_broken)
            {
                throw new IOException("The pipe is being closed.");
            }

            counted(count);
            long before = _total;
            _total += count;
            if (before < frameBytes && _total >= frameBytes)
            {
                _broken = !firstFrame();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                closed();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// The capture helper's stand-in: blank frames into a pipe every frame interval (none when silent) until its deadline file
/// names a time past, or until its frame limit, when it exits with the test's status; a reader that goes away ends it with 8.
/// </summary>
internal sealed class FakeHelper : IRealtimeProcess
{
    private readonly FakeRealtime _owner;
    private readonly ToolProcessRequest _request;
    private readonly AnonymousPipeServerStream _reader = new(PipeDirection.In);
    private readonly AnonymousPipeClientStream _writer;
    private readonly CancellationTokenSource _killed = new();
    private readonly TaskCompletionSource<ToolProcessResult> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _deadline;
    private readonly DateTimeOffset _startDeadline;

    public FakeHelper(FakeRealtime owner, ToolProcessRequest request)
    {
        _owner = owner;
        _request = request;
        _writer = new AnonymousPipeClientStream(PipeDirection.Out, _reader.ClientSafePipeHandle);
        List<string> arguments = [.. request.Arguments];
        _deadline = arguments[arguments.IndexOf("--until-file") + 1];
        _startDeadline = FakeRealtime.ReadDeadline(_deadline);
        owner.DeadlineAtHelperStart = _startDeadline;
        _ = Task.Run(RunAsync);
    }

    public Stream StandardInput => throw new InvalidOperationException("the helper's stdin is closed");

    public Stream StandardOutput => _reader;

    public Task<ToolProcessResult> WaitAsync(CancellationToken cancellationToken) => _exit.Task;

    public async Task KillAsync()
    {
        await _killed.CancelAsync();
        await _exit.Task;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _killed.Dispose();
    }

    private async Task RunAsync()
    {
        byte[] frame = new byte[FakeRealtime.FrameBytes];
        int code = 0;
        int written = 0;
        // A deadline caught mid-move, or unreadable, keeps the last one read, as the helper's own reading does.
        DateTimeOffset deadline = _startDeadline;
        try
        {
            while (true)
            {
                if (_owner.HelperFrameLimit is { } limit && written >= limit)
                {
                    code = _owner.HelperExit;
                    break;
                }

                if (FakeRealtime.TryReadDeadline(_deadline, out DateTimeOffset read))
                {
                    deadline = read;
                }

                if (deadline <= DateTimeOffset.UtcNow)
                {
                    _owner.DeadlineAtHelperEnd = deadline;
                    break;
                }

                if (!_owner.HelperSilent)
                {
                    await _writer.WriteAsync(frame, _killed.Token);
                    written++;
                    _owner.CountHelperFrame();
                }

                await Task.Delay(_owner.FrameInterval, _killed.Token);
            }
        }
        catch (IOException)
        {
            code = 8;
        }
        catch (OperationCanceledException)
        {
            code = -1;
        }

        await _writer.DisposeAsync();
        FakeRealtime.Log(_request, _owner.HelperLines);
        _exit.TrySetResult(FakeRealtime.Exit(code));
    }
}
