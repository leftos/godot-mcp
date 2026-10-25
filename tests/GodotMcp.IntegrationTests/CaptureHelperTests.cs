using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
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
/// cannot see), its games launched unfocused so they leave the foreground alone, and needs the helper published into
/// bin/capture, which the group's gate does first.
/// </summary>
public sealed class CaptureHelperTests : IAsyncDisposable
{
    private const int Fps = 30;
    private static readonly TimeSpan ClipLength = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HelperCeiling = TimeSpan.FromSeconds(60);

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new(launchesUnfocused: true);
    private readonly TempDirectory _temp = new();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _harness.DisposeAsync();
        }
        finally
        {
            _probe.Dispose();
            _temp.Dispose();
        }
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

    /// <summary>
    /// A visible launch leaves the foreground alone: the window shows on the user's desktop without taking focus. It is read
    /// once the game's main window exists, which Godot creates and shows, focused unless told otherwise, before any script runs.
    /// Windows' foreground lock keeps a launch from a background process off the foreground of a desktop in use anyway, so the
    /// window is also checked for the no-activate style an unfocused window is created with, and for not being topmost, which
    /// would keep it above the user's windows.
    /// </summary>
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AVisibleLaunchLeavesTheForegroundAlone()
    {
        nint before = CaptureWindows.Foreground();
        string hwnd = await LaunchVisibleAsync(TestContext.Current.CancellationToken);

        nint after = CaptureWindows.Foreground();
        int game = _harness.Sessions.Resolve(null).GameProcessId ?? throw new InvalidOperationException("the hello carried no game pid.");
        Assert.True(
            CaptureWindows.OwnerOf(after) != game,
            FormattableString.Invariant(
                $"the launch took the foreground: window {before} had it before, and the game's (pid {game}, main window {hwnd}) window {after} after"
            )
        );
        long style = CaptureWindows.ExtendedStyle(nint.Parse(hwnd, CultureInfo.InvariantCulture));
        Assert.True(
            (style & CaptureWindows.NoActivateStyle) != 0,
            FormattableString.Invariant($"the game's main window {hwnd} can be activated (extended style 0x{style:X}): it was not created unfocused")
        );
        Assert.True(
            (style & CaptureWindows.TopmostStyle) == 0,
            FormattableString.Invariant($"the game's main window {hwnd} is topmost (extended style 0x{style:X}): it sits above the user's windows")
        );
    }

    /// <summary>
    /// A visible launch ends behind the window that had the foreground before it, the window the user works in: the
    /// unfocused override shows the game's window minimized and the harness restores it at the bottom of the z-order. The
    /// order is read through GetWindow, walking down from the window that was in front: the push is posted after the
    /// restore and lands a few ms after it, so the wait is a hang guard that ends at once when the game exits or the
    /// harness's restore loop fails. A topmost window in front, such as an always-on-top overlay, has every other window
    /// below it, and the shell's desktop or taskbar stands under every window, so in either case the test cannot tell and
    /// skips. A foreground that changes during the wait raises the window it activates, so the test cannot tell the push
    /// from that activation either, and skips.
    /// </summary>
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AVisibleLaunchEndsBehindTheWindowInFront()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        nint before = CaptureWindows.Foreground();
        Assert.True(before != nint.Zero, "no window had the foreground before the launch, so there is no window to stay in front");
        SkipIfTheForegroundCannotBeBehind(before);
        nint window = nint.Parse(await LaunchVisibleAsync(cancellation), CultureInfo.InvariantCulture);
        using var game = Process.GetProcessById(
            _harness.Sessions.Resolve(null).GameProcessId ?? throw new InvalidOperationException("the hello carried no game pid.")
        );

        bool behind = await Poll.UntilAsync(
            () =>
            {
                CaptureWindows.ThrowIfEnded(_harness, game);
                Assert.True(
                    CaptureWindows.Exists(before),
                    FormattableString.Invariant($"window {before}, which had the foreground before the launch, closed during the test")
                );
                // Read after the z-order, so an activation between the two reads is still seen.
                bool above = CaptureWindows.IsAbove(before, window);
                SkipIfTheForegroundMoved(before);
                return above;
            },
            CaptureWindows.HangGuard,
            cancellation
        );

        Assert.True(
            behind,
            FormattableString.Invariant($"the game's main window {window} is above window {before} (pid {CaptureWindows.OwnerOf(before)}), ")
                + "which had the foreground before the launch"
        );
    }

    /// <summary>
    /// Skips the z-order test when another window has taken the foreground since the launch began: activating a window
    /// raises it, so the z-order could come from that activation rather than the push, and the test can neither pass nor fail.
    /// </summary>
    private static void SkipIfTheForegroundMoved(nint before)
    {
        nint now = CaptureWindows.Foreground();
        if (now != before)
        {
            Assert.Skip(
                $"the foreground changed during the launch (from {Describe(before)} to {Describe(now)}), "
                    + "so the z-order cannot be attributed to the push"
            );
        }
    }

    /// <summary>
    /// Skips the z-order test when the window with the foreground before the launch has no place above the game's window
    /// to keep: a topmost window has every other window below it, and the shell's desktop or taskbar (a shell window class,
    /// or a window of the shell's <c>explorer</c>) stands under every window. A process that has exited since the window
    /// was read is not the shell.
    /// </summary>
    private static void SkipIfTheForegroundCannotBeBehind(nint before)
    {
        int owner = CaptureWindows.OwnerOf(before);
        if ((CaptureWindows.ExtendedStyle(before) & CaptureWindows.TopmostStyle) != 0)
        {
            Assert.Skip(
                FormattableString.Invariant($"window {before} (pid {owner}), which has the foreground, is topmost, ")
                    + "so every other window is below it and the test cannot tell; give a window that is not topmost the foreground"
            );
        }

        string name = ProcessNameOf(owner);
        if (CaptureWindows.HasShellClass(before) || string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip(
                FormattableString.Invariant($"window {before} (pid {owner}, {name}), which has the foreground, ")
                    + "is the shell's desktop or taskbar, which stands under every window, so the test cannot tell; "
                    + "give an application window the foreground"
            );
        }
    }

    /// <summary>A window as its handle, its owner's pid and the owner's image name: <c>hwnd/pid (name)</c>.</summary>
    private static string Describe(nint window)
    {
        int owner = CaptureWindows.OwnerOf(window);
        return FormattableString.Invariant($"{window}/{owner} ({ProcessNameOf(owner)})");
    }

    /// <summary>A process's image name without its extension; <c>exited</c> when it has exited since its window was read.</summary>
    private static string ProcessNameOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception exited) when (exited is ArgumentException or InvalidOperationException)
        {
            return "exited";
        }
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
    /// 8 and the line saying how many frames reached the reader. The helper sees the reader gone at its next frame's write,
    /// which a busy machine can delay by seconds, so the wait on its exit is a hang guard, and the deadline lies past it, so
    /// only the reader's death can end a run that passes.
    /// </summary>
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AReaderThatDiesEndsTheHelperWithExitEight()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string hwnd = await LaunchVisibleAsync(cancellation);
        string deadline = _temp.Combine("deadline.txt");
        WriteDeadline(deadline, DateTime.UtcNow.AddMinutes(10));
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        ceiling.CancelAfter(HelperCeiling);
        using Process helper = Start(HelperPath(), ["--hwnd", hwnd, "--fps", $"{Fps}", "--until-file", deadline], redirectInput: false);
        try
        {
            Task<string> errors = helper.StandardError.ReadToEndAsync(cancellation);
            byte[] frame = new byte[1 << 20];
            int read = await ReadFirstFrameAsync(helper, frame, ceiling.Token, cancellation);
            if (read == 0)
            {
                // Its stderr ends with its exit, which a helper that closed its stdout is making.
                Assert.Fail($"the helper closed its stdout without writing a frame: {await errors}");
            }

            helper.StandardOutput.Close();
            await WaitForExitAsync(helper, "its reader closed", cancellation);
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
        WriteDeadline(deadline, DateTime.UtcNow.AddMinutes(10));
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
            await WaitForExitAsync(helper, "its window closed", cancellation);
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

    /// <summary>
    /// The first frame leaves with the audio clock's start: ffmpeg puts video time zero at the first frame's arrival, so audio
    /// written before it plays that much late against the picture. At 1 fps a first frame held back for one timer tick lets
    /// about a second of audio through first; the bar is a quarter of that and four times the tick at 30 fps. The amount is
    /// read from the bytes on the pipe when the frame arrives, not from elapsed time, and the deadline is a minute out, a hang
    /// guard the test never reaches: it stops the helper once it has its answer.
    /// </summary>
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheFirstFrameLeavesWithTheAudioClocksStart()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string hwnd = await LaunchVisibleAsync(cancellation);
        int game = _harness.Sessions.Resolve(null).GameProcessId ?? throw new InvalidOperationException("the hello carried no game pid.");
        string deadline = _temp.Combine("deadline.txt");
        WriteDeadline(deadline, DateTime.UtcNow.AddMinutes(1));
        string pipe = "gmcp-test-" + Guid.NewGuid().ToString("N");
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        ceiling.CancelAfter(HelperCeiling);
        string[] arguments =
        [
            "--hwnd",
            hwnd,
            "--fps",
            "1",
            "--until-file",
            deadline,
            "--audio-pid",
            game.ToString(CultureInfo.InvariantCulture),
            "--audio-pipe",
            pipe,
        ];
        using Process helper = Start(HelperPath(), arguments, redirectInput: false);
        try
        {
            Task<string> errors = helper.StandardError.ReadToEndAsync(ceiling.Token);
            byte[] frame = new byte[1 << 20];
            Task<int> firstFrame = helper.StandardOutput.BaseStream.ReadAsync(frame, ceiling.Token).AsTask();
            using NamedPipeClientStream audio = new(".", pipe, PipeDirection.In, PipeOptions.Asynchronous);
            Task connecting = audio.ConnectAsync(ceiling.Token);
            Task exited = helper.WaitForExitAsync(ceiling.Token);
            if (await Task.WhenAny(connecting, exited) != connecting)
            {
                Assert.Fail($"the helper exited before its audio pipe opened: {await errors}");
            }
            await connecting;
            Assert.False(firstFrame.IsCompleted, "the first frame arrived before the audio pipe connected, so the audio before it went uncounted");
            long audioBytes = await CountAudioBeforeAsync(audio, firstFrame, ceiling.Token);
            if (await firstFrame == 0)
            {
                Assert.Fail($"the helper closed its stdout without writing a frame: {await errors}");
            }
            double audioMs = audioBytes / 4 / 48.0;
            Assert.True(
                audioMs < 250,
                FormattableString.Invariant($"{audioMs:F0} ms of audio preceded the first frame at 1 fps; the tracks must start together")
            );
        }
        finally
        {
            Stop(helper);
        }
    }

    /// <summary>Launches the probe on this desktop, not quiet but unfocused, and returns its game window's handle as a decimal.</summary>
    private async Task<string> LaunchVisibleAsync(CancellationToken cancellationToken)
    {
        await _harness.Sessions.LaunchAsync(new LaunchRequest(_probe.Directory, null, [], [], false, false, Prepare: true), null, cancellationToken);
        int game = _harness.Sessions.Resolve(null).GameProcessId ?? throw new InvalidOperationException("the hello carried no game pid.");
        using var process = Process.GetProcessById(game);
        nint hwnd = nint.Zero;
        bool found = await Poll.UntilAsync(
            () =>
            {
                CaptureWindows.ThrowIfEnded(_harness, process);
                process.Refresh();
                hwnd = process.MainWindowHandle;
                return hwnd != nint.Zero;
            },
            CaptureWindows.HangGuard,
            cancellationToken
        );
        Assert.True(found, FormattableString.Invariant($"the game (pid {game}) showed no main window in {CaptureWindows.HangGuard.TotalSeconds} s"));
        await CaptureWindows.AssertRestoredAsync(_harness, game, hwnd, cancellationToken);
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

    /// <summary>
    /// Waits for <paramref name="helper"/> to exit after <paramref name="cause"/>, failing when it is still running at the
    /// hang guard, <see cref="HelperCeiling"/>, which a helper that saw its cause never reaches.
    /// </summary>
    private static async Task WaitForExitAsync(Process helper, string cause, CancellationToken cancellationToken)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        guard.CancelAfter(HelperCeiling);
        try
        {
            await helper.WaitForExitAsync(guard.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Assert.Fail(FormattableString.Invariant($"the helper was still running {HelperCeiling.TotalSeconds} s after {cause}"));
        }
    }

    /// <summary>
    /// Reads the helper's first frame into <paramref name="frame"/>: the bytes read, 0 when the helper closed its stdout first,
    /// as it does when no frame came in its own wait. The read ends at <paramref name="ceiling"/>, a hang guard a helper that
    /// sends frames never reaches, and the test fails then saying so.
    /// </summary>
    private static async Task<int> ReadFirstFrameAsync(Process helper, byte[] frame, CancellationToken ceiling, CancellationToken cancellationToken)
    {
        try
        {
            return await helper.StandardOutput.BaseStream.ReadAsync(frame, ceiling);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Assert.Fail(FormattableString.Invariant($"the helper wrote no frame and kept running for {HelperCeiling.TotalSeconds} s"));
            throw;
        }
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
    /// Counts the audio bytes read before <paramref name="firstFrame"/> completes. Audio arrives every 10 ms, so a read
    /// completes soon after the frame does; a read that completes after it is not counted. Reads are small, so one read
    /// in flight when the frame lands moves the count by at most 21 ms of audio.
    /// </summary>
    private static async Task<long> CountAudioBeforeAsync(Stream audio, Task firstFrame, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        long bytes = 0;
        while (true)
        {
            int read = await audio.ReadAsync(buffer, cancellationToken);
            if (read == 0 || firstFrame.IsCompleted)
            {
                return bytes;
            }
            bytes += read;
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
