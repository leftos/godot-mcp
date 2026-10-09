using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The window capture helper alone against a visible InputProbe: its frames piped into ffmpeg make a clip of the
/// deadline's length at the size its probe names, read back with ffprobe; an offset crop with odd sides is written padded
/// to even and its frames are the window's pixels; a crop past the window is refused, and a window closed mid-capture ends
/// the run cleanly. It runs on the user's desktop (the capture group skips the hidden desktop, which Windows.Graphics.Capture
/// cannot see), and needs the helper published into bin/capture, which the group's gate does first.
/// </summary>
public sealed class CaptureHelperTests : IAsyncDisposable
{
    private const int Fps = 30;
    private static readonly TimeSpan ClipLength = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HelperCeiling = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FirstFrameWait = TimeSpan.FromSeconds(15);

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly TempDirectory _temp = new();

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
        _temp.Dispose();
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TwoSecondsOfTheWindowMakeATwoSecondClipAtTheProbedSize()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string hwnd = await LaunchVisibleAsync(cancellation);
        HelperRun probed = await RunHelperAsync(["--hwnd", hwnd, "--probe"], cancellation);
        Assert.True(probed.ExitCode == 0, $"the probe exited {probed.ExitCode}: {probed.StandardError}");
        string size = probed.StandardOutput.Trim();
        string[] sides = size.Split('x');

        (string clip, string report) = await RecordAsync(hwnd, size, null, cancellation);

        (double seconds, int width, int height) = await ProbeClipAsync(clip, cancellation);
        Assert.True(
            Math.Abs(seconds - ClipLength.TotalSeconds) <= 0.5,
            FormattableString.Invariant($"the clip lasts {seconds} s, not {ClipLength.TotalSeconds} s within 0.5 s; the helper said: {report}")
        );
        Assert.Equal(int.Parse(sides[0], CultureInfo.InvariantCulture), width);
        Assert.Equal(int.Parse(sides[1], CultureInfo.InvariantCulture), height);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ACropLargerThanTheWindowIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string hwnd = await LaunchVisibleAsync(cancellation);

        HelperRun refused = await RunHelperAsync(["--hwnd", hwnd, "--crop", "0,0,10000,10000", "--probe"], cancellation);

