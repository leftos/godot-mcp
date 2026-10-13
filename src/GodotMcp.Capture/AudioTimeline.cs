namespace GodotMcp.Capture;

/// <summary>
/// Places each loopback packet on the track by its device timestamp rather than by the order packets arrive in. A
/// timestamp is a QPC instant in 100 ns units, as IAudioCaptureClient::GetBuffer reports it, and the track's frame 0 is
/// at <paramref name="startQpc"/>. A packet that starts past the track's end is preceded by silence for the frames lost
/// before it; one that starts before the end has its leading frames that fall before the end dropped, so nothing plays
/// twice.
/// <para>
/// A packet starting within 2 ms of the end continues the track as it is, so rounding and timestamp jitter add no
/// clicks; a larger drift is corrected in one step. At the 17-23 ppm the device clock was measured to drift from QPC, the
/// track drifts about one frame a second from the stamps, so about every 100 s one packet is moved by about 2 ms: its
/// first 2 ms cut, or 2 ms of silence put before it.
/// </para>
/// <para>
/// When no packet has come for a while (a process with no render stream open sends none), <see cref="SilenceToNow"/>
/// keeps the track no more than 200 ms behind the clock; a packet stamped inside silence already written is trimmed.
/// </para>
/// </summary>
internal sealed class AudioTimeline(long startQpc, int sampleRate)
{
    private const long QpcUnitsPerSecond = 10_000_000;

    /// <summary>Four times the longest gap between packets measured on a game with a render stream open (49.6 ms).</summary>
    private const long StallQpcUnits = 2_000_000;

    private readonly long _continuityFrames = sampleRate / 500;

    /// <summary>The frames on the track so far, silence included.</summary>
    public long End { get; private set; }

    /// <summary>
    /// Places a packet of <paramref name="frames"/> frames whose first frame was recorded at <paramref name="qpcPosition"/>,
    /// and returns the silent frames to write before it: 0 when it lands where the track already ends. The caller writes
    /// that silence, then the packet without its first <paramref name="dropped"/> frames, which overlap the track.
    /// </summary>
    public long SilenceBefore(long qpcPosition, int frames, out int dropped)
    {
        long gap = FrameAt(qpcPosition) - End;
        if (Math.Abs(gap) <= _continuityFrames)
        {
            dropped = 0;
            End += frames;
            return 0;
        }
        if (gap > 0)
        {
            dropped = 0;
            End += gap + frames;
            return gap;
        }
        dropped = (int)Math.Min(-gap, frames);
        End += frames - dropped;
        return 0;
    }

    /// <summary>
    /// Places a packet whose timestamp is uncertain where the track already ends: at worst that one packet is out of place,
    /// and the next packet with a sound timestamp is placed against it.
    /// </summary>
    public void Append(int frames) => End += frames;

    /// <summary>
    /// Returns the silent frames that bring the track to 200 ms before <paramref name="nowQpc"/>, and counts them on the
    /// track; 0 while the track is nearer the clock than that, as it always is while packets keep coming.
    /// </summary>
    public long SilenceToNow(long nowQpc)
    {
        long floor = FrameAt(nowQpc - StallQpcUnits);
        if (floor <= End)
        {
            return 0;
        }
        long silence = floor - End;
        End = floor;
        return silence;
    }

    /// <summary>The frame index of a QPC instant, rounded to the nearest frame, half away from zero.</summary>
    private long FrameAt(long qpcPosition)
    {
        long scaled = (qpcPosition - startQpc) * sampleRate;
        long half = QpcUnitsPerSecond / 2;
        return scaled >= 0 ? (scaled + half) / QpcUnitsPerSecond : -((half - scaled) / QpcUnitsPerSecond);
    }
}
