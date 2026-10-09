using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// options.record against the InputProbe: Movie Maker writes an .avi from launch, record_mark notes movie frames, and the stop
/// encodes one .mp4 clip per start-stop pair with ffmpeg. The files are read back with ffprobe, found beside ffmpeg.
/// </summary>
public sealed class RecordingTests : IAsyncDisposable
{
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly RuntimeTools _tools;

    public RecordingTests() => _tools = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARecordedRunLeavesAPlayableAvi()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        LaunchResult launched = await LaunchAsync(record: true);
        await Task.Delay(1000, cancellation);

        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        string movie = launched.Recording?.Path ?? throw new InvalidOperationException("run_project returned no recording path.");
        string recordings = Path.Combine(ProjectPaths.Normalise(_probe.Directory), ".godot", "godot-mcp", "recordings");
        Assert.Equal(recordings, Path.GetDirectoryName(movie));
        Assert.EndsWith("-InputProbe.avi", movie, StringComparison.Ordinal);
        Assert.False(stopped.Killed);
        Assert.Equal(new RecordingResult { Path = movie }, stopped.Recording);
        IReadOnlyList<ProbedStream> streams = await ProbeAsync(movie);
        ProbedStream video = Assert.Single(streams, stream => stream.Type == "video");
        Assert.Equal("60/1", video.FrameRate);
        Assert.True(video.Packets > 0, $"the movie has no video frames: {movie}");
        Assert.Single(streams, stream => stream.Type == "audio");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task MarksCutClipsOfTheMarkedLength()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        LaunchResult launched = await LaunchAsync(record: true);
        await FrameAsync("pause", null);
        List<long> spans = [];
        foreach (int steps in new[] { 10, 20 })
        {
            long start = await MarkAsync("start");
            await FrameAsync("step", steps);
            long stop = await MarkAsync("stop");
            Assert.True(stop - start >= steps, $"a {steps}-frame step between marks at {start} and {stop}");
            spans.Add(stop - start);
        }

        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        RecordingResult recording = stopped.Recording ?? throw new InvalidOperationException("stop_project returned no recording.");
        Assert.Null(recording.Error);
        Assert.Null(recording.Path);
        Assert.False(File.Exists(launched.Recording!.Path), "the full movie was not deleted");
        Assert.Equal([RecordingCut.ClipPath(launched.Recording.Path!, 1), RecordingCut.ClipPath(launched.Recording.Path!, 2)], recording.Clips);
        for (int i = 0; i < spans.Count; i++)
        {
            Assert.EndsWith($"-clip{i + 1}.mp4", recording.Clips![i], StringComparison.Ordinal);
            IReadOnlyList<ProbedStream> streams = await ProbeAsync(recording.Clips![i]);
            ProbedStream video = Assert.Single(streams, stream => stream.Type == "video");
            ProbedStream audio = Assert.Single(streams, stream => stream.Type == "audio");
            Assert.Equal("h264", video.Codec);
            Assert.Equal("aac", audio.Codec);
            Assert.Equal(spans[i], video.Packets);
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task DropIdleCutsTheIdleOut()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        LaunchResult launched = await LaunchAsync(record: true, dropIdle: true);

        // The probe's own scene is still while paused, so every stepped frame would be idle too: a full-window rect that turns
        // black and white on alternate processed frames makes each step a frame that differs.
        await _tools.RunScriptAsync(
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
                + "\tvar script := GDScript.new()\n"
                + "\tscript.source_code = \"extends ColorRect\\nvar n := 0\\nfunc _process(_delta: float) -> void:\\n"
                + "\\tn += 1\\n\\tcolor = Color.WHITE if n % 2 == 0 else Color.BLACK\\n\"\n"
                + "\tscript.reload()\n"
                + "\tvar flicker := ColorRect.new()\n"
                + "\tflicker.set_script(script)\n"
                + "\tflicker.size = scene_tree.root.get_visible_rect().size\n"
                + "\tscene_tree.root.add_child(flicker)\n"
                + "\treturn true\n",
            10_000,
            cancellationToken: cancellation
        );
        await FrameAsync("pause", null);
        long start = await MarkAsync("start");
        await Task.Delay(1000, cancellation);
        await FrameAsync("step", 10);
        long stop = await MarkAsync("stop");

        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        RecordingResult recording = stopped.Recording ?? throw new InvalidOperationException("stop_project returned no recording.");
        Assert.Null(recording.Error);
        string clip = Assert.Single(recording.Clips!);
        Assert.Equal(RecordingCut.ClipPath(launched.Recording!.Path!, 1), clip);
        IReadOnlyList<ProbedStream> streams = await ProbeAsync(clip);
        ProbedStream video = Assert.Single(streams);
        Assert.Equal("h264", video.Codec);
        Assert.True(stop - start > 40, $"the marks at {start} and {stop} do not span the idle second");
        // The still frame at the start mark and the 10 stepped ones: measured 11 kept of a 253-frame span (2026-09-27).
        Assert.InRange(video.Packets, 10, 12);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGameThatQuitsItselfIsCut()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        LaunchResult launched = await LaunchAsync(record: true);
        await MarkAsync("start");

        // The quit waits for a timer, so the script's reply goes out before the game ends.
        await _tools.RunScriptAsync(
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
                + "\tscene_tree.create_timer(0.2).timeout.connect(scene_tree.quit)\n\treturn true\n",
            10_000,
            cancellationToken: cancellation
        );
        RecordingResult recording = await WaitForOutcomeAsync(cancellation);

        Assert.Null(recording.Error);
        Assert.Null(recording.Path);
        string clip = Assert.Single(recording.Clips!);
        Assert.Equal(RecordingCut.ClipPath(launched.Recording!.Path!, 1), clip);
        Assert.True(File.Exists(clip));
        Assert.False(File.Exists(launched.Recording.Path), "the full movie was not deleted");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task RestartFinishesTheOldRecording()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        LaunchResult launched = await LaunchAsync(record: true);
        await MarkAsync("start");

        RestartResult restarted = await _harness.Sessions.RestartAsync(null, prepare: true, cancellation);
        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);

