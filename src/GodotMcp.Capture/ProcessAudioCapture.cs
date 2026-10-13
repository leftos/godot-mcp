using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace GodotMcp.Capture;

/// <summary>
/// Captures what one process tree renders, as 48 kHz stereo 16-bit PCM, at a constant rate. While the process holds a
/// render stream open, as Godot's WASAPI driver does, the loopback stream delivers a packet every 10 ms, silent ones
/// included, each stamped with the instant its first frame was recorded; each packet is placed on the track by that stamp
/// (<see cref="AudioTimeline"/>), so a lost packet leaves silence in its place and a late one keeps its place. A process
/// with no stream open sends no packets, so while none come the track is filled with silence to 200 ms behind the clock.
/// The track is filled with silence up to the run's end: the output is exactly as long as the run, whatever the process
/// played.
/// </summary>
internal sealed class ProcessAudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private const int BitsPerSample = 16;
    public const int BytesPerFrame = Channels * BitsPerSample / 8;

    private const long QpcUnitsPerSecond = 10_000_000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly AudioInterop.IAudioClient _client;
    private readonly AudioInterop.IAudioCaptureClient _captureClient;
    private readonly AutoResetEvent _samplesReady;
    private long _captured;
    private int _discontinuities;
    private int _timestampErrors;

    /// <summary>Activates and initialises the stream without starting it; throws <see cref="AudioActivationException"/> on failure.</summary>
    public ProcessAudioCapture(int processId)
    {
        _client = AudioInterop.ActivateProcessLoopback(processId);
        _samplesReady = new AutoResetEvent(false);
        try
        {
            // The virtual loopback device has no mix format of its own: the caller names the format and the engine
            // converts to it (AUTOCONVERTPCM), as Microsoft's ApplicationLoopback sample does.
            var format = AudioInterop.WaveFormatEx.Pcm(SampleRate, Channels, BitsPerSample);
            uint flags = AudioInterop.StreamFlagsLoopback | AudioInterop.StreamFlagsEventCallback | AudioInterop.StreamFlagsAutoConvertPcm;
            _client.Initialize(AudioInterop.ShareModeShared, flags, 0, 0, format, nint.Zero);
            _client.SetEventHandle(_samplesReady.SafeWaitHandle.DangerousGetHandle());
            _captureClient = (AudioInterop.IAudioCaptureClient)_client.GetService(AudioInterop.AudioCaptureClientIid);
        }
        catch (COMException failure)
        {
            _samplesReady.Dispose();
            throw new AudioActivationException(FormattableString.Invariant($"initialisation failed with HRESULT 0x{failure.HResult:X8}"), failure);
        }
    }

    /// <summary>
    /// Starts the stream and writes audio into <paramref name="sink"/> until <paramref name="deadline"/> passes, then
    /// silence up to that instant with no slack held back, so the track lasts as long as the run; completes the sink at the
    /// end and returns how many of the frames the loopback stream delivered rather than silence filling, and how many
    /// packets it flagged as not following on from the one before or as stamped at an uncertain instant.
    /// </summary>
    public CaptureCounts Run(Deadline deadline, ChannelWriter<byte[]> sink)
    {
        var clock = Stopwatch.StartNew();
        AudioTimeline timeline = new(QpcNow(), SampleRate);
        _client.Start();
        try
        {
            while (!deadline.HasPassed())
            {
                bool signalled = _samplesReady.WaitOne(PollInterval);
                DrainPackets(sink, timeline);
                if (!signalled)
                {
                    WriteSilence(sink, timeline.SilenceToNow(QpcNow()));
                }
            }
            DrainPackets(sink, timeline);
            FillSilence(sink, timeline.End, FramesAt(clock.Elapsed));
            return new CaptureCounts(_captured, _discontinuities, _timestampErrors);
        }
        finally
        {
            _client.Stop();
            sink.TryComplete();
        }
    }

    public void Dispose()
    {
        Marshal.ReleaseComObject(_captureClient);
        Marshal.ReleaseComObject(_client);
        _samplesReady.Dispose();
    }

    private static long FramesAt(TimeSpan elapsed) => elapsed <= TimeSpan.Zero ? 0 : elapsed.Ticks * SampleRate / TimeSpan.TicksPerSecond;

    /// <summary>Now on the performance counter, in the 100 ns units GetBuffer stamps packets with.</summary>
    private static long QpcNow() => (long)((Int128)Stopwatch.GetTimestamp() * QpcUnitsPerSecond / Stopwatch.Frequency);

    /// <summary>Writes silence from <paramref name="written"/> frames up to <paramref name="due"/>.</summary>
    private static void FillSilence(ChannelWriter<byte[]> sink, long written, long due) => WriteSilence(sink, due - written);

    private static void WriteSilence(ChannelWriter<byte[]> sink, long frames)
    {
        if (frames > 0)
        {
            sink.TryWrite(new byte[frames * BytesPerFrame]);
        }
    }

    /// <summary>Reads every packet the engine holds and places each on <paramref name="timeline"/>.</summary>
    private void DrainPackets(ChannelWriter<byte[]> sink, AudioTimeline timeline)
    {
        while (_captureClient.GetNextPacketSize() > 0)
        {
            _captureClient.GetBuffer(out nint data, out uint frames, out uint flags, out _, out ulong qpcPosition);
            try
            {
                CountFlags(flags);
                if (frames > 0)
                {
                    WritePacket(sink, timeline, new Packet(data, (int)frames, flags, (long)qpcPosition));
                }
            }
            finally
            {
                _captureClient.ReleaseBuffer(frames);
            }
        }
    }

    private void CountFlags(uint flags)
    {
        if ((flags & AudioInterop.BufferFlagsDataDiscontinuity) != 0)
        {
            _discontinuities++;
        }
        if ((flags & AudioInterop.BufferFlagsTimestampError) != 0)
        {
            _timestampErrors++;
        }
    }

    /// <summary>
    /// Writes the silence the timeline places before a packet, then the packet without the leading frames that overlap the
    /// track; a packet flagged silent is written as zeros, and one whose timestamp is uncertain goes where the track ends.
    /// </summary>
    private void WritePacket(ChannelWriter<byte[]> sink, AudioTimeline timeline, Packet packet)
    {
        int dropped = 0;
        if ((packet.Flags & AudioInterop.BufferFlagsTimestampError) != 0)
        {
            timeline.Append(packet.Frames);
        }
        else
        {
            WriteSilence(sink, timeline.SilenceBefore(packet.QpcPosition, packet.Frames, out dropped));
        }
        int kept = packet.Frames - dropped;
        if (kept == 0)
        {
            return;
        }
        byte[] chunk = new byte[kept * BytesPerFrame];
        if ((packet.Flags & AudioInterop.BufferFlagsSilent) == 0)
        {
            Marshal.Copy(packet.Data + (dropped * BytesPerFrame), chunk, 0, chunk.Length);
        }
        sink.TryWrite(chunk);
        _captured += kept;
    }

    /// <summary>One packet as GetBuffer hands it over: its data, its length in frames, its flags and its timestamp.</summary>
    private readonly record struct Packet(nint Data, int Frames, uint Flags, long QpcPosition);

    /// <summary>
    /// The frames the loopback stream delivered, the packets it flagged as a discontinuity and those it flagged as stamped
    /// at an uncertain instant.
    /// </summary>
    public readonly record struct CaptureCounts(long Captured, int Discontinuities, int TimestampErrors);
}
