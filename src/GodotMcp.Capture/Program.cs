using System.Diagnostics;
using Windows.Graphics;
using Windows.Graphics.Capture;

namespace GodotMcp.Capture;

/// <summary>
/// Records one window, by handle, through Windows.Graphics.Capture and writes a crop of its frames as raw BGRA to stdout
/// at a constant rate, for ffmpeg to read with -f rawvideo; with --audio-pid, also the audio a process tree renders, as
/// raw s16le into a named pipe. A run lasts until the instant a file holds, which may be rewritten while it runs, or until
/// the window closes. Everything the helper says goes to stderr; each kind of end has its exit code (<see cref="CaptureExit"/>).
/// </summary>
internal static class Program
{
    private const string Name = "godot-mcp-capture";
    private const int ProcessLoopbackMinimumBuild = 20348;

    /// <summary>Below this share of the requested rate, the run says the reader fell behind.</summary>
    private const double KeptUpShare = 0.95;
    private static readonly TimeSpan FirstFrameWait = TimeSpan.FromSeconds(5);

    private static async Task<int> Main(string[] args)
    {
        var options = CaptureArgs.Parse(args, out string? usageError);
        // One deadline for the whole run, so the video and the audio end at the same instant. A probe records nothing.
        Deadline? deadline = options is null || options.Probe ? null : Deadline.FromFile(options.UntilFile!, out usageError);
        if (options is null || (deadline is null && !options.Probe))
        {
            await SayAsync(usageError!).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(CaptureArgs.Usage).ConfigureAwait(false);
            return CaptureExit.Usage;
        }
        try
        {
            return await CaptureWindowAsync(options, deadline).ConfigureAwait(false);
        }
        catch (CaptureStartException failure)
        {
            string detail = failure.InnerException?.Message ?? failure.Message;
            await SayAsync(CaptureExit.CaptureStartLine(failure.Message, failure.HResult, detail)).ConfigureAwait(false);
            return CaptureExit.CaptureStart;
        }
    }

    private static Task SayAsync(string line) => Console.Error.WriteLineAsync($"{Name}: {line}");

    /// <summary><paramref name="deadline"/> is null only for a probe.</summary>
    private static async Task<int> CaptureWindowAsync(CaptureArgs options, Deadline? deadline)
    {
        string? refusal = RefuseWindow(options.Hwnd);
        using WindowCapture? capture = refusal is null ? WindowCapture.Create(options.Hwnd, options.Crop, out refusal) : null;
        if (capture is null)
        {
            await SayAsync(refusal!).ConfigureAwait(false);
            return CaptureExit.Usage;
        }

        // The audio pipe is created here, before the first video byte: ffmpeg opens its second input only after the first
        // has delivered data, so the pipe exists by the time it is opened. A probe with --audio-pid activates the audio
        // too, so an activation failure is refused before ffmpeg starts.
        using AudioTrack? audio = options.AudioPid is null
            ? null
            : await OpenAudioAsync(options.AudioPid.Value, options.AudioPipe).ConfigureAwait(false);
        if (options.AudioPid is not null && audio is null)
        {
            return CaptureExit.AudioActivation;
        }
        if (options.Probe)
        {
            SizeInt32 item = capture.ItemSize;
            await SayAsync(FormattableString.Invariant($"item {item.Width}x{item.Height}, crop {capture.CropRect}")).ConfigureAwait(false);
            Console.Out.WriteLine(Crop.ProbeLine(capture.CropRect));
            return CaptureExit.Done;
        }
        return await RecordAsync(capture, options.Fps, deadline!, audio).ConfigureAwait(false);
    }

    private static string? RefuseWindow(nint hwnd)
    {
        if (!WindowFinder.IsWindow(hwnd))
        {
            return FormattableString.Invariant($"--hwnd {hwnd} names no window.");
        }
        if (WindowFinder.IsMinimized(hwnd))
        {
            return "the window is minimized; a minimized window has no surface to capture. Restore it and rerun.";
        }
        return GraphicsCaptureSession.IsSupported() ? null : "Windows.Graphics.Capture is not supported on this machine.";
    }

