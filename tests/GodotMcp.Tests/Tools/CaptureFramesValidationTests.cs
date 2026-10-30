using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>capture_frames' argument checks, which refuse before anything reaches a game, and its default allowance; no Godot runs here.</summary>
public sealed class CaptureFramesValidationTests : IDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;

    public CaptureFramesValidationTests()
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
    [InlineData(false, false, null, null)]
    [InlineData(false, true, null, null)]
    [InlineData(true, true, 0.1, 0.3)]
    [InlineData(true, true, 0.1, null)]
    [InlineData(false, true, 0.1, null)]
    [InlineData(false, true, null, 0.3)]
    public async Task NeitherFormOrBothIsRefused(bool atGiven, bool optionsGiven, double? every, double? span)
    {
        double[]? at = atGiven ? [0.5] : null;
        CaptureFramesOptions? options = optionsGiven ? new CaptureFramesOptions(every, span, TimeoutMs: 1000) : null;

        McpException refused = await RefusedAsync(at, options);

        Assert.Equal("pass at (a list of seconds) or options.every and options.for, not both.", refused.Message);
    }

    [Fact]
    public async Task AnEmptyAtIsRefused()
    {
        McpException refused = await RefusedAsync([], null);

        Assert.Equal("at takes 1 to 1000 points; got 0.", refused.Message);
    }

    [Fact]
    public async Task MoreThanAThousandPointsAreRefused()
    {
        double[] at = [.. Enumerable.Range(0, 1001).Select(index => index * 0.1)];

        McpException refused = await RefusedAsync(at, null);

        Assert.Equal("at takes 1 to 1000 points; got 1001.", refused.Message);
    }

    [Theory]
    [InlineData(-0.5, "at[1] must be a number of seconds from 0 to 120; got -0.5.")]
    [InlineData(double.NaN, "at[1] must be a number of seconds from 0 to 120; got NaN.")]
    [InlineData(double.PositiveInfinity, "at[1] must be a number of seconds from 0 to 120; got Infinity.")]
    [InlineData(120.5, "at[1] must be a number of seconds from 0 to 120; got 120.5.")]
    public async Task APointOutOfRangeIsRefused(double point, string message)
    {
        McpException refused = await RefusedAsync([0, point], null);

        Assert.Equal(message, refused.Message);
    }

    [Fact]
    public async Task DescendingPointsAreRefused()
    {
        McpException refused = await RefusedAsync([0.5, 1.2, 1.0], null);

        Assert.Equal("at must be ascending; at[2] is 1, less than at[1], 1.2.", refused.Message);
    }

    [Theory]
    [InlineData(0.0, "options.every must be a number of seconds greater than 0; got 0.")]
    [InlineData(-0.1, "options.every must be a number of seconds greater than 0; got -0.1.")]
    [InlineData(double.NaN, "options.every must be a number of seconds greater than 0; got NaN.")]
    public async Task AnEveryOfZeroOrLessIsRefused(double every, string message)
    {
        McpException refused = await RefusedAsync(null, new CaptureFramesOptions(every, 1));

        Assert.Equal(message, refused.Message);
    }

    [Theory]
    [InlineData(0.0, "options.for must be greater than 0 and at most 120 seconds; got 0.")]
    [InlineData(120.001, "options.for must be greater than 0 and at most 120 seconds; got 120.001.")]
    public async Task AForOutOfRangeIsRefused(double span, string message)
    {
        McpException refused = await RefusedAsync(null, new CaptureFramesOptions(1, span));

        Assert.Equal(message, refused.Message);
    }

    [Fact]
    public async Task EveryAndForMakingMoreThanAThousandPointsAreRefused()
    {
        McpException refused = await RefusedAsync(null, new CaptureFramesOptions(0.01, 120));

        Assert.Equal("options.every 0.01 over options.for 120 makes 12001 points; at most 1000 are allowed.", refused.Message);
    }

    [Fact]
    public void EveryAndForMakingExactlyAThousandPointsWithTheOneAtZeroAreAccepted() =>
        Assert.Equal(1000, RuntimeTools.CapturePoints(null, new CaptureFramesOptions(0.0625, 62.4375)).Length);

    [Fact]
    public async Task EveryAndForMakingAThousandAndOnePointsWithTheOneAtZeroAreRefused()
    {
        McpException refused = await RefusedAsync(null, new CaptureFramesOptions(0.0625, 62.5));

        Assert.Equal("options.every 0.0625 over options.for 62.5 makes 1001 points; at most 1000 are allowed.", refused.Message);
    }

    [Fact]
    public async Task AnEveryLongerThanForIsRefused()
    {
        McpException refused = await RefusedAsync(null, new CaptureFramesOptions(2, 1));

        Assert.Equal("options.every 2 is longer than options.for 1, so there is no point to capture.", refused.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(600_001)]
    public async Task ATimeoutOutOfRangeIsRefused(int timeoutMs)
    {
        McpException refused = await RefusedAsync([0.5], new CaptureFramesOptions(TimeoutMs: timeoutMs));

        Assert.Equal($"options.timeoutMs must be between 1 and 600000; got {timeoutMs}.", refused.Message);
    }

    [Fact]
    public async Task AnEmptyCropIsRefusedAsTakeScreenshotRefusesIt()
    {
        McpException refused = await RefusedAsync([0.5], new CaptureFramesOptions(Crop: new ScreenshotCrop(0, 0, 0, 10)));

        Assert.Equal("A crop needs a width and a height of at least 1 pixel; got 0x10.", refused.Message);
    }

    [Fact]
    public void EqualNeighboursAreAccepted() => Assert.Equal([0.1, 0.3, 0.3], RuntimeTools.CapturePoints([0.1, 0.3, 0.3], null));

    [Fact]
    public void EveryAndForGiveEachPointUpToFor() =>
        Assert.Equal([0, 0.5, 1.0], RuntimeTools.CapturePoints(null, new CaptureFramesOptions(0.5, 1.2)));

    [Fact]
    public void EveryAndForStartWithAPointAtZero() => Assert.Equal([0, 0.5, 1.0], RuntimeTools.CapturePoints(null, new CaptureFramesOptions(0.5, 1)));

    [Fact]
    public void AForThatIsAMultipleOfEveryIsItselfAPoint()
    {
        double[] points = RuntimeTools.CapturePoints(null, new CaptureFramesOptions(0.1, 0.3));

        Assert.Equal(4, points.Length);
        Assert.Equal(0, points[0]);
        Assert.Equal(0.3, points[^1], 9);
    }

    [Fact]
    public async Task AValidCallWithoutASessionSaysNoneIsRunning()
    {
        McpException refused = await RefusedAsync(
            [0, 0.5, 120],
            new CaptureFramesOptions(Crop: new ScreenshotCrop(0, 0, 10, 10), TimeoutMs: 600_000)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidCallWithAMethodCallWithoutASessionSaysNoneIsRunning()
    {
        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Call: new MethodCall("TimeProbe", "start_clock")));

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACaptureSendsItsCallToTheBridge()
    {
        JsonElement args = JsonSerializer.Deserialize<JsonElement>("[300]");
        CaptureFramesOptions options = new(Call: new MethodCall("TimeProbe", "start_clock", args));

        JsonObject parameters = RuntimeTools.BuildCaptureParameters([0.3], TimeSpan.FromSeconds(11), options);

        JsonObject call = parameters["call"]!.AsObject();
        Assert.Equal("TimeProbe", call["node"]!.GetValue<string>());
        Assert.Equal("start_clock", call["method"]!.GetValue<string>());
        Assert.Equal("[300]", call["args"]!.ToJsonString());
        Assert.Equal("[0.3]", parameters["points"]!.ToJsonString());
        Assert.Equal(11_000, parameters["deadlineMs"]!.GetValue<long>());
    }

    [Fact]
    public void ACaptureWithoutACallSendsNone() =>
        Assert.False(RuntimeTools.BuildCaptureParameters([0.3], TimeSpan.FromSeconds(11), null).ContainsKey("call"));

    [Theory]
    [InlineData("", "start_clock", "node is empty. ")]
    [InlineData("TimeProbe", " ", "method is empty. ")]
    public async Task ACallWithAnEmptyNodeOrMethodIsRefused(string node, string method, string start)
    {
        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Call: new MethodCall(node, method)));

        Assert.StartsWith(start, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACaptureSendsAGameToolCallAsCallGameToolsRequest()
    {
        CaptureFramesOptions options = new(Call: new MethodCall(Tool: "SetMood", Args: Json("""{"mood": "Angry", "times": 3}""")));

        JsonObject parameters = RuntimeTools.BuildCaptureParameters([0.3], TimeSpan.FromSeconds(11), options);

        Assert.Equal(
            """{"tool":"SetMood","request":{"op":"tool_call","name":"SetMood","args":{"mood":"Angry","times":3},"maxDepth":8}}""",
            parameters["call"]!.ToJsonString()
        );
    }

    [Fact]
    public async Task AValidGameToolCallWithoutASessionSaysNoneIsRunning()
    {
        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Call: new MethodCall(Tool: "Heal")));

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SetMood", "Main", "go", null, TimeValidationTests.BothSuffix)]
    [InlineData(null, null, null, """{"mood": "Angry"}""", TimeValidationTests.NeitherSuffix)]
    [InlineData("SetMood", null, null, """["Angry"]""", TimeValidationTests.ToolArgsSuffix)]
    [InlineData(null, "TimeProbe", "start_clock", """{"ms": 300}""", TimeValidationTests.MethodArgsSuffix)]
    public async Task ACallOfNeitherOrBothFormsOrTheOtherFormsArgsIsRefused(string? tool, string? node, string? method, string? args, string suffix)
    {
        MethodCall call = new(node, method, args is null ? null : Json(args), tool);

        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Call: call));

        Assert.Equal("options.call" + suffix, refused.Message);
    }

    [Fact]
    public async Task AnEmptyToolNameIsRefusedAsCallGameToolRefusesIt()
    {
        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Call: new MethodCall(Tool: "")));

        Assert.Equal("name is empty. Pass a tool's name; list_game_tools lists them.", refused.Message);
    }

    [Fact]
    public void TheDefaultAllowanceIsTheLastPointPlusTenSecondsAndATenthOfASecondAPoint()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1200 + 10_000 + 300), RuntimeTools.CaptureAllowance([0.1, 0.5, 1.2], null, null));
        Assert.Equal(TimeSpan.FromMilliseconds(10_100), RuntimeTools.CaptureAllowance([0], null, null));
    }

    [Fact]
    public void TheReplyMayComeTwentyOneSecondsAfterTheRelease()
    {
        // 5 s for the answer to the cancel, and 2 s for each of the 8 frames the bridge may still be encoding then.
        RuntimeTools.BridgeCall call = RuntimeTools.CaptureCall([], TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(10), call.Release);
        Assert.Equal(TimeSpan.FromSeconds(31), call.Timeout);
    }

    [Fact]
    public void TimeoutMsReplacesTheDefaultAllowance() =>
        Assert.Equal(TimeSpan.FromMilliseconds(1500), RuntimeTools.CaptureAllowance([0.05, 5.0], 1500, null));

    [Fact]
    public void AStartsTimeoutIsAddedToTheAllowance()
    {
        CaptureStart start = new(Expression: "true");

        Assert.Equal(TimeSpan.FromMilliseconds(300 + 10_000 + 100 + 10_000), RuntimeTools.CaptureAllowance([0.3], null, start));
        Assert.Equal(TimeSpan.FromMilliseconds(1500 + 2000), RuntimeTools.CaptureAllowance([0.3], 1500, start with { TimeoutMs = 2000 }));
    }

    [Fact]
    public void TheAllowanceWithAStartIsCappedAtTheMaximumTimeout() =>
        Assert.Equal(
            TimeSpan.FromMilliseconds(RuntimeTools.MaxCaptureTimeoutMs),
            RuntimeTools.CaptureAllowance([0.3], 595_000, new CaptureStart(Expression: "true"))
        );

    [Fact]
    public void ACaptureSendsItsStartToTheBridge()
    {
        CaptureStart start = new(
            Node: "TimeProbe",
            Property: "state",
            EqualsValue: Json("\"done\""),
            TimeoutMs: 3000,
            Edge: true,
            Then: new WaitThen(TimeScale: 0.5)
        );

        JsonObject parameters = RuntimeTools.BuildCaptureParameters([0.3], TimeSpan.FromSeconds(14), new CaptureFramesOptions(Start: start));

        JsonObject sent = parameters["start"]!.AsObject();
        Assert.Equal("property", sent["kind"]!.GetValue<string>());
        Assert.Equal("TimeProbe", sent["node"]!.GetValue<string>());
        Assert.Equal("state", sent["property"]!.GetValue<string>());
        Assert.Equal("\"done\"", sent["equals"]!.ToJsonString());
        Assert.Equal(3000, sent["timeoutMs"]!.GetValue<int>());
        Assert.True(sent["edge"]!.GetValue<bool>(), sent.ToJsonString());
        Assert.Equal("""{"timeScale":0.5}""", sent["then"]!.ToJsonString());
    }

    [Fact]
    public void ACaptureSendsItsStartCallToTheBridge()
    {
        CaptureStart start = new(Node: "TimeProbe", Expression: "node.then_frame >= 0", Edge: true, Call: new MethodCall("TimeProbe", "record_then"));

        JsonObject parameters = RuntimeTools.BuildCaptureParameters([0], TimeSpan.FromSeconds(21), new CaptureFramesOptions(Start: start));

        JsonObject call = parameters["start"]!["call"]!.AsObject();
        Assert.Equal("TimeProbe", call["node"]!.GetValue<string>());
        Assert.Equal("record_then", call["method"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("SetMood", "Main", "go", null, TimeValidationTests.BothSuffix)]
    [InlineData(null, null, null, """{"mood": "Angry"}""", TimeValidationTests.NeitherSuffix)]
    [InlineData("SetMood", null, null, """["Angry"]""", TimeValidationTests.ToolArgsSuffix)]
    [InlineData(null, "TimeProbe", "start_clock", """{"ms": 300}""", TimeValidationTests.MethodArgsSuffix)]
    public async Task AStartCallIsRefusedAsOptionsCallIs(string? tool, string? node, string? method, string? args, string suffix)
    {
        MethodCall call = new(node, method, args is null ? null : Json(args), tool);

        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Start: new CaptureStart(Expression: "true", Call: call)));

        Assert.Equal("options.start.call" + suffix, refused.Message);
    }

    [Fact]
    public void ACaptureStartThenPauseIsRefused()
    {
        CaptureStart start = new(Node: "TimeProbe", Expression: "node.state == 'done'", Then: new WaitThen(Pause: true));

        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildCaptureParameters([0.3], TimeSpan.FromSeconds(14), new CaptureFramesOptions(Start: start))
        );

        Assert.Equal(
            "capture_frames' start.then cannot pause: a paused game adds no game time, so the capture would never advance.",
            refused.Message
        );
    }

    [Fact]
    public void AStartLeftAtItsDefaultsWaitsTenSecondsWithNoEdgeOrThen()
    {
        JsonObject parameters = RuntimeTools.BuildCaptureParameters(
            [0.3],
            TimeSpan.FromSeconds(21),
            new CaptureFramesOptions(Start: new CaptureStart(Node: "TimeProbe", Exists: true))
        );

        JsonObject sent = parameters["start"]!.AsObject();
        Assert.Equal("exists", sent["kind"]!.GetValue<string>());
        Assert.Equal(10_000, sent["timeoutMs"]!.GetValue<int>());
        Assert.False(sent.ContainsKey("edge"), sent.ToJsonString());
        Assert.False(sent.ContainsKey("then"), sent.ToJsonString());
    }

    [Fact]
    public void ACaptureWithoutAStartSendsNone() =>
        Assert.False(RuntimeTools.BuildCaptureParameters([0.3], TimeSpan.FromSeconds(11), new CaptureFramesOptions()).ContainsKey("start"));

    [Fact]
    public void ACaptureSendsASignalStartAndItsCallToTheBridge()
    {
        CaptureStart start = new(
            Node: "TimeProbe",
            Signal: "fired",
            TimeoutMs: 4000,
            Then: new WaitThen(TimeScale: 0.3),
            Call: new MethodCall("TimeProbe", "fire_now", Json("[7]"))
        );

        JsonObject parameters = RuntimeTools.BuildCaptureParameters([0.3], TimeSpan.FromSeconds(15), new CaptureFramesOptions(Start: start));

        JsonObject sent = parameters["start"]!.AsObject();
        Assert.Equal("signal", sent["kind"]!.GetValue<string>());
        Assert.Equal("TimeProbe", sent["node"]!.GetValue<string>());
        Assert.Equal("fired", sent["signal"]!.GetValue<string>());
        Assert.Equal(4000, sent["timeoutMs"]!.GetValue<int>());
        Assert.Equal("""{"timeScale":0.3}""", sent["then"]!.ToJsonString());
        Assert.Equal("fire_now", sent["call"]!["method"]!.GetValue<string>());
        Assert.False(sent.ContainsKey("edge"), sent.ToJsonString());
    }

    [Fact]
    public async Task AnEdgeSignalStartIsRefusedAsAnEdgeSignalWaitIs()
    {
        McpException refused = await RefusedAsync(
            [0.3],
            new CaptureFramesOptions(Start: new CaptureStart(Node: "TimeProbe", Signal: "fired", Edge: true))
        );

        Assert.Equal("options.edge applies to exists, property and expression waits; this condition is signal.", refused.Message);
    }

    [Fact]
    public async Task ASignalStartThatChecksOnceIsRefusedAsASignalWaitIs()
    {
        McpException refused = await RefusedAsync(
            [0.3],
            new CaptureFramesOptions(Start: new CaptureStart(Node: "TimeProbe", Signal: "fired", TimeoutMs: 0))
        );

        Assert.Equal("timeoutMs 0 checks once, which a signal wait cannot do; give it a timeout.", refused.Message);
    }

    [Theory]
    [InlineData("uiChanged")]
    [InlineData("gameMs")]
    [InlineData("frames")]
    [InlineData("text")]
    [InlineData("settled")]
    [InlineData("state")]
    [InlineData("output")]
    public async Task AStartOfAnotherKindIsRefused(string kind)
    {
        CaptureStart start = kind switch
        {
            "uiChanged" => new(UiChanged: Json("true")),
            "gameMs" => new(GameMs: 500),
            "text" => new(Text: "Start", Under: "Lobby"),
            "settled" => new(Node: "Lobby", Stable: 10),
            "state" => new(Node: "Board", EqualsValue: Json("2"), State: "turn"),
            "output" => new(Output: "joined"),
            _ => new(Frames: 30),
        };

        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Start: start));

        Assert.Equal($"options.start takes an exists, property, expression or signal condition; this condition is {kind}.", refused.Message);
    }

    [Theory]
    [InlineData("\"now\"")]
    [InlineData("false")]
    [InlineData("\"later\"")]
    public async Task AStartWithUiChangedIsRefusedWhateverItsValue(string uiChangedJson)
    {
        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Start: new CaptureStart(UiChanged: Json(uiChangedJson))));

        Assert.Equal("options.start takes an exists, property, expression or signal condition; this condition is uiChanged.", refused.Message);
    }

    // A text field makes the start a text condition, refused as one before a text condition's own refusals could send the
    // agent to add text or drop node.
    [Theory]
    [InlineData("under")]
    [InlineData("node")]
    public async Task AStartWithATextFieldIsRefusedAsText(string companion)
    {
        CaptureStart start = companion == "under" ? new(Under: "Lobby") : new(Node: "Lobby", Text: "Start");

        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Start: start));

        Assert.Equal("options.start takes an exists, property, expression or signal condition; this condition is text.", refused.Message);
    }

    [Fact]
    public async Task AnEdgeStartThatChecksOnceIsRefused()
    {
        McpException refused = await RefusedAsync(
            [0.3],
            new CaptureFramesOptions(Start: new CaptureStart(Expression: "true", TimeoutMs: 0, Edge: true))
        );

        Assert.Equal("timeoutMs 0 checks once, which an edge wait cannot meet; give it a timeout.", refused.Message);
    }

    [Fact]
    public async Task AValidStartWithoutASessionSaysNoneIsRunning()
    {
        CaptureStart start = new(Node: "TimeProbe", Expression: "node.state == 'done'", Edge: true, Then: new WaitThen(TimeScale: 0.2));

        McpException refused = await RefusedAsync([0.05, 0.15], new CaptureFramesOptions(Start: start));

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACaptureSendsItsTracksToTheBridgeAsWatchSendsThem()
    {
        CaptureTracks tracks = new(
            Properties: [new WatchPropertyTrack("TimeProbe", "process_frames"), new WatchPropertyTrack("TimeProbe", "state", Name: "phase")],
            Expressions: [new WatchExpressionTrack("drawn", "Engine.get_process_frames()", Node: "TimeProbe")]
        );

        JsonObject parameters = RuntimeTools.BuildCaptureParameters([0.3], TimeSpan.FromSeconds(11), new CaptureFramesOptions(Tracks: tracks));

        Assert.Equal(
            """{"properties":[{"node":"TimeProbe","property":"process_frames"},"""
                + """{"node":"TimeProbe","property":"state","name":"phase"}],"expressions":"""
                + """[{"expression":"Engine.get_process_frames()","node":"TimeProbe","name":"drawn"}]}""",
            parameters["tracks"]!.ToJsonString()
        );
    }

    [Fact]
    public void ACaptureWithoutTracksSendsNone() =>
        Assert.False(RuntimeTools.BuildCaptureParameters([0.3], TimeSpan.FromSeconds(11), new CaptureFramesOptions()).ContainsKey("tracks"));

    [Theory]
    [InlineData("signals", """[{"node": "TimeProbe", "signal": "fired"}]""")]
    [InlineData("monitors", """["frame_ms"]""")]
    [InlineData("audio", """{"players": {}}""")]
    public async Task AWatchOnlyTrackIsRefusedNamingWatch(string field, string json)
    {
        WatchPropertyTrack[] properties = [new("TimeProbe", "process_frames")];
        CaptureTracks tracks = field switch
        {
            "signals" => new(Properties: properties, Signals: Json(json)),
            "monitors" => new(Properties: properties, Monitors: Json(json)),
            _ => new(Properties: properties, Audio: Json(json)),
        };

        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Tracks: tracks));

        Assert.Equal(
            $"options.tracks.{field} is refused: capture_frames reads property and expression tracks only; watch records signals, "
                + "monitors and audio.",
            refused.Message
        );
    }

    [Fact]
    public async Task EmptyTracksAreRefused()
    {
        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Tracks: new CaptureTracks(Properties: [])));

        Assert.Equal(
            "options.tracks needs at least one track: {properties: [{node, property}]} or {expressions: [{name, expression}]}.",
            refused.Message
        );
    }

    [Theory]
    [InlineData(false, "TimeProbe:process_frames")]
    [InlineData(true, "drawn")]
    public async Task ATrackWithMinDeltaIsRefused(bool expression, string key)
    {
        CaptureTracks tracks = expression
            ? new(Expressions: [new WatchExpressionTrack("drawn", "Engine.get_process_frames()", MinDelta: 1)])
            : new(Properties: [new WatchPropertyTrack("TimeProbe", "process_frames", MinDelta: 1)]);

        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Tracks: tracks));

        Assert.Equal(
            $"options.tracks reads each track at every point and keeps every value, so minDelta does not apply; drop it from track '{key}'.",
            refused.Message
        );
    }

    [Fact]
    public async Task TwoTracksOfOneKeyAreRefusedAsWatchRefusesThem()
    {
        CaptureTracks tracks = new(
            Properties: [new WatchPropertyTrack("TimeProbe", "state", Name: "phase")],
            Expressions: [new WatchExpressionTrack("phase", "true")]
        );

        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Tracks: tracks));

        Assert.Equal("Two tracks are keyed 'phase'; give one of them a different name.", refused.Message);
    }

    [Fact]
    public async Task MoreThanThirtyTwoTracksAreRefused()
    {
        WatchExpressionTrack[] expressions = [.. Enumerable.Range(0, 33).Select(index => new WatchExpressionTrack($"t{index}", "true"))];

        McpException refused = await RefusedAsync([0.3], new CaptureFramesOptions(Tracks: new CaptureTracks(Expressions: expressions)));

        Assert.Equal("options.tracks holds 33 property and expression tracks; at most 32 together.", refused.Message);
    }

    [Fact]
    public void APointCarriesItsFramesValuesAndTrackErrors()
    {
        JsonObject reply = JsonNode
            .Parse(
                """
                {"frames": [
                  {"at": 0, "frame": 40, "gameSeconds": 0.016, "late": 0.016, "path": "C:/shots/a.png", "width": 4, "height": 2,
                   "values": {"phase": "idle", "drawn": null}, "trackErrors": {"drawn": "Invalid call."}},
                  {"at": 0.1, "frame": 46, "gameSeconds": 0.1, "late": 0, "path": "C:/shots/b.png", "width": 4, "height": 2,
                   "values": {"phase": "done", "drawn": 46}}
                ]}
                """
            )!
            .AsObject();

        JsonArray points = RuntimeTools.CompactFrames(reply)["points"]!.AsArray();

        Assert.Equal("""{"phase":"idle","drawn":null}""", points[0]!["values"]!.ToJsonString());
        Assert.Equal("""{"drawn":"Invalid call."}""", points[0]!["trackErrors"]!.ToJsonString());
        Assert.Equal("""{"phase":"done","drawn":46}""", points[1]!["values"]!.ToJsonString());
        Assert.False(points[1]!.AsObject().ContainsKey("trackErrors"), points[1]!.ToJsonString());
    }

    [Theory]
    [InlineData(0, 1, "anim-0.png")]
    [InlineData(3, 9, "anim-3.png")]
    [InlineData(0, 10, "anim-00.png")]
    [InlineData(7, 12, "anim-07.png")]
    [InlineData(5, 100, "anim-005.png")]
    public void FrameKOfNLandsAtTheStemWithKPaddedToTheWidthOfN(int index, int count, string expected) =>
        Assert.Equal($".tmp/shots/{expected}", FrameTargets.Name(".tmp/shots/anim.png", index, count));

    [Fact]
    public void AFrameStemMustEndInPng()
    {
        using TempDirectory checkout = TmpCheckout();

        McpException refused = Assert.Throws<McpException>(() => ResolveFrames(checkout.Path, ".tmp/anim", 3, overwrite: false));

        Assert.Equal("path must end in .png: .tmp/anim", refused.Message);
    }

    [Fact]
    public void AFrameStemOutsideATmpFolderIsRefused()
    {
        using TempDirectory checkout = TmpCheckout();

        McpException refused = Assert.Throws<McpException>(() => ResolveFrames(checkout.Path, "shots/anim.png", 3, overwrite: false));

        Assert.Equal(ScreenshotPathValidationTests.TmpOnlyRefusal("capture_frames"), refused.Message);
    }

    [Theory]
    // 12 points can give 1 to 12 distinct files: anim-0 to anim-8 for up to 9 of them, anim-00 to anim-11 for 10 to 12.
    [InlineData("anim-7.png", 12, true)]
    [InlineData("anim-07.png", 12, true)]
    [InlineData("anim-11.png", 12, true)]
    [InlineData("anim-12.png", 12, false)]
    [InlineData("anim-07.png", 9, false)]
    [InlineData("anim-4.png", 5, true)]
    [InlineData("anim-5.png", 5, false)]
    [InlineData("anim.png", 5, false)]
    public void EveryNameACaptureCouldWriteIsCheckedAtTheStart(string existing, int points, bool refused)
    {
        using TempDirectory checkout = TmpCheckout();
        string shots = Directory.CreateDirectory(Path.Combine(checkout.Path, ".tmp", "shots")).FullName;
        File.WriteAllBytes(Path.Combine(shots, existing), [1]);

        if (refused)
        {
            McpException refusal = Assert.Throws<McpException>(() => ResolveFrames(checkout.Path, ".tmp/shots/anim.png", points, overwrite: false));
            Assert.Equal($".tmp/shots/{existing} exists; pass options.overwrite true to replace it", refusal.Message);
        }
        else
        {
            ResolveFrames(checkout.Path, ".tmp/shots/anim.png", points, overwrite: false);
        }

        ResolveFrames(checkout.Path, ".tmp/shots/anim.png", points, overwrite: true);
    }

    [Fact]
    public void AFrameCopyThatFailsRemovesTheCopiesKeepsTheCaptureAndWarns()
    {
        using TempDirectory checkout = TmpCheckout();
        string captureFolder = Directory.CreateDirectory(Path.Combine(checkout.Path, ".godot", "godot-mcp", "screenshots")).FullName;
        File.WriteAllBytes(Path.Combine(captureFolder, "f0.png"), [1]);
        File.WriteAllBytes(Path.Combine(captureFolder, "f1.png"), [2]);
        FrameTargets targets = ResolveFrames(checkout.Path, ".tmp/shots/anim.png", 2, overwrite: false);
        string shots = Directory.CreateDirectory(Path.Combine(checkout.Path, ".tmp", "shots")).FullName;
        File.WriteAllBytes(Path.Combine(shots, "anim-1.png"), [9]);
        JsonObject result = new() { ["folder"] = captureFolder, ["files"] = new JsonArray("f0.png", "f1.png") };

        targets.Deliver(result);

        string warning = result["warning"]!.GetValue<string>();
        Assert.StartsWith("the frames could not be copied to .tmp/shots/anim-1.png: ", warning, StringComparison.Ordinal);
        Assert.EndsWith($"; they are kept in {captureFolder}", warning, StringComparison.Ordinal);
        Assert.Equal(captureFolder, result["folder"]!.GetValue<string>());
        Assert.Equal(["f0.png", "f1.png"], result["files"]!.AsArray().Select(file => file!.GetValue<string>()));
        Assert.False(File.Exists(Path.Combine(shots, "anim-0.png")));
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(shots, "anim-1.png")));
    }

    /// <summary>A git checkout tracking nothing, so only its .tmp folder is open.</summary>
    private static TempDirectory TmpCheckout()
    {
        TempDirectory checkout = new();
        Git.Run(checkout.Path, "init", "--quiet", "--initial-branch=main");
        return checkout;
    }

    private static FrameTargets ResolveFrames(string project, string path, int points, bool overwrite) =>
        FrameTargets.Resolve(OutputPlaces.Find(project, NullLogger.Instance), path, points, overwrite);

    private async Task<McpException> RefusedAsync(double[]? at, CaptureFramesOptions? options) =>
        await Assert.ThrowsAsync<McpException>(() => _tools.CaptureFramesAsync(at, options, null, TestContext.Current.CancellationToken));

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
