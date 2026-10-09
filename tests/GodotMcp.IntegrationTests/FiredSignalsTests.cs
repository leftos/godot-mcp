using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The fired list click and mouse_button answer, against the InputProbe running in the real Godot: one shared run, reset
/// before each test, the widgets built by run_script as children of the root, above Main.
/// </summary>
public sealed class FiredSignalsTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Drawn = "await scene_tree.process_frame\n\tawait scene_tree.process_frame\n\treturn true";
    private const string HideMain = "scene_tree.root.get_node(\"Main\").visible = false\n\t";
    private const string World2DBlock = "var world := Node2D.new()\n\tworld.name = \"World2D\"\n\tscene_tree.root.add_child(world)\n\t";

    // Waits a few physics ticks, so the shapes are in their spaces before a click picks them.
    private const string PhysicsTicks = "for tick in 3:\n\t\tawait scene_tree.physics_frame\n\t";
    private const string Area = "/root/World2D/Target2D";

    // Card (mouse-ignoring) at (400, 200), 160 x 100, whose script declares played(title): Hit, a text-less Button filling
    // the card whose pressed handler emits played, and Title, a Label reading "Strike", both owned by Card.
    private const string CardBlock =
        "var card := Control.new()\n\t"
        + "var card_script := GDScript.new()\n\t"
        + "card_script.source_code = \"extends Control\\n\\nsignal played(title: String)\\n\"\n\t"
        + "card_script.reload()\n\t"
        + "card.set_script(card_script)\n\t"
        + "card.name = \"Card\"\n\t"
        + "card.mouse_filter = Control.MOUSE_FILTER_IGNORE\n\t"
        + "card.position = Vector2(400, 200)\n\t"
        + "card.size = Vector2(160, 100)\n\t"
        + "scene_tree.root.add_child(card)\n\t"
        + "var hit := Button.new()\n\t"
        + "hit.name = \"Hit\"\n\t"
        + "hit.size = Vector2(160, 100)\n\t"
        + "hit.pressed.connect(func() -> void: card.emit_signal(\"played\", \"Strike\"))\n\t"
        + "card.add_child(hit)\n\t"
        + "hit.owner = card\n\t"
        + "var title := Label.new()\n\t"
        + "title.name = \"Title\"\n\t"
        + "title.text = \"Strike\"\n\t"
        + "title.position = Vector2(10, 10)\n\t"
        + "title.size = Vector2(140, 30)\n\t"
        + "card.add_child(title)\n\t"
        + "title.owner = card\n\t";

    // An Area2D named Target2D under World2D at (400, 250) with a 20 x 20 square shape, whose input_event has one handler.
    private const string Area2DBlock =
        "var area := Area2D.new()\n\t"
        + "area.name = \"Target2D\"\n\t"
        + "area.position = Vector2(400, 250)\n\t"
        + "var shape := CollisionShape2D.new()\n\t"
        + "var square := RectangleShape2D.new()\n\t"
        + "square.size = Vector2(20, 20)\n\t"
        + "shape.shape = square\n\t"
        + "area.add_child(shape)\n\t"
        + "area.set_meta(\"events\", 0)\n\t"
        + "area.input_event.connect(func(_viewport: Node, _event: InputEvent, _shape: int) -> void:\n\t\t"
        + "area.set_meta(\"events\", int(area.get_meta(\"events\")) + 1))\n\t"
        + "world.add_child(area)\n\t";

    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickOnAButtonListsItsButtonDownPressedAndButtonUp()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Button("button", "Ready", 400, 250) + Drawn, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Ready"), cancellation);

        const string path = "/root/Ready";
        AssertOrder(clicked, (path, "button_down"), (path, "pressed"), (path, "button_up"));
        Assert.Equal(0, Find(clicked, path, "button_down")["frame"]!.GetValue<int>());
        Assert.True(Find(clicked, path, "pressed")["frame"]!.GetValue<int>() > 0, clicked.ToJsonString());
        Assert.Contains(path, ListenedOn(clicked));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AToggleListsToggledBeforePressedWithOneListener()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Button("toggle", "Toggle", 400, 250)
                + "toggle.toggle_mode = true\n\t"
                + "toggle.set_meta(\"on\", false)\n\t"
                + "toggle.toggled.connect(func(on: bool) -> void: toggle.set_meta(\"on\", on))\n\t"
                + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Toggle"), cancellation);

        const string path = "/root/Toggle";
        AssertOrder(clicked, (path, "toggled"), (path, "pressed"));
        JsonObject toggled = Find(clicked, path, "toggled");
        Assert.Equal("[true]", toggled["args"]!.ToJsonString());
        Assert.Equal(1, toggled["listeners"]!.GetValue<int>());
        Assert.Equal(0, Find(clicked, path, "pressed")["listeners"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADisabledButtonWarnsThatThePressSetOffNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Button("button", "Ready", 400, 250) + "button.disabled = true\n\t" + Drawn, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Ready"), cancellation);

        Assert.Contains("/root/Ready is disabled, so the press set off none of its own signals", Warning(clicked), StringComparison.Ordinal);
        Assert.Equal(-1, IndexOf(Entries(clicked), "/root/Ready", "button_down"));
        Assert.Equal(-1, IndexOf(Entries(clicked), "/root/Ready", "pressed"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AButtonGroupListsTheOtherButtonsToggledFalseFirst()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Button("first", "First", 200, 250)
                + Button("second", "Second", 400, 250)
                + "var group := ButtonGroup.new()\n\t"
                + "for member: Button in [first, second]:\n\t\tmember.toggle_mode = true\n\t\tmember.button_group = group\n\t"
                + "first.button_pressed = true\n\t"
                + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Second"), cancellation);

        AssertOrder(clicked, ("/root/First", "toggled"), ("/root/Second", "toggled"));
        Assert.Equal("[false]", Find(clicked, "/root/First", "toggled")["args"]!.ToJsonString());
        Assert.Equal("[true]", Find(clicked, "/root/Second", "toggled")["args"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATabBarClickListsTabSelectedAndTabChangedBeforeTabClicked()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            "var bar := TabBar.new()\n\tbar.name = \"Seats\"\n\tbar.position = Vector2(20, 20)\n\tbar.size = Vector2(300, 40)\n\t"
                + "bar.add_tab(\"Alex\")\n\tbar.add_tab(\"Sam\")\n\tbar.add_tab(\"Kit\")\n\t"
                + "scene_tree.root.add_child(bar)\n\t"
                + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Seats", Item: new InputItem(Text: "Kit")), cancellation);

        const string path = "/root/Seats";
        AssertOrder(clicked, (path, "tab_selected"), (path, "tab_changed"), (path, "tab_clicked"));
        Assert.Equal("[2]", Find(clicked, path, "tab_changed")["args"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATextTargetOnACardLabelListsItsRootsScriptSignalBeforeTheButtonsPressed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(CardBlock + Drawn, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget(Text: "Strike"), cancellation);

        AssertOrder(clicked, ("/root/Card", "played"), ("/root/Card/Hit", "pressed"));
        JsonObject played = Find(clicked, "/root/Card", "played");
        Assert.Equal("[\"Strike\"]", played["args"]!.ToJsonString());
        Assert.Equal(0, played["listeners"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnOptionButtonPickListsItemSelectedBeforeIndexPressedWithoutThePopupInternals()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            "var weapon := OptionButton.new()\n\tweapon.name = \"Weapon\"\n\t"
                + "weapon.position = Vector2(40, 40)\n\tweapon.size = Vector2(160, 30)\n\t"
                + "weapon.add_item(\"Sword\")\n\tweapon.add_item(\"Bow\")\n\tweapon.add_item(\"Axe\")\n\t"
                + "scene_tree.root.add_child(weapon)\n\t"
                + Drawn,
            cancellation
        );
        string popup = (await RunAsync("return str(scene_tree.root.get_node(\"Weapon\").get_popup().get_path())", cancellation)).GetValue<string>();

        JsonNode clicked = await ClickAsync(new InputTarget("Weapon", Item: new InputItem(Text: "Axe")), cancellation);

        AssertOrder(clicked, ("/root/Weapon", "item_selected"), (popup, "index_pressed"));
        Assert.Equal("[2]", Find(clicked, "/root/Weapon", "item_selected")["args"]!.ToJsonString());
        Assert.DoesNotContain(Entries(clicked), entry => Node(entry).StartsWith(popup + "/", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWorldAreaClickListsItsInputEventButEveryPressAndReleaseNotMotion()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + World2DBlock + Area2DBlock + PhysicsTicks + "return true", cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Target2D"), cancellation);

        AssertPressAndRelease(clicked, "the click");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWorldClickListsItsReleaseWhenFramesOutrunPhysicsTicks()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // Uncapped frames (no fps cap, no vsync) outrun the physics ticks, so the settle frames can end before the tick that
        // picks the release.
        JsonNode pacing = await RunAsync(
            HideMain + World2DBlock + Area2DBlock + PhysicsTicks + "return [Engine.max_fps, DisplayServer.window_get_vsync_mode()]",
            cancellation
        );
        try
        {
            await RunAsync("Engine.max_fps = 0\n\tDisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)\n\treturn true", cancellation);
            for (int click = 1; click <= 6; click++)
            {
                JsonNode clicked = await ClickAsync(new InputTarget("Target2D"), cancellation);

                AssertPressAndRelease(clicked, $"click {click}");
            }
        }
        finally
        {
            await RunAsync(
                $"Engine.max_fps = {pacing[0]!.GetValue<int>()}\n\t"
                    + $"DisplayServer.window_set_vsync_mode({pacing[1]!.GetValue<int>()} as DisplayServer.VSyncMode)\n\treturn true",
                cancellation
            );
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APressThenReleaseAsSeparateMouseButtonCallsListsTheReleaseOnThePressChain()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Button("first", "First", 200, 250) + Button("second", "Second", 400, 250) + Drawn, cancellation);

        JsonNode pressed = JsonNode.Parse(await _tools.MouseButtonAsync(new InputTarget("First"), "left", "press", cancellationToken: cancellation))!;
        JsonNode released = JsonNode.Parse(
            await _tools.MouseButtonAsync(new InputTarget("Second"), "left", "release", cancellationToken: cancellation)
        )!;

        Assert.Equal(0, Find(pressed, "/root/First", "button_down")["frame"]!.GetValue<int>());
        Assert.Equal(0, Find(released, "/root/First", "button_up")["listeners"]!.GetValue<int>());
        Assert.Contains("/root/First", ListenedOn(released));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APausedGameWarnsThatTheControlCannotProcess()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Button("button", "Ready", 400, 250) + "scene_tree.paused = true\n\t" + Drawn, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Ready"), cancellation);

        // The viewport still grabs focus for the press while the tree is paused, so fired is not empty.
        bool focused = Entries(clicked)
            .Any(entry => entry["node"]!.GetValue<string>() == "/root/Ready" && entry["signal"]!.GetValue<string>() == "focus_entered");
        Assert.True(focused, clicked.ToJsonString());
        Assert.Contains(
            "the game is paused and /root/Ready cannot process, so it received no input; resume, or step with frame_control",
            Warning(clicked),
            StringComparison.Ordinal
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AControlWhoseProcessingIsDisabledInARunningGameGetsNoPausedWarning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Button("button", "Ready", 400, 250) + "button.process_mode = Node.PROCESS_MODE_DISABLED\n\t" + Drawn, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Ready"), cancellation);

        string? warning = clicked["warning"]?.GetValue<string>();
        Assert.True(warning is null || !warning.Contains("the game is paused", StringComparison.Ordinal), clicked.ToJsonString());
    }

    // A ScrollContainer follows focus through the root's gui_focus_changed with an engine callable, which no game wrote.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task ListenersLeaveOutTheBridgeAndEngineWiring()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            "var scroll := ScrollContainer.new()\n\tscroll.name = \"Scroll\"\n\t"
                + "scroll.position = Vector2(300, 200)\n\tscroll.size = Vector2(200, 100)\n\t"
                + "scene_tree.root.add_child(scroll)\n\t"
                + "var inside := Button.new()\n\tinside.name = \"Inside\"\n\tinside.text = \"Inside\"\n\t"
                + "inside.custom_minimum_size = Vector2(120, 40)\n\tscroll.add_child(inside)\n\t"
                + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Inside"), cancellation);

        Assert.Equal(0, Find(clicked, "/root", "gui_focus_changed")["listeners"]!.GetValue<int>());
        Assert.Equal(0, Find(clicked, "/root/Scroll/Inside", "button_down")["listeners"]!.GetValue<int>());
    }

    // A Button named name at (x, y), 120 x 40, reading its name, held by the script variable variable, under the root.
    private static string Button(string variable, string name, int x, int y) =>
        $"var {variable} := Button.new()\n\t"
        + $"{variable}.name = \"{name}\"\n\t"
        + $"{variable}.text = \"{name}\"\n\t"
        + $"{variable}.position = Vector2({x}, {y})\n\t"
        + $"{variable}.size = Vector2(120, 40)\n\t"
        + $"scene_tree.root.add_child({variable})\n\t";

    // The area's input_event listed for the press and the release, each a mouse button event, and never for a motion.
    private static void AssertPressAndRelease(JsonNode clicked, string label)
    {
        JsonObject[] events = [.. Entries(clicked).Where(entry => Is(entry, Area, "input_event"))];
        Assert.True(events.Sum(entry => entry["count"]?.GetValue<int>() ?? 1) == 2, $"{label}: {clicked.ToJsonString()}");
        Assert.All(events, entry => Assert.Contains("InputEventMouseButton", entry["args"]!.ToJsonString(), StringComparison.Ordinal));
        Assert.DoesNotContain("InputEventMouseMotion", clicked["fired"]!.ToJsonString(), StringComparison.Ordinal);
    }

    private static void AssertOrder(JsonNode result, params (string Node, string Signal)[] expected)
    {
        JsonObject[] fired = Entries(result);
        int[] indexes = [.. expected.Select(item => IndexOf(fired, item.Node, item.Signal))];
        bool ordered = indexes.All(index => index >= 0) && indexes.Zip(indexes.Skip(1)).All(pair => pair.First < pair.Second);
        Assert.True(ordered, $"expected {string.Join(", ", expected)} in that order: {result.ToJsonString()}");
    }

    private static JsonObject Find(JsonNode result, string node, string signal)
    {
        JsonObject? entry = Entries(result).FirstOrDefault(item => Is(item, node, signal));
        Assert.True(entry is not null, $"no {signal} on {node}: {result.ToJsonString()}");
        return entry;
    }

    private static int IndexOf(JsonObject[] fired, string node, string signal) => Array.FindIndex(fired, entry => Is(entry, node, signal));

    private static bool Is(JsonObject entry, string node, string signal) => Node(entry) == node && entry["signal"]!.GetValue<string>() == signal;

    private static string Node(JsonObject entry) => entry["node"]!.GetValue<string>();

    private static JsonObject[] Entries(JsonNode result) => [.. result["fired"]!.AsArray().Select(entry => entry!.AsObject())];

    private static string[] ListenedOn(JsonNode result) => [.. result["listenedOn"]!.AsArray().Select(path => path!.GetValue<string>())];

    private static string Warning(JsonNode result) => result["warning"]?.GetValue<string>() ?? $"no warning: {result.ToJsonString()}";

    private async Task<JsonNode> ClickAsync(InputTarget target, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.ClickAsync(target, "left", false, cancellationToken: cancellation))!;

    private async Task<JsonNode> RunAsync(string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
        return JsonNode.Parse(json)!["value"]!;
    }
}
