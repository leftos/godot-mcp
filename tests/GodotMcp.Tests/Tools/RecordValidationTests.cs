using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Session;
using GodotMcp.Tests.Wire;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// record_mark's argument checks and a real-time start's refusals, each before any process starts, and a recording's end when
/// its session or the server ends, on fake games attached to a real registry whose real-time environment is fake; no Godot,
/// helper or ffmpeg runs here.
/// </summary>
public sealed class RecordValidationTests : IAsyncDisposable
{
    private const string Game = "game";

    private readonly RegistryHarness _harness = new();
    private readonly FakeRealtime _fake = new();
    private readonly List<FakeBridge> _games = [];
    private readonly RuntimeTools _tools;

    public RecordValidationTests()
    {
        _harness.Sessions.Realtime = _fake.Environment;
        _tools = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (FakeBridge game in _games)
        {
            game.Dispose();
        }

        _fake.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Start")]
    [InlineData("pause")]
    public async Task AMarkOtherThanStartOrStopIsRefused(string mark)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.RecordMarkAsync(mark, cancellationToken: Token));

        Assert.Equal($"mark must be \"start\" or \"stop\"; got \"{mark}\".", refused.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(-30)]
    public async Task FpsOutsideOneToSixtyIsRefused(int fps)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.RecordMarkAsync("start", new RecordMarkOptions(Fps: fps), cancellationToken: Token)
        );

        Assert.Equal($"fps must be 1 to 60; got {fps}.", refused.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public async Task MaxSecondsOutsideOneToSixHundredIsRefused(int maxSeconds)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.RecordMarkAsync("start", new RecordMarkOptions(MaxSeconds: maxSeconds), cancellationToken: Token)
        );

        Assert.Equal($"maxSeconds must be 1 to 600; got {maxSeconds}.", refused.Message);
    }

    [Fact]
    public void TheAudioOptionDeserialisesAndIsUnsetWhenOmitted()
    {
        RecordMarkOptions? bound = JsonSerializer.Deserialize<RecordMarkOptions>("""{"fps":24,"maxSeconds":5}""", ToolJson.Options);
        RecordMarkOptions? on = JsonSerializer.Deserialize<RecordMarkOptions>("""{"fps":24,"audio":true}""", ToolJson.Options);
        RecordMarkOptions? off = JsonSerializer.Deserialize<RecordMarkOptions>("""{"fps":24,"audio":false}""", ToolJson.Options);

        Assert.Equal(new RecordMarkOptions(24, 5, Audio: null), bound);
        Assert.Equal(new RecordMarkOptions(24, Audio: true), on);
        Assert.Equal(new RecordMarkOptions(24, Audio: false), off);
    }

    [Fact]
    public async Task OptionsWithStopAreRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.RecordMarkAsync("stop", new RecordMarkOptions(), cancellationToken: Token)
        );

        Assert.Equal("options belong to record_mark start; stop takes none.", refused.Message);
    }

    [Fact]
    public void AMovieStartTakesAPathAndOverwrite()
    {
        RecordMarkOptions? options = RuntimeTools.CheckMarkOptions("start", new RecordMarkOptions(Path: "clips/x.mp4", Overwrite: true));

        RuntimeTools.CheckMovieOptions(options);
        RuntimeTools.CheckMovieOptions(RuntimeTools.CheckMarkOptions("start", null));
        Assert.Equal(new RecordMarkOptions(Path: "clips/x.mp4", Overwrite: true), options);
    }

    [Fact]
    public void AMovieStartRefusesFpsEvenAtItsDefault()
    {
        RecordMarkOptions? options = RuntimeTools.CheckMarkOptions("start", new RecordMarkOptions(Fps: 30, Path: "x.mp4"));

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckMovieOptions(options));

        Assert.Equal(
            "a Movie Maker recording's start mark takes only options.path and options.overwrite; fps, maxSeconds and audio set a real-time recording",
            refused.Message
        );
    }

    [Fact]
    public async Task AnOverwriteWithoutAPathIsRefusedOnARealtimeStart()
    {
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.RecordMarkAsync("start", new RecordMarkOptions(Overwrite: true), cancellationToken: Token)
        );

        Assert.Equal("options.overwrite needs options.path", refused.Message);
        Assert.Empty(_fake.Starts);
    }

    [Fact]
    public void AnOverwriteWithoutAPathIsRefusedOnAMovieStart()
    {
        // A Movie Maker start's options pass through the same check before its own.
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckMarkOptions("start", new RecordMarkOptions(Overwrite: true)));

        Assert.Equal("options.overwrite needs options.path", refused.Message);
    }

    [Fact]
    public async Task ARealtimeStartStillDefaultsTo30FpsAnd600SecondsWithSound()
    {
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd, ProcessId = 77 });
        DateTimeOffset before = DateTimeOffset.UtcNow;

        JsonNode started = JsonNode.Parse(await _tools.RecordMarkAsync("start", new RecordMarkOptions(), cancellationToken: Token))!;
        DateTimeOffset after = DateTimeOffset.UtcNow;
        await _tools.RecordMarkAsync("stop", cancellationToken: Token);

        Assert.Equal(30, started["fps"]!.GetValue<int>());
        Assert.True(started["audio"]!.GetValue<bool>());
        ToolProcessRequest capture = Assert.Single(_fake.Starts, request => request.Arguments.Contains("--until-file"));
        List<string> arguments = [.. capture.Arguments];
        Assert.Equal("30", arguments[arguments.IndexOf("--fps") + 1]);
        Assert.Equal("77", arguments[arguments.IndexOf("--audio-pid") + 1]);
        DateTimeOffset deadline = _fake.DeadlineAtHelperStart ?? throw new InvalidOperationException("the helper read no deadline");
        TimeSpan ahead = RealtimeRecording.FirstFrameAllowance * LoadClock.BackstopFactor + TimeSpan.FromSeconds(600);
        Assert.InRange(deadline, before + ahead - TimeSpan.FromSeconds(2), after + ahead + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AStopWithNothingRunningIsRefused()
    {
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });

        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.RecordMarkAsync("stop", cancellationToken: Token));

        Assert.Equal("No real-time recording is running in session 'game'; record_mark start first.", refused.Message);
    }

    [Fact]
    public async Task AStartOnAHeadlessSessionIsRefused()
    {
        await AttachAsync(new FakeHello());

        McpException refused = await StartRefusedAsync();

        Assert.Equal("Session 'game' is headless and has no window to record.", refused.Message);
    }

    [Fact]
    public async Task AStartOnAQuietAttachIsRefusedWithTheFix()
    {
        await AttachQuietAsync();

        McpException refused = await StartRefusedAsync();

        Assert.Equal(
            "Session 'game' is quiet, so its window is on the server's hidden desktop, which a real-time recording cannot see. Launch "
                + "it with options.quiet: false (options.mute: true keeps it silent), or with options.record for a Movie Maker recording.",
            refused.Message
        );
    }

    [Fact]
    public async Task AQuietSessionIsRefusedBeforeItsWindowIsRead()
    {
        await AttachQuietAsync();

        await StartRefusedAsync();

        Assert.Equal(0, _fake.WindowReads);
        Assert.Empty(_fake.Events);
    }

    [Fact]
    public async Task AStartWithNoFfmpegIsRefused()
    {
        _fake.Ffmpeg = new FfmpegLookup(null, null);
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });

        McpException refused = await StartRefusedAsync();

        Assert.Equal(
            "A real-time recording needs ffmpeg: set FFMPEG_PATH to ffmpeg.exe, or install it with winget install Gyan.FFmpeg.",
            refused.Message
        );
    }

    [Fact]
    public async Task AStartWithAMissingConfiguredFfmpegIsRefused()
    {
        _fake.Ffmpeg = new FfmpegLookup(null, @"C:\nowhere\ffmpeg.exe");
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });

        McpException refused = await StartRefusedAsync();

        Assert.Equal(
            @"A real-time recording needs ffmpeg, and FFMPEG_PATH is 'C:\nowhere\ffmpeg.exe', which does not exist: point it at ffmpeg.exe, "
                + "or install ffmpeg with winget install Gyan.FFmpeg.",
            refused.Message
        );
    }

    [Fact]
    public async Task AStartWithNoHelperIsRefused()
    {
        _fake.Helper = null;
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });

        McpException refused = await StartRefusedAsync();

        Assert.Equal(
            "A real-time recording needs Windows 10 2004 (build 19041) or later and capture/godot-mcp-capture.exe beside the server "
                + $"({FakeRealtime.ExpectedHelper}); reinstall godot-mcp.",
            refused.Message
        );
    }

    [Fact]
    public async Task AStartOnAnEmbeddedWindowIsRefused()
    {
        _fake.TopLevel = false;
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });

        McpException refused = await StartRefusedAsync();

        Assert.Equal(
            "The game's window in session 'game' is embedded in another window, so a real-time recording cannot capture it alone; run "
                + "the game in its own window.",
            refused.Message
        );
    }

    [Fact]
    public async Task AStartOnAMinimizedWindowIsRefused()
    {
        _fake.Reading = _fake.Reading with { Minimized = true };
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });

        McpException refused = await StartRefusedAsync();

        Assert.Equal(
            "The game's window is minimized, so Windows composes no picture of it; restore it, then record_mark start again.",
            refused.Message
        );
        Assert.Empty(_fake.Events);
    }

    [Fact]
    public async Task AStartWhileRunningIsRefusedWithTheAge()
    {
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });
        JsonNode started = JsonNode.Parse(await _tools.RecordMarkAsync("start", cancellationToken: Token))!;

        McpException refused = await StartRefusedAsync();
        JsonNode stopped = JsonNode.Parse(await _tools.RecordMarkAsync("stop", cancellationToken: Token))!;

        // The age is whole seconds since the first start was asked for, which a busy machine may stretch past 0.
        Assert.Matches(
            @"^A real-time recording is already running in session 'game' \(started \d+ s ago\); record_mark stop first\.$",
            refused.Message
        );
        Assert.Equal("realtime", started["mode"]!.GetValue<string>());
        Assert.Equal("start", started["mark"]!.GetValue<string>());
        Assert.Equal(FakeRealtime.Width, started["width"]!.GetValue<int>());
        Assert.Equal("stop", stopped["mark"]!.GetValue<string>());
        Assert.Equal(started["path"]!.GetValue<string>(), stopped["clip"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADetachDuringAStartEndsTheCapture()
    {
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });
        TaskCompletionSource probing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fake.BeforeProbe = async _ =>
        {
            probing.TrySetResult();
            await detached.Task.WaitAsync(RegistryHarness.HangGuard, Token);
        };
        Task<string> start = _tools.RecordMarkAsync("start", cancellationToken: Token);
        await probing.Task.WaitAsync(RegistryHarness.HangGuard, Token);

        DetachResult result = await _harness.Sessions.DetachAsync(Game, Token);
        detached.TrySetResult();

        string path = JsonNode.Parse(await start)!["path"]!.GetValue<string>();
        await RegistryHarness.WaitUntilAsync(() => File.Exists(path));
        // A helper killed, as one is when its deadline cannot be moved, never reads the deadline it ends on.
        Assert.True(_fake.HelperKilledAt is null, "the helper was killed rather than ended by its deadline");
        Assert.NotNull(_fake.DeadlineAtHelperEnd);
        Assert.Null(result.Recording);
    }

    [Fact]
    public async Task AServerShutdownFinishesARunningRecording()
    {
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });
        string path = JsonNode.Parse(await _tools.RecordMarkAsync("start", cancellationToken: Token))!["path"]!.GetValue<string>();

        _harness.Sessions.Shutdown();

        Assert.True(File.Exists(path), $"the clip {path} was not finished before the shutdown returned");
        Assert.False(File.Exists(Path.ChangeExtension(path, ".mkv")), "the .mkv was left behind");
        Assert.False(File.Exists(Path.ChangeExtension(path, ".deadline")), "the deadline file was left behind");
    }

    [Fact]
    public async Task AThenRecordInARealtimeRecordingIsRefusedWithThePausePattern()
    {
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });
        await _tools.RecordMarkAsync("start", cancellationToken: Token);

        McpException refused = await ThenRecordRefusedAsync();
        await _tools.RecordMarkAsync("stop", cancellationToken: Token);

        Assert.Equal(
            "options.then.record places a mark in a Movie Maker recording's met frame; this session's recording is real-time, so use "
                + "then: {pause: true}, then record_mark start, then frame_control resume.",
            refused.Message
        );
        Assert.DoesNotContain(_games[^1].TakeQueued(), request => request["command"]?.GetValue<string>() == "wait_for");
    }

    [Fact]
    public async Task AThenRecordWithNoRecordingIsRefused()
    {
        await AttachAsync(new FakeHello { WindowHandle = FakeRealtime.Hwnd });

        McpException refused = await ThenRecordRefusedAsync();

        Assert.Equal("options.then.record needs a recording: start one with run_project's record option.", refused.Message);
        Assert.Empty(_fake.Starts);
        Assert.DoesNotContain(_games[^1].TakeQueued(), request => request["command"]?.GetValue<string>() == "wait_for");
    }

    /// <summary>A wait_for with then.record start, refused; a wait the fake game were sent would time out with another text.</summary>
    private Task<McpException> ThenRecordRefusedAsync() =>
        Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(
                new WaitCondition(Expression: "true"),
                1000,
                new WaitOptions(Then: new WaitThen(Record: new ThenRecord("start"))),
                cancellationToken: Token
            )
        );

    private Task<McpException> StartRefusedAsync() =>
        Assert.ThrowsAsync<McpException>(() => _tools.RecordMarkAsync("start", cancellationToken: Token));

    private async Task AttachAsync(FakeHello hello) => _games.Add(await _harness.AttachFakeGameAsync(_harness.Project(Game), Game, hello));

    /// <summary>Attaches a quiet session to a fake game whose hello carries a window handle.</summary>
    private async Task AttachQuietAsync()
    {
        string projectDir = _harness.Project(Game);
        string attachFile = AttachFile.PathIn(projectDir);
        Task<AttachResult> attach = _harness.Sessions.AttachAsync(
            new AttachRequest(projectDir, Game, RegistryHarness.LongWait, false, true, null),
            Token
        );
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile), attach);
        string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
        _games.Add(await FakeBridge.DialAsync(_harness.Listener.Port, token, projectDir, new FakeHello { WindowHandle = FakeRealtime.Hwnd }, Token));
        await attach;
    }
}
