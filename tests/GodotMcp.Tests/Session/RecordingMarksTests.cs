using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

public sealed class RecordingMarksTests
{
    private const string Movie = @"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe.avi";

    [Fact]
    public void MarksMustAlternate()
    {
        Recording recording = new(Movie);

        SessionException stopFirst = Assert.Throws<SessionException>(() => recording.Mark("stop", 5));
        MarkResult started = recording.Mark("start", 30);
        SessionException secondStart = Assert.Throws<SessionException>(() => recording.Mark("start", 40));
        SessionException emptyClip = Assert.Throws<SessionException>(() => recording.Mark("stop", 30));
        MarkResult stopped = recording.Mark("stop", 90);
        SessionException secondStop = Assert.Throws<SessionException>(() => recording.Mark("stop", 100));

        Assert.Equal("No clip is open; mark start first.", stopFirst.Message);
        Assert.Equal(new MarkResult("start", 30, 0.5), started);
        Assert.Equal("A clip is already open, started at frame 30; mark stop before the next start.", secondStart.Message);
        Assert.Equal("A clip needs at least one frame: stop is at frame 30 and its start at frame 30.", emptyClip.Message);
        Assert.Equal(new MarkResult("stop", 90, 1.5), stopped);
        Assert.Equal("No clip is open; mark start first.", secondStop.Message);
        Assert.Equal([new ClipSpan(30, 90)], recording.Clips());
    }

    [Fact]
    public void AnOpenStartClipsToTheEnd()
    {
        Recording recording = new(Movie);
        recording.Mark("start", 0);
        recording.Mark("stop", 60);
        recording.Mark("start", 120);

        IReadOnlyList<ClipSpan> clips = recording.Clips();
        List<string> encode = RecordingCut.EncodeArguments(Movie, clips[1], RecordingCut.ClipPath(Movie, 2), dropIdle: false);
        List<string> copy = RecordingCut.CopyArguments(Movie, clips[1], RecordingCut.FallbackClipPath(Movie, 2));

        Assert.Equal([new ClipSpan(0, 60), new ClipSpan(120, null)], clips);
        Assert.DoesNotContain("-to", encode);
        Assert.DoesNotContain("-to", copy);
        Assert.Equal(@"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe-clip2.mp4", encode[^1]);
        Assert.Equal(@"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe-clip2.avi", copy[^1]);
    }

    [Fact]
    public void AKilledOrMissingMovieIsNeverAGoodPath()
    {
        string notWritten = $"The recording {Movie} was not written (the game may have failed before its first frame), so there is nothing to cut.";

        Assert.Equal(
            new RecordingResult { Path = Movie, Error = RecordingCut.KilledMovie },
            RecordingCut.Unusable(Movie, exists: true, killed: true)
        );
        Assert.Equal(new RecordingResult { Error = notWritten }, RecordingCut.Unusable(Movie, exists: false, killed: true));
        Assert.Equal(new RecordingResult { Error = notWritten }, RecordingCut.Unusable(Movie, exists: false, killed: false));
        Assert.Null(RecordingCut.Unusable(Movie, exists: true, killed: false));
    }

