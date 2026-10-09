using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>A real-time recording's ffmpeg command lines, its encoder choice and the one-frame encoder probe, with no ffmpeg run.</summary>
public sealed class RealtimeEncodeTests
{
    private const string Ffmpeg = @"C:\ffmpeg\bin\ffmpeg.exe";
    private const string Log = @"C:\Games\Probe\.godot\godot-mcp\recordings\realtime-Probe-ffmpeg.log";
    private const string Mkv = @"C:\Games\Probe\.godot\godot-mcp\recordings\20261009-120000-000-Probe-realtime.mkv";
    private const string Mp4 = @"C:\Games\Probe\.godot\godot-mcp\recordings\20261009-120000-000-Probe-realtime.mp4";

    /// <summary>The spike's crop; 1280x720 is already even, so the encode's stream size is the crop's own.</summary>
    private static readonly PixelRect Crop = new(1, 31, 1280, 720);

    private static readonly string[] Input =
    [
        "-hide_banner",
        "-v",
        "error",
        "-use_wallclock_as_timestamps",
        "1",
        "-f",
        "rawvideo",
        "-pix_fmt",
        "bgra",
        "-video_size",
        "1280x720",
        "-framerate",
        "30",
        "-i",
        "-",
    ];

    private static readonly string[] Tail = ["-fps_mode", "vfr", "-y", Mkv];

    private static readonly string[] AllEncoders =
    [
        RealtimeEncode.H264Nvenc,
        RealtimeEncode.HevcNvenc,
        RealtimeEncode.Libx264,
        RealtimeEncode.H264Mf,
    ];

