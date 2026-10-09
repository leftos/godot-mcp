using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// record_mark's real-time recording of a visible InputProbe's window: the hello's window handle, a clip as long as the wall
/// time between its marks and as large as the window, a frame that matches a screenshot of the same still, the .mp4 it
/// finishes as, and the refusal of a quiet run. It runs on the user's desktop (the capture group skips the hidden desktop,
/// which Windows.Graphics.Capture cannot see), with the capture helper the group's gate publishes into bin/capture; the
/// clips are read back with ffprobe and ffmpeg, found beside ffmpeg.
/// </summary>
public sealed class RealtimeRecordingTests : IAsyncDisposable
{
    /// <summary>How long after a stop the helper may still capture: it reads its deadline file every 200 ms.</summary>
    private const double DeadlinePollSeconds = 0.2;

    /// <summary>
    /// How long before start answers a clip may begin: the clip holds every frame from the first, and start answers only once
    /// ffmpeg has opened its encoder, which measured 0.1 to 0.3 s after the first frame (libx264 about 0.15 s, h264_nvenc about
    /// 0.25 s, at -stats_period 0.1); doubled for a loaded machine.
    /// </summary>
    private const double EncoderOpenLagSeconds = 0.6;

    private const double LengthTolerance = 0.2;

    /// <summary>
    /// The mean difference, in levels of 255 per channel, a clip's frame may have from a screenshot of the same still: the
    /// encode to yuv420p H.264 loses colour detail at edges, so the screenshot baselines' 2 levels per pixel cannot hold.
    /// </summary>
    private const double FrameTolerance = 6;

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly RuntimeTools _tools;

    // Whether a start answered with no stop after it, so a test that failed between them still ends its recording.
    private bool _recording;

