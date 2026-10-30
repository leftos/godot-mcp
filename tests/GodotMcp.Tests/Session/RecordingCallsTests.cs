using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>A Movie Maker recording's ledger of the frames drawn during calls: merged, sorted, and clipped to a span.</summary>
public sealed class RecordingCallsTests
{
    private const string Movie = @"C:\Games\Probe\.godot\godot-mcp\recordings\20261030-120000-000-Probe.avi";

    /// <summary>A clip open from the movie's first frame, which every call falls in.</summary>
    private static readonly ClipSpan Everything = new(0, null);

    [Fact]
    public void DisjointCallsStaySeparateInFrameOrder()
    {
        Recording recording = new(Movie);

        recording.AddCall(20, 25);
        recording.AddCall(1, 3);
        recording.AddCall(5, 6);

        Assert.Equal([new FrameRange(1, 3), new FrameRange(5, 6), new FrameRange(20, 25)], recording.CallRanges(Everything));
    }

    [Fact]
    public void OverlappingAndAdjacentCallsMerge()
    {
        Recording recording = new(Movie);

        recording.AddCall(1, 5);
        recording.AddCall(4, 9);
        recording.AddCall(10, 12);
        recording.AddCall(30, 30);
        recording.AddCall(20, 22);
        recording.AddCall(13, 19);

        Assert.Equal([new FrameRange(1, 22), new FrameRange(30, 30)], recording.CallRanges(Everything));
    }

    [Fact]
    public void ACallInsideAnotherLeavesItAsItIs()
    {
        Recording recording = new(Movie);

        recording.AddCall(10, 40);
        recording.AddCall(15, 20);

        Assert.Equal([new FrameRange(10, 40)], recording.CallRanges(Everything));
    }

    [Fact]
    public void CallRangesAreClippedToTheSpan()
    {
        Recording recording = new(Movie);
        recording.AddCall(1, 5);
        recording.AddCall(10, 20);
        recording.AddCall(30, 40);
        recording.AddCall(50, 60);

        Assert.Equal([new FrameRange(3, 5), new FrameRange(10, 20), new FrameRange(30, 35)], recording.CallRanges(new ClipSpan(3, 36)));
        Assert.Equal([new FrameRange(40, 40)], recording.CallRanges(new ClipSpan(40, 45)));
        Assert.Empty(recording.CallRanges(new ClipSpan(41, 50)));
    }

    [Fact]
    public void ACallOnTheStopMarksFrameIsNotInTheClip()
    {
        Recording recording = new(Movie);
        recording.AddCall(10, 15);
        recording.AddCall(20, 20);

        Assert.Equal([new FrameRange(10, 15)], recording.CallRanges(new ClipSpan(10, 20)));
    }

    [Fact]
    public void AnOpenClipRunsToTheLastCall()
    {
        Recording recording = new(Movie);
        recording.AddCall(10, 15);
        recording.AddCall(50, 60);

        Assert.Equal([new FrameRange(12, 15), new FrameRange(50, 60)], recording.CallRanges(new ClipSpan(12, null)));
    }

    [Fact]
    public void ARecordingWithNoCallsHasNoRanges() => Assert.Empty(new Recording(Movie).CallRanges(Everything));

    [Fact]
    public void KeepToEndKeepsFromTheLastCallsEndToAStoppedSpansLastFrame()
    {
        Recording recording = new(Movie);
        recording.AddCall(10, 15);
        recording.AddCall(30, 35);

        recording.KeepToEnd();

        Assert.Equal([new FrameRange(10, 15), new FrameRange(30, 99)], recording.CallRanges(new ClipSpan(0, 100)));
    }

    [Fact]
    public void KeepToEndRunsAnOpenClipToTheEndOfTheMovie()
    {
        Recording recording = new(Movie);
        recording.Mark(Recording.StartMark, 12);
        recording.AddCall(10, 15);
        recording.AddCall(50, 60);

        recording.KeepToEnd();

        Assert.Equal([new FrameRange(12, 15), new FrameRange(50, FrameRange.OpenEnd)], recording.CallRanges(Assert.Single(recording.Clips())));
    }

    [Fact]
    public void KeepToEndWithoutCallsKeepsFromTheClipsStart()
    {
        Recording recording = new(Movie);
        recording.Mark(Recording.StartMark, 40);

        recording.KeepToEnd();

        Assert.Equal([new FrameRange(40, FrameRange.OpenEnd)], recording.CallRanges(Assert.Single(recording.Clips())));
    }

    [Fact]
    public void ATailInsideACallMergesIntoIt()
    {
        Recording recording = new(Movie);
        recording.AddCall(10, 50);
        recording.Mark(Recording.StartMark, 20);

        recording.KeepToEnd();

        Assert.Equal([new FrameRange(20, FrameRange.OpenEnd)], recording.CallRanges(Assert.Single(recording.Clips())));
    }

    [Fact]
    public void ATailAfterAStoppedClipAddsNothingToIt()
    {
        Recording recording = new(Movie);
        recording.Mark(Recording.StartMark, 0);
        recording.AddCall(5, 8);
        recording.Mark(Recording.StopMark, 20);
        recording.Mark(Recording.StartMark, 30);
        recording.AddCall(35, 40);

        recording.KeepToEnd();

        IReadOnlyList<ClipSpan> clips = recording.Clips();
        Assert.Equal([new FrameRange(5, 8)], recording.CallRanges(clips[0]));
        Assert.Equal([new FrameRange(35, FrameRange.OpenEnd)], recording.CallRanges(clips[1]));
    }

    [Fact]
    public void ACallAnsweredBeforeItWasReadIsRefused()
    {
        Recording recording = new(Movie);

        Assert.Throws<ArgumentOutOfRangeException>(() => recording.AddCall(9, 8));
        Assert.Empty(recording.CallRanges(Everything));
    }
}
