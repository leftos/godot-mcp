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

    // The refusals of an options.call of neither form, both, or args of the other form's shape, after the option's path.
    internal const string BothSuffix = " takes either {node, method, args: [...]} for a method or {tool, args: {...}} for a game tool, not both.";
    internal const string NeitherSuffix = " needs {node, method} for a method or {tool} for a game tool.";
    internal const string ToolArgsSuffix =
        ".args for a game tool is an object of named arguments, as call_game_tool takes them: "
        + "{tool: \"<name>\", args: {\"<parameter>\": value}}.";
    internal const string MethodArgsSuffix =
        ".args for a method is an array of positional arguments; for named arguments, call a game tool with {tool, args}.";
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
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "start_clock", Json("[300]")));

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
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "start_clock", Json("[300]")));

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
    [InlineData("signal")]
    [InlineData("uiChanged")]
    public async Task ACallOnASignalOrUiChangedWaitIsRefused(string kind)
    {
        WaitCondition condition = kind == "signal" ? new(Node: "Main", Signal: "fired") : new(UiChanged: true);
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "start_clock"));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(condition, 1000, options, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            $"options.call is taken only by a gameMs, frames, exists, property or expression wait; this condition is {kind}.",
            refused.Message
        );
    }

    [Fact]
    public void ACallOnAnExistsPropertyOrExpressionWaitIsAcceptedAndSent()
    {
        WaitCondition[] conditions =
        [
            new WaitCondition(Expression: "true"),
            new WaitCondition(Node: "Main", Property: "state", EqualsValue: Json("\"done\"")),
            new WaitCondition(Node: "Main", Exists: true),
        ];
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "record_then", Json("[7]")));

        foreach (WaitCondition condition in conditions)
        {
            JsonObject parameters = RuntimeTools.BuildWaitParameters(condition, 1000, options);

            Assert.Equal("""{"node":"TimeProbe","method":"record_then","args":[7]}""", parameters["call"]!.ToJsonString());
        }
    }

    [Fact]
    public void AnEdgeWaitWithACallSendsBoth()
    {
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "record_then"), Edge: true);

        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 1000, options);

        Assert.Equal("record_then", parameters["call"]!["method"]!.GetValue<string>());
        Assert.True(parameters["edge"]!.GetValue<bool>(), parameters.ToJsonString());
    }

    [Fact]
    public async Task ACheckOnceExpressionWaitWithACallIsAccepted()
    {
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "record_then"));

        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 0, options);
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(Expression: "true"), 0, options, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal("""{"node":"TimeProbe","method":"record_then","args":[]}""", parameters["call"]!.ToJsonString());
        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
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

    [Fact]
    public void AThenWithoutCallOrTimeScaleIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 1000, new WaitOptions(Then: new WaitThen()))
        );

        Assert.Equal("options.then needs call, timeScale or both.", refused.Message);
    }

    [Theory]
    [InlineData(0.0, "options.then.timeScale must be greater than 0 and at most 100; got 0.")]
    [InlineData(-1.0, "options.then.timeScale must be greater than 0 and at most 100; got -1.")]
    [InlineData(100.5, "options.then.timeScale must be greater than 0 and at most 100; got 100.5.")]
    public void AThenTimeScaleOutOfRangeIsRefused(double timeScale, string message)
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 1000, new WaitOptions(Then: new WaitThen(TimeScale: timeScale)))
        );

        Assert.Equal(message, refused.Message);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(100.0)]
    public void AThenTimeScaleWithinRangeIsAccepted(double timeScale)
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(
            new WaitCondition(Expression: "true"),
            1000,
            new WaitOptions(Then: new WaitThen(TimeScale: timeScale))
        );

        Assert.Equal(timeScale, parameters["then"]!["timeScale"]!.GetValue<double>());
    }

    [Fact]
    public void AThenIsAcceptedBesideEveryConditionKind()
    {
        WaitCondition[] conditions =
        [
            new WaitCondition(Expression: "true"),
            new WaitCondition(Node: "Main", Property: "state", EqualsValue: Json("\"done\"")),
            new WaitCondition(Node: "Main", Exists: true),
            new WaitCondition(Node: "Main", Signal: "fired"),
            new WaitCondition(GameMs: 500),
            new WaitCondition(Frames: 3),
            new WaitCondition(UiChanged: true),
        ];

        foreach (WaitCondition condition in conditions)
        {
            JsonObject parameters = RuntimeTools.BuildWaitParameters(condition, 1000, new WaitOptions(Then: new WaitThen(TimeScale: 2)));

            Assert.Equal(2, parameters["then"]!["timeScale"]!.GetValue<double>());
        }
    }

    [Fact]
    public void AnEdgeWaitOnExistsPropertyOrExpressionIsAcceptedAndSent()
    {
        WaitCondition[] conditions =
        [
            new WaitCondition(Expression: "true"),
            new WaitCondition(Node: "Main", Property: "state", EqualsValue: Json("\"done\"")),
            new WaitCondition(Node: "Main", Exists: true),
        ];

        foreach (WaitCondition condition in conditions)
        {
            JsonObject parameters = RuntimeTools.BuildWaitParameters(condition, 1000, new WaitOptions(Edge: true));

            Assert.True(parameters["edge"]!.GetValue<bool>(), parameters.ToJsonString());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void AWaitWithoutEdgeSendsNone(bool? edge)
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 1000, new WaitOptions(Edge: edge));

        Assert.False(parameters.ContainsKey("edge"), parameters.ToJsonString());
    }

    [Theory]
    [InlineData("signal")]
    [InlineData("uiChanged")]
    [InlineData("gameMs")]
    [InlineData("frames")]
    public void AnEdgeWaitOnAnotherKindIsRefused(string kind)
    {
        WaitCondition condition = kind switch
        {
            "signal" => new WaitCondition(Node: "Main", Signal: "fired"),
            "uiChanged" => new WaitCondition(UiChanged: true),
            "gameMs" => new WaitCondition(GameMs: 500),
            _ => new WaitCondition(Frames: 3),
        };

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(condition, 1000, new WaitOptions(Edge: true)));

        Assert.Equal($"options.edge applies to exists, property and expression waits; this condition is {kind}.", refused.Message);
    }

    [Fact]
    public void ACheckOnceEdgeWaitIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 0, new WaitOptions(Edge: true))
        );

        Assert.Equal("timeoutMs 0 checks once, which an edge wait cannot meet; give it a timeout.", refused.Message);
    }

    [Fact]
    public async Task ACheckOnceWaitWithThenIsAccepted()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(
                new WaitCondition(Expression: "true"),
                0,
                new WaitOptions(Then: new WaitThen(TimeScale: 0.5)),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AThenSendsItsCallAndScaleToTheBridge()
    {
        WaitOptions options = new(Then: new WaitThen(Call: new MethodCall("TimeProbe", "record_then", Json("[7]")), TimeScale: 0.5));

        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 1000, options);

        Assert.Equal("""{"call":{"node":"TimeProbe","method":"record_then","args":[7]},"timeScale":0.5}""", parameters["then"]!.ToJsonString());
    }

    [Fact]
    public void AThenBesideACallOnAFramesWaitSendsBoth()
    {
        WaitOptions options = new(
            Call: new MethodCall("TimeProbe", "start_clock", Json("[300]")),
            Then: new WaitThen(Call: new MethodCall("TimeProbe", "record_then"), TimeScale: 0.5)
        );

        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Frames: 3), null, options);

        Assert.Equal("start_clock", parameters["call"]!["method"]!.GetValue<string>());
        Assert.Equal("[300]", parameters["call"]!["args"]!.ToJsonString());
        Assert.Equal("record_then", parameters["then"]!["call"]!["method"]!.GetValue<string>());
        Assert.Equal(0.5, parameters["then"]!["timeScale"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("TimeProbe", "")]
    [InlineData("", "record_then")]
    public async Task AThenCallWithAnEmptyNameIsRefusedAsCallMethodRefusesIt(string node, string method)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WaitOptions options = new(Then: new WaitThen(Call: new MethodCall(node, method)));

        McpException expected = await Assert.ThrowsAsync<McpException>(() => _tools.CallMethodAsync(node, method, cancellationToken: cancellation));
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(Expression: "true"), 1000, options, cancellationToken: cancellation)
        );

        Assert.Equal(expected.Message, refused.Message);
        Assert.Contains(" is empty. ", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("options.call")]
    [InlineData("options.then.call")]
    public void AGameToolCallSendsTheToolAndCallGameToolsRequest(string field)
    {
        WaitOptions options = WithCall(field, new MethodCall(Tool: "SetMood", Args: Json("""{"mood": "Angry", "times": 3}""")));

        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(GameMs: 500), null, options);

        Assert.Equal(
            """{"tool":"SetMood","request":{"op":"tool_call","name":"SetMood","args":{"mood":"Angry","times":3},"maxDepth":8}}""",
            SentCall(field, parameters).ToJsonString()
        );
    }

    [Theory]
    [InlineData("options.call")]
    [InlineData("options.then.call")]
    public void AGameToolCallWithoutArgsSendsAnEmptyObject(string field)
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(GameMs: 500), null, WithCall(field, new MethodCall(Tool: "Heal")));

        Assert.Equal("{}", SentCall(field, parameters)["request"]!["args"]!.ToJsonString());
    }

    [Theory]
    [InlineData("options.call")]
    [InlineData("options.then.call")]
    public async Task AGameToolCallIsAcceptedAndAsksForASession(string field)
    {
        WaitOptions options = WithCall(field, new MethodCall(Tool: "SetMood", Args: Json("""{"mood": "Angry"}""")));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(GameMs: 500), null, options, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("options.call", "SetMood", "Main", null, null, BothSuffix)]
    [InlineData("options.then.call", "SetMood", null, "go", null, BothSuffix)]
    [InlineData("options.call", null, null, null, null, NeitherSuffix)]
    [InlineData("options.then.call", null, null, null, """{"mood": "Angry"}""", NeitherSuffix)]
    [InlineData("options.call", "SetMood", null, null, """["Angry"]""", ToolArgsSuffix)]
    [InlineData("options.then.call", "SetMood", null, null, "\"Angry\"", ToolArgsSuffix)]
    [InlineData("options.call", null, "TimeProbe", "start_clock", """{"ms": 300}""", MethodArgsSuffix)]
    [InlineData("options.then.call", null, "TimeProbe", "record_then", "7", MethodArgsSuffix)]
    public async Task ACallOfNeitherOrBothFormsOrTheOtherFormsArgsIsRefused(
        string field,
        string? tool,
        string? node,
        string? method,
        string? args,
        string suffix
    )
    {
        MethodCall call = new(node, method, args is null ? null : Json(args), tool);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(GameMs: 500), null, WithCall(field, call), cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal(field + suffix, refused.Message);
    }

    [Theory]
    [InlineData("options.call", "")]
    [InlineData("options.then.call", " ")]
    public async Task AnEmptyToolNameIsRefusedAsCallGameToolRefusesIt(string field, string name)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        McpException expected = await Assert.ThrowsAsync<McpException>(() => _tools.CallGameToolAsync(name, cancellationToken: cancellation));
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(GameMs: 500), null, WithCall(field, new MethodCall(Tool: name)), cancellationToken: cancellation)
        );

        Assert.Equal(expected.Message, refused.Message);
        Assert.StartsWith("name is empty. ", refused.Message, StringComparison.Ordinal);
    }

    private static WaitOptions WithCall(string field, MethodCall call) =>
        field == "options.call" ? new WaitOptions(Call: call) : new WaitOptions(Then: new WaitThen(Call: call));

    private static JsonObject SentCall(string field, JsonObject parameters) =>
        field == "options.call" ? parameters["call"]!.AsObject() : parameters["then"]!["call"]!.AsObject();

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
