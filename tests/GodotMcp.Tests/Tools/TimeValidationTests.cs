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
        "condition needs exactly one of: {node, exists}, {node, property, equals}, {node, signal}, {expression}, {uiChanged: true | \"now\"}, "
        + "{gameMs}, {frames}, {text}, {node, stable}, {node, state, equals}, {output} (with stream and after optional).";
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

    [Theory]
    [InlineData("pause", null)]
    [InlineData("resume", null)]
    [InlineData("time_scale", 1.0)]
    public void UntilIsRefusedOnAnyActionButStep(string action, double? scale)
    {
        StepOptions options = new(Until: new WaitCondition(Expression: "true"));

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters(action, null, scale, options));

        Assert.Equal("options.until applies to step only.", refused.Message);
    }

    [Fact]
    public void UntilRefusesAFramesCondition()
    {
        StepOptions options = new(Until: new WaitCondition(Frames: 5));

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", 10, null, options));

        Assert.Equal("frame_control step's until takes any wait_for condition but frames: count is the most frames a step runs.", refused.Message);
    }

    [Fact]
    public void UntilRefusesAnOutputCondition()
    {
        StepOptions options = new(Until: new WaitCondition(Output: "joined"));

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", null, null, options));

        Assert.Equal(
            "frame_control step's until takes any wait_for condition but output: a step waits on the game's frames, not its printed lines.",
            refused.Message
        );
    }

    [Fact]
    public void UntilRefusesAFramesConditionThatAlsoNamesANode()
    {
        StepOptions options = new(Until: new WaitCondition(Node: "Main", Frames: 5));

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", null, null, options));

        Assert.Equal("frame_control step's until takes any wait_for condition but frames: count is the most frames a step runs.", refused.Message);
    }

    [Theory]
    [InlineData("two kinds", ConditionMessage)]
    [InlineData("node alone", ConditionMessage)]
    [InlineData("gameMs 0", "gameMs must be between 1 and 120000; got 0.")]
    public void UntilRefusesAConditionWaitForRefuses(string refusedCase, string message)
    {
        WaitCondition until = refusedCase switch
        {
            "two kinds" => new WaitCondition(Node: "Main", Exists: true, Expression: "true"),
            "node alone" => new WaitCondition(Node: "Main"),
            _ => new WaitCondition(GameMs: 0),
        };

        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildFrameParameters("step", null, null, new StepOptions(Until: until))
        );

        Assert.Equal(message, refused.Message);
    }

    [Fact]
    public void AStepUntilDefaultsToAThousandFramesAndCarriesItsCondition()
    {
        StepOptions options = new(Until: new WaitCondition(Node: "TimeProbe", Expression: "node.process_frames > 3"));

        JsonObject parameters = RuntimeTools.BuildFrameParameters("step", null, null, options);

        Assert.Equal(1000, parameters["count"]!.GetValue<int>());
        Assert.Equal((long)RuntimeTools.StepAllowance(1000).TotalMilliseconds, parameters["deadlineMs"]!.GetValue<long>());
        JsonObject until = parameters["until"]!.AsObject();
        Assert.Equal("expression", until["kind"]!.GetValue<string>());
        Assert.Equal("TimeProbe", until["node"]!.GetValue<string>());
        Assert.Equal("node.process_frames > 3", until["expression"]!.GetValue<string>());
    }

    [Fact]
    public void AStepWithoutUntilStaysAtOneFrame()
    {
        JsonObject parameters = RuntimeTools.BuildFrameParameters("step", null, null, null);

        Assert.Equal(1, parameters["count"]!.GetValue<int>());
        Assert.False(parameters.ContainsKey("until"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void AStepUntilsCountStaysWithinOneToAThousand(int count)
    {
        StepOptions options = new(Until: new WaitCondition(UiChanged: Json("true")));

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", count, null, options));

        Assert.Equal("count must be between 1 and 1000.", refused.Message);
    }

    [Fact]
    public void AStepUntilTakesAnExplicitCount()
    {
        StepOptions options = new(Until: new WaitCondition(Node: "TimeProbe", Signal: "fired"));

        JsonObject parameters = RuntimeTools.BuildFrameParameters("step", 40, null, options);

        Assert.Equal(40, parameters["count"]!.GetValue<int>());
        Assert.Equal("signal", parameters["until"]!["kind"]!.GetValue<string>());
    }

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
    [InlineData("Main", null, null, null, null, null, "true")]
    [InlineData("Main", null, null, null, null, null, "\"now\"")]
    [InlineData("", null, null, null, null, null, "true")]
    [InlineData(null, true, null, null, null, null, "true")]
    [InlineData(null, true, null, null, null, null, "\"now\"")]
    [InlineData(null, null, null, null, null, "true", "true")]
    [InlineData("Main", null, null, null, "fired", null, "true")]
    public async Task WaitForRefusesAConditionThatIsNotExactlyOneKind(
        string? node,
        bool? exists,
        string? property,
        string? equalsJson,
        string? signal,
        string? expression,
        string? uiChangedJson
    )
    {
        JsonElement? equals = equalsJson is null ? null : JsonSerializer.Deserialize<JsonElement>(equalsJson);
        JsonElement? uiChanged = uiChangedJson is null ? null : Json(uiChangedJson);
        WaitCondition condition = new(node, exists, property, equals, signal, expression, uiChanged);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(condition, 1000, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ConditionMessage, refused.Message);
    }

    [Fact]
    public async Task ATextConditionWithANodeIsRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(Node: "Main", Text: "Start"), 1000, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal("a text condition takes no node; scope it with under.", refused.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    public void ABlankTextIsRefused(string text)
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(new WaitCondition(Text: text), 1000));

        Assert.Equal("condition.text is blank; give the text a visible Control shows.", refused.Message);
    }

    [Fact]
    public void UnderWithoutTextIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(new WaitCondition(Under: "Lobby"), 1000));

        Assert.Equal("condition.under scopes a text condition; give text with it.", refused.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    public void ABlankUnderIsRefused(string under)
    {
        WaitCondition condition = new(Text: "Start", Under: under);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(condition, 1000));

        Assert.Equal("condition.under is blank; name the node to scope the text to, or leave under out.", refused.Message);
    }

    [Fact]
    public void ATextConditionIsOfTheTextKindAndCarriesItsText()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Text: "Start"), 1000);

        Assert.Equal("text", parameters["kind"]!.GetValue<string>());
        Assert.Equal("Start", parameters["text"]!.GetValue<string>());
    }

    [Fact]
    public void ATextLeaveIsOfTheTextKindWithExistsFalse()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Exists: false, Text: "Start", Under: "Lobby"), 1000);

        Assert.Equal("text", parameters["kind"]!.GetValue<string>());
        Assert.False(parameters["exists"]!.GetValue<bool>());
        Assert.Equal("Lobby", parameters["under"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("contains")]
    public void ATextConditionCarriesItsMatchMode(string match)
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Text: "Deep Mine", Match: match), 1000);

        Assert.Equal("text", parameters["kind"]!.GetValue<string>());
        Assert.Equal(match, parameters["match"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("exact")]
    public void AnExactTextConditionCarriesNoMatch(string? match)
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Text: "Deep Mine", Match: match), 1000);

        Assert.False(parameters.ContainsKey("match"));
    }

    [Theory]
    [InlineData("Contains")]
    [InlineData("suffix")]
    public void AnUnknownMatchModeIsRefused(string match)
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Text: "Deep Mine", Match: match), 1000)
        );

        Assert.Equal($"match is \"{match}\"; it must be \"exact\", \"prefix\" or \"contains\".", refused.Message);
    }

    [Fact]
    public void AMatchWithoutTextIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Node: "Lobby", Match: "prefix"), 1000)
        );

        Assert.Equal("match applies only to a text condition or target; this one has no text.", refused.Message);
    }

    [Fact]
    public void AStepUntilTakesATextConditionWithAMatchMode()
    {
        StepOptions options = new(Until: new WaitCondition(Text: "Deep Mine", Match: "prefix"));

        JsonObject parameters = RuntimeTools.BuildFrameParameters("step", null, null, options);

        Assert.Equal("prefix", parameters["until"]!["match"]!.GetValue<string>());
    }

    [Fact]
    public void ACheckOnceTextWaitIsAccepted()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Text: "Start"), 0);

        Assert.Equal(0, parameters["timeoutMs"]!.GetValue<int>());
    }

    [Fact]
    public void AStepUntilTakesATextCondition()
    {
        StepOptions options = new(Until: new WaitCondition(Text: "Start"));

        JsonObject parameters = RuntimeTools.BuildFrameParameters("step", null, null, options);

        Assert.Equal("text", parameters["until"]!["kind"]!.GetValue<string>());
        Assert.Equal("Start", parameters["until"]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("position")]
    public void ANodeWithStableIsOfTheSettledKindWithOrWithoutAProperty(string? property)
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Node: "Lobby", Property: property, Stable: 10), 1000);

        Assert.Equal("settled", parameters["kind"]!.GetValue<string>());
        Assert.Equal(10, parameters["stable"]!.GetValue<int>());
        Assert.Equal(property, parameters["property"]?.GetValue<string>());
    }

    [Fact]
    public void StableWithoutANodeIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(new WaitCondition(Stable: 10), 1000));

        Assert.Equal(ConditionMessage, refused.Message);
    }

    [Fact]
    public void ASettledConditionWithEqualsIsRefused()
    {
        WaitCondition condition = new(Node: "Lobby", Property: "position", EqualsValue: JsonSerializer.Deserialize<JsonElement>("3"), Stable: 10);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(condition, 1000));

        Assert.Equal("a settled condition takes no equals; it waits for the value to hold, whatever it is.", refused.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void AStableOutsideItsRangeIsRefused(int stable)
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Node: "Lobby", Stable: stable), 1000)
        );

        Assert.Equal($"condition.stable must be 1 to 600 frames; got {stable}.", refused.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(600)]
    public void AStableAtTheEdgeOfItsRangeIsAccepted(int stable)
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Node: "Lobby", Stable: stable), 1000);

        Assert.Equal("settled", parameters["kind"]!.GetValue<string>());
        Assert.Equal(stable, parameters["stable"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(1, 10_200)]
    [InlineData(600, 70_100)]
    public void ASettledWaitWithoutATimeoutGetsTheStepAllowanceOfItsReadings(int stable, int expected)
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Node: "Lobby", Stable: stable), null);

        Assert.Equal(expected, parameters["timeoutMs"]!.GetValue<int>());
    }

    [Fact]
    public void ACheckOnceSettledWaitIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(new WaitCondition(Node: "Lobby", Stable: 10), 0));

        Assert.Equal("timeoutMs 0 checks once, which a settled wait cannot meet; give it a timeout.", refused.Message);
    }

    [Fact]
    public void ASettledWaitTakesACall()
    {
        WaitOptions options = new(Call: new MethodCall("Lobby", "arm"));

        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Node: "Lobby", Stable: 10), 1000, options);

        Assert.Equal("settled", parameters["kind"]!.GetValue<string>());
        Assert.Equal("arm", parameters["call"]!["method"]!.GetValue<string>());
    }

    [Fact]
    public void AStepUntilTakesASettledCondition()
    {
        StepOptions options = new(Until: new WaitCondition(Node: "Lobby", Stable: 10));

        JsonObject parameters = RuntimeTools.BuildFrameParameters("step", null, null, options);

        Assert.Equal("settled", parameters["until"]!["kind"]!.GetValue<string>());
        Assert.Equal(10, parameters["until"]!["stable"]!.GetValue<int>());
    }

    [Fact]
    public void ANodeWithStateAndEqualsIsOfTheStateKindAndNotTheProperty()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(
            new WaitCondition(Node: "Board", EqualsValue: Json("7"), State: "seats[0].hp"),
            1000
        );

        Assert.Equal("state", parameters["kind"]!.GetValue<string>());
        Assert.Equal("seats[0].hp", parameters["state"]!.GetValue<string>());
        Assert.Equal(7, parameters["equals"]!.GetValue<int>());
        Assert.False(parameters.ContainsKey("property"), parameters.ToJsonString());
    }

    [Fact]
    public void APropertyConditionWithEqualsIsStillOfThePropertyKind()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(
            new WaitCondition(Node: "Main", Property: "state", EqualsValue: Json("\"done\"")),
            1000
        );

        Assert.Equal("property", parameters["kind"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ABlankStateIsRefused(string state)
    {
        WaitCondition condition = new(Node: "Board", EqualsValue: Json("7"), State: state);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(condition, 1000));

        Assert.Equal(
            "condition.state is blank; give a key path into the node's state, such as seats[0].hp, or $ for the whole state.",
            refused.Message
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void AStateWithoutEqualsIsRefused(string? equals)
    {
        WaitCondition condition = new(Node: "Board", EqualsValue: equals is null ? null : Json(equals), State: "turn");

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(condition, 1000));

        Assert.Equal("a state condition needs equals, the JSON value the key must equal; null is not supported.", refused.Message);
    }

    [Fact]
    public void AStateWithoutANodeIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(EqualsValue: Json("2"), State: "turn"), 1000)
        );

        Assert.Equal(ConditionMessage, refused.Message);
    }

    [Theory]
    [InlineData("property")]
    [InlineData("stable")]
    public void AStateWithAPropertyOrStableIsRefused(string companion)
    {
        WaitCondition condition =
            companion == "property"
                ? new(Node: "Board", Property: "turn", EqualsValue: Json("2"), State: "turn")
                : new(Node: "Board", EqualsValue: Json("2"), Stable: 10, State: "turn");

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(condition, 1000));

        Assert.Equal("a state condition takes no property or stable; state names the key path to read.", refused.Message);
    }

    [Fact]
    public void ACheckOnceStateWaitIsAccepted()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Node: "Board", EqualsValue: Json("2"), State: "turn"), 0);

        Assert.Equal("state", parameters["kind"]!.GetValue<string>());
        Assert.Equal(0, parameters["timeoutMs"]!.GetValue<int>());
    }

    [Fact]
    public void AStateWaitWithoutATimeoutGetsTheDefault()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Node: "Board", EqualsValue: Json("2"), State: "turn"), null);

        Assert.Equal(10_000, parameters["timeoutMs"]!.GetValue<int>());
    }

    [Fact]
    public void AStateWaitTakesACallAndEdge()
    {
        WaitOptions options = new(Call: new MethodCall("Board", "next_turn"), Edge: true);

        JsonObject parameters = RuntimeTools.BuildWaitParameters(
            new WaitCondition(Node: "Board", EqualsValue: Json("2"), State: "turn"),
            1000,
            options
        );

        Assert.Equal("next_turn", parameters["call"]!["method"]!.GetValue<string>());
        Assert.True(parameters["edge"]!.GetValue<bool>(), parameters.ToJsonString());
    }

    [Fact]
    public void AStepUntilTakesAStateCondition()
    {
        StepOptions options = new(Until: new WaitCondition(Node: "Board", EqualsValue: Json("2"), State: "turn"));

        JsonObject parameters = RuntimeTools.BuildFrameParameters("step", null, null, options);

        Assert.Equal("state", parameters["until"]!["kind"]!.GetValue<string>());
        Assert.Equal("turn", parameters["until"]!["state"]!.GetValue<string>());
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
    [InlineData(0, "true")]
    [InlineData(1000, "true")]
    [InlineData(1000, "\"now\"")]
    public async Task AUiChangedWaitIsAccepted(int timeoutMs, string uiChangedJson)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(UiChanged: Json(uiChangedJson)), timeoutMs, null, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckOnceUiChangedNowWaitIsRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(UiChanged: Json("\"now\"")), 0, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal("timeoutMs 0 checks once, which a {uiChanged: \"now\"} wait cannot meet; give it a timeout.", refused.Message);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("\"later\"")]
    [InlineData("3")]
    public async Task AUiChangedOfAnotherValueIsRefused(string uiChangedJson)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(UiChanged: Json(uiChangedJson)), 1000, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal($"uiChanged takes true or \"now\"; got {uiChangedJson}.", refused.Message);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"now\"")]
    public void AUiChangedValueTravelsToTheBridgeAsGiven(string uiChangedJson)
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(UiChanged: Json(uiChangedJson)), 1000);

        Assert.Equal("uiChanged", parameters["kind"]!.GetValue<string>());
        Assert.Equal(uiChangedJson, parameters["uiChanged"]!.ToJsonString());
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

    [Fact]
    public async Task ACallOnAUiChangedWaitIsRefused()
    {
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "start_clock"));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(UiChanged: Json("true")), 1000, options, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            "options.call is taken only by a gameMs, frames, exists, property, expression, text, settled, state or signal wait; "
                + "this condition is uiChanged.",
            refused.Message
        );
    }

    [Fact]
    public void ACallOnASignalWaitIsAcceptedAndSent()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(
            new WaitCondition(Node: "Main", Signal: "fired"),
            1000,
            new WaitOptions(Call: new MethodCall("TimeProbe", "start_clock"))
        );

        Assert.Equal("signal", parameters["kind"]!.GetValue<string>());
        Assert.Equal("{\"node\":\"TimeProbe\",\"method\":\"start_clock\",\"args\":[]}", parameters["call"]!.ToJsonString());
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
    public void AThenWithoutCallTimeScaleOrPauseIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 1000, new WaitOptions(Then: new WaitThen()))
        );

        Assert.Equal("options.then needs call, timeScale, pause or a mix.", refused.Message);
    }

    [Fact]
    public void AThenWithAPauseOfFalseAloneIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 1000, new WaitOptions(Then: new WaitThen(Pause: false)))
        );

        Assert.Equal("options.then needs call, timeScale, pause or a mix.", refused.Message);
    }

    [Fact]
    public void AThenWithOnlyAPauseIsAccepted()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(
            new WaitCondition(Expression: "true"),
            1000,
            new WaitOptions(Then: new WaitThen(Pause: true))
        );

        Assert.Equal("""{"pause":true}""", parameters["then"]!.ToJsonString());
    }

    [Fact]
    public void AThenWithACallAndAPauseIsAccepted()
    {
        WaitOptions options = new(Then: new WaitThen(Call: new MethodCall("TimeProbe", "record_then"), Pause: true));

        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Expression: "true"), 1000, options);

        Assert.Equal("record_then", parameters["then"]!["call"]!["method"]!.GetValue<string>());
        Assert.True(parameters["then"]!["pause"]!.GetValue<bool>(), parameters.ToJsonString());
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
            new WaitCondition(UiChanged: Json("true")),
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
    [InlineData("settled")]
    public void AnEdgeWaitOnAnotherKindIsRefused(string kind)
    {
        WaitCondition condition = kind switch
        {
            "signal" => new WaitCondition(Node: "Main", Signal: "fired"),
            "uiChanged" => new WaitCondition(UiChanged: Json("true")),
            "gameMs" => new WaitCondition(GameMs: 500),
            "settled" => new WaitCondition(Node: "Lobby", Stable: 10),
            _ => new WaitCondition(Frames: 3),
        };

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(condition, 1000, new WaitOptions(Edge: true)));

        Assert.Equal($"options.edge applies to exists, property, expression, text and state waits; this condition is {kind}.", refused.Message);
    }

    [Fact]
    public void AnEdgeTextWaitIsAcceptedAndSent()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Text: "Start"), 1000, new WaitOptions(Edge: true));

        Assert.Equal("text", parameters["kind"]!.GetValue<string>());
        Assert.True(parameters["edge"]!.GetValue<bool>(), parameters.ToJsonString());
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
