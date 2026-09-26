using GodotMcp.Server.Session;
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
        List<string> arguments = RecordingCut.ClipArguments(Movie, clips[1], RecordingCut.ClipPath(Movie, 2));

        Assert.Equal([new ClipSpan(0, 60), new ClipSpan(120, null)], clips);
        Assert.DoesNotContain("-to", arguments);
        Assert.Equal(@"C:\Games\Probe\.godot\godot-mcp\recordings\20260926-120000-000-Probe-clip2.avi", arguments[^1]);
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
    public void ClipArgumentsCopyStreamsBetweenTheMarks()
    {
        string clip = RecordingCut.ClipPath(Movie, 1);

        List<string> fromTheFirstFrame = RecordingCut.ClipArguments(Movie, new ClipSpan(0, 30), clip);
        List<string> between = RecordingCut.ClipArguments(Movie, new ClipSpan(90, 150), clip);

        // Each boundary sits half a frame before its frame: frame 90 is at 1.5 s, frame 150 at 2.5 s.
        Assert.Equal(["-hide_banner", "-nostdin", "-y", "-i", Movie, "-ss", "0", "-to", "0.491667", "-c", "copy", clip], fromTheFirstFrame);
        Assert.Equal(["-hide_banner", "-nostdin", "-y", "-i", Movie, "-ss", "1.491667", "-to", "2.491667", "-c", "copy", clip], between);
    }
}