        Assert.Equal(2, refused.ExitCode);
        Assert.Contains("--crop 0,0,10000,10000 reaches outside the capture item, which is ", refused.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, refused.StandardOutput);
    }

    /// <summary>
    /// A crop with an odd offset and odd sides is written padded to even, and its frames are the window's pixels rather
    /// than a black frame of the right size: the clip's frame size is the padded crop and its middle frame is not black.
    /// </summary>
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnOffsetCropWithOddSidesIsPaddedAndShowsTheWindow()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string hwnd = await LaunchVisibleAsync(cancellation);
        HelperRun probed = await RunHelperAsync(["--hwnd", hwnd, "--probe"], cancellation);
        Assert.True(probed.ExitCode == 0, $"the probe exited {probed.ExitCode}: {probed.StandardError}");
        (int itemWidth, int itemHeight) = ItemSize(probed.StandardError);
        string crop = FormattableString.Invariant($"1,31,{itemWidth - 3},{itemHeight - 33}");
        int width = RoundUpToEven(itemWidth - 3);
        int height = RoundUpToEven(itemHeight - 33);

        (string clip, string report) = await RecordAsync(hwnd, FormattableString.Invariant($"{width}x{height}"), crop, cancellation);

        (_, int frameWidth, int frameHeight) = await ProbeClipAsync(clip, cancellation);
        Assert.True(
            frameWidth == width && frameHeight == height,
            FormattableString.Invariant($"the clip is {frameWidth}x{frameHeight}, not the padded crop {width}x{height}; the helper said: {report}")
        );
        double luma = await MeanLumaAsync(clip, cancellation);
        Assert.True(
            luma > 8,
            FormattableString.Invariant($"the middle frame's mean luma is {luma:F1} of 255, so the crop is all but black; the helper said: {report}")
        );
    }

    /// <summary>
    /// A reader that goes away ends the run: a frame written to a pipe nobody reads fails, and the helper stops with exit
    /// 8 and the line saying how many frames reached the reader.
    /// </summary>
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AReaderThatDiesEndsTheHelperWithExitEight()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string hwnd = await LaunchVisibleAsync(cancellation);
        string deadline = _temp.Combine("deadline.txt");
        WriteDeadline(deadline, DateTime.UtcNow.AddMinutes(1));
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        ceiling.CancelAfter(HelperCeiling);
        using Process helper = Start(HelperPath(), ["--hwnd", hwnd, "--fps", $"{Fps}", "--until-file", deadline], redirectInput: false);
        try
        {
            Task<string> errors = helper.StandardError.ReadToEndAsync(ceiling.Token);
            byte[] frame = new byte[1 << 20];
            Task<int> firstFrame = helper.StandardOutput.BaseStream.ReadAsync(frame, ceiling.Token).AsTask();
            Assert.True(
                await Task.WhenAny(firstFrame, Task.Delay(FirstFrameWait, cancellation)) == firstFrame,
                $"the helper wrote no frame in {FirstFrameWait.TotalSeconds} s"
            );
            Assert.True(await firstFrame > 0, "the helper closed its stdout without writing a frame");
            helper.StandardOutput.Close();
            Task exited = helper.WaitForExitAsync(CancellationToken.None);
            Assert.True(
                await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(3), cancellation)) == exited,
                "the helper did not exit within 3 s of its reader closing"
            );
            Assert.Equal(8, helper.ExitCode);
            string said = await errors;
            Assert.Contains("the reader closed the video stream after ", said, StringComparison.Ordinal);
            Assert.Contains(" frames.", said, StringComparison.Ordinal);
        }
        finally
        {
            Stop(helper);
        }
    }

    /// <summary>
    /// A window closed mid-capture is the run's stop signal: once the first frame has arrived, killing the game's process
    /// ends the helper at once with status 0, saying the frames written are kept.
    /// </summary>
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AWindowClosedMidCaptureEndsTheHelperCleanly()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string hwnd = await LaunchVisibleAsync(cancellation);
        int game = _harness.Sessions.Resolve(null).GameProcessId ?? throw new InvalidOperationException("the hello carried no game pid.");
        string deadline = _temp.Combine("deadline.txt");
        WriteDeadline(deadline, DateTime.UtcNow.AddMinutes(1));
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        ceiling.CancelAfter(HelperCeiling);
        using Process helper = Start(HelperPath(), ["--hwnd", hwnd, "--fps", $"{Fps}", "--until-file", deadline], redirectInput: false);
        try
        {
            Task<string> errors = helper.StandardError.ReadToEndAsync(ceiling.Token);
            TaskCompletionSource firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task drained = DrainFramesAsync(helper.StandardOutput.BaseStream, firstFrame, ceiling.Token);
            Task signal = await Task.WhenAny(firstFrame.Task, drained);
            Assert.True(signal == firstFrame.Task, "the helper's stdout ended before its first frame arrived");
            using var running = Process.GetProcessById(game);
            running.Kill(entireProcessTree: true);
            Task exited = helper.WaitForExitAsync(CancellationToken.None);
            Assert.True(
                await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(3), cancellation)) == exited,
                "the helper did not exit within 3 s of its window closing"
            );
            Assert.Equal(0, helper.ExitCode);
            await drained;
            string said = await errors;
            Assert.Contains("the window closed, so the capture ended; the frames written are kept.", said, StringComparison.Ordinal);
            Assert.Contains(" frames in ", said, StringComparison.Ordinal);
        }
        finally
        {
            Stop(helper);
        }
    }

    /// <summary>Launches the probe on this desktop, not quiet, and returns its game window's handle as a decimal.</summary>
    private async Task<string> LaunchVisibleAsync(CancellationToken cancellationToken)
    {
        await _harness.Sessions.LaunchAsync(new LaunchRequest(_probe.Directory, null, [], [], false, false, Prepare: true), null, cancellationToken);
        int game = _harness.Sessions.Resolve(null).GameProcessId ?? throw new InvalidOperationException("the hello carried no game pid.");
        using var process = Process.GetProcessById(game);
        nint hwnd = nint.Zero;
        bool found = await Poll.UntilAsync(
            () =>
            {
                process.Refresh();
                hwnd = process.MainWindowHandle;
                return hwnd != nint.Zero;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken
        );
        Assert.True(found, $"the game (pid {game}) showed no main window in 10 s");
        return ((long)hwnd).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Records the window into an .mkv through the helper and ffmpeg, at the size a probe names for <paramref name="crop"/>
    /// (all of the window when it is null). The deadline file starts a minute out and is moved to
    /// two seconds after the first frame reaches this process, as a stop moves it, so the clip's length does not depend on
    /// how long the helper took to start. ffmpeg stamps each frame with its arrival time, as the real-time recording's
    /// pipeline does: a reader that falls behind gets fewer frames from the helper, never a shorter clip.
    /// </summary>
    private async Task<(string Clip, string Report)> RecordAsync(string hwnd, string size, string? crop, CancellationToken cancellationToken)
    {
        string deadline = _temp.Combine("deadline.txt");
        WriteDeadline(deadline, DateTime.UtcNow.AddMinutes(1));
        string clip = _temp.Combine("clip.mkv");
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ceiling.CancelAfter(HelperCeiling);
        List<string> helperArguments = ["--hwnd", hwnd, "--fps", $"{Fps}", "--until-file", deadline];
        if (crop is not null)
        {
            helperArguments.AddRange(["--crop", crop]);
        }
        using Process helper = Start(HelperPath(), helperArguments, redirectInput: false);
        using Process ffmpeg = Start(
            FfmpegPath(),
            [
                "-v",
                "error",
                "-use_wallclock_as_timestamps",
                "1",
                "-f",
                "rawvideo",
                "-pix_fmt",
                "bgra",
                "-video_size",
                size,
                "-framerate",
                $"{Fps}",
                "-i",
                "-",
                "-c:v",
                "libx264",
                "-preset",
                "ultrafast",
                "-pix_fmt",
                "yuv420p",
                "-fps_mode",
                "vfr",
                "-y",
                clip,
            ],
            redirectInput: true
        );
        try
        {
            Task<string> helperErrors = helper.StandardError.ReadToEndAsync(ceiling.Token);
            Task<string> ffmpegErrors = ffmpeg.StandardError.ReadToEndAsync(ceiling.Token);
            Task<string> ffmpegOutput = ffmpeg.StandardOutput.ReadToEndAsync(ceiling.Token);
            await PipeAsync(helper.StandardOutput.BaseStream, ffmpeg.StandardInput.BaseStream, deadline, ceiling.Token);
            await helper.WaitForExitAsync(ceiling.Token);
            await ffmpeg.WaitForExitAsync(ceiling.Token);
            string said = await helperErrors;
            Assert.True(helper.ExitCode == 0, $"the helper exited {helper.ExitCode}: {said}");
            Assert.Contains(" frames in ", said, StringComparison.Ordinal);
            Assert.True(ffmpeg.ExitCode == 0, $"ffmpeg exited {ffmpeg.ExitCode}: {await ffmpegErrors}{await ffmpegOutput}");
            return (clip, said);
        }
        finally
        {
            Stop(helper);
            Stop(ffmpeg);
        }
    }

    /// <summary>Copies the helper's frames into ffmpeg until the helper closes its stdout; the first bytes start the deadline.</summary>
    private static async Task PipeAsync(Stream frames, Stream encoder, string deadline, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1 << 20];
        bool started = false;
        int read;
        while ((read = await frames.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (!started)
            {
                started = true;
                WriteDeadline(deadline, DateTime.UtcNow + ClipLength);
            }
            await encoder.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await encoder.FlushAsync(cancellationToken);
        encoder.Close();
    }

    /// <summary>Writes the deadline beside the file and moves it over, so the helper never reads half a line.</summary>
    private static void WriteDeadline(string path, DateTime endUtc)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, endUtc.ToString("o", CultureInfo.InvariantCulture) + "\n");
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<HelperRun> RunHelperAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ceiling.CancelAfter(HelperCeiling);
        using Process helper = Start(HelperPath(), arguments, redirectInput: false);
        try
        {
            Task<string> errors = helper.StandardError.ReadToEndAsync(ceiling.Token);
            string output = await helper.StandardOutput.ReadToEndAsync(ceiling.Token);
            await helper.WaitForExitAsync(ceiling.Token);
            return new HelperRun(helper.ExitCode, output, await errors);
        }
        finally
        {
            Stop(helper);
        }
    }

    /// <summary>The clip's length in seconds and its frame size, as ffprobe reads them.</summary>
    private static async Task<(double Seconds, int Width, int Height)> ProbeClipAsync(string clip, CancellationToken cancellationToken)
    {
        string ffmpeg = FfmpegPath();
        string ffprobe = Path.Combine(
            Path.GetDirectoryName(ffmpeg)!,
            Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.Ordinal)
        );
        using Process probe = Start(
            ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "format=duration:stream=width,height", "-of", "json", clip],
            redirectInput: false
        );
        try
        {
            Task<string> errors = probe.StandardError.ReadToEndAsync(cancellationToken);
            string output = await probe.StandardOutput.ReadToEndAsync(cancellationToken);
            await probe.WaitForExitAsync(cancellationToken);
            Assert.True(probe.ExitCode == 0, $"ffprobe failed on {clip}: {await errors}");
            using var probed = JsonDocument.Parse(output);
            JsonElement stream = probed.RootElement.GetProperty("streams")[0];
            string duration = probed.RootElement.GetProperty("format").GetProperty("duration").GetString()!;
            return (
                double.Parse(duration, CultureInfo.InvariantCulture),
                stream.GetProperty("width").GetInt32(),
                stream.GetProperty("height").GetInt32()
            );
        }
        finally
        {
            Stop(probe);
        }
    }

    /// <summary>The item size the probe names on stderr: `item &lt;W&gt;x&lt;H&gt;, crop &lt;x,y,w,h&gt;`.</summary>
    private static (int Width, int Height) ItemSize(string said)
    {
        Match item = Regex.Match(said, @"item (\d+)x(\d+)");
        Assert.True(item.Success, $"the probe said no item size: {said}");
        return (int.Parse(item.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(item.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>The frame side the helper writes for a crop's side: an odd one padded up to even, as the helper does.</summary>
    private static int RoundUpToEven(int value) => value + (value & 1);

    /// <summary>The mean luma of one frame from the middle of the clip, read as raw gray pixels through ffmpeg.</summary>
    private static async Task<double> MeanLumaAsync(string clip, CancellationToken cancellationToken)
    {
        using Process ffmpeg = Start(
            FfmpegPath(),
            ["-v", "error", "-ss", "1", "-i", clip, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "-"],
            redirectInput: false
        );
        try
        {
            Task<string> errors = ffmpeg.StandardError.ReadToEndAsync(cancellationToken);
            using MemoryStream gray = new();
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(gray, cancellationToken);
            await ffmpeg.WaitForExitAsync(cancellationToken);
            Assert.True(ffmpeg.ExitCode == 0, $"ffmpeg read no frame from {clip}: {await errors}");
            byte[] pixels = gray.ToArray();
            Assert.True(pixels.Length > 0, $"ffmpeg wrote no frame from {clip}: {await errors}");
            double total = 0;
            foreach (byte pixel in pixels)
            {
                total += pixel;
            }
            return total / pixels.Length;
        }
        finally
        {
            Stop(ffmpeg);
        }
    }

    /// <summary>
    /// Drains the helper's frames to the end, so one writing faster than the test reads never stalls on a full pipe, and
    /// sets <paramref name="firstFrame"/> once the first of them has arrived.
    /// </summary>
    private static async Task DrainFramesAsync(Stream frames, TaskCompletionSource firstFrame, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1 << 20];
        while (await frames.ReadAsync(buffer, cancellationToken) > 0)
        {
            firstFrame.TrySetResult();
        }
    }

    /// <summary>
    /// Starts a tool with its output and errors redirected. Its stdin is ours to write when <paramref name="redirectInput"/>
    /// is set; otherwise it is redirected too and closed at once, so the tool never reads this process's stdin.
    /// </summary>
    private static Process Start(string program, IReadOnlyList<string> arguments, bool redirectInput)
    {
        ProcessStartInfo startInfo = new(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{program} did not start.");
        if (!redirectInput)
        {
            process.StandardInput.Close();
        }
        return process;
    }

    private static void Stop(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
    }

    private static string HelperPath() =>
        Installation.FindCaptureHelper()
        ?? throw new InvalidOperationException(
            $"{Installation.CaptureHelperFileName} is not in bin/capture; run pwsh run.ps1 itest -Filter \"*CaptureHelperTests\", which publishes it."
        );

    private static string FfmpegPath() =>
        Installation.FindFfmpeg(out _) ?? throw new InvalidOperationException("ffmpeg is not on PATH; install it with winget install Gyan.FFmpeg.");

    private sealed record HelperRun(int ExitCode, string StandardOutput, string StandardError);
}
