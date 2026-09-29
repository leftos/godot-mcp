using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>frame_control's and wait_for's argument checks, which refuse before anything reaches a game; no Godot runs here.</summary>
public sealed class TimeValidationTests : IDisposable
{
    private const string ConditionMessage =
        "condition needs exactly one of: {node, exists}, {node, property, equals}, {node, signal}, {expression}, {uiChanged: true}, "
        + "{gameMs}, {frames}.";
    private const string ScaleMessage = "time_scale needs scale, greater than 0 and at most 100.";
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;

    public TimeValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_sessions, TestCSharp.Unused());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
    }

    [Theory]
    [InlineData("jump", null, null, null, null, "action must be one of pause, resume, step, time_scale.")]
    [InlineData("Pause", null, null, null, null, "action must be one of pause, resume, step, time_scale.")]
    [InlineData("pause", 2, null, null, null, "count applies to step only.")]
    [InlineData("time_scale", 2, 1.0, null, null, "count applies to step only.")]
    [InlineData("time_scale", null, null, null, null, ScaleMessage)]
    [InlineData("time_scale", null, 0.0, null, null, ScaleMessage)]
    [InlineData("time_scale", null, -1.0, null, null, ScaleMessage)]
    [InlineData("time_scale", null, 100.5, null, null, ScaleMessage)]
    [InlineData("resume", null, 2.0, null, null, "scale applies to time_scale only.")]
    [InlineData("step", null, 2.0, null, null, "scale applies to time_scale only.")]
    [InlineData("step", null, null, "idle", null, "unit must be process or physics.")]
    [InlineData("step", null, null, "Physics", null, "unit must be process or physics.")]
    [InlineData("pause", null, null, "physics", null, "unit applies to step only.")]
    [InlineData("pause", null, null, null, true, "screenshot applies to step only.")]
    [InlineData("time_scale", null, 1.0, null, true, "screenshot applies to step only.")]
    [InlineData("step", 0, null, null, null, "count must be between 1 and 1000.")]
    [InlineData("step", 1001, null, null, null, "count must be between 1 and 1000.")]
    public async Task FrameControlRefusesBadArguments(string action, int? count, double? scale, string? unit, bool? screenshot, string message)
    {
        StepOptions? options = unit is null && screenshot is null ? null : new StepOptions(unit, screenshot);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.FrameControlAsync(action, count, scale, options, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(message, refused.Message);
    }

    [Theory]
    [InlineData(1500, 90)]
    [InlineData(1000, 60)]
    [InlineData(17, 2)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    public void ClipFramesRoundsClipTimeUpToMovieFrames(int milliseconds, int frames) => Assert.Equal(frames, RuntimeTools.ClipFrames(milliseconds));

    [Fact]
    public void ARecordedWaitCountsMovieFramesAndIsReleasedAtTheStepAllowance()
    {
        JsonObject parameters = [];

        TimeSpan release = RuntimeTools.WaitRelease(parameters, 1500, recording: true);

        Assert.Equal(90, parameters["timeoutFrames"]!.GetValue<int>());
        Assert.Equal(TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(100 * 90), release);
    }

    [Fact]
    public void AWaitOutsideARecordingOrCheckingOnceKeepsItsMilliseconds()
    {
        JsonObject parameters = [];

        Assert.Equal(TimeSpan.FromMilliseconds(1500), RuntimeTools.WaitRelease(parameters, 1500, recording: false));
        Assert.Equal(TimeSpan.Zero, RuntimeTools.WaitRelease(parameters, 0, recording: true));
        Assert.False(parameters.ContainsKey("timeoutFrames"));
    }

    [Fact]
    public void ARecordedInputCallGetsTheStepAllowanceOfItsClipFrames()
    {
        Assert.Equal(TimeSpan.FromSeconds(13), RuntimeTools.InputAllowance(TimeSpan.FromMilliseconds(500), recording: true));
        Assert.Equal(TimeSpan.FromMilliseconds(10_500), RuntimeTools.InputAllowance(TimeSpan.FromMilliseconds(500), recording: false));
    }

    [Theory]
    [InlineData(0, "process", "samples must be between 1 and 600.")]
    [InlineData(601, "process", "samples must be between 1 and 600.")]
    [InlineData(-1, "process", "samples must be between 1 and 600.")]
    [InlineData(60, "idle", "unit must be process or physics.")]
    [InlineData(60, "Physics", "unit must be process or physics.")]
    public async Task MonitorPropertyRefusesBadOptions(int samples, string unit, string message)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.MonitorPropertyAsync("TimeProbe", "process_frames", new MonitorOptions(samples, unit), null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(message, refused.Message);
    }

    [Theory]
    [InlineData("", "process_frames", "node is empty.")]
    [InlineData(" ", "process_frames", "node is empty.")]
    [InlineData("TimeProbe", "", "property is empty.")]
    public async Task MonitorPropertyRefusesAnEmptyNodeOrProperty(string node, string property, string start)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.MonitorPropertyAsync(node, property, null, null, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith(start, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "process")]
    [InlineData(600, "physics")]
    public async Task AMonitorWithinRangeIsAccepted(int samples, string unit)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.MonitorPropertyAsync(
                "TimeProbe",
                "position:x",
                new MonitorOptions(samples, unit, false),
                null,
                TestContext.Current.CancellationToken
            )
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorParametersCarryTheDefaultsAndTheDeadline()
    {
        JsonObject parameters = RuntimeTools.BuildMonitorParameters("TimeProbe", "position:x", null);

        Assert.Equal(
            """{"node":"TimeProbe","property":"position:x","samples":60,"unit":"process","changesOnly":true,"deadlineMs":16000}""",
            parameters.ToJsonString()
        );
    }

    [Theory]
    [InlineData(null, null, null, null, null, null, null)]
    [InlineData("Main", null, null, null, null, null, null)]
    [InlineData(null, true, null, null, null, null, null)]
    [InlineData("Main", null, "state", null, null, null, null)]
    [InlineData("Main", null, null, "\"done\"", null, null, null)]
    [InlineData(null, null, "state", "\"done\"", null, null, null)]
    [InlineData(null, null, null, null, "fired", null, null)]
    [InlineData("Main", true, null, null, "fired", null, null)]
    [InlineData("Main", null, "state", "\"done\"", null, "true", null)]
    [InlineData(null, null, null, null, "fired", "true", null)]
    [InlineData("", true, null, null, null, null, null)]
    [InlineData("Main", null, "state", "null", null, null, null)]
    [InlineData(null, null, null, null, null, null, false)]
    [InlineData("Main", null, null, null, null, null, true)]
    [InlineData("", null, null, null, null, null, true)]
    [InlineData(null, true, null, null, null, null, true)]
    [InlineData(null, null, null, null, null, "true", true)]
    [InlineData("Main", null, null, null, "fired", null, true)]
    public async Task WaitForRefusesAConditionThatIsNotExactlyOneKind(
        string? node,
        bool? exists,
        string? property,
        string? equalsJson,
        string? signal,
        string? expression,
        bool? uiChanged
    )
    {
        JsonElement? equals = equalsJson is null ? null : JsonSerializer.Deserialize<JsonElement>(equalsJson);
        WaitCondition condition = new(node, exists, property, equals, signal, expression, uiChanged);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(condition, 1000, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ConditionMessage, refused.Message);
    }

    [Fact]
    public async Task WaitForRefusesAMissingCondition()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(null!, 1000, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ConditionMessage, refused.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(120_001)]
    public async Task WaitForRefusesATimeoutOutOfRange(int timeoutMs)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(Expression: "true"), timeoutMs, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal("timeoutMs must be between 0 and 120000.", refused.Message);
    }

    [Fact]
    public async Task ACheckOnceWaitIsAccepted()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(Expression: "true"), 0, null, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task AUiChangedWaitIsAccepted(int timeoutMs)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(UiChanged: true), timeoutMs, null, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckOnceSignalWaitIsRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(Node: "Main", Signal: "fired"), 0, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal("timeoutMs 0 checks once, which a signal wait cannot do; give it a timeout.", refused.Message);
    }

    [Theory]
    [InlineData(0, null, "gameMs must be between 1 and 120000; got 0.")]
    [InlineData(120_001, null, "gameMs must be between 1 and 120000; got 120001.")]
    [InlineData(null, 0, "frames must be between 1 and 7200; got 0.")]
    [InlineData(null, 7201, "frames must be between 1 and 7200; got 7201.")]
    [InlineData(null, -3, "frames must be between 1 and 7200; got -3.")]
    public async Task WaitForRefusesGameTimeOutOfRange(int? gameMs, int? frames, string message)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(GameMs: gameMs, Frames: frames), null, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(message, refused.Message);
    }

    [Theory]
    [InlineData("Main", null, 500, null)]
    [InlineData("Main", null, null, 3)]
    [InlineData(null, "true", 500, null)]
    [InlineData(null, null, 500, 3)]
    public async Task AGameTimeWaitWithAnotherFieldIsRefused(string? node, string? expression, int? gameMs, int? frames)
    {
        WaitCondition condition = new(Node: node, Expression: expression, GameMs: gameMs, Frames: frames);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(condition, null, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ConditionMessage, refused.Message);
    }

    [Theory]
    [InlineData(500, null, "gameMs")]
    [InlineData(null, 3, "frames")]
    public async Task ACheckOnceGameTimeWaitIsRefused(int? gameMs, int? frames, string kind)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(GameMs: gameMs, Frames: frames), 0, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal($"timeoutMs 0 checks once, which a {kind} wait cannot do; give it a timeout.", refused.Message);
    }

    [Theory]
    [InlineData(500, null)]
    [InlineData(null, 3)]
    public async Task AGameTimeWaitIsAccepted(int? gameMs, int? frames)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(GameMs: gameMs, Frames: frames), null, null, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(15_000, null, 25_000)]
    [InlineData(120_000, null, 130_000)]
    [InlineData(null, 3, 10_300)]
    [InlineData(null, 7200, 600_000)]
    [InlineData(null, null, 10_000)]
    public void AWaitWithoutATimeoutSendsItsKindsDefault(int? gameMs, int? frames, int expected)
    {
        WaitCondition condition = gameMs is null && frames is null ? new(Expression: "true") : new(GameMs: gameMs, Frames: frames);

        JsonObject parameters = RuntimeTools.BuildWaitParameters(condition, null);

        Assert.Equal(expected, parameters["timeoutMs"]!.GetValue<int>());
    }

    [Fact]
    public void AnExplicitTimeoutWinsOverAGameTimeWaitsDefault()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(GameMs: 15_000), 2000);

        Assert.Equal(2000, parameters["timeoutMs"]!.GetValue<int>());
        Assert.Equal("gameMs", parameters["kind"]!.GetValue<string>());
        Assert.Equal(15_000, parameters["gameMs"]!.GetValue<int>());
    }

    [Fact]
    public async Task AValidFrameControlCallWithoutASessionSaysNoneIsRunning()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.FrameControlAsync("step", 1000, null, new StepOptions("physics", true), null, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidWaitForCallWithoutASessionSaysNoneIsRunning()
    {
        WaitCondition condition = new(Node: "Main", Property: "state", EqualsValue: JsonSerializer.Deserialize<JsonElement>("\"done\""));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(condition, 120_000, null, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScreenshotWaitAsksTheBridgeForTheCaptureAndItsPreview()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 1000, new WaitOptions(Screenshot: true));

        Assert.True(parameters["screenshot"]!.GetValue<bool>());
        Assert.Equal(480, parameters["previewMaxWidth"]!.GetValue<int>());
        Assert.Equal("expression", parameters["kind"]!.GetValue<string>());
        Assert.Equal(1000, parameters["timeoutMs"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void AWaitWithoutScreenshotAsksForNoCapture(bool? screenshot)
    {
        WaitOptions? options = screenshot is null ? null : new WaitOptions(screenshot);

        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 1000, options);

        Assert.False(parameters.ContainsKey("screenshot"), parameters.ToJsonString());
        Assert.False(parameters.ContainsKey("previewMaxWidth"), parameters.ToJsonString());
    }

    [Theory]
    [InlineData(500, null)]
    [InlineData(null, 3)]
    public async Task AGameTimeWaitWithACallIsAccepted(int? gameMs, int? frames)
    {
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "start_clock", [Json("300")]));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(
                new WaitCondition(GameMs: gameMs, Frames: frames),
                null,
                options,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500, null)]
    [InlineData(null, 3)]
    public void AGameTimeWaitSendsItsCallToTheBridge(int? gameMs, int? frames)
    {
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "start_clock", [Json("300")]));

        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(GameMs: gameMs, Frames: frames), null, options);

        JsonObject call = parameters["call"]!.AsObject();
        Assert.Equal("TimeProbe", call["node"]!.GetValue<string>());
        Assert.Equal("start_clock", call["method"]!.GetValue<string>());
        Assert.Equal("[300]", call["args"]!.ToJsonString());
    }

    [Fact]
    public void ACallWithoutArgsSendsAnEmptyList()
    {
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "start_clock"));

        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(GameMs: 500), null, options);

        Assert.Equal("[]", parameters["call"]!["args"]!.ToJsonString());
    }

    [Fact]
    public void AWaitWithoutACallSendsNone() =>
        Assert.False(RuntimeTools.BuildWaitParameters(new WaitCondition(GameMs: 500), null, new WaitOptions()).ContainsKey("call"));

    [Theory]
    [InlineData(null, null, "true", "expression")]
    [InlineData("Main", true, null, "exists")]
    public async Task ACallOnAnotherWaitKindIsRefused(string? node, bool? exists, string? expression, string kind)
    {
        WaitCondition condition = new(Node: node, Exists: exists, Expression: expression);
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "start_clock"));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(condition, 1000, options, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal($"options.call is taken only by a gameMs or frames wait; this condition is {kind}.", refused.Message);
    }

    [Theory]
    [InlineData("TimeProbe", "")]
    [InlineData("", "start_clock")]
    public async Task ACallWithAnEmptyNameIsRefusedAsCallMethodRefusesIt(string node, string method)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WaitOptions options = new(Call: new MethodCall(node, method));

        McpException expected = await Assert.ThrowsAsync<McpException>(() => _tools.CallMethodAsync(node, method, cancellationToken: cancellation));
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(GameMs: 500), null, options, cancellationToken: cancellation)
        );

        Assert.Equal(expected.Message, refused.Message);
        Assert.Contains(" is empty. ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScreenshotWaitChecksItsConditionBeforeTheSession()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(null!, 1000, new WaitOptions(Screenshot: true), cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal(ConditionMessage, refused.Message);
    }

    [Fact]
    public async Task AValidScreenshotWaitWithoutASessionSaysNoneIsRunning()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(
                new WaitCondition(Expression: "true"),
                0,
                new WaitOptions(Screenshot: true),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
