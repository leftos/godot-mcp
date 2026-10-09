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
    public async Task StepUntilFromPausedStopsPausedOnTheMetFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await FrameAsync("pause");
        long target = await ReadIntAsync("process_frames") + 7;

        JsonObject stepped = await FrameAsync("step", options: new StepOptions(Until: FramesReach(target)));
        long read = await InspectIntAsync("process_frames", cancellation);
        await Task.Delay(300, cancellation);
        long later = await InspectIntAsync("process_frames", cancellation);

        Assert.True(stepped["met"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.True(stepped["value"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.False(stepped.ContainsKey("last"), stepped.ToJsonString());
        Assert.Equal(7, stepped["processFrames"]!.GetValue<int>());
        Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.True(stepped["frame"]!.GetValue<long>() > 0, stepped.ToJsonString());
        Assert.Equal(target, read);
        Assert.Equal(target, later);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepUntilFromRunningStopsPausedOnTheMetFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        long target = await ReadIntAsync("process_frames") + 120;

        JsonObject stepped = await FrameAsync("step", options: new StepOptions(Until: FramesReach(target)));
        await Task.Delay(300, cancellation);

        Assert.True(stepped["met"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.InRange(stepped["processFrames"]!.GetValue<int>(), 1, 120);
        Assert.Equal(target, await InspectIntAsync("process_frames", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepUntilNeverMetRunsCountFramesAndAnswersLast()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await FrameAsync("pause");
        long before = await ReadIntAsync("process_frames");

        StepOptions options = new(Until: new WaitCondition(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"never\"")));
        JsonObject stepped = await FrameAsync("step", count: 5, options: options);

        Assert.False(stepped["met"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.Equal("idle", stepped["last"]!.GetValue<string>());
        Assert.False(stepped.ContainsKey("value"), stepped.ToJsonString());
        Assert.Equal(5, stepped["processFrames"]!.GetValue<int>());
        Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.Equal(before + 5, await InspectIntAsync("process_frames", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepUntilASignalStopsOnTheFrameItFiredIn()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await FrameAsync("pause");
        // Armed well out: arm's timer runs while the game is paused, so a shorter one could fire before the step connects,
        // and the frame the handler records is then the one the step's own check met the signal in.
        await RunAsync(
            $"var probe: Node = {Probe}\n\t"
                + "probe.fired.connect(func(value: Variant) -> void: probe.set_meta(&\"fired_frame\", Engine.get_process_frames()))\n\t"
                + "probe.arm(1000)\n\t"
                + "return true"
        );

        JsonObject stepped = await FrameAsync("step", options: new StepOptions(Until: new WaitCondition(Node: "TimeProbe", Signal: "fired")));
        long read = await InspectIntAsync("process_frames", cancellation);
        await Task.Delay(300, cancellation);
        long later = await InspectIntAsync("process_frames", cancellation);
        long fired = (await RunAsync($"return {Probe}.get_meta(&\"fired_frame\", -1)")).GetValue<long>();
        bool paused = (await RunAsync("return scene_tree.paused")).GetValue<bool>();

        Assert.True(stepped["met"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.True(fired > 0, $"the signal never fired: {stepped.ToJsonString()}");
        Assert.Equal(1000, Assert.Single(stepped["args"]!.AsArray())!.GetValue<int>());
        Assert.False(stepped.ContainsKey("last"), stepped.ToJsonString());
        Assert.Equal(fired, stepped["frame"]!.GetValue<long>());
        Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.True(paused, "the game is still paused on the met frame");
        Assert.Equal(read, later);
        Assert.InRange(stepped["processFrames"]!.GetValue<int>(), 2, 999);
        Assert.Equal("done", (await RunAsync($"return {Probe}.state")).GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepUntilAPhysicsConditionStopsOnTheTickItMet()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await FrameAsync("pause");
        long before = await ReadIntAsync("physics_ticks");
        long target = before + 5;

        JsonObject stepped = await FrameAsync(
            "step",
            options: new StepOptions(Unit: "physics", Until: new WaitCondition(Node: "TimeProbe", Expression: $"node.physics_ticks >= {target}"))
        );
        long read = await InspectIntAsync("physics_ticks", cancellation);
        await Task.Delay(300, cancellation);
        long later = await InspectIntAsync("physics_ticks", cancellation);

        Assert.True(stepped["met"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.Equal(5, stepped["physicsFrames"]!.GetValue<int>());
        Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.Equal(before + stepped["physicsFrames"]!.GetValue<int>(), read);
        Assert.Equal(read, later);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStepUntilOnAUiChangedWithNoBaselineIsRefusedAndLeavesTheClockFree()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await FrameAsync("pause");

        McpException refused = await Assert.ThrowsAsync<McpException>(() => FrameAsync("step", options: new StepOptions(Until: UiChangedCondition)));
        JsonObject stepped = await FrameAsync("step", count: 1);

        Assert.Contains(NoUiBaseline, refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, stepped["processFrames"]!.GetValue<int>());
        Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStepUntilOnAMissingSignalIsRefusedAndLeavesTheClockFree()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await FrameAsync("pause");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            FrameAsync("step", options: new StepOptions(Until: new WaitCondition(Node: "TimeProbe", Signal: "nope")))
        );
        JsonObject stepped = await FrameAsync("step", count: 1);

        Assert.Contains("/root/TimeProbe has no signal 'nope'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, stepped["processFrames"]!.GetValue<int>());
        Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepUntilWithScreenshotCapturesTheMetFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await FrameAsync("pause");
        long target = await ReadIntAsync("process_frames") + 3;

        List<ContentBlock> blocks =
        [
            .. await _tools.FrameControlAsync(
                "step",
                null,
                null,
                new StepOptions(Screenshot: true, Until: FramesReach(target)),
                cancellationToken: cancellation
            ),
        ];
        JsonNode reply = JsonNode.Parse(Text(blocks))!;
        string path = reply["screenshot"]!["path"]!.GetValue<string>();
        JsonNode pixel = await RunAsync(
            $"var c := Image.load_from_file(\"{path.Replace('\\', '/')}\").get_pixel(600, 310)\n\treturn [c.r8, c.g8, c.b8]"
        );

        // ticker.gd paints the Swatch Color8((process_frames % 16) * 16, 0, 0) each frame, so the met frame shows target.
        long expectedRed = target % 16 * 16;
        Assert.True(reply["met"]!.GetValue<bool>(), reply.ToJsonString());
        Assert.Equal(3, reply["processFrames"]!.GetValue<int>());
        Assert.InRange(pixel[0]!.GetValue<int>(), expectedRed - 3, expectedRed + 3);
        Assert.Equal(target, await InspectIntAsync("process_frames", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWatchAroundAStepUntilEndsAtTheStepsFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await FrameAsync("pause");
        long target = await ReadIntAsync("process_frames") + 4;

        WatchTracks tracks = new([new WatchPropertyTrack("TimeProbe", "process_frames")]);
        JsonNode.Parse(await _tools.WatchAsync("start", tracks, cancellationToken: cancellation));
        await Task.Delay(200, cancellation);
        JsonObject stepped = await FrameAsync("step", options: new StepOptions(Until: FramesReach(target)));
        await Task.Delay(100, cancellation);
        JsonObject timeline = JsonNode.Parse(await _tools.WatchAsync("stop", cancellationToken: cancellation))!.AsObject();

        Assert.True(stepped["met"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.Equal(4, stepped["processFrames"]!.GetValue<int>());
        Assert.Equal(4, timeline["frames"]!.GetValue<int>());
        JsonArray points = timeline["tracks"]![0]!["points"]!.AsArray();
        long lastFrame = points[^1]![0]!.GetValue<long>();
        Assert.Equal(stepped["frame"]!.GetValue<long>(), timeline["startFrame"]!.GetValue<long>() + lastFrame);
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
    public async Task AnEdgeWaitOnAnAlreadyTruePropertyWaitsForTheNextRise()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        // state reads done now, idle from 1 s, when record_then logs the fall's frame as then_frame, and done again at 2 s, when
        // arm adds Armed just before it sets state: a then that counts TimeProbe's children sees Armed only at that second rise.
        await RunAsync(
            $"var probe: Node = {Probe}\n\tprobe.state = \"done\"\n\tvar fall: Signal = scene_tree.create_timer(1.0).timeout\n\t"
                + "fall.connect(probe.set.bind(\"state\", \"idle\"))\n\tfall.connect(probe.record_then)\n\tprobe.arm(2000)\n\treturn true"
        );
        WaitOptions options = new(Edge: true, Then: new WaitThen(Call: new MethodCall("TimeProbe", "get_child_count")));

        IEnumerable<ContentBlock> blocks = await _tools.WaitForAsync(
            new WaitCondition(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\"")),
            5000,
            options,
            cancellationToken: cancellation
        );
        JsonObject waited = JsonNode.Parse(Text(blocks))!.AsObject();

        // The wait began in the frame its met frame less its frames names, which must come before the fall, when state still
        // read done, so a wait that ignored edge would have been met there; an edge wait is met only after the fall.
        long fell = await ReadIntAsync("then_frame");
        long metFrame = waited["then"]!["frame"]!.GetValue<long>();
        long began = metFrame - waited["frames"]!.GetValue<long>();
        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(began <= fell, $"began {began}, fell {fell}: {waited.ToJsonString()}");
        Assert.True(metFrame > fell, $"met {metFrame}, fell {fell}: {waited.ToJsonString()}");
        Assert.Equal(2, waited["then"]!["call"]!["value"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnEdgeScreenshotWaitMeetsARiseBeforeItsFirstDraw()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        // The bridge reads a request in its own _process, before TimeProbe's, and checks a screenshot wait at each frame's draw,
        // after TimeProbe's, so the engine's frame count less TimeProbe's reads n at the request and n - 1 at every draw: the
        // condition is false when the wait starts and true from its first draw on, a rise inside the wait's first frame.
        await RunAsync($"{Probe}.n = Engine.get_process_frames() - {Probe}.process_frames\n\treturn true");

        IEnumerable<ContentBlock> blocks = await _tools.WaitForAsync(
            new WaitCondition(Node: "TimeProbe", Expression: "Engine.get_process_frames() - node.process_frames < node.n"),
            2000,
            new WaitOptions(Screenshot: true, Edge: true),
            cancellationToken: cancellation
        );
        JsonObject waited = JsonNode.Parse(Text(blocks))!.AsObject();

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(File.Exists(waited["screenshot"]!["path"]!.GetValue<string>()), waited.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnEdgeWaitOnAPropertyTrueThroughoutTimesOut()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await RunAsync($"{Probe}.state = \"done\"\n\treturn true");

        IEnumerable<ContentBlock> blocks = await _tools.WaitForAsync(
            new WaitCondition(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\"")),
            500,
            new WaitOptions(Edge: true),
            cancellationToken: cancellation
        );
        JsonObject waited = JsonNode.Parse(Text(blocks))!.AsObject();

        Assert.False(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal("done", waited["last"]!.GetValue<string>());
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

    // Armed is added by arm(300), so [0] fails on every check before then: each counts as not met.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnExpressionMetAfterFailedChecksCountsThem()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        await ArmAsync(300);

        const string Expression =
            "node.find_children('Armed', '', false, false).size() > 0 and node.find_children('Armed', '', false, false)[0].name == 'Armed'";
        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Expression: Expression));

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(waited["failedChecks"]?["count"]?.GetValue<int>() >= 1, waited.ToJsonString());
        Assert.Equal("Invalid index of type int for base type Array", waited["failedChecks"]?["error"]?.GetValue<string>());
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
    public async Task CaptureFramesTakesEachPointInOneCall()
    {
        JsonObject captured = await CaptureFramesAsync([0.1, 0.3, 0.3], null, TestContext.Current.CancellationToken);

        JsonArray points = captured["points"]!.AsArray();
        Assert.Equal(3, points.Count);
        Assert.Equal([0.1, 0.3, 0.3], points.Select(point => point!["at"]!.GetValue<double>()));
        Assert.Equal(points[1]!["file"]!.GetValue<int>(), points[2]!["file"]!.GetValue<int>());
        Assert.Equal(2, captured["files"]!.AsArray().Count);
        Assert.Equal(1, captured["shared"]!.GetValue<int>());
        Assert.True(captured["width"]!.GetValue<int>() > 0, captured.ToJsonString());
        Assert.True(captured["height"]!.GetValue<int>() > 0, captured.ToJsonString());
        Assert.All(FramePaths(captured), path => Assert.True(File.Exists(path), captured.ToJsonString()));
        Assert.All(
            points,
            point => Assert.True(point!["gameSeconds"]!.GetValue<double>() >= point["at"]!.GetValue<double>(), captured.ToJsonString())
        );
        long[] numbers = [.. points.Select(point => point!["frame"]!.GetValue<long>())];
        Assert.True(numbers.Zip(numbers.Skip(1)).All(pair => pair.First <= pair.Second), captured.ToJsonString());
        Assert.False(captured.ContainsKey("stopped"), captured.ToJsonString());
        Assert.False(captured.ContainsKey("missed"), captured.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CaptureFramesEveryForTakesEachPoint()
    {
        JsonObject captured = await CaptureFramesAsync(null, new CaptureFramesOptions(0.1, 0.3), TestContext.Current.CancellationToken);

        JsonArray points = captured["points"]!.AsArray();
        Assert.Equal(3, points.Count);
        Assert.All(FramePaths(captured), path => Assert.True(File.Exists(path), captured.ToJsonString()));
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

            JsonNode point = Assert.Single(captured["points"]!.AsArray())!;
            Assert.True(point["gameSeconds"]!.GetValue<double>() >= 8.0, captured.ToJsonString());
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

            JsonNode point = Assert.Single(captured["points"]!.AsArray())!;
            Assert.Equal(0.05, point["at"]!.GetValue<double>());
            Assert.True(captured["stopped"]!.GetValue<bool>(), captured.ToJsonString());
            Assert.Equal([5.0], captured["missed"]!.AsArray().Select(point => point!.GetValue<double>()));
        }
        finally
        {
            await FrameAsync("time_scale", scale: 1);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CaptureFramesCountsFromTheCall()
    {
        await AddTimeProbeAsync(TestContext.Current.CancellationToken);
        CaptureFramesOptions options = new(Call: new MethodCall("TimeProbe", "start_clock"));

        JsonObject captured = await CaptureFramesAsync([0.3], options, TestContext.Current.CancellationToken);

        // start_clock sums the probe's own delta from the frame it is called in. A capture whose clock starts in that same
        // frame has summed the same deltas at the frame it grabbed; a call a round trip ahead of the clock puts the probe's
        // sum ahead by those frames' delta, and a call after the clock's first frame puts it behind.
        JsonNode point = Assert.Single(captured["points"]!.AsArray())!;
        long grabbed = point["frame"]!.GetValue<long>();
        long started = captured["call"]!["value"]!.GetValue<long>();
        JsonNode? probeSeconds = await RunAsync($"return {Probe}.clock_at({grabbed})");
        Assert.True(started <= grabbed, captured.ToJsonString());
        Assert.NotNull(probeSeconds);
        Assert.Equal(point["gameSeconds"]!.GetValue<double>(), probeSeconds.GetValue<double>(), 6);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CaptureFramesWithAStartCountsFromTheMetFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await ArmAsync(500);
        CaptureStart start = new(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\""), TimeoutMs: 5000);

        JsonObject captured = await CaptureFramesAsync([0, 0.1], new CaptureFramesOptions(Start: start), cancellation);

        // arm(500) sets state to done half a second after the call, so the start is met in a frame well after the capture's
        // request; the point at 0 is due in the clock's first frame, the met frame itself. A capture counting from its call
        // would take it about half a second before the start.
        JsonArray points = captured["points"]!.AsArray();
        long metFrame = captured["start"]!["frame"]!.GetValue<long>();
        Assert.True(captured["start"]!["met"]!.GetValue<bool>(), captured.ToJsonString());
        Assert.Equal(2, points.Count);
        Assert.Equal(metFrame, points[0]!["frame"]!.GetValue<long>());
        Assert.True(points[1]!["frame"]!.GetValue<long>() > metFrame, captured.ToJsonString());
        Assert.False(captured.ContainsKey("stopped"), captured.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CaptureFramesWithAStartMetAtTheRequestCountsFromTheNextFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        CaptureStart start = new(Node: "TimeProbe", Exists: true);

        JsonObject captured = await CaptureFramesAsync([0], new CaptureFramesOptions(Start: start), cancellation);

        // TimeProbe is there when the request comes, so the start is met at its first check, in the request's frame, and the
        // clock starts at the next process frame, as a capture without a start does.
        JsonNode point = Assert.Single(captured["points"]!.AsArray())!;
        Assert.True(captured["start"]!["met"]!.GetValue<bool>(), captured.ToJsonString());
        Assert.Equal(captured["start"]!["frame"]!.GetValue<long>() + 1, point["frame"]!.GetValue<long>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACaptureWhoseStartTimesOutStopsWithNoFrames()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        CaptureStart start = new(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\""), TimeoutMs: 300);

        JsonObject captured = await CaptureFramesAsync([0.05], new CaptureFramesOptions(Start: start), cancellation);

        Assert.True(captured["stopped"]!.GetValue<bool>(), captured.ToJsonString());
        Assert.Empty(captured["points"]!.AsArray());
        Assert.Empty(captured["files"]!.AsArray());
        Assert.Equal([0.05], captured["missed"]!.AsArray().Select(point => point!.GetValue<double>()));
        Assert.False(captured["start"]!["met"]!.GetValue<bool>(), captured.ToJsonString());
        Assert.Equal("idle", captured["start"]!["last"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACaptureStartsThenTimeScaleMakesThePointsCountAtTheNewScale()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await ArmAsync(300);
        CaptureStart start = new(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\""), Then: new WaitThen(TimeScale: 8));
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            JsonObject captured = await CaptureFramesAsync([4.0], new CaptureFramesOptions(Start: start), cancellation);
            watch.Stop();

            // At time_scale 8 from the met frame on, 4 s of game time take half a second; at 1 they take at least 4 s.
            JsonNode point = Assert.Single(captured["points"]!.AsArray())!;
            Assert.True(point["gameSeconds"]!.GetValue<double>() >= 4.0, captured.ToJsonString());
            Assert.Equal(8, captured["start"]!["then"]!["timeScale"]!.GetValue<double>());
            Assert.True(
                watch.Elapsed < TimeSpan.FromSeconds(4),
                $"4 s of game time after a start setting time_scale 8 took {watch.Elapsed}; at time_scale 1 it takes at least 4 s"
            );
        }
        finally
        {
            await FrameAsync("time_scale", scale: 1);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForGameMsWithACallCountsFromTheCall()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        WaitOptions options = new(Call: new MethodCall("TimeProbe", "start_clock", Json("[300]")));

        // start_clock(300) pauses the tree in the frame the probe's own sum from the call reaches 300 ms, after the bridge's
        // check of that frame. A wait whose clock starts in the call's frame has the same sum there and is met; one started a
        // round trip later is behind when the pause stops its count, and times out.
        IEnumerable<ContentBlock> blocks = await _tools.WaitForAsync(new WaitCondition(GameMs: 300), 5000, options, cancellationToken: cancellation);
        JsonObject waited = JsonNode.Parse(Text(blocks))!.AsObject();

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(waited["value"]!.GetValue<int>() >= 300, waited.ToJsonString());
        Assert.True(waited["call"]!["value"]!.GetValue<long>() > 0, waited.ToJsonString());
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AnExpressionWaitWithACallIsMetOnTheCallsEffectWithItsResult(bool edge, bool screenshot)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        // then_frame is -1 until record_then sets it to the frame it runs in, so the condition is false before the call and
        // true from the call on. The wait's first check follows the call in the call's frame, and is met there with frames 0;
        // an edge wait's baseline check comes before the call, finds it false, and makes that first check a rise. A screenshot
        // wait checks at that frame's draw instead, and its poll reads the met draw a frame later, with frames 1.
        WaitOptions options = new(Screenshot: screenshot, Call: new MethodCall("TimeProbe", "record_then"), Edge: edge);

        IEnumerable<ContentBlock> blocks = await _tools.WaitForAsync(
            new WaitCondition(Node: "TimeProbe", Expression: "node.then_frame >= 0"),
            3000,
            options,
            cancellationToken: cancellation
        );
        JsonObject waited = JsonNode.Parse(Text(blocks))!.AsObject();

        long recorded = await ReadIntAsync("then_frame");
        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal(screenshot ? 1 : 0, waited["frames"]!.GetValue<int>());
        Assert.Equal(recorded, waited["call"]!["value"]!.GetValue<long>());
        Assert.Equal(screenshot, waited.ContainsKey("screenshot"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnExpressionWaitWhoseCallErrorsAnswersTheErrorAndNoWait()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);

        // The condition holds at once, so a wait that went on past the failed call would be met rather than answer the error.
        McpException wait = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(
                new WaitCondition(Expression: "true"),
                3000,
                new WaitOptions(Call: new MethodCall("TimeProbe", "fail_on_null")),
                cancellationToken: cancellation
            )
        );

        Assert.Contains("queue_free", wait.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACallThatErrorsAnswersTheErrorAndNoFrames()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        MethodCall failing = new("TimeProbe", "fail_on_null");

        McpException capture = await Assert.ThrowsAsync<McpException>(() =>
            CaptureFramesAsync([0.1], new CaptureFramesOptions(Call: failing), cancellation)
        );
        McpException wait = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(new WaitCondition(Frames: 3), null, new WaitOptions(Call: failing), cancellationToken: cancellation)
        );

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            CaptureFramesAsync([0.1], new CaptureFramesOptions(Call: new MethodCall("TimeProbe", "no_such_method")), cancellation)
        );

        Assert.Contains("queue_free", capture.Message, StringComparison.Ordinal);
        Assert.Contains("queue_free", wait.Message, StringComparison.Ordinal);
        Assert.Contains("Node '/root/TimeProbe' has no method 'no_such_method'.", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForExpressionWithThenRecordsTheMetFrameAndSetsTheScale()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await RunAsync($"{Probe}.n = {Probe}.process_frames + 2\n\treturn true");

        JsonObject waited = JsonNode
            .Parse(
                Text(
                    await _tools.WaitForAsync(
                        new WaitCondition(Node: "TimeProbe", Expression: "node.process_frames >= node.n"),
                        null,
                        new WaitOptions(Then: new WaitThen(Call: new MethodCall("TimeProbe", "record_then"), TimeScale: 0.5)),
                        cancellationToken: cancellation
                    )
                )
            )!
            .AsObject();

        long recorded = (await RunAsync($"return {Probe}.then_frame")).GetValue<long>();
        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal(recorded, waited["then"]!["frame"]!.GetValue<long>());
        Assert.Equal(1.0, (await RunAsync($"return {Probe}.then_scale")).GetValue<double>());
        Assert.Equal(0.5, waited["then"]!["timeScale"]!.GetValue<double>());
        Assert.Equal(0.5, (await RunAsync("return Engine.time_scale")).GetValue<double>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForExpressionWithThenAndScreenshotCapturesTheMetFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await RunAsync($"{Probe}.n = {Probe}.process_frames + 2\n\treturn true");

        List<ContentBlock> blocks =
        [
            .. await _tools.WaitForAsync(
                new WaitCondition(Node: "TimeProbe", Expression: "node.process_frames >= node.n"),
                null,
                new WaitOptions(Screenshot: true, Then: new WaitThen(Call: new MethodCall("TimeProbe", "record_then"), TimeScale: 0.5)),
                cancellationToken: cancellation
            ),
        ];
        JsonObject waited = JsonNode.Parse(Text(blocks))!.AsObject();

        // The then runs inside the met frame's draw check, so then.frame, the frame record_then read and the captured draw are
        // the one frame.
        long recorded = (await RunAsync($"return {Probe}.then_frame")).GetValue<long>();
        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal(recorded, waited["then"]!["frame"]!.GetValue<long>());
        Assert.Null(waited["warning"]);
        Assert.True(File.Exists(waited["screenshot"]!["path"]!.GetValue<string>()), waited.ToJsonString());
        Assert.Equal("image/png", Assert.Single(blocks.OfType<ImageContentBlock>()).MimeType);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWaitThatTimesOutRunsNoThen()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await RunAsync($"{Probe}.then_frame = -1\n\treturn true");

        JsonObject waited = JsonNode
            .Parse(
                Text(
                    await _tools.WaitForAsync(
                        new WaitCondition(Expression: "false"),
                        300,
                        new WaitOptions(Then: new WaitThen(Call: new MethodCall("TimeProbe", "record_then"), TimeScale: 0.5)),
                        cancellationToken: cancellation
                    )
                )
            )!
            .AsObject();

        Assert.False(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.False(waited.ContainsKey("then"), waited.ToJsonString());
        Assert.Equal(-1, (await RunAsync($"return {Probe}.then_frame")).GetValue<int>());
        Assert.Equal(1.0, (await RunAsync("return Engine.time_scale")).GetValue<double>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AThenCallOnAMissingMethodFailsTheWaitAndLeavesTheScale()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.WaitForAsync(
                new WaitCondition(Expression: "true"),
                null,
                new WaitOptions(Then: new WaitThen(Call: new MethodCall("TimeProbe", "no_such_method"), TimeScale: 0.5)),
                cancellationToken: cancellation
            )
        );

        Assert.Contains(
            "but then.call failed, so timeScale was not set: Node '/root/TimeProbe' has no method 'no_such_method'.",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(1.0, (await RunAsync("return Engine.time_scale")).GetValue<double>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForThenPauseHoldsTheMetFrameForLaterReads()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await RunAsync($"{Probe}.n = {Probe}.process_frames + 3\n\treturn true");
        long n = (await RunAsync($"return {Probe}.n")).GetValue<long>();

        JsonObject waited = JsonNode
            .Parse(
                Text(
                    await _tools.WaitForAsync(FramesReach(n), null, new WaitOptions(Then: new WaitThen(Pause: true)), cancellationToken: cancellation)
                )
            )!
            .AsObject();

        long met = await ReadIntAsync("process_frames");
        await Task.Delay(300, cancellation);
        long later = await ReadIntAsync("process_frames");
        JsonObject resumed = await FrameAsync("resume");
        await Task.Delay(300, cancellation);
        long running = await ReadIntAsync("process_frames");

        // The pause lands in the met frame, before any node's _process, so the counter still reads the met frame's value on a
        // read made 300 ms later: it cannot drift while the game is paused. A resume lets it run on.
        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(waited["then"]!["paused"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal(n, met);
        Assert.Equal(met, later);
        Assert.False(resumed["paused"]!.GetValue<bool>(), resumed.ToJsonString());
        Assert.True(running > later, $"the probe did not run again after resume: {later} then {running}");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACheckOnceWaitWithThenPauseHoldsTheMetFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);

        JsonObject waited = JsonNode
            .Parse(
                Text(
                    await _tools.WaitForAsync(
                        new WaitCondition(Expression: "true"),
                        0,
                        new WaitOptions(Then: new WaitThen(Pause: true)),
                        cancellationToken: cancellation
                    )
                )
            )!
            .AsObject();

        long met = await ReadIntAsync("process_frames");
        await Task.Delay(300, cancellation);
        long later = await ReadIntAsync("process_frames");
        await FrameAsync("resume");

        // A check-once wait is met in the frame after it starts and pauses there, like a waiting one, so the counter still
        // reads the met frame's value on a read made 300 ms later: it cannot drift while the game is paused.
        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal(0, waited["frames"]!.GetValue<int>());
        Assert.True(waited["then"]!["paused"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal(met, later);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForThenPauseWithScreenshotHoldsTheDrawnFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        await RunAsync($"{Probe}.n = {Probe}.process_frames + 3\n\treturn true");
        long n = (await RunAsync($"return {Probe}.n")).GetValue<long>();

        List<ContentBlock> blocks =
        [
            .. await _tools.WaitForAsync(
                FramesReach(n),
                null,
                new WaitOptions(Screenshot: true, Then: new WaitThen(Pause: true)),
                cancellationToken: cancellation
            ),
        ];
        JsonObject waited = JsonNode.Parse(Text(blocks))!.AsObject();
        string path = waited["screenshot"]!["path"]!.GetValue<string>();
        JsonNode pixel = await RunAsync(
            $"var c := Image.load_from_file(\"{path.Replace('\\', '/')}\").get_pixel(600, 310)\n\treturn [c.r8, c.g8, c.b8]"
        );
        long met = await ReadIntAsync("process_frames");
        await Task.Delay(300, cancellation);
        long later = await ReadIntAsync("process_frames");
        await FrameAsync("resume");

        // The wait checks at each frame's draw, and a frame the window does not draw is not checked, so the met draw can be a
        // frame after the condition first held: the drawn frame is at or after n. The pause holds exactly the frame it
        // checked, so the Swatch shows the counter the pause froze, and that counter cannot move on.
        Assert.True(waited["then"]!["paused"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(met >= n, $"the drawn frame {met} is before the target {n}");
        Assert.InRange(pixel[0]!.GetValue<int>(), (int)(met % 16 * 16) - 3, (int)(met % 16 * 16) + 3);
        Assert.Equal(met, later);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForThenPauseAndTimeScaleLeavesTheScaleSetWhilePaused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);
        try
        {
            JsonObject waited = JsonNode
                .Parse(
                    Text(
                        await _tools.WaitForAsync(
                            new WaitCondition(Expression: "true"),
                            null,
                            new WaitOptions(Then: new WaitThen(TimeScale: 0.5, Pause: true)),
                            cancellationToken: cancellation
                        )
                    )
                )!
                .AsObject();

            JsonObject resumed = await FrameAsync("resume");

            // The scale is set before the pause and holds through it, so resuming does not bring it back to 1.
            Assert.True(waited["then"]!["paused"]!.GetValue<bool>(), waited.ToJsonString());
            Assert.Equal(0.5, waited["then"]!["timeScale"]!.GetValue<double>());
            Assert.Equal(0.5, resumed["timeScale"]!.GetValue<double>());
            Assert.False(resumed["paused"]!.GetValue<bool>(), resumed.ToJsonString());
        }
        finally
        {
            await FrameAsync("time_scale", scale: 1);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForThenPauseOnATimeoutLeavesTheGameRunning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddTimeProbeAsync(cancellation);

        JsonObject waited = JsonNode
            .Parse(
                Text(
                    await _tools.WaitForAsync(
                        new WaitCondition(Expression: "false"),
                        300,
                        new WaitOptions(Then: new WaitThen(Pause: true)),
                        cancellationToken: cancellation
                    )
                )
            )!
            .AsObject();
        long before = await ReadIntAsync("process_frames");
        await Task.Delay(200, cancellation);
        long after = await ReadIntAsync("process_frames");

        Assert.False(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.False(waited.ContainsKey("then"), waited.ToJsonString());
        Assert.True(after > before, $"a timed-out wait with pause stopped the game: {before} then {after}");
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

    /// <summary>The files a capture took: its folder joined with each distinct name, as the compact result names them.</summary>
    private static IEnumerable<string> FramePaths(JsonObject captured) =>
        captured["files"]!.AsArray().Select(name => Path.Combine(captured["folder"]!.GetValue<string>(), name!.GetValue<string>()));

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

    /// <summary>TimeProbe's property read through inspect_node, a read tool that runs no game code.</summary>
    private async Task<long> InspectIntAsync(string property, CancellationToken cancellationToken) =>
        JsonNode.Parse(await _tools.InspectNodeAsync("TimeProbe", [property], cancellationToken: cancellationToken))!["properties"]![
            property
        ]!.GetValue<long>();

    /// <summary>A step's until met once TimeProbe has processed <paramref name="target"/> frames.</summary>
    private static WaitCondition FramesReach(long target) => new(Node: "TimeProbe", Expression: $"node.process_frames >= {target}");

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
