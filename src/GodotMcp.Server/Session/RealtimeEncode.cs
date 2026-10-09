using System.Collections.Concurrent;
using System.Globalization;

namespace GodotMcp.Server.Session;

/// <summary>
/// A real-time recording's ffmpeg command lines: the encode of the capture helper's raw BGRA frames from stdin, stamped by
/// the wall clock so a slow capture keeps its real length, into a Matroska file; the remux of that file to an .mp4; and the
/// one-frame test encodes that tell which encoders an ffmpeg can run.
/// </summary>
internal static class RealtimeEncode
{
    public const string H264Nvenc = "h264_nvenc";
    public const string HevcNvenc = "hevc_nvenc";
    public const string Libx264 = "libx264";
    public const string H264Mf = "h264_mf";

    /// <summary>The longest side <see cref="H264Nvenc"/> encodes; a clip wider or taller needs <see cref="HevcNvenc"/>.</summary>
    public const int H264NvencMaxSide = 4096;

    /// <summary>How long one candidate's test encode may take.</summary>
    public static readonly TimeSpan ProbeCeiling = TimeSpan.FromSeconds(10);

    private static readonly string[] SmallPreference = [H264Nvenc, Libx264, H264Mf];
    private static readonly string[] LargePreference = [HevcNvenc, Libx264];

    /// <summary>
    /// The encoders a clip of <paramref name="crop"/> is tried with, in the order it is tried: up to
    /// <see cref="H264NvencMaxSide"/> on both sides of the crop's even size <see cref="H264Nvenc"/>, <see cref="Libx264"/>,
    /// <see cref="H264Mf"/>; above it on either side <see cref="HevcNvenc"/>, <see cref="Libx264"/>.
    /// </summary>
    public static IReadOnlyList<string> Preference(PixelRect crop)
    {
        (int width, int height) = WindowRect.OutputSize(crop);
        return width > H264NvencMaxSide || height > H264NvencMaxSide ? LargePreference : SmallPreference;
    }

    /// <summary>
    /// ffmpeg's arguments for the encode: raw BGRA frames of the crop's even size at <paramref name="fps"/> on stdin, each
    /// stamped with the wall clock as it arrives, encoded with <paramref name="encoder"/> to <paramref name="outputPath"/> at
    /// a variable frame rate. An nvenc encoder takes BGRA as it is: a <c>-pix_fmt yuv420p</c> before it would move the
    /// conversion onto the CPU. The software encoders get yuv420p. No <c>-nostdin</c>, since the video comes on stdin.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="encoder"/> is not one of the recording's encoders.</exception>
    public static List<string> EncodeArguments(string encoder, PixelRect crop, int fps, string outputPath)
    {
        (int width, int height) = WindowRect.OutputSize(crop);
        List<string> arguments = ["-hide_banner", "-v", "error", "-use_wallclock_as_timestamps", "1", "-f", "rawvideo", "-pix_fmt", "bgra"];
        arguments.AddRange([
            "-video_size",
            FormattableString.Invariant($"{width}x{height}"),
            "-framerate",
            fps.ToString(CultureInfo.InvariantCulture),
        ]);
        arguments.AddRange(["-i", "-", "-c:v", encoder, .. EncoderOptions(encoder), "-fps_mode", "vfr", "-y", outputPath]);
        return arguments;
    }

    /// <summary>
    /// ffmpeg's arguments for the remux of the finished Matroska file to an .mp4 by stream copy, its index at the front so a
    /// player starts at once; an HEVC stream is tagged <c>hvc1</c>, the tag players open. Nothing is piped into the remux, so
    /// it takes <c>-nostdin</c>.
    /// </summary>
    public static List<string> RemuxArguments(string mkv, string mp4, bool isHevc)
    {
        List<string> arguments = ["-hide_banner", "-v", "error", "-nostdin", "-i", mkv, "-c", "copy", "-movflags", "+faststart"];
        if (isHevc)
        {
            arguments.AddRange(["-tag:v", "hvc1"]);
        }

        arguments.AddRange(["-y", mp4]);
        return arguments;
    }

    /// <summary>
    /// ffmpeg's arguments for one candidate's test encode: one 256x256 BGRA frame of a generated colour, encoded with
    /// <paramref name="encoder"/> and thrown away. An encoder must encode to count, since ffmpeg lists nvenc on builds that
    /// fail it at run time on a machine without the GPU; the one frame goes through the same BGRA format as the real encode,
    /// so a candidate is probed on what it is asked to encode. <c>-nostdin</c>, since nothing is piped in.
    /// </summary>
    public static List<string> ProbeArguments(string encoder) =>
        [
            "-hide_banner",
            "-v",
            "error",
            "-nostdin",
            "-f",
            "lavfi",
            "-i",
            "color=size=256x256:rate=1",
            "-vf",
            "format=bgra",
            "-frames:v",
            "1",
            "-c:v",
            encoder,
            .. PixelFormatFor(encoder),
            "-f",
            "null",
            "-",
        ];

