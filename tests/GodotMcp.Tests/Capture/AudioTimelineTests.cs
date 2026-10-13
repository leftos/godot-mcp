using GodotMcp.Capture;

namespace GodotMcp.Tests.Capture;

/// <summary>
/// Loopback packets placed by their device timestamps (100 ns QPC units) at 48 kHz, where a 480-frame packet lasts
/// 100 000 units: a lost packet leaves silence in its place, an overlapping one is trimmed, a packet within 2 ms of the
/// track's end continues it, one with an uncertain timestamp is appended, and a track with no packets is kept 200 ms
/// behind the clock.
/// </summary>
public sealed class AudioTimelineTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void ALostPacketLeavesSilenceInItsPlace()
    {
        var timeline = new AudioTimeline(startQpc: 0, SampleRate);

        Assert.Equal(0, timeline.SilenceBefore(qpcPosition: 0, frames: 480, out int firstDropped));
        Assert.Equal(0, timeline.SilenceBefore(qpcPosition: 100_000, frames: 480, out int secondDropped));
        Assert.Equal(960, timeline.SilenceBefore(qpcPosition: 400_000, frames: 480, out int afterLossDropped));

        Assert.Equal([0, 0, 0], [firstDropped, secondDropped, afterLossDropped]);
        Assert.Equal(2400, timeline.End);
    }

    [Fact]
    public void AnOverlappingPacketIsTrimmedNotDuplicated()
    {
        var timeline = new AudioTimeline(startQpc: 0, SampleRate);
        timeline.SilenceBefore(qpcPosition: 0, frames: 480, out _);
        timeline.SilenceBefore(qpcPosition: 100_000, frames: 480, out _);

        // Starts at frame 720, 240 frames before the track's end at 960.
        Assert.Equal(0, timeline.SilenceBefore(qpcPosition: 150_000, frames: 480, out int partlyDropped));
        Assert.Equal(240, partlyDropped);
        Assert.Equal(1200, timeline.End);

        // Wholly before the track's end: nothing of it is kept.
        Assert.Equal(0, timeline.SilenceBefore(qpcPosition: 0, frames: 480, out int whollyDropped));
        Assert.Equal(480, whollyDropped);
        Assert.Equal(1200, timeline.End);
    }

    [Fact]
    public void AFirstPacketAfterTheStartGetsThatMuchLeadingSilence()
    {
        var timeline = new AudioTimeline(startQpc: 7_000_000, SampleRate);

        // 50 ms after the stream started.
        Assert.Equal(2400, timeline.SilenceBefore(qpcPosition: 7_500_000, frames: 480, out int dropped));

        Assert.Equal(0, dropped);
        Assert.Equal(2880, timeline.End);
    }

    [Fact]
    public void AFirstPacketStampedBeforeTheStartLosesTheFramesBeforeIt()
    {
        var timeline = new AudioTimeline(startQpc: 1_000_000, SampleRate);

        // 5 ms (240 frames) before the start.
        Assert.Equal(0, timeline.SilenceBefore(qpcPosition: 950_000, frames: 960, out int dropped));

        Assert.Equal(240, dropped);
        Assert.Equal(720, timeline.End);
    }

    /// <summary>
    /// After a packet ending at frame 480 (100 000 units), the next one starting 96 frames (2 ms) either side of that
    /// continues the track as it is; one starting 97 frames away is moved by its whole distance.
    /// </summary>
    [Theory]
    [InlineData(100_000 + 20_000, 0, 0)]
    [InlineData(100_000 - 20_000, 0, 0)]
    [InlineData(100_000 + 20_105, 97, 0)]
    [InlineData(100_000 - 20_105, 0, 97)]
    public void APacketWithin2MsOfTheEndContinuesTheTrack(long qpcPosition, long silence, int dropped)
    {
        var timeline = new AudioTimeline(startQpc: 0, SampleRate);
        timeline.SilenceBefore(qpcPosition: 0, frames: 480, out _);

        Assert.Equal(silence, timeline.SilenceBefore(qpcPosition, frames: 480, out int actualDropped));

        Assert.Equal(dropped, actualDropped);
        Assert.Equal(480 + silence + 480 - dropped, timeline.End);
    }

    /// <summary>A frame lasts 208⅓ units: 200 104 units is frame 960.4992 and 200 105 is frame 960.504.</summary>
    [Theory]
    [InlineData(200_104, 480)]
    [InlineData(200_105, 481)]
    public void ATimestampBetweenTwoFramesRoundsToTheNearer(long qpcPosition, long silence)
    {
        var timeline = new AudioTimeline(startQpc: 0, SampleRate);
        timeline.SilenceBefore(qpcPosition: 0, frames: 480, out _);

        Assert.Equal(silence, timeline.SilenceBefore(qpcPosition, frames: 480, out _));
    }

    [Fact]
    public void APacketWithAnUncertainTimestampIsAppendedAtTheEnd()
    {
        var timeline = new AudioTimeline(startQpc: 0, SampleRate);
        timeline.SilenceBefore(qpcPosition: 0, frames: 480, out _);
        timeline.SilenceBefore(qpcPosition: 100_000, frames: 480, out _);

        timeline.Append(frames: 480);

        Assert.Equal(1440, timeline.End);
        // The next packet with a sound timestamp follows on with no silence.
        Assert.Equal(0, timeline.SilenceBefore(qpcPosition: 300_000, frames: 480, out int dropped));
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void ASecondWithNoPacketsGives800MsOfSilenceAndAPacketStampedInsideItIsTrimmed()
    {
        var timeline = new AudioTimeline(startQpc: 0, SampleRate);

        Assert.Equal(38_400, timeline.SilenceToNow(nowQpc: 10_000_000));
        Assert.Equal(38_400, timeline.End);

        // Stamped at 700 ms (frame 33 600): it lies wholly inside the silence already written.
        Assert.Equal(0, timeline.SilenceBefore(qpcPosition: 7_000_000, frames: 480, out int dropped));
        Assert.Equal(480, dropped);
        Assert.Equal(38_400, timeline.End);
    }

    /// <summary>Each packet is drained 50 ms after its last frame, about the longest gap measured between packets.</summary>
    [Fact]
    public void PacketsEvery10MsNeverTriggerTheSilenceFloor()
    {
        var timeline = new AudioTimeline(startQpc: 0, SampleRate);
        long filled = 0;

        for (long packet = 0; packet < 100; packet++)
        {
            long qpcPosition = packet * 100_000;
            Assert.Equal(0, timeline.SilenceBefore(qpcPosition, frames: 480, out _));
            filled += timeline.SilenceToNow(nowQpc: qpcPosition + 100_000 + 500_000);
        }

        Assert.Equal(0, filled);
        Assert.Equal(48_000, timeline.End);
    }
}
