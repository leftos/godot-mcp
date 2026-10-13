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
/// which Windows.Graphics.Capture cannot see), its games launched unfocused so they leave the foreground alone, with the
/// capture helper the group's gate publishes into bin/capture; the
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

    /// <summary>The probe's session, named for its folder.</summary>
    private const string Session = "InputProbe";

    private const string QuitSoonScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\tscene_tree.create_timer(0.3).timeout.connect(scene_tree.quit)\n\treturn true\n";

    private static readonly string[] GodotMcpVariables =
    [
        GodotCommandLine.PortVariable,
        GodotCommandLine.TokenVariable,
        GodotCommandLine.QuietVariable,
        GodotCommandLine.HiddenDesktopVariable,
    ];

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new(launchesUnfocused: true);
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
            try
            {
                await _harness.DisposeAsync();
            }
            finally
            {
                _probe.Dispose();
            }
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

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AStopProjectEndsTheRecordingAtTheStopAndReturnsItsClip()
    {
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        var wall = Stopwatch.StartNew();
        string path = (await MarkAsync("start"))["path"]!.GetValue<string>();
        TimeSpan started = wall.Elapsed;
        RecordingResult running = ListedRecording() ?? throw new InvalidOperationException("list_sessions showed no recording.");
        await Task.Delay(1000, Token);
        TimeSpan stopping = wall.Elapsed;
        StopResult stopped = await _harness.Sessions.StopAsync(Session, Token);
        _recording = false;

        Assert.Equal(
            new RecordingResult
            {
                Mode = "realtime",
                Path = path,
                StartedSeconds = running.StartedSeconds,
            },
            running
        );
        RecordingResult recording = stopped.Recording ?? throw new InvalidOperationException("stop_project returned no recording.");
        Assert.Equal("realtime", recording.Mode);
        Assert.Equal(path, Assert.Single(recording.Clips!));
        Assert.Equal(running.StartedSeconds, recording.StartedSeconds);
        Assert.Null(recording.Error);
        Assert.False(File.Exists(Path.ChangeExtension(path, ".mkv")), "the .mkv was not deleted after the remux");
        double between = (stopping - started).TotalSeconds;
        (double seconds, _, _) = await ProbeClipAsync(path);
        double longest = between + DeadlinePollSeconds + EncoderOpenLagSeconds;
        Assert.True(
            seconds >= between - LengthTolerance && seconds <= longest,
            FormattableString.Invariant($"the clip lasts {seconds:F3} s; start answered and stop_project was called {between:F3} s apart, ")
                + FormattableString.Invariant($"so it must last {between - LengthTolerance:F3} to {longest:F3} s")
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARestartReturnsTheOldGamesClipAsPreviousRecording()
    {
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        // Only a lower bound: a busy machine lengthens the gap between the launch and the start, never shortens it.
        await Task.Delay(1100, Token);
        string path = (await MarkAsync("start"))["path"]!.GetValue<string>();
        RecordingResult running = ListedRecording() ?? throw new InvalidOperationException("list_sessions showed no recording.");
        await Task.Delay(500, Token);

        var sinceRestart = Stopwatch.StartNew();
        RestartResult restarted = await _harness.Sessions.RestartAsync(Session, prepare: false, Token);
        _recording = false;
        RecordingResult? listedAfterRestart = ListedRecording();
        await AssertWindowRestoredAsync(Token);
        string nextPath = (await MarkAsync("start"))["path"]!.GetValue<string>();
        RecordingResult next = ListedRecording() ?? throw new InvalidOperationException("list_sessions showed no recording after the restart.");
        double nextBound = sinceRestart.Elapsed.TotalSeconds;
        JsonNode nextStopped = await MarkAsync("stop");

        RecordingResult previous =
            restarted.PreviousRecording ?? throw new InvalidOperationException("restart_project returned no previousRecording.");
        Assert.Equal("realtime", previous.Mode);
        Assert.Equal(path, Assert.Single(previous.Clips!));
        Assert.Null(previous.Error);
        Assert.True(running.StartedSeconds >= 1, $"startedSeconds is {running.StartedSeconds}; the start came over 1 s after the launch");
        Assert.Equal(running.StartedSeconds, previous.StartedSeconds);
        (double seconds, _, _) = await ProbeClipAsync(path);
        Assert.True(seconds >= 0.5 - LengthTolerance, FormattableString.Invariant($"the old game's clip lasts {seconds:F3} s"));
        Assert.Null(restarted.Recording);
        Assert.Null(listedAfterRestart);
        // The new game's recording starts normally, and counts from the new game's launch, which came after the restart was asked for.
        Assert.NotEqual(path, nextPath);
        Assert.True(
            next.StartedSeconds <= nextBound,
            FormattableString.Invariant($"startedSeconds is {next.StartedSeconds}, but the restart was asked for {nextBound:F1} s before")
        );
        Assert.Equal(nextPath, ClipPath(nextStopped));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGameThatQuitsKeepsItsClipForTheNextStop()
    {
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        string path = (await MarkAsync("start"))["path"]!.GetValue<string>();
        await Task.Delay(500, Token);

        await _tools.RunScriptAsync(QuitSoonScript, 10_000, cancellationToken: Token);
        bool finished = await Poll.UntilAsync(() => ListedRecording()?.Clips is not null, CaptureWindows.HangGuard, Token);
        RecordingResult? listed = ListedRecording();
        StopResult stopped = await _harness.Sessions.StopAsync(Session, Token);
        RecordingResult? listedAfterStop = ListedRecording();
        StopResult again = await _harness.Sessions.StopAsync(Session, Token);
        _recording = false;

        Assert.True(finished, "list_sessions showed no clip after the game quit");
        Assert.Equal(path, Assert.Single(listed!.Clips!));
        Assert.Equal(path, Assert.Single(stopped.Recording?.Clips ?? []));
        Assert.True(stopped.AlreadyExited);
        Assert.Null(listedAfterStop);
        Assert.Null(again.Recording);
        (double seconds, _, _) = await ProbeClipAsync(path);
        Assert.True(seconds >= 0.5 - LengthTolerance, FormattableString.Invariant($"the clip lasts {seconds:F3} s"));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AMaxSecondsEndFinishesTheClipForTheNextStopWithNoWarning()
    {
        const int MaxSeconds = 2;
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        string answer = await _tools.RecordMarkAsync("start", new RecordMarkOptions(MaxSeconds: MaxSeconds), cancellationToken: Token);
        _recording = true;
        string path = JsonNode.Parse(answer)!["path"]!.GetValue<string>();

        bool finished = await Poll.UntilAsync(() => ListedRecording()?.Clips is not null, CaptureWindows.HangGuard, Token);
        StopResult stopped = await _harness.Sessions.StopAsync(Session, Token);
        _recording = false;

        Assert.True(finished, "list_sessions showed no clip after maxSeconds");
        RecordingResult recording = stopped.Recording ?? throw new InvalidOperationException("stop_project returned no recording.");
        Assert.Equal(path, Assert.Single(recording.Clips!));
        Assert.Null(recording.Error);
        // A busy machine may warn of a low frame rate; the cap itself is no warning.
        Assert.True(recording.Warning is null || recording.Warning.StartsWith("the capture got", StringComparison.Ordinal), recording.Warning);
        (double seconds, _, _) = await ProbeClipAsync(path);
        double longest = MaxSeconds + DeadlinePollSeconds + LengthTolerance;
        Assert.True(
            seconds >= MaxSeconds - LengthTolerance && seconds <= longest,
            FormattableString.Invariant($"the clip lasts {seconds:F3} s; with maxSeconds {MaxSeconds} it must last ")
                + FormattableString.Invariant($"{MaxSeconds - LengthTolerance:F3} to {longest:F3} s")
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ADetachWaitsForTheClipAndReportsIt()
    {
        Task<AttachResult> attach = _harness.Sessions.AttachAsync(
            new AttachRequest(_probe.Directory, null, TimeSpan.FromSeconds(60), null, false, null),
            TestContext.Current.CancellationToken
        );
        Assert.True(await Poll.UntilAsync(() => File.Exists(AttachFile.PathIn(_probe.Directory)), TimeSpan.FromSeconds(10), Token));
        using Process game = StartGameByHand();
        try
        {
            await attach;
            GodotSession session = _harness.Sessions.Resolve(null);
            int gameProcessId = session.GameProcessId ?? throw new InvalidOperationException("the hello carried no game pid.");
            nint window = (nint)(session.WindowHandle ?? throw new InvalidOperationException("the hello carried no window handle."));
            // The harness restores only the windows of runs; an attached game's window, shown minimized, is restored here.
            if (CaptureWindows.IsMinimized(window))
            {
                CaptureWindows.RestoreBehind(window);
            }

            await CaptureWindows.AssertRestoredAsync(_harness, gameProcessId, window, Token);
            string path = (await MarkAsync("start"))["path"]!.GetValue<string>();
            await Task.Delay(500, Token);

            DetachResult detached = await _harness.Sessions.DetachAsync(null, Token);
            _recording = false;

            RecordingResult recording = detached.Recording ?? throw new InvalidOperationException("detach_project returned no recording.");
            Assert.Equal("realtime", recording.Mode);
            Assert.Equal(path, Assert.Single(recording.Clips!));
            Assert.True(File.Exists(path), $"the clip {path} was not finished when detach_project returned");
            Assert.False(File.Exists(Path.ChangeExtension(path, ".mkv")), "the .mkv was left behind");
            (double seconds, _, _) = await ProbeClipAsync(path);
            Assert.True(seconds >= 0.5 - LengthTolerance, FormattableString.Invariant($"the clip lasts {seconds:F3} s"));
        }
        finally
        {
            // A detached game keeps running, so it is ended here, before the probe's folder goes.
            game.Kill(entireProcessTree: true);
            await ProcessExit.WaitUntilGoneAsync(game, TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Launches the probe; a visible launch is handed over only once its window is restored behind the user's windows.</summary>
    private async Task<LaunchResult> LaunchAsync(bool quiet, CancellationToken cancellationToken)
    {
        LaunchResult launched = await _harness.Sessions.LaunchAsync(
            new LaunchRequest(_probe.Directory, null, [], [], quiet, false, Prepare: true),
            null,
            cancellationToken
        );
        if (!quiet)
        {
            await AssertWindowRestoredAsync(cancellationToken);
        }

        return launched;
    }

    /// <summary>Waits until the session's game window, as its latest hello names it, is restored behind the user's windows.</summary>
    private async Task AssertWindowRestoredAsync(CancellationToken cancellationToken)
    {
        GodotSession session = _harness.Sessions.Resolve(null);
        int game = session.GameProcessId ?? throw new InvalidOperationException("the hello carried no game pid.");
        long window = session.WindowHandle ?? throw new InvalidOperationException("the hello carried no window handle.");
        await CaptureWindows.AssertRestoredAsync(_harness, game, (nint)window, cancellationToken);
    }

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

    private RecordingResult? ListedRecording() => Assert.Single(_harness.Sessions.List(includeStopped: true)).Recording;

    /// <summary>
    /// Godot on the probe as a user's script would start it, with no GODOT_MCP_* variables, so its bridge finds only the
    /// attach file; its window shows unfocused and minimized, as the harness's override asks.
    /// </summary>
    private Process StartGameByHand()
    {
        ProcessStartInfo startInfo = new(Installation.FindGodot())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(_probe.Directory);
        foreach (string variable in GodotMcpVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        Process game = Process.Start(startInfo) ?? throw new InvalidOperationException("Godot did not start.");
        game.StandardInput.Close();
        game.BeginOutputReadLine();
        game.BeginErrorReadLine();
        return game;
    }

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
