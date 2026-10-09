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
    public void AnAudioOptionIsRefusedAsUnknown()
    {
        RecordMarkOptions? bound = JsonSerializer.Deserialize<RecordMarkOptions>("""{"fps":24,"maxSeconds":5}""", ToolJson.Options);

        Assert.Equal(new RecordMarkOptions(24, 5), bound);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RecordMarkOptions>("""{"fps":24,"audio":true}""", ToolJson.Options));
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
            "A real-time recording needs Windows 10 1903 or later and capture/godot-mcp-capture.exe beside the server "
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
            await detached.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        };
        Task<string> start = _tools.RecordMarkAsync("start", cancellationToken: Token);
        await probing.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        await _harness.Sessions.DetachAsync(Game, Token);
        detached.TrySetResult();

        string path = JsonNode.Parse(await start)!["path"]!.GetValue<string>();
        await RegistryHarness.WaitUntilAsync(() => File.Exists(path));
        Assert.NotNull(_fake.DeadlineAtHelperEnd);
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
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile));
        string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
        _games.Add(await FakeBridge.DialAsync(_harness.Listener.Port, token, projectDir, new FakeHello { WindowHandle = FakeRealtime.Hwnd }, Token));
        await attach;
    }
}