    [Fact]
    public async Task AMissingMovieWithoutMarksHasNoPath()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"godot-mcp-missing-{Guid.NewGuid():N}.avi");

        RecordingResult result = await RecordingCut.FinishAsync(new Recording(missing), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Null(result.Path);
        Assert.Null(result.Clips);
        Assert.StartsWith($"The recording {missing} was not written", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeArgumentsSeekTheInputBetweenTheMarks()
    {
        string clip = RecordingCut.ClipPath(Movie, 1);

        List<string> fromTheFirstFrame = RecordingCut.EncodeArguments(Movie, new ClipSpan(0, 30), clip, dropIdle: false);
        List<string> between = RecordingCut.EncodeArguments(Movie, new ClipSpan(90, 150), clip, dropIdle: false);

        // Each boundary sits half a frame before its frame: frame 90 is at 1.5 s, frame 150 at 2.5 s.
        Assert.Equal(@"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe-clip1.mp4", clip);
        Assert.Equal([.. Head("0", "0.491667"), .. Encode(EvenSize), "-c:a", "aac", "-movflags", "+faststart", clip], fromTheFirstFrame);
        Assert.Equal([.. Head("1.491667", "2.491667"), .. Encode(EvenSize), "-c:a", "aac", "-movflags", "+faststart", clip], between);
    }

    [Fact]
    public void DropIdleDecimatesAndDropsTheAudio()
    {
        string clip = RecordingCut.ClipPath(Movie, 1);

        List<string> arguments = RecordingCut.EncodeArguments(Movie, new ClipSpan(90, 150), clip, dropIdle: true);

        Assert.Equal(
            [.. Head("1.491667", "2.491667"), .. Encode($"mpdecimate,setpts=N/60/TB,{EvenSize}"), "-an", "-movflags", "+faststart", clip],
            arguments
        );
    }

    [Fact]
    public void CopyArgumentsCopyStreamsBetweenTheMarks()
    {
        string clip = RecordingCut.FallbackClipPath(Movie, 1);

        List<string> fromTheFirstFrame = RecordingCut.CopyArguments(Movie, new ClipSpan(0, 30), clip);
        List<string> between = RecordingCut.CopyArguments(Movie, new ClipSpan(90, 150), clip);

        Assert.Equal(@"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe-clip1.avi", clip);
        Assert.Equal(["-hide_banner", "-nostdin", "-y", "-i", Movie, "-ss", "0", "-to", "0.491667", "-c", "copy", clip], fromTheFirstFrame);
        Assert.Equal(["-hide_banner", "-nostdin", "-y", "-i", Movie, "-ss", "1.491667", "-to", "2.491667", "-c", "copy", clip], between);
    }

    [Fact]
    public void TheCeilingScalesWithTheClip()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), RecordingCut.CeilingFor(new ClipSpan(0, 60)));
        Assert.Equal(TimeSpan.FromSeconds(120), RecordingCut.CeilingFor(new ClipSpan(600, 600 + (40 * 60))));
        Assert.Equal(TimeSpan.FromSeconds(300), RecordingCut.CeilingFor(new ClipSpan(600, 600 + (100 * 60))));
        Assert.Equal(TimeSpan.FromSeconds(1800), RecordingCut.CeilingFor(new ClipSpan(0, null)));
        Assert.Equal(TimeSpan.FromSeconds(1500), RecordingCut.CeilingFor(new ClipSpan(6000, null)));
    }

    [Fact]
    public void DropIdleWithoutRecordIsRefused()
    {
        LaunchRequest request = ProjectProfile.Empty(@"C:\Games\Probe").Merge(null, [], [], new RunOptions(DropIdle: true)).Request;

        SessionException refused = Assert.Throws<SessionException>(() => GodotCommandLine.RefuseUnrecordable(request));
        GodotCommandLine.RefuseUnrecordable(request with { Record = true });

        Assert.Equal(
            "options.dropIdle drops the idle frames of a recording's clips, and this run does not record. Add options.record: true, or drop "
                + "options.dropIdle.",
            refused.Message
        );
    }

    [Fact]
    public async Task AFailedEncodeFallsBackToACopy()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"godot-mcp-cut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string movie = Path.Combine(directory, "20260926-120000-000-Probe.avi");
        await File.WriteAllTextAsync(movie, "movie", TestContext.Current.CancellationToken);
        Recording recording = new(movie) { DropIdle = true };
        recording.Mark("start", 0);
        recording.Mark("stop", 60);
        recording.Mark("start", 120);
        recording.Mark("stop", 180);
        List<ToolProcessRequest> requests = [];
        string log = Path.Combine(directory, RecordingCut.LogFileName);

        try
        {
            // The first clip's encode fails as an ffmpeg without libx264 does; every other run succeeds.
            RecordingResult result = await RecordingCut.CutClipsAsync(
                recording,
                "ffmpeg.exe",
                (request, _) =>
                {
                    requests.Add(request);
                    int exitCode = requests.Count == 1 ? 8 : 0;
                    return Task.FromResult(new ToolProcessResult(exitCode, TimeSpan.Zero, KilledByCeiling: false));
                },
                TestContext.Current.CancellationToken
            );

            string copied = RecordingCut.FallbackClipPath(movie, 1);
            Assert.Null(result.Path);
            Assert.Equal([copied, RecordingCut.ClipPath(movie, 2)], result.Clips!);
            Assert.Equal(
                $"ffmpeg failed (exit code 8) encoding {RecordingCut.ClipPath(movie, 1)}. See {log}. Clip 1 was copied to {copied}, "
                    + "its idle frames kept, as recorded instead.",
                result.Error
            );
            Assert.Equal(["libx264", "copy", "libx264"], requests.Select(request => request.Arguments.Contains("libx264") ? "libx264" : "copy"));
            Assert.Equal([false, true, true], requests.Select(request => request.AppendToLog));
            Assert.False(File.Exists(movie), "the full movie was not deleted");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AFailedCopyAfterAFailedEncodeKeepsTheFullMovie()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"godot-mcp-cut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string movie = Path.Combine(directory, "20260926-120000-000-Probe.avi");
        await File.WriteAllTextAsync(movie, "movie", TestContext.Current.CancellationToken);
        Recording recording = new(movie);
        recording.Mark("start", 0);
        recording.Mark("stop", 60);
        string log = Path.Combine(directory, RecordingCut.LogFileName);

        try
        {
            RecordingResult result = await RecordingCut.CutClipsAsync(
                recording,
                "ffmpeg.exe",
                (_, _) => Task.FromResult(new ToolProcessResult(1, TimeSpan.Zero, KilledByCeiling: false)),
                TestContext.Current.CancellationToken
            );

            Assert.Equal(
                new RecordingResult
                {
                    Path = movie,
                    Error =
                        $"ffmpeg failed (exit code 1) encoding {RecordingCut.ClipPath(movie, 1)}. See {log}. ffmpeg failed (exit code 1) "
                        + $"cutting {RecordingCut.FallbackClipPath(movie, 1)}; the full file is kept. See {log}.",
                },
                result
            );
            Assert.True(File.Exists(movie));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    private static string[] Head(string start, string stop) => ["-hide_banner", "-nostdin", "-y", "-ss", start, "-to", stop, "-i", Movie];

    private static string[] Encode(string filter) => ["-vf", filter, "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p"];
}
