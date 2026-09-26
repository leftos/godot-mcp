using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// frame_control and wait_for against the InputProbe with time_probe.tscn added under the root: TimeProbe, a pausable node
/// counting its process frames and physics ticks and summing their delta, a Swatch repainted from the frame count each frame,
/// and arm(ms), which after ms adds a child Armed, sets state to "done" and emits fired(ms).
/// </summary>
public sealed class TimeTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Probe = "scene_tree.root.get_node(\"TimeProbe\")";
    private const string PausedRefusal = "The game is paused, so only a signal wait can be met; resume or step it first.";
    private const string SteppingRefusal = "A step is still running on this game; wait for its reply before pause, resume or another step.";
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly RuntimeTools _tools;

    public TimeTests() => _tools = new RuntimeTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task PauseStopsPausableNodesButBridgeStillAnswers()
    {
        await StartAsync(TestContext.Current.CancellationToken);

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
        await StartAsync(TestContext.Current.CancellationToken);
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
        await StartAsync(TestContext.Current.CancellationToken);
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
        await StartAsync(TestContext.Current.CancellationToken);

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
        await StartAsync(TestContext.Current.CancellationToken);
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
        await StartAsync(TestContext.Current.CancellationToken);
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
        await StartAsync(TestContext.Current.CancellationToken);
        await ArmAsync(300);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "Armed", Exists: true));

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(waited["value"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(waited["frames"]!.GetValue<int>() > 0, waited.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForPropertyEquals()
    {
        await StartAsync(TestContext.Current.CancellationToken);
        await ArmAsync(300);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\"")));

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal("done", waited["value"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForSignalReturnsArgs()
    {
        await StartAsync(TestContext.Current.CancellationToken);
        await ArmAsync(500);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Signal: "fired"));

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal("[500]", waited["args"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForSignalWhilePaused()
    {
        await StartAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        await ArmAsync(300);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Signal: "fired"));
        bool stillPaused = (await RunAsync("return scene_tree.paused")).GetValue<bool>();

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.True(stillPaused);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForExpression()
    {
        await StartAsync(TestContext.Current.CancellationToken);
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
        await StartAsync(TestContext.Current.CancellationToken);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => WaitAsync(new WaitCondition(Expression: "1 +")));

        Assert.StartsWith("wait_for failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("the expression does not parse: ", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForTimesOutWithLastValue()
    {
        await StartAsync(TestContext.Current.CancellationToken);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\"")), 300);

        Assert.False(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal("idle", waited["last"]!.GetValue<string>());
        Assert.True(waited["elapsedMs"]!.GetValue<double>() >= 300, waited.ToJsonString());
        Assert.True(waited["frames"]!.GetValue<int>() > 0, waited.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForPropertyWhilePausedIsRefused()
    {
        await StartAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "state", EqualsValue: Json("\"done\"")))
        );

        Assert.Contains(PausedRefusal, refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStepWhileAnotherRunsIsRefusedAndTheFirstCountsTrue()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await StartAsync(cancellation);
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
        await StartAsync(TestContext.Current.CancellationToken);
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
        await StartAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        await RunAsync($"{Probe}.pause_after(300)\n\treturn true");

        McpException failed = await Assert.ThrowsAsync<McpException>(() => FrameAsync("step", count: 1000));

        Assert.Matches(@"The game changed its own pause state during the step, after \d+ of 1000 frames\.", failed.Message);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StepPhysicsWithScreenshotShowsTheFrameAfterTheLastTick()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await StartAsync(cancellation);
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
        await StartAsync(TestContext.Current.CancellationToken);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Signal: "fired"), 300);

        Assert.False(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.False(waited.ContainsKey("last"), waited.ToJsonString());
        Assert.False(waited.ContainsKey("args"), waited.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForSignalOnANodeFreedMidWaitTimesOutCleanly()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await StartAsync(cancellation);

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
        await StartAsync(TestContext.Current.CancellationToken);

        JsonObject waited = await WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "n", EqualsValue: Json("3")));

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal(3, waited["value"]!.GetValue<int>());
        Assert.Equal(0, waited["frames"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForAPropertyTheNodeLacksFails()
    {
        await StartAsync(TestContext.Current.CancellationToken);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            WaitAsync(new WaitCondition(Node: "TimeProbe", Property: "nope:x", EqualsValue: Json("1")))
        );

        Assert.Contains("'/root/TimeProbe' has no property 'nope'.", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Launches the probe and adds time_probe.tscn under the root as TimeProbe.</summary>
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        await _harness.Sessions.LaunchAsync(new LaunchRequest(_probe.Directory, null, [], [], true, false), null, cancellationToken);
        await RunAsync("var probe: Node = load(\"res://time_probe.tscn\").instantiate()\n\tscene_tree.root.add_child(probe)\n\treturn probe");
    }

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

    private async Task<JsonObject> WaitAsync(WaitCondition condition, int timeoutMs = 10_000) =>
        JsonNode.Parse(await _tools.WaitForAsync(condition, timeoutMs, cancellationToken: TestContext.Current.CancellationToken))!.AsObject();

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string Text(IEnumerable<ContentBlock> blocks) => string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text));
}
