using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// finishes as, the refusal of a quiet run, and the game's own sound as the clip's audio track, absent with a warning on a
/// muted run and absent without one when options.audio is false. It runs on the user's desktop (the capture group skips the
/// hidden desktop, which Windows.Graphics.Capture cannot see), its games launched unfocused so they leave the foreground
/// alone, with the capture helper the group's gate publishes into bin/capture; the clips are read back with ffprobe and
/// ffmpeg, found beside ffmpeg.
/// </summary>
public sealed partial class RealtimeRecordingTests : IAsyncDisposable
{
    /// <summary>
    /// The loudest the clip's audio may peak and still count as silence: the tone is sent at about -18 dBFS (half of full
    /// scale through a -12 dB player), and the spike measured a muted run's track at -96.7 dB.
    /// </summary>
    private const double SilenceDb = -60;

    /// <summary>How long after a stop the helper may still capture: it reads its deadline file every 200 ms.</summary>
    private const double DeadlinePollSeconds = 0.2;

    private const double LengthTolerance = 0.2;

    /// <summary>The capture helper's line when the window's closing ended its capture.</summary>
    private const string WindowClosedLine = "the window closed, so the capture ended";

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

    // A node whose exit blocks the game's main thread for 30 s: the game acknowledges a quit and holds its window open past
    // any stop's grace, until the stop kills it.
    private const string HoldsQuitScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\tvar script := GDScript.new()\n"
        + "\tscript.source_code = \"extends Node\\n\\n\\nfunc _exit_tree() -> void:\\n\\tOS.delay_msec(30000)\\n\"\n"
        + "\tscript.reload()\n"
        + "\tvar node := Node.new()\n"
        + "\tnode.set_script(script)\n"
        + "\tscene_tree.root.add_child(node)\n"
        + "\treturn true\n";

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
                CaptureWindows.ThrowIfEnded(_harness, game);
                game.Refresh();
                main = game.MainWindowHandle;
                return main != nint.Zero;
            },
            CaptureWindows.HangGuard,
            Token
        );

        Assert.True(
            found,
            FormattableString.Invariant($"the game (pid {game.Id}) showed no main window in {CaptureWindows.HangGuard.TotalSeconds} s")
        );
        Assert.Equal((long)main, session.WindowHandle);
    }

    /// <summary>
    /// The clip spans the marks: it holds every frame from the first, which reaches ffmpeg before start answers, to the
    /// capture's end, which comes after stop is called, so it lasts at least the time between start's answer and stop's call.
    /// Every frame it holds was stamped after start was asked for and before stop answered, so it lasts at most that.
    /// Both bounds are read from the marks' own calls and answers, so a busy machine moves them with the clip: the encoder's
    /// opening, which start waits for, can take seconds then, and the clip starts that long before start answers.
    /// </summary>
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
        TimeSpan answered = wall.Elapsed;

        double between = (stopping - started).TotalSeconds;
        double around = answered.TotalSeconds;
        (double seconds, _, _) = await ProbeClipAsync(ClipPath(stopped));
        Assert.True(
            seconds >= between - LengthTolerance && seconds <= around + LengthTolerance,
            FormattableString.Invariant($"the clip lasts {seconds:F3} s; start answered and stop was called {between:F3} s apart, ")
                + FormattableString.Invariant($"and start was asked for and stop answered {around:F3} s apart, so it must last ")
                + FormattableString.Invariant($"{between - LengthTolerance:F3} to {around + LengthTolerance:F3} s")
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
        (int R, int G, int B)? flat = FlatColour(frame);
        Assert.True(
            flat is null,
            $"the clip's frame is one flat colour ({flat?.R},{flat?.G},{flat?.B}): the game window drew nothing on screen; "
                + "check that a Compatibility-renderer window shows its picture on this machine"
        );
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

    /// <summary>
    /// A stop_project ends the capture before it quits the game, so the clip ends at the stop rather than at the window's
    /// teardown. The order is read from the capture helper's log, which says when the window's closing ended the capture.
    /// The game holds its quit in a node's exit for longer than the stop's grace, so its window stays until the stop kills it
    /// seconds after the quit was asked for, and the helper, which reads its deadline every 0.2 s, has seen the stop long
    /// before; a stop that quit the game first would leave the capture running until the kill closed the window.
    /// </summary>
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AStopProjectEndsTheRecordingAtTheStopAndReturnsItsClip()
    {
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        await _tools.RunScriptAsync(HoldsQuitScript, 10_000, cancellationToken: Token);
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
        Assert.True(
            seconds >= between - LengthTolerance,
            FormattableString.Invariant($"the clip lasts {seconds:F3} s; start answered and stop_project was called {between:F3} s apart, ")
                + FormattableString.Invariant($"so it must last at least {between - LengthTolerance:F3} s")
        );
        string said = ReadCaptureLog(path);
        // The helper's frame count is its last line, so a log that has it holds every line the capture's end wrote.
        Assert.Contains(" frames in ", said, StringComparison.Ordinal);
        Assert.False(
            said.Contains(WindowClosedLine, StringComparison.Ordinal),
            $"the capture ended when the game's window closed, not at the stop before the quit; the helper said: {said}"
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
        var restarted = (RestartResult)await _harness.Sessions.RestartAsync(Session, prepare: false, Token);
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

    /// <summary>
    /// A maxSeconds cap ends the capture by itself, and the clip waits for the next stop. The cap counts from the first frame,
    /// so the clip lasts at least the cap. How far past it the capture ran is read from the helper's frame count, which a
    /// busy machine can only lower: the helper writes at most one frame per tick of its rate, from after start was asked
    /// for until its deadline, the first frame's time plus the cap, which start moves there once the encoder has opened and
    /// the helper reads within 0.2 s. A cap the capture ignored, or counted from start's own allowance, runs to many more.
    /// </summary>
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AMaxSecondsEndFinishesTheClipForTheNextStopWithNoWarning()
    {
        const int MaxSeconds = 2;
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        var wall = Stopwatch.StartNew();
        string answer = await _tools.RecordMarkAsync("start", new RecordMarkOptions(MaxSeconds: MaxSeconds), cancellationToken: Token);
        double startTook = wall.Elapsed.TotalSeconds;
        _recording = true;
        JsonNode started = JsonNode.Parse(answer)!;
        string path = started["path"]!.GetValue<string>();
        int fps = started["fps"]!.GetValue<int>();

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
        Assert.True(
            seconds >= MaxSeconds - LengthTolerance,
            FormattableString.Invariant($"the clip lasts {seconds:F3} s; with maxSeconds {MaxSeconds} it must last at least ")
                + FormattableString.Invariant($"{MaxSeconds - LengthTolerance:F3} s")
        );
        string said = ReadCaptureLog(path);
        Match written = FramesLine().Matches(said).LastOrDefault() ?? throw new InvalidOperationException($"the helper said no frame count: {said}");
        int frames = int.Parse(written.Groups[1].Value, CultureInfo.InvariantCulture);
        // The first frame goes out before the helper's timer starts, and a tick the helper missed fires once when it catches up.
        double most = fps * (MaxSeconds + startTook + DeadlinePollSeconds) + 2;
        Assert.True(
            frames <= most,
            FormattableString.Invariant($"the helper wrote {frames} frames at {fps} fps; start took {startTook:F3} s and maxSeconds is ")
                + FormattableString.Invariant($"{MaxSeconds}, so a capture the cap ended writes at most {most:F0}; the helper said: {said}")
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ADetachWaitsForTheClipAndReportsIt()
    {
        Task<AttachResult> attach = _harness.Sessions.AttachAsync(
            new AttachRequest(_probe.Directory, null, TimeSpan.FromSeconds(60), null, false, null),
            TestContext.Current.CancellationToken
        );
        string attachFile = AttachFile.PathIn(_probe.Directory);
        bool written = await Poll.UntilAsync(() => attach.IsCompleted || File.Exists(attachFile), CaptureWindows.HangGuard, Token);
        if (attach.IsFaulted)
        {
            // The attach failed before a game could answer it; awaiting it fails the test with the attach's own error.
            await attach;
        }

        Assert.True(
            written && File.Exists(attachFile),
            FormattableString.Invariant($"attach_project wrote no attach file {attachFile} in {CaptureWindows.HangGuard.TotalSeconds} s")
        );
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

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AWindowMinimizedMidRecordingIsWarnedOf()
    {
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        long window = _harness.Sessions.Resolve(null).WindowHandle ?? throw new InvalidOperationException("the hello carried no window handle.");
        await MarkAsync("start");

        CaptureWindows.MinimizeWindow((nint)window);
        bool minimized = await Poll.UntilAsync(() => CaptureWindows.IsMinimized((nint)window), CaptureWindows.HangGuard, Token);
        Assert.True(
            minimized,
            FormattableString.Invariant($"the game's window {window} was not minimized in {CaptureWindows.HangGuard.TotalSeconds} s")
        );
        await Task.Delay(500, Token);
        CaptureWindows.RestoreBehind((nint)window);
        await AssertWindowRestoredAsync(Token);
        JsonNode stopped = await MarkAsync("stop");

        string warning = stopped["warning"]?.GetValue<string>() ?? "";
        Assert.Contains("the game's window was minimized from", warning, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AToneTheGamePlaysIsInTheClipsAudioTrack()
    {
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        JsonNode started = await MarkAsync("start");
        await _tools.ClickAsync(new InputTarget("ToneButton"), cancellationToken: Token);
        await Task.Delay(2500, Token);
        JsonNode stopped = await MarkAsync("stop");

        string clip = ClipPath(stopped);
        Assert.True(started["audio"]!.GetValue<bool>(), $"start says the clip has no audio track: {started.ToJsonString()}");
        Assert.True(stopped["clip"]!["audio"]!.GetValue<bool>(), $"stop says the clip has no audio track: {stopped.ToJsonString()}");
        Assert.Equal(1, await AudioStreamCountAsync(clip));
        double maxVolume = await MaxVolumeAsync(clip);
        TestContext.Current.TestOutputHelper?.WriteLine(FormattableString.Invariant($"the clip's audio peaks at {maxVolume:F1} dB"));
        Assert.True(
            maxVolume > SilenceDb,
            FormattableString.Invariant($"the clip's audio peaks at {maxVolume:F1} dB, no louder than {SilenceDb} dB: the tone is not in it")
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AMutedRunRecordsNoAudioTrackAndSaysWhy()
    {
        const string NoTrack = $"no audio: session '{Session}' renders no sound (options.mute or a quiet run), so the clip has no audio track";
        await LaunchAsync(
            new LaunchRequest(_probe.Directory, null, [], [], false, false, Prepare: true) { Mute = true },
            TestContext.Current.CancellationToken
        );
        JsonNode started = await MarkAsync("start");
        await Task.Delay(500, Token);
        JsonNode stopped = await MarkAsync("stop");

        Assert.False(started["audio"]!.GetValue<bool>());
        Assert.Contains(NoTrack, Warnings(started));
        Assert.False(stopped["clip"]!["audio"]!.GetValue<bool>());
        Assert.Contains(NoTrack, Warnings(stopped));
        Assert.Equal(0, await AudioStreamCountAsync(ClipPath(stopped)));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AudioFalseRecordsNoAudioTrackAndWarnsOfNone()
    {
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        string answer = await _tools.RecordMarkAsync("start", new RecordMarkOptions(Audio: false), cancellationToken: Token);
        _recording = true;
        JsonNode started = JsonNode.Parse(answer)!;
        await Task.Delay(500, Token);
        JsonNode stopped = await MarkAsync("stop");

        Assert.False(started["audio"]!.GetValue<bool>());
        Assert.False(stopped["clip"]!["audio"]!.GetValue<bool>());
        // Only the audio's warnings are ruled out: a busy machine may warn of a low frame rate.
        Assert.DoesNotContain(Warnings(started), warning => warning.StartsWith("no audio", StringComparison.Ordinal));
        Assert.DoesNotContain(Warnings(stopped), warning => warning.StartsWith("no audio", StringComparison.Ordinal));
        Assert.Equal(0, await AudioStreamCountAsync(ClipPath(stopped)));
    }

    /// <summary>Launches the probe; a visible launch is handed over only once its window is restored behind the user's windows.</summary>
    private Task<LaunchResult> LaunchAsync(bool quiet, CancellationToken cancellationToken) =>
        LaunchAsync(new LaunchRequest(_probe.Directory, null, [], [], quiet, false, Prepare: true), cancellationToken);

    /// <summary>Launches the probe as <paramref name="request"/> asks, handed over as the other launch is.</summary>
    private async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        LaunchResult launched = await _harness.Sessions.LaunchAsync(request, null, cancellationToken);
        if (!request.Quiet)
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

    /// <summary>A start's or stop's warnings, each one part of its "; "-joined warning; none when it has no warning.</summary>
    private static string[] Warnings(JsonNode marked) => marked["warning"]?.GetValue<string>().Split("; ") ?? [];

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

    /// <summary>
    /// The capture helper's log for the clip at <paramref name="clipPath"/>, which the recording keeps beside it as
    /// <c>&lt;stem&gt;-capture.log</c>; read once the recording has finished, opened to share with any writer still closing it.
    /// </summary>
    private static string ReadCaptureLog(string clipPath)
    {
        string log = Path.ChangeExtension(clipPath, null) + "-capture.log";
        using FileStream stream = new(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
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

    /// <summary>
    /// The colour an rgb24 frame is flat in: its most common colour when at least 99% of its pixels are within 2 levels of it,
    /// channel by channel (a blank clip still carries a few stray pixels); null when the frame shows a picture.
    /// </summary>
    private static (int R, int G, int B)? FlatColour(byte[] rgb)
    {
        (int R, int G, int B) top = MostCommonColour(rgb);
        long pixels = rgb.Length / 3;
        long near = 0;
        for (int i = 0; i + 2 < rgb.Length; i += 3)
        {
            if (Math.Abs(rgb[i] - top.R) <= 2 && Math.Abs(rgb[i + 1] - top.G) <= 2 && Math.Abs(rgb[i + 2] - top.B) <= 2)
            {
                near++;
            }
        }

        return near * 100 >= pixels * 99 ? top : null;
    }

    /// <summary>The colour the most pixels of an rgb24 frame have.</summary>
    private static (int R, int G, int B) MostCommonColour(byte[] rgb)
    {
        Dictionary<int, int> counts = [];
        for (int i = 0; i + 2 < rgb.Length; i += 3)
        {
            int key = (rgb[i] << 16) | (rgb[i + 1] << 8) | rgb[i + 2];
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        int colour = counts.MaxBy(pair => pair.Value).Key;
        return (colour >> 16, (colour >> 8) & 0xFF, colour & 0xFF);
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

    /// <summary>
    /// The length in seconds of the clip's video stream, not the container's, which an audio track may make longer, and its
    /// frame size, as ffprobe reads them.
    /// </summary>
    private static async Task<(double Seconds, int Width, int Height)> ProbeClipAsync(string clip)
    {
        (byte[] output, string errors, int exitCode) = await RunAsync(
            FfprobePath(),
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration,width,height", "-of", "json", clip]
        );
        Assert.True(exitCode == 0, $"ffprobe failed on {clip}: {errors}");
        using var probed = JsonDocument.Parse(output);
        JsonElement stream = probed.RootElement.GetProperty("streams")[0];
        string duration = stream.GetProperty("duration").GetString()!;
        return (
            double.Parse(duration, CultureInfo.InvariantCulture),
            stream.GetProperty("width").GetInt32(),
            stream.GetProperty("height").GetInt32()
        );
    }

    /// <summary>The clip's audio streams, as ffprobe counts them.</summary>
    private static async Task<int> AudioStreamCountAsync(string clip)
    {
        (byte[] output, string errors, int exitCode) = await RunAsync(
            FfprobePath(),
            ["-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_name", "-of", "json", clip]
        );
        Assert.True(exitCode == 0, $"ffprobe failed on {clip}: {errors}");
        using var probed = JsonDocument.Parse(output);
        return probed.RootElement.TryGetProperty("streams", out JsonElement streams) ? streams.GetArrayLength() : 0;
    }

    /// <summary>The loudest sample of the clip's first audio stream, in dB, as ffmpeg's volumedetect reads it.</summary>
    private static async Task<double> MaxVolumeAsync(string clip)
    {
        (_, string errors, int exitCode) = await RunAsync(
            FfmpegPath(),
            ["-hide_banner", "-nostats", "-i", clip, "-map", "0:a:0", "-af", "volumedetect", "-f", "null", "-"]
        );
        Assert.True(exitCode == 0, $"ffmpeg's volumedetect failed on {clip}: {errors}");
        Match peak = MaxVolumeLine().Match(errors);
        Assert.True(peak.Success, $"volumedetect reported no max_volume for {clip}: {errors}");
        return double.Parse(peak.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"max_volume: (-?\d+(?:\.\d+)?) dB")]
    private static partial Regex MaxVolumeLine();

    [GeneratedRegex(@"(\d+) frames in ")]
    private static partial Regex FramesLine();

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

    private static string FfprobePath()
    {
        string ffmpeg = FfmpegPath();
        return Path.Combine(Path.GetDirectoryName(ffmpeg)!, Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.Ordinal));
    }

    private static string FfmpegPath() =>
        Installation.FindFfmpeg(out _) ?? throw new InvalidOperationException("ffmpeg is not on PATH; install it with winget install Gyan.FFmpeg.");
}