        RecordingResult previous = restarted.PreviousRecording ?? throw new InvalidOperationException("restart returned no previousRecording.");
        Assert.Null(previous.Error);
        Assert.Equal([RecordingCut.ClipPath(launched.Recording!.Path!, 1)], previous.Clips);
        string movie = restarted.Recording?.Path ?? throw new InvalidOperationException("restart returned no recording path.");
        Assert.NotEqual(launched.Recording.Path, movie);
        Assert.Equal(new RecordingResult { Path = movie }, stopped.Recording);
        Assert.True(File.Exists(movie));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task WithoutFfmpegTheFullFileIsKept()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string? saved = Environment.GetEnvironmentVariable(Installation.FfmpegPathVariable);
        string missing = Path.Combine(_probe.Directory, "no-ffmpeg", "ffmpeg.exe");
        StopResult stopped;
        LaunchResult launched;
        try
        {
            // The harness's registry runs in this process, so the variable reaches the cut the stop makes.
            Environment.SetEnvironmentVariable(Installation.FfmpegPathVariable, missing);
            launched = await LaunchAsync(record: true);
            await MarkAsync("start");
            await FrameAsync("step", 5);
            await MarkAsync("stop");
            stopped = await _harness.Sessions.StopAsync(null, cancellation);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Installation.FfmpegPathVariable, saved);
        }

        string movie = launched.Recording!.Path!;
        Assert.Equal(
            new RecordingResult
            {
                Path = movie,
                Error =
                    $"FFMPEG_PATH is '{missing}', which does not exist, so the recording was not cut; the full file is kept. "
                    + "Point it at ffmpeg.exe, or install ffmpeg with: winget install Gyan.FFmpeg",
            },
            stopped.Recording
        );
        Assert.True(File.Exists(movie));
        Assert.False(File.Exists(RecordingCut.ClipPath(movie, 1)));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AQuietNonRecordingSessionIsRefusedWithTheFix()
    {
        await LaunchAsync(record: false);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.RecordMarkAsync("start", cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            "Session 'InputProbe' is quiet, so its window is on the server's hidden desktop, which a real-time recording cannot see. "
                + "Launch it with options.quiet: false (options.mute: true keeps it silent), or with options.record for a Movie Maker "
                + "recording.",
            refused.Message
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AMovieRunStillMarksWithModeMovie()
    {
        await LaunchAsync(record: true);

        string json = await _tools.RecordMarkAsync("start", cancellationToken: TestContext.Current.CancellationToken);

        JsonNode reply = JsonNode.Parse(json)!;
        Assert.Equal("start", reply["mark"]!.GetValue<string>());
        Assert.Equal("movie", reply["mode"]!.GetValue<string>());
        Assert.True(reply["frame"]!.GetValue<long>() >= 0, json);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task OptionsOnAMovieSessionAreRefused()
    {
        await LaunchAsync(record: true);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.RecordMarkAsync("start", new RecordMarkOptions(), cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            "options belong to a real-time recording; session 'InputProbe' records with Movie Maker, whose marks take none.",
            refused.Message
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AWaitTimeoutInARecordingRunsItsLengthInMovieFrames()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(record: true);
        await SlowTheGameAsync();

        string json = await _tools.WaitForAsync(new WaitCondition(Expression: "false"), 1500, null, cancellation);

        JsonNode reply = JsonNode.Parse(json)!;
        Assert.False(reply["met"]!.GetValue<bool>(), json);
        Assert.Equal(1500 * GodotCommandLine.MovieFramesPerSecond / 1000, reply["frames"]!.GetValue<int>());
        Assert.Equal(1500, reply["clipMs"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ADragInARecordingLastsItsDurationInMovieFrames()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(record: true);
        await SlowTheGameAsync();
        long before = (await ReadAsync("Engine.get_process_frames()")).GetValue<long>();

        await _tools.DragAsync(new(null, 100, 100), new(null, 300, 100), 500, "left", cancellationToken: cancellation);

        long after = (await ReadAsync("Engine.get_process_frames()")).GetValue<long>();
        long[] motions =
        [
            .. (await ReadAsync("scene_tree.root.get_node(\"Slow\").motion_frames")).AsArray().Select(frame => frame!.GetValue<long>()),
        ];
        // 500 ms of clip time is ceil(500 x 60 / 1000) = 30 frames: the bridge sends one held-button motion a frame, 30 in
        // all, on consecutive frames; the release follows a frame after the last motion and the reply two frames later.
        const int frames = 500 * GodotCommandLine.MovieFramesPerSecond / 1000;
        Assert.Equal(frames, motions.Length);
        Assert.Equal(frames - 1, motions[^1] - motions[0]);
        Assert.True(
            after - before >= frames + 3,
            $"the drag's frames {before} to {after} are fewer than {frames} motions, the release and 2 settles"
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARawWaitInARecordingLastsItsLengthInMovieFrames()
    {
        await LaunchAsync(record: true);
        await SlowTheGameAsync();
        long before = (await ReadAsync("Engine.get_process_frames()")).GetValue<long>();

        await _tools.SimulateInputAsync(
            [new JsonObject { ["type"] = "wait", ["ms"] = 1000 }],
            cancellationToken: TestContext.Current.CancellationToken
        );

        long after = (await ReadAsync("Engine.get_process_frames()")).GetValue<long>();
        // 1000 ms of clip time is 60 movie frames; a wall-clock wait in the slowed game would span about 10. A few frames more
        // are the round trips between the reads and the wait (measured 4 at 240 fps, 8 at 20 fps).
        const int frames = 1000 * GodotCommandLine.MovieFramesPerSecond / 1000;
        Assert.InRange(after - before, frames, frames + 20);
    }

    /// <summary>
    /// Adds /root/Slow, which sleeps 100 ms each frame (so the game runs at 10 fps at most and any wall-clock bound ends early)
    /// and notes the process frame of each mouse motion that carries a held button in motion_frames.
    /// </summary>
    private async Task SlowTheGameAsync() =>
        await _tools.RunScriptAsync(
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
                + "\tvar script := GDScript.new()\n"
                + "\tscript.source_code = \"extends Node\\nvar motion_frames: Array = []\\n"
                + "func _process(_delta: float) -> void:\\n\\tOS.delay_msec(100)\\n"
                + "func _input(event: InputEvent) -> void:\\n"
                + "\\tif event is InputEventMouseMotion and event.button_mask != 0:\\n"
                + "\\t\\tmotion_frames.append(Engine.get_process_frames())\\n\"\n"
                + "\tscript.reload()\n"
                + "\tvar slow := Node.new()\n"
                + "\tslow.name = \"Slow\"\n"
                + "\tslow.set_script(script)\n"
                + "\tscene_tree.root.add_child(slow)\n"
                + "\treturn true\n",
            10_000,
            cancellationToken: TestContext.Current.CancellationToken
        );

    /// <summary>The value of a GDScript expression, run by run_script with scene_tree in scope.</summary>
    private async Task<JsonNode> ReadAsync(string expression)
    {
        string json = await _tools.RunScriptAsync(
            $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\treturn {expression}\n",
            10_000,
            cancellationToken: TestContext.Current.CancellationToken
        );
        return JsonNode.Parse(json)!["value"]!;
    }

    /// <summary>Polls list_sessions until the only session's recording has an outcome: clips, or an error.</summary>
    private async Task<RecordingResult> WaitForOutcomeAsync(CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (
                Assert.Single(_harness.Sessions.List(includeStopped: true)).Recording is { } recording
                && (recording.Clips is not null || recording.Error is not null)
            )
            {
                return recording;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException($"the recording had no outcome after 30 s: {Assert.Single(_harness.Sessions.List(includeStopped: true))}");
    }

    private Task<LaunchResult> LaunchAsync(bool record, bool dropIdle = false) =>
        _harness.Sessions.LaunchAsync(
            new LaunchRequest(_probe.Directory, null, [], [], true, false, Prepare: true) { Record = record, DropIdle = dropIdle },
            null,
            TestContext.Current.CancellationToken
        );

    private async Task<long> MarkAsync(string mark)
    {
        string json = await _tools.RecordMarkAsync(mark, cancellationToken: TestContext.Current.CancellationToken);
        JsonNode reply = JsonNode.Parse(json)!;
        Assert.Equal(mark, reply["mark"]!.GetValue<string>());
        return reply["frame"]!.GetValue<long>();
    }

    private async Task FrameAsync(string action, int? count) =>
        await _tools.FrameControlAsync(action, count, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>The file's streams as ffprobe reads them, counting every packet (a video packet is one MJPEG frame).</summary>
    private static async Task<IReadOnlyList<ProbedStream>> ProbeAsync(string file)
    {
        string ffmpeg =
            Installation.FindFfmpeg(out _)
            ?? throw new InvalidOperationException("ffmpeg is not on PATH; install it with winget install Gyan.FFmpeg.");
        ProcessStartInfo startInfo = new(
            Path.Combine(Path.GetDirectoryName(ffmpeg)!, Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.Ordinal))
        )
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (
            string argument in new[]
            {
                "-v",
                "error",
                "-count_packets",
                "-show_entries",
                "stream=codec_type,codec_name,r_frame_rate,nb_read_packets",
                "-of",
                "json",
                file,
            }
        )
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process ffprobe = Process.Start(startInfo)!;
        ffprobe.StandardInput.Close();
        Task<string> error = ffprobe.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        string output = await ffprobe.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await ffprobe.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(ffprobe.ExitCode == 0, $"ffprobe failed on {file}: {await error}");
        using var probed = JsonDocument.Parse(output);
        return
        [
            .. probed
                .RootElement.GetProperty("streams")
                .EnumerateArray()
                .Select(stream => new ProbedStream(
                    stream.GetProperty("codec_type").GetString()!,
                    stream.GetProperty("codec_name").GetString()!,
                    stream.GetProperty("r_frame_rate").GetString()!,
                    long.Parse(stream.GetProperty("nb_read_packets").GetString()!, CultureInfo.InvariantCulture)
                )),
        ];
    }

    private sealed record ProbedStream(string Type, string Codec, string FrameRate, long Packets);
}
