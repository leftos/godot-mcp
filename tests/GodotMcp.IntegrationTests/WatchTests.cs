using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// watch against the InputProbe: a WatchMover Node2D added by run_script, whose go() tweens position:x from 0 to 100 over
/// 0.3 s and whose answer() returns 42, the TimeProbe scene, an OpenButton that shows a hidden panel, and nodes with user
/// signals and a WatchJumper that moves and emits moved in one _process once armed; monitors read a frame run_script
/// holds, the nodes it adds and a custom monitor it registers. One shared run, reset
/// before each test, after which a watch a failed test left behind is stopped; batch_drive runs on a server built over the
/// shared registry, as BatchTests builds it.
/// </summary>
public sealed class WatchTests : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string RunningRefusal = "A watch is already running on this game; end it with watch {action: \"stop\"} before starting another.";
    private const string MoverScript =
        "extends Node2D\\n\\n\\nfunc go() -> void:\\n\\tcreate_tween().tween_property(self, 'position:x', 100.0, 0.3)\\n\\n\\n"
        + "func answer() -> int:\\n\\treturn 42\\n";
    private const string JumperScript =
        "extends Node2D\\n\\nsignal moved\\n\\nvar armed := false\\nvar moved_delta := 0.0\\n\\n\\n"
        + "func _process(delta: float) -> void:\\n\\tif armed:\\n\\t\\tarmed = false\\n\\t\\tmoved_delta = delta\\n\\t\\t"
        + "position.x = 50.0\\n\\t\\tmoved.emit()\\n";

    // Once armed, adds a child named Kid three frames later, and with arm_and_raise also has the scene's Main raise its
    // probe error then, from the game's own _process. shout, for an expression to call, push_errors the first time.
    private const string GrowerScript =
        "extends Node\\n\\nvar frames_left := 0\\nvar raise := false\\nvar shouted := false\\n\\n\\n"
        + "func arm() -> void:\\n\\tframes_left = 3\\n\\n\\n"
        + "func arm_and_raise() -> void:\\n\\traise = true\\n\\tframes_left = 3\\n\\n\\n"
        + "func shout() -> int:\\n\\tif not shouted:\\n\\t\\tshouted = true\\n\\t\\tpush_error('grower shout')\\n\\treturn 1\\n\\n\\n"
        + "func _process(_delta: float) -> void:\\n\\tif frames_left <= 0:\\n\\t\\treturn\\n\\tframes_left -= 1\\n\\t"
        + "if frames_left == 0:\\n\\t\\tvar kid := Node.new()\\n\\t\\tkid.name = 'Kid'\\n\\t\\tadd_child(kid)\\n\\t\\t"
        + "if raise:\\n\\t\\t\\tget_tree().current_scene.probe_push_error()\\n";
    private readonly SharedProbeSession _shared;
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly RuntimeTools _tools;

    public WatchTests(SharedProbeSession shared)
    {
        _shared = shared;
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(shared.Sessions);
        services.AddSingleton(TestCSharp.Unused());
        services.AddMcpServer().WithToolsFromAssembly(typeof(RuntimeTools).Assembly);
        _services = services.BuildServiceProvider();
        McpServerOptions options = _services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        _server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), options, null, _services);
        _tools = new RuntimeTools(shared.Sessions, TestCSharp.Unused());
    }

    public async ValueTask InitializeAsync()
    {
        await _shared.ResetAsync(TestContext.Current.CancellationToken);
        try
        {
            await WatchAsync("stop", TestContext.Current.CancellationToken);
        }
        catch (McpException)
        {
            // No watch was left running: the usual case.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _services.DisposeAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARunFollowsAPropertyAndAnExpressionOverATweenItsCallStarts()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string mover = await AddMoverAsync();
        WatchTracks tracks = new(
            Properties: [new WatchPropertyTrack(mover, "position:x", MinDelta: 1)],
            Expressions: [new WatchExpressionTrack("far", "node.position.x > 50", Node: mover)]
        );

        JsonObject timeline = await WatchAsync(
            "run",
            cancellation,
            tracks,
            new WatchWindow(Frames: 60),
            new WatchOptions(Call: new MethodCall(mover, "go"))
        );

        Assert.Equal(60, Int(timeline["frames"]));
        Assert.False(timeline.ContainsKey("stopped"), timeline.ToJsonString());
        Assert.True(timeline.ContainsKey("call"), timeline.ToJsonString());
        JsonObject x = timeline["tracks"]![0]!.AsObject();
        Assert.Equal(0, x["first"]!.GetValue<double>());
        Assert.Equal(100, x["last"]!.GetValue<double>(), 3);
        Assert.Equal(100, x["max"]!.GetValue<double>(), 3);
        Assert.True(x["points"]!.AsArray().Count > 5, x.ToJsonString());
        JsonObject far = timeline["tracks"]![1]!.AsObject();
        Assert.Equal("far", far["name"]!.GetValue<string>());
        Assert.Equal(1, Int(far["changes"]));
        Assert.Equal([false, true], far["points"]!.AsArray().Select(point => point![2]!.GetValue<bool>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWatchStartedBeforeAClickSeesThePanelOpenWhenStopped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string panel = await AddOpenerAsync();

        JsonObject started = await WatchAsync("start", cancellation, new WatchTracks([new WatchPropertyTrack(panel, "visible")]));
        await _tools.ClickAsync(new InputTarget("OpenButton"), "left", false, cancellationToken: cancellation);
        await _tools.WaitForAsync(new WaitCondition(Frames: 3), cancellationToken: cancellation);
        JsonObject timeline = await WatchAsync("stop", cancellation);

        Assert.True(Int(started["startFrame"]) > 0, started.ToJsonString());
        Assert.Equal(panel, started["tracks"]![0]!["node"]!.GetValue<string>());
        Assert.Equal("stop", timeline["stopped"]!.GetValue<string>());
        JsonObject track = timeline["tracks"]![0]!.AsObject();
        Assert.Equal([false, true], track["points"]!.AsArray().Select(point => point![2]!.GetValue<bool>()));
        Assert.Equal(Int(started["startFrame"]), Int(timeline["startFrame"]));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWatchOnAPausedGameListsThePausedFramesAndSamplesTheSteppedOnes()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _tools.RunScriptAsync(
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t"
                + "var probe: Node = load(\"res://time_probe.tscn\").instantiate()\n\tscene_tree.root.add_child(probe)\n\treturn true\n",
            ScriptTimeoutMs,
            cancellationToken: cancellation
        );
        await _tools.FrameControlAsync("pause", cancellationToken: cancellation);

        await WatchAsync("start", cancellation, new WatchTracks([new WatchPropertyTrack("TimeProbe", "process_frames")]));
        await Task.Delay(200, cancellation);
        await _tools.FrameControlAsync("step", 3, cancellationToken: cancellation);
        await Task.Delay(100, cancellation);
        JsonObject timeline = await WatchAsync("stop", cancellation);
        await _tools.FrameControlAsync("resume", cancellationToken: cancellation);

        Assert.Equal(3, Int(timeline["frames"]));
        JsonArray paused = timeline["paused"]!.AsArray();
        Assert.Equal(0, Int(paused[0]![0]));
        Assert.True(paused.Count >= 2, $"paused before and after the step: {paused.ToJsonString()}");
        JsonObject track = timeline["tracks"]![0]!.AsObject();
        Assert.Equal(2, Int(track["changes"]));
        Assert.Equal(Int(track["first"]) + 2, Int(track["last"]));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARunIsABatchStep()
    {
        JsonObject args = new()
        {
            ["action"] = "run",
            ["tracks"] = new JsonObject
            {
                ["expressions"] = new JsonArray(new JsonObject { ["name"] = "ticking", ["expression"] = "Engine.get_process_frames() > 0" }),
            },
            ["window"] = new JsonObject { ["frames"] = 10 },
        };

        JsonObject batch = JsonNode
            .Parse(
                await _tools.BatchDriveAsync(
                    [new BatchStep(Tool: "watch", Args: args)],
                    _server,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )!
            .AsObject();

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        JsonObject result = batch["steps"]![0]!["result"]!.AsObject();
        Assert.Equal(10, Int(result["frames"]));
        Assert.True(result["tracks"]![0]!["first"]!.GetValue<bool>(), result.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStartWithACallAnswersTheCallsValue()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string mover = await AddMoverAsync();

        JsonObject started = await WatchAsync(
            "start",
            cancellation,
            new WatchTracks([new WatchPropertyTrack(mover, "position:x")]),
            new WatchWindow(Frames: 30),
            new WatchOptions(Call: new MethodCall(mover, "answer"))
        );
        JsonObject timeline = await WatchAsync("stop", cancellation);

        Assert.Equal(42, Int(started["call"]!["value"]));
        Assert.Equal(Int(started["startFrame"]), Int(timeline["startFrame"]));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ANodeFreedDuringTheWindowIsRecordedOnce()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string mover = await AddMoverAsync();

        await WatchAsync("start", cancellation, new WatchTracks([new WatchPropertyTrack(mover, "position:x")]));
        await RunAsync($"scene_tree.root.get_node(\"{mover}\").queue_free()\n\treturn true");
        await _tools.WaitForAsync(new WaitCondition(Frames: 3), cancellationToken: cancellation);
        JsonObject timeline = await WatchAsync("stop", cancellation);

        JsonObject track = timeline["tracks"]![0]!.AsObject();
        Assert.Equal("""{"$freed":true}""", track["last"]!.ToJsonString());
        Assert.Single(track["points"]!.AsArray(), point => point![2] is JsonObject marker && marker.ContainsKey("$freed"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnEngineErrorAnExpressionTrackRaisesIsNotInTheCallsErrors()
    {
        (JsonObject timeline, string grower) = await RunGrowerWatchAsync("arm", [], TestContext.Current.CancellationToken);

        Assert.Null(timeline["errors"]);
        AssertNullThenKid(timeline, grower);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnErrorGameCodeAnExpressionTrackCallsRaisesIsInTheCallsErrors()
    {
        (JsonObject timeline, string grower) = await RunGrowerWatchAsync("arm", ["node.shout()"], TestContext.Current.CancellationToken);

        JsonNode error = Assert.Single(timeline["errors"]!.AsArray())!;
        Assert.Equal("grower shout", error["message"]!.GetValue<string>());
        AssertNullThenKid(timeline, grower);
        Assert.Equal(1, Int(timeline["tracks"]![1]!["last"]));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TheGamesOwnErrorDuringAWatchWithAnExpressionTrackIsStillInTheCallsErrors()
    {
        (JsonObject timeline, string grower) = await RunGrowerWatchAsync("arm_and_raise", [], TestContext.Current.CancellationToken);

        JsonNode error = Assert.Single(timeline["errors"]!.AsArray())!;
        Assert.Equal("probe fixture error", error["message"]!.GetValue<string>());
        AssertNullThenKid(timeline, grower);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStopWithNoWatchIsRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => WatchAsync("stop", TestContext.Current.CancellationToken));

        Assert.Contains("No watch runs:", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASecondStartIsRefusedWhileOneRuns()
    {
        WatchTracks tracks = new(Expressions: [new WatchExpressionTrack("frame", "Engine.get_process_frames()")]);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await WatchAsync("start", cancellation, tracks);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => WatchAsync("start", cancellation, tracks));
        JsonObject timeline = await WatchAsync("stop", cancellation);

        Assert.Contains(RunningRefusal, refused.Message, StringComparison.Ordinal);
        Assert.Equal("stop", timeline["stopped"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SignalsFiredFromRunScriptAreRecordedWithNoneAndSixArguments()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string emitter = (
            await RunAsync(
                "var emitter := Node.new()\n\temitter.name = \"WatchEmitter\"\n\temitter.add_user_signal(\"ping\")\n\t"
                    + "emitter.add_user_signal(\"dealt\")\n\tscene_tree.root.add_child(emitter)\n\treturn str(emitter.get_path())"
            )
        ).GetValue<string>();
        WatchTracks tracks = new(
            Signals: [new WatchSignalTrack(Node: emitter, Signal: "ping"), new WatchSignalTrack(Node: emitter, Signal: "dealt")]
        );

        JsonObject started = await WatchAsync("start", cancellation, tracks);
        await RunAsync(
            $"var emitter: Node = scene_tree.root.get_node(\"{emitter}\")\n\temitter.emit_signal(\"ping\")\n\t"
                + "emitter.emit_signal(\"dealt\", 1, 2.5, \"ace\", Vector2(3, 4), emitter, [1, \"two\"])\n\treturn true"
        );
        JsonObject timeline = await WatchAsync("stop", cancellation);

        Assert.Equal([1, 1], started["signals"]!.AsArray().Select(track => Int(track!["connected"])));
        JsonArray events = timeline["events"]!.AsArray();
        Assert.Equal(["ping", "dealt"], events.Select(item => item![3]!.GetValue<string>()));
        Assert.Equal("[]", events[0]![4]!.ToJsonString());
        JsonArray dealt = events[1]![4]!.AsArray();
        Assert.Equal(6, dealt.Count);
        Assert.Equal((1, 2.5, "ace", 3), (Int(dealt[0]), dealt[1]!.GetValue<double>(), dealt[2]!.GetValue<string>(), Int(dealt[3]!["x"])));
        Assert.Contains(emitter, dealt[4]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(2, dealt[5]!.AsArray().Count);
        Assert.Equal(1, Int(timeline["eventCounts"]![$"{emitter}:dealt"]));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AGroupTrackRecordsEveryMemberWithTheSignalAndSkipsTheRest()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            "for index in 3:\n\t\tvar card := Node.new()\n\t\tcard.name = \"WatchCard%d\" % index\n\t\tif index < 2:\n\t\t\t"
                + "card.add_user_signal(\"dealt\")\n\t\tcard.add_to_group(\"watch_cards\")\n\t\tscene_tree.root.add_child(card)\n\t"
                + "return true"
        );

        JsonObject started = await WatchAsync(
            "start",
            cancellation,
            // WatchCard0 is also named by a node track first, so it is connected and counted once, under that track.
            new WatchTracks(
                Signals: [new WatchSignalTrack(Node: "WatchCard0", Signal: "dealt"), new WatchSignalTrack(Group: "watch_cards", Signal: "dealt")]
            )
        );
        await RunAsync(
            "scene_tree.root.get_node(\"WatchCard1\").emit_signal(\"dealt\")\n\t"
                + "scene_tree.root.get_node(\"WatchCard0\").emit_signal(\"dealt\")\n\treturn true"
        );
        JsonObject timeline = await WatchAsync("stop", cancellation);

        JsonArray signals = started["signals"]!.AsArray();
        Assert.Equal(
            ("/root/WatchCard0", "dealt", 1),
            (signals[0]!["node"]!.GetValue<string>(), signals[0]!["signal"]!.GetValue<string>(), Int(signals[0]!["connected"]))
        );
        Assert.Equal(
            ("watch_cards", "dealt", 1),
            (signals[1]!["group"]!.GetValue<string>(), signals[1]!["signal"]!.GetValue<string>(), Int(signals[1]!["connected"]))
        );
        Assert.Equal("/root/WatchCard2", started["skipped"]![0]!["node"]!.GetValue<string>());
        Assert.Equal(["/root/WatchCard1", "/root/WatchCard0"], timeline["events"]!.AsArray().Select(item => item![2]!.GetValue<string>()));
        Assert.Equal(1, Int(timeline["eventCounts"]!["/root/WatchCard0:dealt"]));
        Assert.Equal("no signal 'dealt'", timeline["skipped"]![0]!["reason"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASignalEmittedBesideAPropertyChangeIsStampedTheFrameBeforeTheChangeIsSampled()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string jumper = (
            await RunAsync(
                "var jumper := Node2D.new()\n\tjumper.name = \"WatchJumper\"\n\tvar script := GDScript.new()\n\t"
                    + $"script.source_code = \"{JumperScript}\"\n\tscript.reload()\n\tjumper.set_script(script)\n\t"
                    + "scene_tree.root.add_child(jumper)\n\treturn str(jumper.get_path())"
            )
        ).GetValue<string>();
        WatchTracks tracks = new(
            Properties: [new WatchPropertyTrack(jumper, "position:x")],
            Signals: [new WatchSignalTrack(Node: jumper, Signal: "moved")]
        );

        await WatchAsync("start", cancellation, tracks);
        await RunAsync($"scene_tree.root.get_node(\"{jumper}\").armed = true\n\treturn true");
        await _tools.WaitForAsync(new WaitCondition(Frames: 3), cancellationToken: cancellation);
        JsonObject timeline = await WatchAsync("stop", cancellation);

        JsonArray points = timeline["tracks"]![0]!["points"]!.AsArray();
        Assert.Equal(2, points.Count);
        Assert.Equal(50, points[1]![2]!.GetValue<double>());
        JsonArray moved = timeline["events"]![0]!.AsArray();
        Assert.Equal(Int(points[1]![0]) - 1, Int(moved[0]));
        // The signal's game time is its own frame's sample time, that frame's delta (which the jumper kept) before the
        // change's; each is floored to a millisecond.
        double deltaMs = (await RunAsync($"return scene_tree.root.get_node(\"{jumper}\").moved_delta")).GetValue<double>() * 1000;
        Assert.InRange(Int(points[1]![1]) - Int(moved[1]) - deltaMs, -1, 1);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task FrameMsCatchesAFrameRunScriptHoldsFor300Ms()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await WatchAsync("start", cancellation, new WatchTracks(Monitors: ["frame_ms"]));
        await RunAsync("OS.delay_msec(300)\n\treturn true");
        await _tools.WaitForAsync(new WaitCondition(Frames: 3), cancellationToken: cancellation);
        JsonObject timeline = await WatchAsync("stop", cancellation);

        JsonObject frameMs = timeline["monitors"]![0]!.AsObject();
        double max = frameMs["max"]!.GetValue<double>();
        Assert.True(max >= 290, frameMs.ToJsonString());
        JsonArray first = frameMs["spikes"]![0]!.AsArray();
        Assert.Equal((Int(frameMs["maxAt"]), max), (Int(first[0]), first[1]!.GetValue<double>()));
        Assert.True(Int(frameMs["over"]!["count"]) >= 1, frameMs.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ObjectNodesFollowsTheNodesRunScriptAdds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        double before = (await RunAsync("return Performance.get_monitor(Performance.OBJECT_NODE_COUNT)")).GetValue<double>();

        await WatchAsync("start", cancellation, new WatchTracks(Monitors: ["object/nodes"]));
        await RunAsync("for index in 5:\n\t\tscene_tree.root.add_child(Node.new())\n\treturn true");
        await _tools.WaitForAsync(new WaitCondition(Frames: 3), cancellationToken: cancellation);
        JsonObject timeline = await WatchAsync("stop", cancellation);

        JsonObject nodes = timeline["monitors"]![0]!.AsObject();
        Assert.True(nodes["max"]!.GetValue<double>() >= before + 5, $"{before} before: {nodes.ToJsonString()}");
        Assert.True(nodes.ContainsKey("spikes"), $"a series that changed lists its highest readings: {nodes.ToJsonString()}");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACustomMonitorTheGameRegistersIsReadByItsId()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync("Performance.add_custom_monitor(\"watch/frames\", Callable(Engine, \"get_process_frames\"))\n\treturn true");
        try
        {
            JsonObject started = await WatchAsync("start", cancellation, new WatchTracks(Monitors: ["watch/frames"]));
            await _tools.WaitForAsync(new WaitCondition(Frames: 5), cancellationToken: cancellation);
            JsonObject timeline = await WatchAsync("stop", cancellation);

            Assert.True(started["monitors"]![0]!["custom"]!.GetValue<bool>(), started.ToJsonString());
            JsonObject frames = timeline["monitors"]![0]!.AsObject();
            Assert.True(frames["custom"]!.GetValue<bool>(), frames.ToJsonString());
            Assert.True(Int(frames["samples"]) >= 5, frames.ToJsonString());
            Assert.True(frames["max"]!.GetValue<double>() > frames["p50"]!.GetValue<double>(), frames.ToJsonString());
        }
        finally
        {
            await RunAsync("Performance.remove_custom_monitor(\"watch/frames\")\n\treturn true");
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATimeMonitorIsRefusedPointingAtFrameMs()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            WatchAsync("start", TestContext.Current.CancellationToken, new WatchTracks(Monitors: ["time/fps"]))
        );

        Assert.StartsWith("'time/fps' is set once a second", refused.Message, StringComparison.Ordinal);
    }

    private async Task<JsonObject> WatchAsync(
        string action,
        CancellationToken cancellation,
        WatchTracks? tracks = null,
        WatchWindow? window = null,
        WatchOptions? options = null
    ) => JsonNode.Parse(await _tools.WatchAsync(action, tracks, window, options, cancellationToken: cancellation))!.AsObject();

    /// <summary>Adds WatchMover under the root and returns its path.</summary>
    private async Task<string> AddMoverAsync() =>
        (
            await RunAsync(
                "var mover := Node2D.new()\n\t"
                    + "mover.name = \"WatchMover\"\n\t"
                    + "var script := GDScript.new()\n\t"
                    + $"script.source_code = \"{MoverScript}\"\n\t"
                    + "script.reload()\n\t"
                    + "mover.set_script(script)\n\t"
                    + "scene_tree.root.add_child(mover)\n\t"
                    + "return str(mover.get_path())"
            )
        ).GetValue<string>();

    /// <summary>
    /// Adds WatchGrower and runs a 20-frame watch whose call is its <paramref name="arm"/> method, with an expression track
    /// reading its first child, which raises an engine error each frame before Kid is added, then a track on the grower for
    /// each of <paramref name="moreExpressions"/>; returns the timeline and the grower's path.
    /// </summary>
    private async Task<(JsonObject Timeline, string Grower)> RunGrowerWatchAsync(string arm, string[] moreExpressions, CancellationToken cancellation)
    {
        string grower = (
            await RunAsync(
                "var grower := Node.new()\n\tgrower.name = \"WatchGrower\"\n\tvar script := GDScript.new()\n\t"
                    + $"script.source_code = \"{GrowerScript}\"\n\tscript.reload()\n\tgrower.set_script(script)\n\t"
                    + "scene_tree.root.add_child(grower)\n\treturn str(grower.get_path())"
            )
        ).GetValue<string>();
        WatchTracks tracks = new(
            Expressions:
            [
                new WatchExpressionTrack("first", "node.get_child(0)", Node: grower),
                .. moreExpressions.Select((expression, index) => new WatchExpressionTrack($"more{index}", expression, Node: grower)),
            ]
        );

        JsonObject timeline = await WatchAsync(
            "run",
            cancellation,
            tracks,
            new WatchWindow(Frames: 20),
            new WatchOptions(Call: new MethodCall(grower, arm))
        );
        return (timeline, grower);
    }

    /// <summary>The track on the grower at <paramref name="grower"/> read null until Kid was added, then Kid, once.</summary>
    private static void AssertNullThenKid(JsonObject timeline, string grower)
    {
        JsonObject track = timeline["tracks"]![0]!.AsObject();
        JsonArray points = track["points"]!.AsArray();
        Assert.Equal(2, points.Count);
        Assert.Null(points[0]![2]);
        Assert.Equal($"{grower}/Kid", points[1]![2]!.GetValue<string>());
        Assert.True(Int(points[1]![0]) >= 2, track.ToJsonString());
    }

    /// <summary>Adds OpenButton, which shows a hidden OpenedPanel when pressed, and returns the panel's path.</summary>
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

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }

    // GDScript's JSON may write an integer as a float, so numbers are read as doubles.
    private static int Int(JsonNode? node) => (int)node!.GetValue<double>();
}
