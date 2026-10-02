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
/// 0.3 s and whose answer() returns 42, the TimeProbe scene, and an OpenButton that shows a hidden panel. One shared run, reset
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
