using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>capture_input's argument checks and its refusals before anything reaches a game; no Godot runs here.</summary>
public sealed class CaptureValidationTests : IDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;

    public CaptureValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_sessions);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Start")]
    [InlineData("pause")]
    public async Task AModeOtherThanStartOrStopIsRefused(string mode)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.CaptureInputAsync(mode, cancellationToken: Token));

        Assert.Equal($"mode must be start or stop; got '{mode}'", refused.Message);
    }

    [Fact]
    public async Task OptionsWithStopAreRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CaptureInputAsync("stop", new CaptureOptions(Motion: true), cancellationToken: Token)
        );

        Assert.Equal("options apply to mode start only", refused.Message);
    }

    [Theory]
    [InlineData("mouse")]
    [InlineData("Real")]
    [InlineData("")]
    public async Task ASourceOtherThanRealOrSentIsRefused(string source)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CaptureInputAsync("start", new CaptureOptions(Sources: ["sent", source]), cancellationToken: Token)
        );

        Assert.Equal("options.sources must name real, sent or both", refused.Message);
    }

    [Fact]
    public async Task EmptySourcesAreRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CaptureInputAsync("start", new CaptureOptions(Sources: []), cancellationToken: Token)
        );

        Assert.Equal("options.sources is empty", refused.Message);
    }

    [Fact]
    public async Task AStartWhileACaptureRunsIsRefused()
    {
        _sessions.Captures.Begin("game");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CaptureInputAsync("start", session: "game", cancellationToken: Token)
        );

        Assert.Equal("a capture is already running in session 'game'; stop it first", refused.Message);
    }

    [Fact]
    public async Task AStopWithNoCaptureIsRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CaptureInputAsync("stop", session: "game", cancellationToken: Token)
        );

        Assert.Equal("no capture in session 'game': start one with capture_input mode start", refused.Message);
    }

    [Fact]
    public async Task AStopReturnsAnEndedCaptureWhoseSessionIsGone()
    {
        _sessions.Captures.Begin("game");
        _sessions.Captures.Receive(
            "game",
            new JsonObject { ["type"] = "captured", ["events"] = new JsonArray(new JsonObject { ["type"] = "key", ["key"] = "A" }) }
        );
        _sessions.Captures.End("game", CaptureStore.EndedByExit);

        string stopped = await _tools.CaptureInputAsync("stop", session: "game", cancellationToken: Token);

        Assert.Equal("""{"events":[{"type":"key","key":"A"}],"count":1,"truncated":false,"ended":"exit"}""", stopped);
    }

    [Fact]
    public void TheDefaultsAreBothSourcesWithoutPlainMotion()
    {
        RuntimeTools.CaptureSettings settings = RuntimeTools.CheckCaptureArguments("start", null)!;

        Assert.Equal(["real", "sent"], settings.Sources);
        Assert.False(settings.Motion);
        Assert.Null(RuntimeTools.CheckCaptureArguments("stop", null));
    }

    [Fact]
    public void SourcesAreKeptInOrderOnce()
    {
        RuntimeTools.CaptureSettings settings = RuntimeTools.CheckCaptureArguments("start", new CaptureOptions(["sent", "real", "sent"], true))!;

        Assert.Equal(["real", "sent"], settings.Sources);
        Assert.True(settings.Motion);
    }

    [Fact]
    public async Task WithGoodArgumentsTheSessionIsLookedUp()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.CaptureInputAsync("start", cancellationToken: Token));

        Assert.Contains("No Godot session", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItRunsAsABatchStep() => Assert.Contains("capture_input", RuntimeTools.BatchableTools);
}
