using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>Whether a real-time recording records the game's sound, and the warning when it was asked for and cannot.</summary>
public sealed class RealtimeAudioTests
{
    [Fact]
    public void NotRequestedRecordsNoTrackAndWarnsOfNothing()
    {
        Assert.Equal((null, null), RealtimeAudio.Choose("game", requested: false, muted: true, gameProcessId: 4321));
        Assert.Equal((null, null), RealtimeAudio.Choose("game", requested: false, muted: false, gameProcessId: null));
    }

    [Fact]
    public void AMutedSessionRecordsNoTrackAndSaysWhy()
    {
        Assert.Equal(
            (null, "no audio: session 'game' renders no sound (options.mute or a quiet run), so the clip has no audio track"),
            RealtimeAudio.Choose("game", requested: true, muted: true, gameProcessId: 4321)
        );
    }

    [Fact]
    public void AGameWithNoProcessIdRecordsNoTrackAndSaysWhy()
    {
        Assert.Equal(
            (null, "no audio: the game's bridge sent no process id, so the clip has no audio track"),
            RealtimeAudio.Choose("game", requested: true, muted: false, gameProcessId: null)
        );
    }

    [Fact]
    public void RequestedWithAProcessIdRecordsThatProcess() =>
        Assert.Equal((4321, null), RealtimeAudio.Choose("game", requested: true, muted: false, gameProcessId: 4321));
}