    /// <summary>Returns the activated track, or null after one line on stderr when process loopback cannot start.</summary>
    private static async Task<AudioTrack?> OpenAudioAsync(int processId, string? pipeName)
    {
        string? refusal = RefuseAudio(processId);
        if (refusal is not null)
        {
            await SayAsync(refusal).ConfigureAwait(false);
            return null;
        }
        try
        {
            return AudioTrack.Open(processId, pipeName);
        }
        catch (AudioPipeException failure)
        {
            await SayAsync(failure.Message).ConfigureAwait(false);
            return null;
        }
        catch (AudioActivationException failure)
        {
            await SayAsync($"process-loopback audio for process {processId} could not start: {failure.Message}.").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Process loopback activates for any process id, a missing one included, and then records silence; a missing process
    /// is refused here instead, since a silent track for a mistyped id is not what was asked for.
    /// </summary>
    private static string? RefuseAudio(int processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, ProcessLoopbackMinimumBuild))
        {
            return $"process-loopback audio needs Windows build {ProcessLoopbackMinimumBuild} or later.";
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            return null;
        }
        catch (ArgumentException)
        {
            return $"no process has id {processId}, so there is no audio to record.";
        }
    }

    private static async Task<int> RecordAsync(WindowCapture capture, int fps, Deadline deadline, AudioTrack? audio)
    {
        SizeInt32 item = capture.ItemSize;
        string output = Crop.ProbeLine(capture.CropRect);
        await SayAsync(FormattableString.Invariant($"{capture.CropRect} of the {item.Width}x{item.Height} window as {output} at {fps} fps"))
            .ConfigureAwait(false);
        await SayAsync($"recording {deadline.Describe()}").ConfigureAwait(false);
        byte[] frame = new byte[capture.FrameBytes];
        capture.Start();
        // Before the first frame no audio runs, so a failure here ends the run at once.
        if (await AwaitFirstFrameAsync(capture, frame).ConfigureAwait(false) is int ended)
        {
            return ended;
        }

        // The first frame is the run's start for both tracks; ffmpeg lines them up by sample and frame count from there.
        Task<bool> audioRecording = audio?.RecordAsync(deadline) ?? Task.FromResult(true);
        VideoRun run;
        bool audioRead;
        try
        {
            run = await WriteFramesAsync(capture, frame, deadline, fps).ConfigureAwait(false);
        }
        finally
        {
            // Whatever ended the video ends the audio with it, and the audio thread is done before its objects are disposed.
            deadline.EndNow();
            audioRead = await audioRecording.ConfigureAwait(false);
        }
        if (run.Line is not null)
        {
            await SayAsync(run.Line).ConfigureAwait(false);
        }
        await ReportFrameRateAsync(run.Written, run.Elapsed, fps).ConfigureAwait(false);
        return CaptureExit.For(run.End, audioRead);
    }

    /// <summary>
    /// Null once the first frame is in <paramref name="frame"/>, else the run's exit code after its line: a window that
    /// closed first ends the run cleanly with nothing written; a resize, a lost device or no frame in time is a failure.
    /// No audio runs yet, so nothing waits on these.
    /// </summary>
    private static async Task<int?> AwaitFirstFrameAsync(WindowCapture capture, byte[] frame)
    {
        try
        {
            if (await WaitForFirstFrameAsync(capture, frame).ConfigureAwait(false))
            {
                return null;
            }
        }
        catch (WindowResizedException resized)
        {
            await SayAsync($"{resized.Message}; the output size is fixed for a run, so it stopped.").ConfigureAwait(false);
            return CaptureExit.Resized;
        }
        catch (DeviceLostException lost)
        {
            await SayAsync(CaptureExit.DeviceLostLine(lost.HResult, 0)).ConfigureAwait(false);
            return CaptureExit.DeviceLost;
        }
        if (capture.IsClosed)
        {
            await SayAsync("the window closed before its first frame, so nothing was written.").ConfigureAwait(false);
            await ReportFrameRateAsync(0, TimeSpan.Zero, 0).ConfigureAwait(false);
            return CaptureExit.Done;
        }
        await SayAsync(FormattableString.Invariant($"no frame arrived in {FirstFrameWait.TotalSeconds} s; the window is not being composed."))
            .ConfigureAwait(false);
        return CaptureExit.Usage;
    }