    public RealtimeRecordingTests() => _tools = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopLeftRecordingAsync();
        }
        finally
        {
            await _harness.DisposeAsync();
            _probe.Dispose();
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheHelloWindowHandleIsTheGamesMainWindow()
    {
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        GodotSession session = _harness.Sessions.Resolve(null);
        using var game = Process.GetProcessById(session.GameProcessId ?? throw new InvalidOperationException("the hello carried no game pid."));
        nint main = nint.Zero;

        bool found = await Poll.UntilAsync(
            () =>
            {
                game.Refresh();
                main = game.MainWindowHandle;
                return main != nint.Zero;
            },
            TimeSpan.FromSeconds(10),
            Token
        );

        Assert.True(found, $"the game (pid {game.Id}) showed no main window in 10 s");
        Assert.Equal((long)main, session.WindowHandle);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ADragOfKnownDurationMakesAClipOfTheWallClockBetweenTheMarks()
    {
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        var wall = Stopwatch.StartNew();
        await MarkAsync("start");
        TimeSpan started = wall.Elapsed;

        await _tools.DragAsync(new(null, 100, 100), new(null, 300, 200), 1500, "left", cancellationToken: Token);
        TimeSpan stopping = wall.Elapsed;
        JsonNode stopped = await MarkAsync("stop");

        double between = (stopping - started).TotalSeconds;
        (double seconds, _, _) = await ProbeClipAsync(ClipPath(stopped));
        // The clip may run longer than the marks' gap by the encoder-open lag before start answered and the deadline poll after
        // stop; it may not run shorter than the gap beyond the probe's rounding.
        double longest = between + DeadlinePollSeconds + EncoderOpenLagSeconds;
        Assert.True(
            seconds >= between - LengthTolerance && seconds <= longest,
            FormattableString.Invariant($"the clip lasts {seconds:F3} s; the marks were {between:F3} s apart, ")
                + FormattableString.Invariant($"so it must last {between - LengthTolerance:F3} to {longest:F3} s: ")
                + FormattableString.Invariant(
                    $"at least the gap less {LengthTolerance} s, at most the gap plus {DeadlinePollSeconds} s (the helper reads its deadline "
                )
                + FormattableString.Invariant(
                    $"every 0.2 s after stop) plus {EncoderOpenLagSeconds} s (the clip begins at the first frame, up to about 0.3 s before "
                )
                + "start answers once ffmpeg has opened its encoder)"
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheClipSizeIsTheWindowSizeTheLaunchReported()
    {
        LaunchResult launched = await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        WindowSize window = launched.Window ?? throw new InvalidOperationException("run_project reported no window size.");
        JsonNode started = await MarkAsync("start");
        await Task.Delay(500, Token);
        JsonNode stopped = await MarkAsync("stop");

        (_, int width, int height) = await ProbeClipAsync(ClipPath(stopped));

        int evenWidth = window.Width + (window.Width & 1);
        int evenHeight = window.Height + (window.Height & 1);
        Assert.Equal((evenWidth, evenHeight), (width, height));
        Assert.Equal(evenWidth, started["width"]!.GetValue<int>());
        Assert.Equal(evenHeight, started["height"]!.GetValue<int>());
        Assert.Equal(evenWidth, stopped["clip"]!["width"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AFrameMatchesATakeScreenshotOfTheSameMoment()
    {
        LaunchResult launched = await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        WindowSize window = launched.Window ?? throw new InvalidOperationException("run_project reported no window size.");
        await _tools.FrameControlAsync("pause", cancellationToken: Token);
        await MarkAsync("start");
        await Task.Delay(1000, Token);
        List<ContentBlock> blocks = [.. await _tools.TakeScreenshotAsync("path_only", cancellationToken: Token)];
        string screenshot = JsonNode.Parse(string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text)))!["path"]!.GetValue<string>();
        await Task.Delay(500, Token);
        JsonNode stopped = await MarkAsync("stop");

        string size = FormattableString.Invariant($"{window.Width}:{window.Height}");
        byte[] frame = await RawFrameAsync(["-sseof", "-0.3", "-i", ClipPath(stopped), "-frames:v", "1", "-vf", $"crop={size}:0:0"]);
        byte[] shot = await RawFrameAsync(["-i", screenshot, "-frames:v", "1", "-vf", $"scale={size}"]);

        Assert.Equal(shot.Length, frame.Length);
        double mean = MeanDifference(frame, shot);
        Assert.True(
            mean <= FrameTolerance,
            FormattableString.Invariant($"the clip's frame differs from the screenshot by {mean:F2} levels on average")
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AQuietRunIsRefusedWithTheFix()
    {
        await LaunchAsync(quiet: true, TestContext.Current.CancellationToken);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.RecordMarkAsync("start", cancellationToken: Token));

        Assert.Equal(
            "Session 'InputProbe' is quiet, so its window is on the server's hidden desktop, which a real-time recording cannot see. "
                + "Launch it with options.quiet: false (options.mute: true keeps it silent), or with options.record for a Movie Maker "
                + "recording.",
            refused.Message
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheMkvIsGoneAndThePathIsAnMp4()
    {
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        JsonNode started = await MarkAsync("start");
        await Task.Delay(500, Token);
        JsonNode stopped = await MarkAsync("stop");

        string path = started["path"]!.GetValue<string>();
        string recordings = Path.Combine(ProjectPaths.Normalise(_probe.Directory), ".godot", "godot-mcp", "recordings");
        Assert.Equal(recordings, Path.GetDirectoryName(path));
        Assert.EndsWith("-InputProbe-realtime.mp4", path, StringComparison.Ordinal);
        Assert.Equal(path, ClipPath(stopped));
        Assert.True(File.Exists(path), $"the clip {path} was not written");
        Assert.False(File.Exists(Path.ChangeExtension(path, ".mkv")), "the .mkv was not deleted after the remux");
        Assert.Equal("realtime", stopped["mode"]!.GetValue<string>());
    }

    private Task<LaunchResult> LaunchAsync(bool quiet, CancellationToken cancellationToken) =>
        _harness.Sessions.LaunchAsync(new LaunchRequest(_probe.Directory, null, [], [], quiet, false, Prepare: true), null, cancellationToken);

    /// <summary>
    /// Stops a recording a failed test left running, before the project copy goes, so its processes and files end with it; a
    /// stop that fails is written to the test's output, since the test has already failed.
    /// </summary>
    private async Task StopLeftRecordingAsync()
    {
        if (!_recording)
        {
            return;
        }

        try
        {
            await _tools.RecordMarkAsync("stop", cancellationToken: CancellationToken.None);
        }
        catch (McpException e)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"the recording the test left running did not stop cleanly: {e.Message}");
        }
    }

    private async Task<JsonNode> MarkAsync(string mark)
    {
        string answer = await _tools.RecordMarkAsync(mark, cancellationToken: Token);
        _recording = mark == "start";
        JsonNode reply = JsonNode.Parse(answer)!;
        Assert.Equal(mark, reply["mark"]!.GetValue<string>());
        Assert.Equal("realtime", reply["mode"]!.GetValue<string>());
        return reply;
    }

    private static string ClipPath(JsonNode stopped) => stopped["clip"]!["path"]!.GetValue<string>();

    private static double MeanDifference(byte[] first, byte[] second)
    {
        long total = 0;
        for (int i = 0; i < first.Length; i++)
        {
            total += Math.Abs(first[i] - second[i]);
        }

        return (double)total / first.Length;
    }

    /// <summary>One frame as raw rgb24 bytes, through ffmpeg with <paramref name="input"/>'s input and filter arguments.</summary>
    private static async Task<byte[]> RawFrameAsync(IReadOnlyList<string> input)
    {
        (byte[] output, string errors, int exitCode) = await RunAsync(
            FfmpegPath(),
            ["-v", "error", .. input, "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]
        );
        Assert.True(exitCode == 0 && output.Length > 0, $"ffmpeg read no frame ({string.Join(' ', input)}): {errors}");
        return output;
    }

    /// <summary>The clip's length in seconds and its frame size, as ffprobe reads them.</summary>
    private static async Task<(double Seconds, int Width, int Height)> ProbeClipAsync(string clip)
    {
        string ffmpeg = FfmpegPath();
        string ffprobe = Path.Combine(
            Path.GetDirectoryName(ffmpeg)!,
            Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.Ordinal)
        );
        (byte[] output, string errors, int exitCode) = await RunAsync(
            ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "format=duration:stream=width,height", "-of", "json", clip]
        );
        Assert.True(exitCode == 0, $"ffprobe failed on {clip}: {errors}");
        using var probed = JsonDocument.Parse(output);
        JsonElement stream = probed.RootElement.GetProperty("streams")[0];
        string duration = probed.RootElement.GetProperty("format").GetProperty("duration").GetString()!;
        return (
            double.Parse(duration, CultureInfo.InvariantCulture),
            stream.GetProperty("width").GetInt32(),
            stream.GetProperty("height").GetInt32()
        );
    }

    private static async Task<(byte[] Output, string Errors, int ExitCode)> RunAsync(string program, IReadOnlyList<string> arguments)
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

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{program} did not start.");
        process.StandardInput.Close();
        Task<string> errors = process.StandardError.ReadToEndAsync(Token);
        using MemoryStream output = new();
        await process.StandardOutput.BaseStream.CopyToAsync(output, Token);
        await process.WaitForExitAsync(Token);
        return (output.ToArray(), await errors, process.ExitCode);
    }

    private static string FfmpegPath() =>
        Installation.FindFfmpeg(out _) ?? throw new InvalidOperationException("ffmpeg is not on PATH; install it with winget install Gyan.FFmpeg.");
}
