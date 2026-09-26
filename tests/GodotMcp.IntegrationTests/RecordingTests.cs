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
/// cuts one clip per start-stop pair with ffmpeg. The files are read back with ffprobe, found beside ffmpeg.
/// </summary>
public sealed class RecordingTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 120_000;
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly RuntimeTools _tools;

    public RecordingTests() => _tools = new RuntimeTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
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
            ProbedStream video = Assert.Single(await ProbeAsync(recording.Clips![i]), stream => stream.Type == "video");
            Assert.Equal(spans[i], video.Packets);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RecordMarkRefusesANonRecordingSession()
    {
        await LaunchAsync(record: false);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.RecordMarkAsync("start", cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal("session 'InputProbe' is not recording; launch it with options.record.", refused.Message);
    }

    /// <summary>Polls list_sessions until the only session's recording has an outcome: clips, or an error.</summary>
    private async Task<RecordingResult> WaitForOutcomeAsync(CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Assert.Single(_harness.Sessions.List()).Recording is { } recording && (recording.Clips is not null || recording.Error is not null))
            {
                return recording;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException($"the recording had no outcome after 30 s: {Assert.Single(_harness.Sessions.List())}");
    }

    private Task<LaunchResult> LaunchAsync(bool record) =>
        _harness.Sessions.LaunchAsync(
            new LaunchRequest(_probe.Directory, null, [], [], true, false, Prepare: true) { Record = record },
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
                "stream=codec_type,r_frame_rate,nb_read_packets",
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
                    stream.GetProperty("r_frame_rate").GetString()!,
                    long.Parse(stream.GetProperty("nb_read_packets").GetString()!, CultureInfo.InvariantCulture)
                )),
        ];
    }

    private sealed record ProbedStream(string Type, string FrameRate, long Packets);
}
