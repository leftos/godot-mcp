using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// frame_control and wait_for against the InputProbe with time_probe.tscn added under the root: TimeProbe, a pausable node
/// counting its process frames and physics ticks and summing their delta, a Swatch repainted from the frame count each frame,
/// and arm(ms), which after ms adds a child Armed, sets state to "done" and emits fired(ms). One shared run, reset before each
/// test; the reset frees TimeProbe and restores pause, time scale and drawing.
/// </summary>
public sealed class TimeTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Probe = "scene_tree.root.get_node(\"TimeProbe\")";
    private const string PausedRefusal =
        "The game is paused, so only a signal wait or a check-once wait (timeoutMs 0) can be met; resume or step it first.";
    private const string SteppingRefusal = "A step is still running on this game; wait for its reply before pause, resume or another step.";
    private const string NoUiBaseline =
        "uiChanged has no baseline: no input gesture has started since launch or since the last uiChanged wait was met; " + "send the input first";
    private const string TooltipShown =
        "for window in scene_tree.root.get_embedded_subwindows():\n\t\t"
        + "if window.visible and window.theme_type_variation == &\"TooltipPanel\":\n\t\t\t"
        + "return true\n\t"
        + "return false";
    private static readonly WaitCondition UiChangedCondition = new(UiChanged: true);
    private static readonly InputTarget OpenButton = new("OpenButton");
    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task PauseStopsPausableNodesButBridgeStillAnswers()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        JsonObject paused = await FrameAsync("pause");
        long before = await ReadIntAsync("process_frames");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        long after = await ReadIntAsync("process_frames");
        JsonObject resumed = await FrameAsync("resume");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        long running = await ReadIntAsync("process_frames");

        Assert.True(paused["paused"]!.GetValue<bool>(), paused.ToJsonString());
        Assert.Equal(before, after);
        Assert.False(resumed["paused"]!.GetValue<bool>(), resumed.ToJsonString());
        Assert.True(running > after, $"the probe did not run again after resume: {after} then {running}");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepAdvancesExactlyNProcessFrames()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        long before = await ReadIntAsync("process_frames");

        JsonObject stepped = await FrameAsync("step", count: 5);
        long after = await ReadIntAsync("process_frames");

        Assert.Equal(before + 5, after);
        Assert.Equal(5, stepped["processFrames"]!.GetValue<int>());
        Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepPhysicsCountsPhysicsTicks()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        long before = await ReadIntAsync("physics_ticks");

        JsonObject stepped = await FrameAsync("step", count: 4, options: new StepOptions(Unit: "physics"));
        long after = await ReadIntAsync("physics_ticks");

        Assert.Equal(before + 4, after);
        Assert.Equal(4, stepped["physicsFrames"]!.GetValue<int>());
        Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepFromRunningPausesFirst()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        JsonObject stepped = await FrameAsync("step", count: 2);
        long before = await ReadIntAsync("process_frames");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        long after = await ReadIntAsync("process_frames");

        Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.Equal(2, stepped["processFrames"]!.GetValue<int>());
        Assert.Equal(before, after);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TimeScaleScalesDelta()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");

        JsonObject scaled = await FrameAsync("time_scale", scale: 0.5);
        JsonNode before = await RunAsync($"return [{Probe}.physics_delta, Engine.physics_ticks_per_second]");
        await FrameAsync("step", count: 6, options: new StepOptions(Unit: "physics"));
        double after = (await RunAsync($"return {Probe}.physics_delta")).GetValue<double>();
        JsonObject restored = await FrameAsync("time_scale", scale: 1);

        // Every physics tick's delta is 1 / physics_ticks_per_second times Engine.time_scale.
        double expected = 6 * 0.5 / before[1]!.GetValue<double>();
        Assert.Equal(0.5, scaled["timeScale"]!.GetValue<double>());
        Assert.Equal(expected, after - before[0]!.GetValue<double>(), 6);
        Assert.Equal(1.0, restored["timeScale"]!.GetValue<double>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepWithScreenshotShowsFrameN()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        long before = await ReadIntAsync("process_frames");

        List<ContentBlock> blocks =
        [
            .. await _tools.FrameControlAsync("step", 3, null, new StepOptions(Screenshot: true), cancellationToken: cancellation),
        ];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;
        string path = reply["screenshot"]!["path"]!.GetValue<string>();
        JsonNode pixel = await RunAsync(
            $"var c := Image.load_from_file(\"{path.Replace('\\', '/')}\").get_pixel(600, 310)\n\treturn [c.r8, c.g8, c.b8]"
        );

        // ticker.gd paints the Swatch (580, 290)-(620, 330) Color8((process_frames % 16) * 16, 0, 0) each frame.
        long expectedRed = (before + 3) % 16 * 16;
        Assert.Equal(3, reply["processFrames"]!.GetValue<int>());
        Assert.Equal("image/png", Assert.Single(blocks.OfType<ImageContentBlock>()).MimeType);
        Assert.InRange(pixel[0]!.GetValue<int>(), expectedRed - 3, expectedRed + 3);
        Assert.InRange(pixel[1]!.GetValue<int>(), 0, 3);
        Assert.Equal(before + 3, await ReadIntAsync("process_frames"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForNodeExists()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await ArmAsync(300);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "Armed", Exists: true));

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(waited["value"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(waited["frames"]!.GetValue<int>() > 0, waited.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForPropertyEquals()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await ArmAsync(300);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\"")));

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal("done", waited["value"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForSignalReturnsArgs()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await ArmAsync(500);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Signal: "fired"));

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal("[500]", waited["args"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForSignalWhilePaused()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        await ArmAsync(300);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Signal: "fired"));
        bool stillPaused = (await RunAsync("return scene_tree.paused")).GetValue<bool>();

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(stillPaused);
    }

    // A signal wait's node is resolved once, up front; TimeProbe/Missing is not there to resolve.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForSignalOnAMissingNodeIsRefusedNamingTheBaseAndItsChildren()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => WaitAsync(new WaitCondition(Node: "TimeProbe/Missing", Signal: "fired")));

        Assert.Contains(
            "No node 'TimeProbe/Missing' in the running game: a path is read from /root, and /root/TimeProbe has no child 'Missing' ",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("get_scene_tree lists the nodes' paths.", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForExpression()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await ArmAsync(300);

        const string Expression =
            "node.state == \"done\" and tree.root.has_node(\"TimeProbe/Armed\") and root == tree.root and Engine.time_scale > 0 "
            + "and not Input.is_action_pressed(\"ui_accept\")";
        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Expression: Expression));

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(waited["value"]!.GetValue<bool>(), waited.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForExpressionParseErrorFails()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => WaitAsync(new WaitCondition(Expression: "1 +")));

        Assert.StartsWith("wait_for failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("the expression does not parse: ", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForTimesOutWithLastValue()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\"")), 300);

        Assert.False(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal("idle", waited["last"]!.GetValue<string>());
        Assert.True(waited["elapsedMs"]!.GetValue<double>() >= 300, waited.ToJsonString());
        Assert.True(waited["frames"]!.GetValue<int>() > 0, waited.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForPropertyWhilePausedIsRefused()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\"")))
        );

        Assert.Contains(PausedRefusal, refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACheckOnceWaitWorksWhilePaused()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");

        JsonObject holds = await WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"idle\"")), 0);
        JsonObject fails = await WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\"")), 0);

        Assert.True(holds["met"]!.GetValue<bool>(), holds.ToJsonString());
        Assert.Equal("idle", holds["value"]!.GetValue<string>());
        Assert.Equal(0, holds["frames"]!.GetValue<int>());
        Assert.False(fails["met"]!.GetValue<bool>(), fails.ToJsonString());
        Assert.Equal("idle", fails["last"]!.GetValue<string>());
        Assert.Equal(0, fails["frames"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStepWhileAnotherRunsIsRefusedAndTheFirstCountsTrue()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        long before = await ReadIntAsync("process_frames");

        Task<JsonObject> first = FrameAsync("step", count: 300);
        await Task.Delay(100, cancellation);
        McpException secondStep = await Assert.ThrowsAsync<McpException>(() => FrameAsync("step", count: 1));
        McpException resume = await Assert.ThrowsAsync<McpException>(() => FrameAsync("resume"));
        JsonObject scaled = await FrameAsync("time_scale", scale: 1);
        JsonObject stepped = await first;

        Assert.Contains(SteppingRefusal, secondStep.Message, StringComparison.Ordinal);
        Assert.Contains(SteppingRefusal, resume.Message, StringComparison.Ordinal);
        Assert.Equal(1.0, scaled["timeScale"]!.GetValue<double>());
        Assert.Equal(300, stepped["processFrames"]!.GetValue<int>());
        Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.Equal(before + 300, await ReadIntAsync("process_frames"));
    }

    // A 120-frame step's deadline is 10 s + 120 x 100 ms = 22 s, and the server waits 5 s longer for its answer.
    [Fact(Timeout = 60_000)]
    public async Task AStepWhoseFramesStopDrawingEndsAtItsDeadlineAndFreesTheNextStep()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        await RunAsync($"{Probe}.stop_drawing_after(100)\n\treturn true");

        McpException stopped = await Assert.ThrowsAsync<McpException>(() => FrameAsync("step", count: 120));
        bool paused = (await RunAsync("return scene_tree.paused")).GetValue<bool>();
        JsonObject resumed = await FrameAsync("resume");

        Assert.Matches(
            @"The step stopped after \d+ of 120 frames: no frame was drawn for too long \(is the window minimized\?\); the game is left paused\.",
            stopped.Message
        );
        Assert.True(paused);
        Assert.False(resumed["paused"]!.GetValue<bool>(), resumed.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AGamePausingItselfMidStepFailsTheStep()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        await RunAsync($"{Probe}.pause_after(300)\n\treturn true");

        McpException failed = await Assert.ThrowsAsync<McpException>(() => FrameAsync("step", count: 1000));

        Assert.Matches(@"The game changed its own pause state during the step, after \d+ of 1000 frames\.", failed.Message);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepPhysicsWithScreenshotShowsTheFrameAfterTheLastTick()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        long ticksBefore = await ReadIntAsync("physics_ticks");

        List<ContentBlock> blocks =
        [
            .. await _tools.FrameControlAsync("step", 3, null, new StepOptions("physics", true), cancellationToken: cancellation),
        ];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;
        string path = reply["screenshot"]!["path"]!.GetValue<string>();
        JsonNode pixel = await RunAsync(
            $"var c := Image.load_from_file(\"{path.Replace('\\', '/')}\").get_pixel(600, 310)\n\treturn [c.r8, c.g8, c.b8]"
        );
        long frames = await ReadIntAsync("process_frames");

        long expectedRed = frames % 16 * 16;
        Assert.Equal(3, reply["physicsFrames"]!.GetValue<int>());
        Assert.Equal(ticksBefore + 3, await ReadIntAsync("physics_ticks"));
        Assert.Equal("image/png", Assert.Single(blocks.OfType<ImageContentBlock>()).MimeType);
        Assert.InRange(pixel[0]!.GetValue<int>(), expectedRed - 3, expectedRed + 3);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForSignalTimesOutWithoutLast()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Signal: "fired"), 300);

        Assert.False(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.False(waited.ContainsKey("last"), waited.ToJsonString());
        Assert.False(waited.ContainsKey("args"), waited.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForSignalOnANodeFreedMidWaitTimesOutCleanly()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        Task<JsonObject> waiting = WaitAsync(new WaitCondition(Node: "TimeProbe", Signal: "fired"), 1000);
        await Task.Delay(200, cancellation);
        await RunAsync($"{Probe}.queue_free()\n\treturn true");
        JsonObject waited = await waiting;

        Assert.False(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.False(waited.ContainsKey("errors"), waited.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForIntPropertyMatchesAJsonNumber()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "n", EqualsValue: Json("3")));

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal(3, waited["value"]!.GetValue<int>());
        Assert.Equal(0, waited["frames"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForAPropertyTheNodeLacksFails()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "nope:x", EqualsValue: Json("1")))
        );

        Assert.Contains("'/root/TimeProbe' has no property 'nope'.", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForScreenshotShowsTheFrameTheConditionMetOn()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        WaitCondition condition = new(Node: "TimeProbe", Expression: "node.process_frames % 16 == 5");

        List<ContentBlock> blocks =
        [
            .. await _tools.WaitForAsync(condition, 5000, new WaitOptions(Screenshot: true), cancellationToken: cancellation),
        ];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;
        string path = reply["screenshot"]!["path"]!.GetValue<string>();
        JsonNode pixel = await RunAsync(
            $"var c := Image.load_from_file(\"{path.Replace('\\', '/')}\").get_pixel(600, 310)\n\treturn [c.r8, c.g8, c.b8]"
        );

        // ticker.gd paints the Swatch (580, 290)-(620, 330) Color8((process_frames % 16) * 16, 0, 0) each frame, so the frame
        // the condition was met on is red 80, and the one after it 96.
        Assert.True(reply["met"]!.GetValue<bool>(), reply.ToJsonString());
        Assert.Null(reply["warning"]);
        Assert.Equal("image/png", Assert.Single(blocks.OfType<ImageContentBlock>()).MimeType);
        Assert.InRange(pixel[0]!.GetValue<int>(), 80 - 3, 80 + 3);
        Assert.InRange(pixel[1]!.GetValue<int>(), 0, 3);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACheckOnceWaitCapturesTheCurrentFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await FrameAsync("pause");
        long frames = await ReadIntAsync("process_frames");
        WaitCondition condition = new(Node: "TimeProbe", Expression: $"node.process_frames == {frames}");

        List<ContentBlock> blocks = [.. await _tools.WaitForAsync(condition, 0, new WaitOptions(Screenshot: true), cancellationToken: cancellation)];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;
        string path = reply["screenshot"]!["path"]!.GetValue<string>();
        JsonNode pixel = await RunAsync(
            $"var c := Image.load_from_file(\"{path.Replace('\\', '/')}\").get_pixel(600, 310)\n\treturn [c.r8, c.g8, c.b8]"
        );

        // The paused TimeProbe keeps the state the check saw through the frame's draw; ticker.gd paints the Swatch
        // Color8((process_frames % 16) * 16, 0, 0).
        long expectedRed = frames % 16 * 16;
        Assert.True(reply["met"]!.GetValue<bool>(), reply.ToJsonString());
        Assert.Equal(0, reply["frames"]!.GetValue<int>());
        Assert.Null(reply["warning"]);
        Assert.Equal("image/png", Assert.Single(blocks.OfType<ImageContentBlock>()).MimeType);
        Assert.InRange(pixel[0]!.GetValue<int>(), expectedRed - 3, expectedRed + 3);
        Assert.InRange(pixel[1]!.GetValue<int>(), 0, 3);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWaitMetOnAnUndrawnFrameWarnsWithNoImage()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await RunAsync($"{Probe}.stop_drawing_after(1)\n\treturn true");
        await Task.Delay(300, cancellation);
        WaitCondition condition = new(Node: "TimeProbe", Expression: "node.process_frames > 0");

        // The reset turns drawing back on for the next test.
        List<ContentBlock> blocks =
        [
            .. await _tools.WaitForAsync(condition, 2000, new WaitOptions(Screenshot: true), cancellationToken: cancellation),
        ];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;

        Assert.True(reply["met"]!.GetValue<bool>(), reply.ToJsonString());
        Assert.Equal(
            "The frame the condition was met on was not drawn (is the window minimized, or the game in low-processor mode?), so there "
                + "is no screenshot.",
            reply["warning"]?.GetValue<string>()
        );
        Assert.Null(reply["screenshot"]);
        Assert.Empty(blocks.OfType<ImageContentBlock>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWaitThatTimesOutCapturesNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        WaitCondition condition = new(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\""));

        List<ContentBlock> blocks =
        [
            .. await _tools.WaitForAsync(condition, 300, new WaitOptions(Screenshot: true), cancellationToken: cancellation),
        ];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;

        Assert.False(reply["met"]!.GetValue<bool>(), reply.ToJsonString());
        Assert.Equal("idle", reply["last"]!.GetValue<string>());
        Assert.Null(reply["screenshot"]);
        Assert.Null(reply["warning"]);
        Assert.Empty(blocks.OfType<ImageContentBlock>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task UiChangedWithoutGestureIsRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(UiChangedCondition, 300, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Contains(NoUiBaseline, refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task UiChangedReportsPanelOpenedByClick()
    {
        string panel = await AddOpenerAsync();
        await _tools.ClickAsync(OpenButton, "left", false, cancellationToken: TestContext.Current.CancellationToken);

        JsonObject waited = await WaitAsync(UiChangedCondition, 2000);
        McpException again = await Assert.ThrowsAsync<McpException>(() => WaitAsync(UiChangedCondition, 300));

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        JsonObject change = waited["value"]!.AsObject();
        Assert.Contains(panel, Paths(change["appeared"]));
        Assert.Equal(change["appeared"]!.AsArray().Count, change["appearedCount"]!.GetValue<int>());
        Assert.Contains(NoUiBaseline, again.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task UiChangedSeesPressBeforeRelease()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string panel = await AddOpenerAsync();

        await _tools.MouseButtonAsync(OpenButton, "left", "press", cancellationToken: cancellation);
        await _tools.MouseButtonAsync(OpenButton, "left", "release", cancellationToken: cancellation);
        JsonObject waited = await WaitAsync(UiChangedCondition, 1000);

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Contains(panel, Paths(waited["value"]!["appeared"]));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task UiChangedIgnoresTooltip()
    {
        // A motion with no button over ProbeButton (tooltip_text set, centre (80, 90)) starts its tooltip timer:
        // gui/timers/tooltip_delay_sec, 0.5 s by default.
        JsonObject hover = new()
        {
            ["type"] = "mouse_motion",
            ["x"] = 80,
            ["y"] = 90,
        };
        await _tools.SimulateInputAsync([hover], cancellationToken: TestContext.Current.CancellationToken);

        JsonObject waited = await WaitAsync(UiChangedCondition, 1500);
        bool tooltipShown = (await RunAsync(TooltipShown)).GetValue<bool>();

        Assert.True(tooltipShown, "no tooltip showed over ProbeButton, so the wait proves nothing");
        Assert.False(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(waited.ContainsKey("last") && waited["last"] is null, waited.ToJsonString());
        Assert.False(waited.ContainsKey("value"), waited.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task UiChangedReportsFocusMove()
    {
        string before = (await RunAsync("return str(scene_tree.root.gui_get_focus_owner().get_path())")).GetValue<string>();

        await _tools.KeyAsync("Down", "tap", null, cancellationToken: TestContext.Current.CancellationToken);
        JsonObject waited = await WaitAsync(UiChangedCondition, 2000);

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        JsonNode focus = waited["value"]!["focus"]!;
        Assert.EndsWith("Menu/MenuA", before, StringComparison.Ordinal);
        Assert.Equal(before, focus["before"]!.GetValue<string>());
        Assert.EndsWith("Menu/MenuB", focus["after"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, waited["value"]!["appearedCount"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MonitorKeepsACounterThatChangesEveryFrame()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        JsonObject monitored = await MonitorAsync("process_frames", new MonitorOptions(10));

        JsonArray samples = monitored["samples"]!.AsArray();
        Assert.Equal(10, samples.Count);
        Assert.Equal(0, monitored["droppedDuplicates"]!.GetValue<int>());
        Assert.Equal(10, monitored["requested"]!.GetValue<int>());
        Assert.True(monitored["elapsedMs"]!.GetValue<long>() >= 0, monitored.ToJsonString());
        Assert.Equal(Enumerable.Range(0, 10), samples.Select(sample => sample!["frame"]!.GetValue<int>()));
        long first = samples[0]!["value"]!.GetValue<long>();
        Assert.Equal(Enumerable.Range(0, 10).Select(step => first + step), samples.Select(sample => sample!["value"]!.GetValue<long>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MonitorDropsAConstantPropertysRepeats()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        JsonObject monitored = await MonitorAsync("n", new MonitorOptions(10));

        JsonNode sample = Assert.Single(monitored["samples"]!.AsArray())!;
        Assert.Equal(0, sample["frame"]!.GetValue<int>());
        Assert.Equal(3, sample["value"]!.GetValue<int>());
        Assert.Equal(9, monitored["droppedDuplicates"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MonitorWithoutChangesOnlyKeepsEverySample()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        JsonObject monitored = await MonitorAsync("n", new MonitorOptions(10, ChangesOnly: false));

        Assert.Equal(10, monitored["samples"]!.AsArray().Count);
        Assert.All(monitored["samples"]!.AsArray(), sample => Assert.Equal(3, sample!["value"]!.GetValue<int>()));
        Assert.Equal(0, monitored["droppedDuplicates"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MonitorWithUnitPhysicsSamplesEachTick()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);

        JsonObject monitored = await MonitorAsync("physics_ticks", new MonitorOptions(10, "physics"));

        JsonArray samples = monitored["samples"]!.AsArray();
        Assert.Equal(10, samples.Count);
        long first = samples[0]!["value"]!.GetValue<long>();
        Assert.Equal(Enumerable.Range(0, 10).Select(step => first + step), samples.Select(sample => sample!["value"]!.GetValue<long>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AGamePausingItselfMidMonitorEndsItWithTheSamplesSoFar()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await RunAsync($"{Probe}.pause_after(200)\n\treturn true");

        JsonObject monitored = await MonitorAsync("process_frames", new MonitorOptions(RuntimeTools.MaxMonitorSamples));

        int pausedAt = monitored["pausedAtFrame"]!.GetValue<int>();
        JsonArray samples = monitored["samples"]!.AsArray();
        Assert.InRange(pausedAt, 1, RuntimeTools.MaxMonitorSamples - 1);
        Assert.Equal(RuntimeTools.MaxMonitorSamples, monitored["requested"]!.GetValue<int>());
        Assert.InRange(samples.Count, 1, pausedAt);
        Assert.All(samples, sample => Assert.True(sample!["frame"]!.GetValue<int>() < pausedAt, monitored.ToJsonString()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MonitorWhilePausedIsRefused()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");

        McpException refused = await Assert.ThrowsAsync<McpException>(() => MonitorAsync("process_frames", new MonitorOptions(10)));

        Assert.Contains(PausedRefusal, refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CaptureFramesTakesEachPointInOneCall()
    {
        JsonObject captured = await CaptureFramesAsync([0.1, 0.3, 0.3], null, TestContext.Current.CancellationToken);

        JsonArray frames = captured["frames"]!.AsArray();
        Assert.Equal(3, frames.Count);
        Assert.Equal([0.1, 0.3, 0.3], frames.Select(frame => frame!["at"]!.GetValue<double>()));
        Assert.Equal(frames[1]!["path"]!.GetValue<string>(), frames[2]!["path"]!.GetValue<string>());
        Assert.All(frames, frame => Assert.True(File.Exists(frame!["path"]!.GetValue<string>()), captured.ToJsonString()));
        Assert.All(
            frames,
            frame => Assert.True(frame!["gameSeconds"]!.GetValue<double>() >= frame["at"]!.GetValue<double>(), captured.ToJsonString())
        );
        long[] numbers = [.. frames.Select(frame => frame!["frame"]!.GetValue<long>())];
        Assert.True(numbers.Zip(numbers.Skip(1)).All(pair => pair.First <= pair.Second), captured.ToJsonString());
        Assert.False(captured.ContainsKey("stopped"), captured.ToJsonString());
        Assert.False(captured.ContainsKey("missed"), captured.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CaptureFramesEveryForTakesEachPoint()
    {
        JsonObject captured = await CaptureFramesAsync(null, new CaptureFramesOptions(0.1, 0.3), TestContext.Current.CancellationToken);

        JsonArray frames = captured["frames"]!.AsArray();
        Assert.Equal(3, frames.Count);
        Assert.All(frames, frame => Assert.True(File.Exists(frame!["path"]!.GetValue<string>()), captured.ToJsonString()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CaptureFramesFollowsTimeScale()
    {
        await FrameAsync("time_scale", scale: 8);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            JsonObject captured = await CaptureFramesAsync([8.0], null, TestContext.Current.CancellationToken);
            watch.Stop();

            JsonNode frame = Assert.Single(captured["frames"]!.AsArray())!;
            Assert.True(frame["gameSeconds"]!.GetValue<double>() >= 8.0, captured.ToJsonString());
            Assert.True(
                watch.Elapsed < TimeSpan.FromSeconds(8),
                $"8 s of game time at time_scale 8 took {watch.Elapsed}; ignoring time_scale takes at least 8 s"
            );
        }
        finally
        {
            await FrameAsync("time_scale", scale: 1);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForGameMsFollowsTimeScale()
    {
        await FrameAsync("time_scale", scale: 4);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            JsonObject waited = await WaitAsync(new WaitCondition(GameMs: 4000), TestContext.Current.CancellationToken);
            watch.Stop();

            Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
            Assert.True(waited["value"]!.GetValue<double>() >= 4000, waited.ToJsonString());
            Assert.True(
                watch.Elapsed < TimeSpan.FromSeconds(4),
                $"4 s of game time at time_scale 4 took {watch.Elapsed}; ignoring time_scale takes at least 4 s"
            );
        }
        finally
        {
            await FrameAsync("time_scale", scale: 1);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForFramesMeetsAfterNFrames()
    {
        JsonObject waited = await WaitAsync(new WaitCondition(Frames: 30), TestContext.Current.CancellationToken);

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.InRange(waited["value"]!.GetValue<int>(), 30, 31);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AGameMsWaitWhilePausedIsRefused()
    {
        await FrameAsync("pause");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            WaitAsync(new WaitCondition(GameMs: 500), TestContext.Current.CancellationToken)
        );

        Assert.Contains(PausedRefusal, refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AGameMsWaitWithScreenshotReturnsTheCapture()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        List<ContentBlock> blocks =
        [
            .. await _tools.WaitForAsync(new WaitCondition(GameMs: 200), null, new WaitOptions(Screenshot: true), cancellationToken: cancellation),
        ];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;

        Assert.True(reply["met"]!.GetValue<bool>(), reply.ToJsonString());
        Assert.True(reply["value"]!.GetValue<double>() >= 200, reply.ToJsonString());
        Assert.Null(reply["warning"]);
        Assert.True(File.Exists(reply["screenshot"]!["path"]!.GetValue<string>()), reply.ToJsonString());
        Assert.Equal("image/png", Assert.Single(blocks.OfType<ImageContentBlock>()).MimeType);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CaptureFramesIsRefusedWhilePaused()
    {
        await FrameAsync("pause");

        McpException refused = await Assert.ThrowsAsync<McpException>(() => CaptureFramesAsync([0.1], null, TestContext.Current.CancellationToken));

        Assert.Contains(
            "The game is paused, so its game time does not advance and capture_frames cannot reach its points",
            refused.Message,
            StringComparison.Ordinal
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CaptureFramesStoppedAtTheTimeoutReportsWhatItMissed()
    {
        await FrameAsync("time_scale", scale: 0.1);
        try
        {
            JsonObject captured = await CaptureFramesAsync(
                [0.05, 5.0],
                new CaptureFramesOptions(TimeoutMs: 1500),
                TestContext.Current.CancellationToken
            );

            JsonNode frame = Assert.Single(captured["frames"]!.AsArray())!;
            Assert.Equal(0.05, frame["at"]!.GetValue<double>());
            Assert.True(captured["stopped"]!.GetValue<bool>(), captured.ToJsonString());
            Assert.Equal([5.0], captured["missed"]!.AsArray().Select(point => point!.GetValue<double>()));
        }
        finally
        {
            await FrameAsync("time_scale", scale: 1);
        }
    }

    // Adds OpenButton, a Button under the root, and OpenedPanel, a Panel hidden until the button is pressed; the button acts on
    // the press (ACTION_MODE_BUTTON_PRESS), not the release, and takes no focus. Returns the panel's path.
    private async Task<string> AddOpenerAsync() =>
        (
            await RunAsync(
                "var button := Button.new()\n\t"
                    + "button.name = \"OpenButton\"\n\t"
                    + "button.text = \"Open\"\n\t"
                    + "button.focus_mode = Control.FOCUS_NONE\n\t"
                    + "button.action_mode = BaseButton.ACTION_MODE_BUTTON_PRESS\n\t"
                    + "button.position = Vector2(540, 300)\n\t"
                    + "button.size = Vector2(80, 40)\n\t"
                    + "var panel := Panel.new()\n\t"
                    + "panel.name = \"OpenedPanel\"\n\t"
                    + "panel.position = Vector2(440, 200)\n\t"
                    + "panel.size = Vector2(80, 60)\n\t"
                    + "panel.visible = false\n\t"
                    + "button.pressed.connect(panel.show)\n\t"
                    + "scene_tree.root.add_child(button)\n\t"
                    + "scene_tree.root.add_child(panel)\n\t"
                    + "return str(panel.get_path())"
            )
        ).GetValue<string>();

    private static IEnumerable<string> Paths(JsonNode? list) => list!.AsArray().Select(path => path!.GetValue<string>());

    /// <summary>Adds time_probe.tscn under the shared run's root as TimeProbe; InitializeAsync has already reset the run.</summary>
    private Task<string> AddTimeProbeAsync(CancellationToken cancellationToken) =>
        _tools.RunScriptAsync(
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t"
                + "var probe: Node = load(\"res://time_probe.tscn\").instantiate()\n\tscene_tree.root.add_child(probe)\n\treturn probe\n",
            ScriptTimeoutMs,
            cancellationToken: cancellationToken
        );

    private Task<JsonNode> ArmAsync(int ms) => RunAsync($"{Probe}.arm({ms})\n\treturn true");

    private async Task<long> ReadIntAsync(string property) => (await RunAsync($"return {Probe}.{property}")).GetValue<long>();

    private async Task<JsonObject> FrameAsync(string action, int? count = null, double? scale = null, StepOptions? options = null)
    {
        IEnumerable<ContentBlock> blocks = await _tools.FrameControlAsync(
            action,
            count,
            scale,
            options,
            cancellationToken: TestContext.Current.CancellationToken
        );
        return JsonNode.Parse(Text(blocks))!.AsObject();
    }

    /// <summary>A wait under the timeout wait_for gives its kind when timeoutMs is left out.</summary>
    private async Task<JsonObject> WaitAsync(WaitCondition condition, CancellationToken cancellationToken) =>
        JsonNode.Parse(Text(await _tools.WaitForAsync(condition, cancellationToken: cancellationToken)))!.AsObject();

    private async Task<JsonObject> MonitorAsync(string property, MonitorOptions options) =>
        JsonNode
            .Parse(await _tools.MonitorPropertyAsync("TimeProbe", property, options, cancellationToken: TestContext.Current.CancellationToken))!
            .AsObject();

    private async Task<JsonObject> CaptureFramesAsync(double[]? at, CaptureFramesOptions? options, CancellationToken cancellationToken) =>
        JsonNode.Parse(await _tools.CaptureFramesAsync(at, options, cancellationToken: cancellationToken))!.AsObject();

    private async Task<JsonObject> WaitAsync(WaitCondition condition, int timeoutMs = 10_000) =>
        JsonNode.Parse(Text(await _tools.WaitForAsync(condition, timeoutMs, cancellationToken: TestContext.Current.CancellationToken)))!.AsObject();

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string Text(IEnumerable<ContentBlock> blocks) => string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text));
}
