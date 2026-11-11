using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;

namespace GodotMcp.IntegrationTests;

public sealed partial class RealtimeRecordingTests
{
    /// <summary>How long ffmpeg is held suspended over the stop: past the helper's 0.2 s deadline poll and the few frames its pipes hold.</summary>
    private static readonly TimeSpan EncoderStall = TimeSpan.FromSeconds(2.5);

    /// <summary>How long the helper writes into the suspended ffmpeg before the stop: enough frames to fill the pipes and block it.</summary>
    private static readonly TimeSpan PipesFill = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// An encoder that stalls over the stop still makes a clip that lasts to its last frame. ffmpeg is suspended after start
    /// answers and before stop is called, so the frames the helper writes from then on wait in the pipes until the resume, and
    /// ffmpeg stamps each with its arrival: the clip holds a frame shown at or after the resume. Its first frame reached
    /// ffmpeg before start answered, so the clip lasts at least from start's answer to the moment before the resume, two
    /// moments the test itself observed; a busy machine only delays the frames' arrival, which lengthens the clip.
    /// </summary>
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnEncoderStalledOverTheStopKeepsTheClipToItsLastFrame()
    {
        await LaunchAsync(quiet: false, TestContext.Current.CancellationToken);
        var wall = Stopwatch.StartNew();
        await MarkAsync("start");
        TimeSpan started = wall.Elapsed;
        using Process encoder = EncodeProcess();
        Task<JsonNode> stop;
        TimeSpan resuming;
        using (EncoderSuspension.Of(encoder))
        {
            await Task.Delay(PipesFill, Token);
            stop = MarkAsync("stop");
            await Task.Delay(EncoderStall, Token);
            resuming = wall.Elapsed;
        }

        string path = ClipPath(await stop);

        double between = (resuming - started).TotalSeconds;
        (double seconds, _, _) = await ProbeClipAsync(path);
        double lastFrameEnd = await LastFrameEndAsync(path);
        Assert.True(
            seconds >= between - LengthTolerance,
            FormattableString.Invariant($"the clip lasts {seconds:F3} s, but its last frame ends at {lastFrameEnd:F3} s; start answered and ")
                + FormattableString.Invariant($"ffmpeg was resumed {between:F3} s apart, so it must last at least {between - LengthTolerance:F3} s")
        );
    }

    /// <summary>The recording's encode: the one ffmpeg this process started that is still running once start has answered.</summary>
    private static Process EncodeProcess()
    {
        int self = Environment.ProcessId;
        CaptureWindows.ProcessRow row = Assert.Single(
            CaptureWindows.Processes(),
            process => process.ParentId == self && string.Equals(process.Image, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
        );
        return Process.GetProcessById(row.Id);
    }

    /// <summary>When the clip's last video frame ends, its presentation time plus its duration, as ffprobe reads its packets.</summary>
    private static async Task<double> LastFrameEndAsync(string clip)
    {
        (byte[] output, string errors, int exitCode) = await RunAsync(
            FfprobePath(),
            ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", clip]
        );
        Assert.True(exitCode == 0, $"ffprobe failed on {clip}: {errors}");
        return System
            .Text.Encoding.UTF8.GetString(output)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(','))
            .Max(fields => double.Parse(fields[0], CultureInfo.InvariantCulture) + double.Parse(fields[1], CultureInfo.InvariantCulture));
    }

    /// <summary>A process held suspended (NtSuspendProcess) until disposed, then resumed once through the same handle.</summary>
    private sealed class EncoderSuspension : IDisposable
    {
        private readonly Process _process;
        private bool _resumed;

        private EncoderSuspension(Process process) => _process = process;

        public static EncoderSuspension Of(Process process)
        {
            int status = NativeMethods.NtSuspendProcess(process.Handle);
            Assert.True(status == 0, FormattableString.Invariant($"NtSuspendProcess of ffmpeg {process.Id} failed: NTSTATUS 0x{status:X8}"));
            return new EncoderSuspension(process);
        }

        public void Dispose()
        {
            if (_resumed)
            {
                return;
            }

            _resumed = true;
            int status = NativeMethods.NtResumeProcess(_process.Handle);
            Assert.True(status == 0, FormattableString.Invariant($"NtResumeProcess of ffmpeg {_process.Id} failed: NTSTATUS 0x{status:X8}"));
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ntdll.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial int NtSuspendProcess(nint process);

        [LibraryImport("ntdll.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial int NtResumeProcess(nint process);
    }
}
