using System.Globalization;
using System.Text;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>A real-time clip of seven frames with a gap keeps its wall-clock length through the stream-copy remux to .mp4.</summary>
public sealed class RealtimeRemuxTests
{
    /// <summary>A seven-frame clip whose last two frames arrive 2.8 s after the fifth is still 3.033 s long after the remux.</summary>
    [Fact]
    public async Task ASevenFrameClipWithAGapKeepsItsLengthThroughTheRemux()
    {
        using TempDirectory directory = new();
        string mkv = directory.Combine("clip.mkv");
        string mp4 = directory.Combine("clip.mp4");
        string ffmpeg = RealtimeRecordingTests.FfmpegPath();

        (byte[] Output, string Errors, int ExitCode) encode = await RealtimeRecordingTests.RunAsync(
            ffmpeg,
            [
                "-hide_banner",
                "-v",
                "error",
                "-nostdin",
                "-f",
                "lavfi",
                "-i",
                "testsrc=size=640x360:rate=30",
                "-vf",
                @"setpts='N/30/TB+if(gte(N\,5)\,2.8/TB\,0)'",
                "-frames:v",
                "7",
                "-c:v",
                RealtimeEncode.Libx264,
                "-preset",
                "ultrafast",
                "-pix_fmt",
                "yuv420p",
                "-fps_mode",
                "vfr",
                "-y",
                mkv,
            ]
        );
        Assert.True(encode.ExitCode == 0, $"the seven-frame encode failed: {encode.Errors}");

        (byte[] Output, string Errors, int ExitCode) remux = await RealtimeRecordingTests.RunAsync(
            ffmpeg,
            RealtimeEncode.RemuxArguments(mkv, mp4, isHevc: false)
        );
        Assert.True(remux.ExitCode == 0, $"the remux failed: {remux.Errors}");

        (byte[] Output, string Errors, int ExitCode) probe = await RealtimeRecordingTests.RunAsync(
            RealtimeRecordingTests.FfprobePath(),
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration", "-of", "csv=p=0", mp4]
        );
        Assert.True(probe.ExitCode == 0, $"ffprobe failed on {mp4}: {probe.Errors}");

        double seconds = double.Parse(Encoding.UTF8.GetString(probe.Output).Trim(), CultureInfo.InvariantCulture);
        Assert.True(seconds >= 3.0, FormattableString.Invariant($"the remuxed clip lasts {seconds:F3} s, but its frames span 3.033 s"));
    }
}