    private static readonly string[] ProbeHead =
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
    ];

    private static readonly string[] ProbeTail = ["-f", "null", "-"];

    [Theory]
    [InlineData(RealtimeEncode.H264Nvenc)]
    [InlineData(RealtimeEncode.HevcNvenc)]
    public void NvencTakesBgraWithoutAPixelFormat(string encoder)
    {
        List<string> arguments = RealtimeEncode.EncodeArguments(encoder, Crop, 30, Mkv);

        Assert.Equal([.. Input, "-c:v", encoder, .. Tail], arguments);
    }

    [Fact]
    public void LibxFourSixFourUsesUltrafastAndYuvFourTwoZero()
    {
        List<string> arguments = RealtimeEncode.EncodeArguments(RealtimeEncode.Libx264, Crop, 30, Mkv);

        Assert.Equal([.. Input, "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", .. Tail], arguments);
    }

    [Fact]
    public void HFourMfUsesYuvFourTwoZero()
    {
        List<string> arguments = RealtimeEncode.EncodeArguments(RealtimeEncode.H264Mf, Crop, 30, Mkv);

        Assert.Equal([.. Input, "-c:v", "h264_mf", "-pix_fmt", "yuv420p", .. Tail], arguments);
    }

    [Fact]
    public void AllEncodersUseWallClockTimestampsAndVfr()
    {
        foreach (string encoder in AllEncoders)
        {
            List<string> arguments = RealtimeEncode.EncodeArguments(encoder, Crop, 30, Mkv);
            int input = arguments.IndexOf("-i");
            int wallClock = arguments.IndexOf("-use_wallclock_as_timestamps");
            int fpsMode = arguments.IndexOf("-fps_mode");

            Assert.InRange(wallClock, 0, input - 1);
            Assert.Equal("1", arguments[wallClock + 1]);
            Assert.True(fpsMode > input, $"{encoder}: -fps_mode is not an output option");
            Assert.Equal("vfr", arguments[fpsMode + 1]);
        }
    }

    // The caller passes the crop, never a size of its own, so an odd crop cannot reach ffmpeg as an odd stream size.
    [Fact]
    public void AnOddCropIsEncodedAtItsEvenSize()
    {
        List<string> arguments = RealtimeEncode.EncodeArguments(RealtimeEncode.Libx264, new PixelRect(1, 31, 641, 359), 60, Mkv);

        Assert.Equal(["-hide_banner", "-v", "error", "-use_wallclock_as_timestamps", "1", "-f", "rawvideo", "-pix_fmt", "bgra"], arguments.Take(9));
        Assert.Equal(["-video_size", "642x360", "-framerate", "60", "-i", "-"], arguments.Skip(9).Take(6));
    }

    [Fact]
    public void NoNostdinIsPassedBecauseTheVideoComesOnStdin()
    {
        foreach (string encoder in AllEncoders)
        {
            List<string> arguments = RealtimeEncode.EncodeArguments(encoder, Crop, 30, Mkv);

            Assert.DoesNotContain("-nostdin", arguments);
            Assert.Equal("-", arguments[arguments.IndexOf("-i") + 1]);
        }
    }

    [Fact]
    public async Task AboveFortyNinetySixOnEitherSideHevcNvencIsChosenWhenItProbed()
    {
        string[] usable = [RealtimeEncode.H264Nvenc, RealtimeEncode.HevcNvenc, RealtimeEncode.Libx264];

        Assert.Equal(RealtimeEncode.HevcNvenc, await ChooseWith(new PixelRect(0, 0, 4097, 2160), usable));
        Assert.Equal(RealtimeEncode.HevcNvenc, await ChooseWith(new PixelRect(0, 0, 1440, 5120), usable));
        Assert.Equal(RealtimeEncode.H264Nvenc, await ChooseWith(new PixelRect(0, 0, 4096, 4096), usable));
    }

    [Fact]
    public async Task AboveFortyNinetySixFallsBackToLibxFourSixFourWhenHevcFailed()
    {
        Assert.Equal(
            RealtimeEncode.Libx264,
            await ChooseWith(new PixelRect(0, 0, 5120, 1440), RealtimeEncode.H264Nvenc, RealtimeEncode.Libx264, RealtimeEncode.H264Mf)
        );
    }

    [Fact]
    public async Task TheChoicePrefersNvencThenLibxThenMf()
    {
        Assert.Equal(RealtimeEncode.H264Nvenc, await ChooseWith(new PixelRect(0, 0, 1920, 1080), AllEncoders));
        Assert.Equal(RealtimeEncode.Libx264, await ChooseWith(new PixelRect(0, 0, 1920, 1080), RealtimeEncode.Libx264, RealtimeEncode.H264Mf));
        Assert.Equal(RealtimeEncode.H264Mf, await ChooseWith(new PixelRect(0, 0, 1920, 1080), RealtimeEncode.H264Mf, RealtimeEncode.HevcNvenc));
    }

    [Fact]
    public async Task NothingUsableIsAnErrorNamingWhatWasTried()
    {
        SessionException none = await Assert.ThrowsAsync<SessionException>(() => ChooseWith(new PixelRect(0, 0, 1280, 720)));
        SessionException wide = await Assert.ThrowsAsync<SessionException>(() => ChooseWith(new PixelRect(0, 0, 5120, 1440), RealtimeEncode.H264Mf));

        Assert.Equal($"ffmpeg at {Ffmpeg} has no working encoder for a 1280x720 clip (tried h264_nvenc, libx264, h264_mf); see {Log}.", none.Message);
        Assert.Equal($"ffmpeg at {Ffmpeg} has no working encoder for a 5120x1440 clip (tried hevc_nvenc, libx264); see {Log}.", wide.Message);
    }

    [Fact]
    public async Task TheProbeStopsAtTheFirstEncoderThatPasses()
    {
        EncoderProbeCache cache = new();
        List<string> ran = [];
        Task<ToolProcessResult> Run(ToolProcessRequest request, CancellationToken _)
        {
            ran.Add(EncoderIn(request));
            return Task.FromResult(new ToolProcessResult(EncoderIn(request) == RealtimeEncode.Libx264 ? 0 : 1, TimeSpan.Zero, KillReason.None));
        }

        string chosen = await cache.ChooseAsync(Ffmpeg, Log, new PixelRect(0, 0, 1920, 1080), Run, TestContext.Current.CancellationToken);

        Assert.Equal(RealtimeEncode.Libx264, chosen);
        Assert.Equal([RealtimeEncode.H264Nvenc, RealtimeEncode.Libx264], ran);
    }

    [Fact]
    public async Task AFaultedProbeIsForgottenAndRetried()
    {
        EncoderProbeCache cache = new();
        int runs = 0;
        Task<ToolProcessResult> Run(ToolProcessRequest _, CancellationToken __)
        {
            runs++;
            return runs == 1
                ? Task.FromException<ToolProcessResult>(new IOException("ffmpeg could not be started"))
                : Task.FromResult(new ToolProcessResult(0, TimeSpan.Zero, KillReason.None));
        }

        await Assert.ThrowsAsync<IOException>(() =>
            cache.IsUsableAsync(Ffmpeg, Log, RealtimeEncode.H264Nvenc, Run, TestContext.Current.CancellationToken)
        );
        Assert.True(await cache.IsUsableAsync(Ffmpeg, Log, RealtimeEncode.H264Nvenc, Run, TestContext.Current.CancellationToken));

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task ACancelledWaiterLeavesTheProbeRunningAndCached()
    {
        EncoderProbeCache cache = new();
        TaskCompletionSource<object?> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ToolProcessResult> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;
        async Task<ToolProcessResult> Run(ToolProcessRequest _, CancellationToken __)
        {
            runs++;
            started.SetResult(null);
            return await finished.Task;
        }

        using CancellationTokenSource cancelled = new();
        Task<bool> waiter = cache.IsUsableAsync(Ffmpeg, Log, RealtimeEncode.H264Nvenc, Run, cancelled.Token);
        await started.Task;
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        finished.SetResult(new ToolProcessResult(0, TimeSpan.Zero, KillReason.None));

        Assert.True(await cache.IsUsableAsync(Ffmpeg, Log, RealtimeEncode.H264Nvenc, Run, TestContext.Current.CancellationToken));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task TheProbeEncodesOneFrameOfTheCandidateWithItsCeiling()
    {
        List<ToolProcessRequest> requests = [];
        bool usable = await RealtimeEncode.ProbeAsync(
            Ffmpeg,
            RealtimeEncode.Libx264,
            Log,
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ToolProcessResult(0, TimeSpan.Zero, KillReason.None));
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(usable);
        ToolProcessRequest probe = Assert.Single(requests);
        Assert.Equal([.. ProbeHead, "libx264", "-pix_fmt", "yuv420p", .. ProbeTail], probe.Arguments);
        Assert.Equal((Ffmpeg, Log, TimeSpan.FromSeconds(10)), (probe.FileName, probe.LogPath, probe.Ceiling));
    }

    [Fact]
    public async Task AKilledProbeRunIsNotUsable()
    {
        bool usable = await RealtimeEncode.ProbeAsync(
            Ffmpeg,
            RealtimeEncode.H264Nvenc,
            Log,
            (_, _) => Task.FromResult(new ToolProcessResult(-1, TimeSpan.Zero, KillReason.Ceiling)),
            TestContext.Current.CancellationToken
        );

        Assert.False(usable);
    }

    [Fact]
    public void TheProbeArgumentsCarryBgraAndNostdin()
    {
        foreach (string encoder in AllEncoders)
        {
            List<string> arguments = RealtimeEncode.ProbeArguments(encoder);
            int input = arguments.IndexOf("-i");
            int filter = arguments.IndexOf("-vf");
            int codec = arguments.IndexOf("-c:v");

            Assert.Contains("-nostdin", arguments);
            Assert.Equal("format=bgra", arguments[filter + 1]);
            Assert.True(filter > input && codec > filter, $"{encoder}: the bgra filter is not between the input and the encoder");
        }
    }

    [Fact]
    public void TheRemuxCopiesWithFaststart()
    {
        Assert.Equal(
            ["-hide_banner", "-v", "error", "-nostdin", "-i", Mkv, "-c", "copy", "-movflags", "+faststart", "-y", Mp4],
            RealtimeEncode.RemuxArguments(Mkv, Mp4, isHevc: false)
        );
    }

    [Fact]
    public void AHevcRemuxIsTaggedHvcOne()
    {
        Assert.Equal(
            ["-hide_banner", "-v", "error", "-nostdin", "-i", Mkv, "-c", "copy", "-movflags", "+faststart", "-tag:v", "hvc1", "-y", Mp4],
            RealtimeEncode.RemuxArguments(Mkv, Mp4, isHevc: true)
        );
    }

    /// <summary>Chooses an encoder for <paramref name="crop"/> over a fresh cache whose ffmpeg passes exactly <paramref name="usable"/>.</summary>
    private static Task<string> ChooseWith(PixelRect crop, params string[] usable) =>
        new EncoderProbeCache().ChooseAsync(
            Ffmpeg,
            Log,
            crop,
            (request, _) => Task.FromResult(new ToolProcessResult(usable.Contains(EncoderIn(request)) ? 0 : 1, TimeSpan.Zero, KillReason.None)),
            TestContext.Current.CancellationToken
        );

    /// <summary>The encoder a probe request is built for.</summary>
    private static string EncoderIn(ToolProcessRequest request)
    {
        List<string> arguments = [.. request.Arguments];
        return arguments[arguments.IndexOf("-c:v") + 1];
    }
}