    /// <summary>
    /// Test-encodes one frame with <paramref name="encoder"/> with <paramref name="run"/>, the run's output going to
    /// <paramref name="logPath"/>; the encoder is usable when its run exited 0 within <see cref="ProbeCeiling"/>. An ffmpeg
    /// that cannot be started at all is thrown, not reported as having no encoder.
    /// </summary>
    public static async Task<bool> ProbeAsync(
        string ffmpegPath,
        string encoder,
        string logPath,
        Func<ToolProcessRequest, CancellationToken, Task<ToolProcessResult>> run,
        CancellationToken cancellationToken
    )
    {
        ToolProcessRequest request = new(ffmpegPath, ProbeArguments(encoder), Path.GetDirectoryName(logPath)!, logPath, ProbeCeiling)
        {
            AppendToLog = true,
        };
        ToolProcessResult result = await run(request, cancellationToken);
        return !result.WasKilled && result.ExitCode == 0;
    }

    private static string[] EncoderOptions(string encoder) =>
        encoder switch
        {
            H264Nvenc or HevcNvenc => [],
            Libx264 => ["-preset", "ultrafast", "-pix_fmt", "yuv420p"],
            H264Mf => ["-pix_fmt", "yuv420p"],
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "not one of the real-time recording's encoders"),
        };

    private static string[] PixelFormatFor(string encoder) => encoder is Libx264 or H264Mf ? ["-pix_fmt", "yuv420p"] : [];
}

/// <summary>
/// Each ffmpeg's encoder probes, one per (ffmpeg path, encoder), run once for the cache's life; the server keeps one. A probe
/// runs to its end whoever waits for it, bounded by <see cref="RealtimeEncode.ProbeCeiling"/>, so one caller's cancel does
/// not cancel it for the next; a probe that ended faulted or cancelled is forgotten, so the next call runs it again.
/// </summary>
internal sealed class EncoderProbeCache
{
    private readonly ConcurrentDictionary<(string FfmpegPath, string Encoder), Lazy<Task<bool>>> _probes = new();

    /// <summary>
    /// Whether the ffmpeg at <paramref name="ffmpegPath"/> test-encodes one frame with <paramref name="encoder"/>. The probe
    /// runs once and its result stays cached; cancelling <paramref name="cancellationToken"/> ends only this wait, leaving
    /// the probe running for the next caller.
    /// </summary>
    public async Task<bool> IsUsableAsync(
        string ffmpegPath,
        string logPath,
        string encoder,
        Func<ToolProcessRequest, CancellationToken, Task<ToolProcessResult>> run,
        CancellationToken cancellationToken
    )
    {
        (string FfmpegPath, string Encoder) key = (ffmpegPath, encoder);
        Lazy<Task<bool>> probe = _probes.GetOrAdd(
            key,
            _ => new Lazy<Task<bool>>(() => RealtimeEncode.ProbeAsync(ffmpegPath, encoder, logPath, run, CancellationToken.None))
        );
        try
        {
            return await probe.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (probe.Value.IsCompleted && !probe.Value.IsCompletedSuccessfully)
        {
            _probes.TryRemove(new KeyValuePair<(string FfmpegPath, string Encoder), Lazy<Task<bool>>>(key, probe));
            throw;
        }
    }

    /// <summary>
    /// The encoder for a clip of <paramref name="crop"/>: the size class's <see cref="RealtimeEncode.Preference"/>, each
    /// candidate probed lazily and stopping at the first that passes.
    /// </summary>
    /// <exception cref="SessionException">None of them passes; the message names those tried and the probe's log.</exception>
    public async Task<string> ChooseAsync(
        string ffmpegPath,
        string logPath,
        PixelRect crop,
        Func<ToolProcessRequest, CancellationToken, Task<ToolProcessResult>> run,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<string> preference = RealtimeEncode.Preference(crop);
        foreach (string encoder in preference)
        {
            if (await IsUsableAsync(ffmpegPath, logPath, encoder, run, cancellationToken))
            {
                return encoder;
            }
        }

        (int width, int height) = WindowRect.OutputSize(crop);
        string size = FormattableString.Invariant($"{width}x{height}");
        throw new SessionException(
            $"ffmpeg at {ffmpegPath} has no working encoder for a {size} clip (tried {string.Join(", ", preference)}); see {logPath}."
        );
    }
}
