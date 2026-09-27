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
        "condition needs exactly one of: {node, exists}, {node, property, equals}, {node, signal}, {expression}, {uiChanged: true}.";
    private const string ScaleMessage = "time_scale needs scale, greater than 0 and at most 100.";
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;

    public TimeValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_sessions);
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
}