    /// <summary>
    /// Writes a frame on every timer tick until the deadline passes or the window closes, and always closes stdout before
    /// the caller waits for the audio: the EOF is what makes ffmpeg flush the frames its encoder still holds (h264_nvenc
    /// keeps a few) and end the video stream, so ffmpeg is never left waiting on a video input that has stopped sending
    /// while the audio drains. A resize, a lost device or a reader gone ends the video with its own end and line.
    /// </summary>
    private static async Task<VideoRun> WriteFramesAsync(WindowCapture capture, byte[] frame, Deadline deadline, int fps)
    {
        int written = 0;
        Stopwatch clock = new();
        try
        {
            // Unbuffered: a frame is one write, so a reader gone fails that write and nothing is left to flush at dispose.
            await using Stream stdout = StandardOutput.Open();
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1.0 / fps));
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false) && !deadline.HasPassed() && !capture.IsClosed)
            {
                clock.Start();
                capture.TryCopyLatestFrame(frame);
                await stdout.WriteAsync(frame).ConfigureAwait(false);
                written++;
            }
            return capture.IsClosed
                ? new VideoRun(
                    CaptureEnd.WindowClosed,
                    written,
                    clock.Elapsed,
                    "the window closed, so the capture ended; the frames written are kept."
                )
                : new VideoRun(CaptureEnd.Finished, written, clock.Elapsed, null);
        }
        catch (WindowResizedException resized)
        {
            return new VideoRun(CaptureEnd.Resized, written, clock.Elapsed, $"{resized.Message}; the output size is fixed for a run, so it stopped.");
        }
        catch (DeviceLostException lost)
        {
            return new VideoRun(CaptureEnd.DeviceLost, written, clock.Elapsed, CaptureExit.DeviceLostLine(lost.HResult, written));
        }
        catch (IOException)
        {
            return new VideoRun(CaptureEnd.ReaderGone, written, clock.Elapsed, CaptureExit.ReaderGoneLine(written));
        }
        finally
        {
            StandardOutput.Close();
        }
    }

    /// <summary>
    /// A write blocks while the reader is busy, and the timer skips the ticks it missed, so a slow reader means fewer frames
    /// rather than a late end; the reader stamps each frame with its arrival time, so the file still plays in real time.
    /// </summary>
    private static async Task ReportFrameRateAsync(int written, TimeSpan elapsed, int fps)
    {
        double seconds = elapsed.TotalSeconds;
        double rate = seconds > 0 ? written / seconds : 0;
        await SayAsync(FormattableString.Invariant($"{written} frames in {seconds:F1} s ({rate:F1} fps of {fps})")).ConfigureAwait(false);
        if (seconds > 0 && rate < KeptUpShare * fps)
        {
            await SayAsync(
                    "the reader fell behind the frame rate; the recording still plays in real time because each frame carries its arrival time."
                )
                .ConfigureAwait(false);
        }
    }

    /// <summary>The pool delivers on its own cadence; the run starts on the first delivered frame, not on a blank one.</summary>
    private static async Task<bool> WaitForFirstFrameAsync(WindowCapture capture, byte[] frame)
    {
        DateTime deadline = DateTime.UtcNow + FirstFrameWait;
        while (DateTime.UtcNow < deadline && !capture.IsClosed)
        {
            if (capture.TryCopyLatestFrame(frame))
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>How a run's video ended: the end, the frames written, the time they took, and the line that says why, if any.</summary>
    private sealed record VideoRun(CaptureEnd End, int Written, TimeSpan Elapsed, string? Line);
}
