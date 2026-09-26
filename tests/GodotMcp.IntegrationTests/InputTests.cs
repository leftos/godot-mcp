using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>The input tools against the InputProbe running in the real Godot, in its own window and letterboxed.</summary>
public sealed class InputTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;

    // The probe's base size is 640 x 360 with stretch canvas_items / keep: a 1000 x 900 window scales it by 1.5625 and
    // adds bars above and below.
    private static readonly string[] Letterboxed = ["--resolution", "1000x900"];
    private static readonly InputTarget DragSource = new("DragSource");
    private static readonly InputTarget DropTarget = new("DropTarget");
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly RuntimeTools _tools;

    public InputTests() => _tools = new RuntimeTools(_harness.Session);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DragFromSourceToTargetDrops()
    {
        await LaunchAsync([]);

        await _tools.DragAsync(DragSource, DropTarget, 300, "left", TestContext.Current.CancellationToken);

        Assert.Equal(("dropped:DragSource", 1), await ReadDropAsync());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DragShorterThanTheThresholdDoesNotDrop()
    {
        await LaunchAsync([]);

        // DragSource's centre is (90, 190); 5 px is under the default 10 px drag threshold.
        await _tools.DragAsync(new(null, 90, 190), new(null, 95, 190), 300, "left", TestContext.Current.CancellationToken);

        Assert.Equal(("drop here", 0), await ReadDropAsync());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DragDropsInALetterboxedWindow()
    {
        await LaunchAsync(Letterboxed);
        await AssertLetterboxedAsync();

        await _tools.DragAsync(DragSource, DropTarget, 300, "left", TestContext.Current.CancellationToken);

        Assert.Equal(("dropped:DragSource", 1), await ReadDropAsync());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ClickHitsTheSmallButtonInALetterboxedWindowByElementAndByPoint()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(Letterboxed);
        await AssertLetterboxedAsync();

        await _tools.ClickAsync(new InputTarget("SmallButton"), "left", false, cancellation);
        int afterElement = (await RunAsync("return scene_tree.root.get_node(\"Main/SmallButton\").press_count")).GetValue<int>();
        JsonNode rect = await FindRectAsync("Button", "SmallButton");
        double x = rect["x"]!.GetValue<double>() + (rect["width"]!.GetValue<double>() / 2);
        double y = rect["y"]!.GetValue<double>() + (rect["height"]!.GetValue<double>() / 2);
        await _tools.ClickAsync(new InputTarget(null, x, y), "left", false, cancellation);
        int afterPoint = (await RunAsync("return scene_tree.root.get_node(\"Main/SmallButton\").press_count")).GetValue<int>();

        Assert.Equal((1, 2), (afterElement, afterPoint));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TypeTextKeepsCaseAndSymbols()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync([]);

        await _tools.ClickAsync(new InputTarget("TextInput"), "left", false, cancellation);
        await _tools.TypeTextAsync("Hello World!", cancellation);

        Assert.Equal("Hello World!", (await RunAsync("return scene_tree.root.get_node(\"Main/TextInput\").text")).GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task KeyPressHoldsShiftUntilRelease()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync([]);

        await _tools.KeyAsync("Shift", "press", null, cancellation);
        bool held = (await RunAsync("return Input.is_key_pressed(KEY_SHIFT)")).GetValue<bool>();
        await _tools.KeyAsync("Shift", "release", null, cancellation);
        bool released = (await RunAsync("return Input.is_key_pressed(KEY_SHIFT)")).GetValue<bool>();

        Assert.Equal((true, false), (held, released));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RawMotionsBetweenAHeldAndAReleasedButtonDrop()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync([]);

        // From DragSource's centre (90, 190) toward DropTarget's (480, 190), with no explicit button_mask.
        await _tools.MouseButtonAsync(DragSource, "left", "press", cancellation);
        await _tools.SimulateInputAsync([Motion(200, 190), Motion(340, 190), Motion(470, 190)], cancellation);
        await _tools.MouseButtonAsync(DropTarget, "left", "release", cancellation);

        Assert.Equal(("dropped:DragSource", 1), await ReadDropAsync());
    }

    private Task<LaunchResult> LaunchAsync(string[] engineArgs) =>
        _harness.Session.LaunchAsync(new LaunchRequest(_probe.Directory, null, engineArgs, [], false), TestContext.Current.CancellationToken);

    // Without the bars the letterboxed tests would prove nothing about mapping viewport points to the window.
    private async Task AssertLetterboxedAsync()
    {
        JsonNode window = await RunAsync(
            "var t := scene_tree.root.get_screen_transform()\n\tvar s := DisplayServer.window_get_size()\n\t"
                + "return {\"width\": s.x, \"height\": s.y, \"origin\": t.origin, \"screen\": str(t)}"
        );
        Assert.Equal((1000, 900), (window["width"]!.GetValue<int>(), window["height"]!.GetValue<int>()));
        Assert.Equal(0.0, window["origin"]!["x"]!.GetValue<double>());
        Assert.True(window["origin"]!["y"]!.GetValue<double>() > 100, window.ToJsonString());
    }

    private async Task<(string Text, int Count)> ReadDropAsync()
    {
        JsonNode drop = await RunAsync(
            "var target = scene_tree.root.get_node(\"Main/DropTarget\")\n\treturn [target.get_node(\"DropLabel\").text, target.drop_count]"
        );
        return (drop[0]!.GetValue<string>(), drop[1]!.GetValue<int>());
    }

    private async Task<JsonNode> FindRectAsync(string classFilter, string name)
    {
        string json = await _tools.GetUiElementsAsync(true, classFilter, TestContext.Current.CancellationToken);
        return Assert.Single(JsonNode.Parse(json)!["elements"]!.AsArray(), element => element!["name"]!.GetValue<string>() == name)!["rect"]!;
    }

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!;
    }

    private static JsonObject Motion(double x, double y) =>
        new()
        {
            ["type"] = "mouse_motion",
            ["x"] = x,
            ["y"] = y,
        };
}
