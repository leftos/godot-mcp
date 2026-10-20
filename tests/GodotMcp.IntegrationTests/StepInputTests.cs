using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// frame_control step's riding input (options.input) against the InputProbe with RidingProbe added under the root: a pausable
/// node counting its _process calls (frames) and noting the frames whose _process, and the ticks whose _physics_process, find
/// probe_jump just pressed, and showing RidingPanel, a hidden Panel, on a P key press in _input. One shared run, reset before
/// each test; the reset frees both nodes and unpauses the game.
/// </summary>
public sealed class StepInputTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Probe = "scene_tree.root.get_node(\"RidingProbe\")";

    // A frame rate low enough that every drawn frame runs at least one physics tick at the default 60 a second, so the tick
    // after a riding press falls in the next stepped frame.
    private const int CappedFps = 20;
    private const string ProbeSource =
        "extends Node\n\n"
        + "var frames: int = 0\n"
        + "var ticks_in_frame: int = 0\n"
        + "var process_hits: Array = []\n"
        + "var physics_hits: Array = []\n"
        + "var shown_frame: int = -1\n"
        + "var panel: Control\n\n\n"
        + "func _physics_process(_delta: float) -> void:\n"
        + "\tif Input.is_action_just_pressed(&\"probe_jump\"):\n"
        + "\t\tphysics_hits.append([frames, ticks_in_frame])\n"
        + "\tticks_in_frame += 1\n\n\n"
        + "func _process(_delta: float) -> void:\n"
        + "\tif Input.is_action_just_pressed(&\"probe_jump\"):\n"
        + "\t\tprocess_hits.append(frames)\n"
        + "\tframes += 1\n"
        + "\tticks_in_frame = 0\n\n\n"
        + "func _input(event: InputEvent) -> void:\n"
        + "\tvar key := event as InputEventKey\n"
        + "\tif key != null and key.pressed and key.keycode == KEY_P:\n"
        + "\t\tpanel.show()\n"
        + "\t\tshown_frame = Engine.get_process_frames()\n";

    // What AnUnreleasedPressIsReportedHeld leaves held.
    private static readonly string[] HeldKeys = ["K"];
    private static readonly string[] HeldActions = ["probe_jump"];
    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARidingActionIsJustPressedInItsFramesProcessAndAtTheNextFramesFirstTick()
    {
        await AddRidingProbeAsync(TestContext.Current.CancellationToken);
        long fps = await CapFpsAsync(CappedFps);
        try
        {
            await FrameAsync("pause");
            long start = await ReadIntAsync("frames");

            JsonObject stepped = await FrameAsync("step", 6, new StepOptions(Input: [Action(3, pressed: true), Action(5, pressed: false)]));
            JsonNode hits = await RunAsync($"return [{Probe}.process_hits, {Probe}.physics_hits]");

            Assert.Equal(6, stepped["processFrames"]!.GetValue<int>());
            Assert.Equal(start + 3, Assert.Single(hits[0]!.AsArray())!.GetValue<long>());
            JsonArray tick = Assert.Single(hits[1]!.AsArray())!.AsArray();
            Assert.Equal((start + 4, 0L), (tick[0]!.GetValue<long>(), tick[1]!.GetValue<long>()));
            Assert.False(stepped.ContainsKey("held"), stepped.ToJsonString());
            Assert.False(stepped.ContainsKey("inputNotSent"), stepped.ToJsonString());
        }
        finally
        {
            await ReleaseAsync(TestContext.Current.CancellationToken, Action(0, pressed: false));
            await RunAsync($"Engine.max_fps = {fps}\n\treturn true");
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnActionSentWhilePausedIsNeverJustPressedInTheStepThatFollows()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddRidingProbeAsync(TestContext.Current.CancellationToken);
        long fps = await CapFpsAsync(CappedFps);
        try
        {
            await FrameAsync("pause");
            await _tools.SimulateActionAsync("probe_jump", new ActionOptions(Mode: "press"), cancellationToken: cancellation);

            JsonObject stepped = await FrameAsync("step", 6);
            JsonNode hits = await RunAsync($"return [{Probe}.process_hits, {Probe}.physics_hits]");

            Assert.Equal(6, stepped["processFrames"]!.GetValue<int>());
            Assert.Empty(hits[0]!.AsArray());
            Assert.Empty(hits[1]!.AsArray());
        }
        finally
        {
            await _tools.SimulateActionAsync("probe_jump", new ActionOptions(Mode: "release"), cancellationToken: cancellation);
            await RunAsync($"Engine.max_fps = {fps}\n\treturn true");
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AUiChangedUntilWithARidingKeyStopsOnTheFrameThePanelIsDrawn()
    {
        await AddRidingProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        try
        {
            StepOptions options = new(
                Until: new WaitCondition(UiChanged: JsonSerializer.SerializeToElement(true)),
                Input: [Key("P", 2, pressed: true)]
            );
            JsonObject stepped = await FrameAsync("step", 10, options);
            long shown = await ReadIntAsync("shown_frame");
            bool visible = (await RunAsync("return scene_tree.root.get_node(\"RidingPanel\").visible")).GetValue<bool>();

            Assert.True(stepped["met"]!.GetValue<bool>(), stepped.ToJsonString());
            Assert.Equal(3, stepped["processFrames"]!.GetValue<int>());
            Assert.Equal(shown, stepped["frame"]!.GetValue<long>());
            Assert.True(visible, "the panel the riding key showed");
            Assert.True(stepped["paused"]!.GetValue<bool>(), stepped.ToJsonString());
        }
        finally
        {
            await ReleaseAsync(TestContext.Current.CancellationToken, Key("P", 0, pressed: false));
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUntilMetEarlyReportsTheEventsLeftUnsent()
    {
        await AddRidingProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        long start = await ReadIntAsync("frames");

        StepOptions options = new(
            Until: new WaitCondition(Node: "RidingProbe", Expression: $"node.frames >= {start + 2}"),
            Input: [Action(0, pressed: true), Action(1, pressed: false), Action(3, pressed: true), Action(4, pressed: false)]
        );
        JsonObject stepped = await FrameAsync("step", 6, options);

        Assert.True(stepped["met"]!.GetValue<bool>(), stepped.ToJsonString());
        Assert.Equal(2, stepped["processFrames"]!.GetValue<int>());
        Assert.Equal(2, stepped["inputNotSent"]!.GetValue<int>());
        Assert.False(stepped.ContainsKey("held"), stepped.ToJsonString());
        Assert.Equal(start + 2, await ReadIntAsync("frames"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUnreleasedPressIsReportedHeld()
    {
        await FrameAsync("pause");
        try
        {
            JsonObject stepped = await FrameAsync("step", 2, new StepOptions(Input: [Key("K", 0, pressed: true), Action(1, pressed: true)]));

            JsonObject held = stepped["held"]!.AsObject();
            Assert.Equal(HeldKeys, held["keys"]!.AsArray().Select(key => key!.GetValue<string>()));
            Assert.Equal(HeldActions, held["actions"]!.AsArray().Select(action => action!.GetValue<string>()));
            Assert.Empty(held["mouseButtons"]!.AsArray());
            Assert.Empty(held["padButtons"]!.AsArray());
        }
        finally
        {
            await ReleaseAsync(TestContext.Current.CancellationToken, Key("K", 0, pressed: false), Action(0, pressed: false));
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUnknownKeyMidStepFailsTheStepAtItsFrame()
    {
        await AddRidingProbeAsync(TestContext.Current.CancellationToken);
        await FrameAsync("pause");
        long start = await ReadIntAsync("frames");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            FrameAsync(
                "step",
                6,
                new StepOptions(Input: [Key("A", 0, pressed: true), Key("A", 1, pressed: false), Key("NoSuchKey", 2, pressed: true)])
            )
        );
        bool paused = (await RunAsync("return scene_tree.paused")).GetValue<bool>();

        Assert.Contains("event 2: unknown key 'NoSuchKey'", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("held:", refused.Message, StringComparison.Ordinal);
        Assert.True(paused, "the game is left paused at the failed event's frame");
        Assert.Equal(start + 3, await ReadIntAsync("frames"));
    }

    private static JsonObject Action(int frame, bool pressed) =>
        new()
        {
            ["type"] = "action",
            ["action"] = "probe_jump",
            ["pressed"] = pressed,
            ["frame"] = frame,
        };

    private static JsonObject Key(string key, int frame, bool pressed) =>
        new()
        {
            ["type"] = "key",
            ["key"] = key,
            ["pressed"] = pressed,
            ["frame"] = frame,
        };

    /// <summary>Lets go of what a test's riding input left held, through simulate_input, so the bridge holds nothing after it.</summary>
    private async Task ReleaseAsync(CancellationToken cancellationToken, params JsonObject[] releases)
    {
        foreach (JsonObject release in releases)
        {
            release.Remove("frame");
        }

        await _tools.SimulateInputAsync(releases, cancellationToken: cancellationToken);
    }

    /// <summary>Adds RidingProbe and RidingPanel under the shared run's root; InitializeAsync has already reset the run.</summary>
    private Task<JsonNode> AddRidingProbeAsync(CancellationToken cancellationToken) =>
        RunBodyAsync(
            "var script := GDScript.new()\n\t"
                + $"script.source_code = {GdString(ProbeSource)}\n\t"
                + "script.reload()\n\t"
                + "var panel := Panel.new()\n\t"
                + "panel.name = \"RidingPanel\"\n\t"
                + "panel.position = Vector2(100, 100)\n\t"
                + "panel.size = Vector2(80, 60)\n\t"
                + "panel.visible = false\n\t"
                + "scene_tree.root.add_child(panel)\n\t"
                + "var probe := Node.new()\n\t"
                + "probe.name = \"RidingProbe\"\n\t"
                + "probe.set_script(script)\n\t"
                + "probe.set(\"panel\", panel)\n\t"
                + "scene_tree.root.add_child(probe)\n\t"
                + "return true",
            cancellationToken
        );

    /// <summary>Sets Engine.max_fps to <paramref name="fps"/> and returns the value it had.</summary>
    private async Task<long> CapFpsAsync(int fps) =>
        (await RunAsync($"var before := Engine.max_fps\n\tEngine.max_fps = {fps}\n\treturn before")).GetValue<long>();

    /// <summary>GDScript source as a GDScript string literal.</summary>
    private static string GdString(string source) =>
        "\""
        + source
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
        + "\"";

    private async Task<long> ReadIntAsync(string property) => (await RunAsync($"return {Probe}.{property}")).GetValue<long>();

    private async Task<JsonObject> FrameAsync(string action, int? count = null, StepOptions? options = null)
    {
        IEnumerable<ContentBlock> blocks = await _tools.FrameControlAsync(
            action,
            count,
            null,
            options,
            cancellationToken: TestContext.Current.CancellationToken
        );
        return JsonNode.Parse(string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text)))!.AsObject();
    }

    private Task<JsonNode> RunAsync(string body) => RunBodyAsync(body, TestContext.Current.CancellationToken);

    private async Task<JsonNode> RunBodyAsync(string body, CancellationToken cancellationToken)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }
}
