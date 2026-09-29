using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The input tools against the InputProbe running in the real Godot: one shared run, reset before each test, except for
/// the letterboxed tests, whose window size is set at launch, so each launches its own.
/// </summary>
public sealed class InputTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;

    // The probe's base size is 640 x 360 with stretch canvas_items / keep: a 1000 x 900 window scales it by 1.5625 and
    // adds bars above and below.
    private static readonly string[] Letterboxed = ["--resolution", "1000x900"];
    private static readonly InputTarget DragSource = new("DragSource");
    private static readonly InputTarget DropTarget = new("DropTarget");

    // Inside ScrollBox, a ScrollContainer spanning (570, 110) to (630, 290) around a 200 x 600 ScrollContent (filter Pass),
    // clear of its scroll bars. A wheel notch or a pan delta of 1 moves it an eighth of its page
    // (scene/gui/scroll_container.cpp L190-226, L311-320 in 4.7.2).
    private static readonly InputTarget ScrollPoint = new(null, 590, 190);

    // ScrollBox's vertical and horizontal scroll bars' values.
    private const string ReadScroll =
        "var box: ScrollContainer = scene_tree.root.get_node(\"Main/ScrollBox\")\n\t"
        + "return [box.get_v_scroll_bar().value, box.get_h_scroll_bar().value]";

    // A real mouse moving over the window, as Godot sees one: a plain motion (device DEVICE_ID_MOUSE, no button_mask) at
    // (600, 20), away from both the source and the target, every frame for 1.4 s, so one lands between the drag's last
    // motion and its release.
    private const string StrayMotionsScript =
        "var far := scene_tree.root.get_screen_transform() * Vector2(600, 20)\n\t"
        + "var until := Time.get_ticks_msec() + 1400\n\t"
        + "var sent := 0\n\t"
        + "while Time.get_ticks_msec() < until:\n\t\t"
        + "var motion := InputEventMouseMotion.new()\n\t\t"
        + "motion.position = far\n\t\t"
        + "motion.global_position = far\n\t\t"
        + "Input.parse_input_event(motion)\n\t\t"
        + "Input.flush_buffered_events()\n\t\t"
        + "sent += 1\n\t\t"
        + "await scene_tree.process_frame\n\t"
        + "return sent";

    // Counts the emulated touch events that reach SmallButton's gui_input, and turns on touch emulation from the mouse.
    private const string CountTouchesScript =
        "var button = scene_tree.root.get_node(\"Main/SmallButton\")\n\t"
        + "button.set_meta(\"touches\", 0)\n\t"
        + "button.gui_input.connect(func(event: InputEvent) -> void:\n\t\t"
        + "if event is InputEventScreenTouch:\n\t\t\t"
        + "button.set_meta(\"touches\", int(button.get_meta(\"touches\")) + 1))\n\t"
        + "Input.emulate_touch_from_mouse = true\n\t"
        + "return true";

    // A real left click on SmallButton (viewport (306, 276)), as Godot sees one: device DEVICE_ID_MOUSE, a press and a
    // release a frame apart, sent a few frames after the script starts. Returns the touches SmallButton saw.
    private const string RealClickScript =
        "var button = scene_tree.root.get_node(\"Main/SmallButton\")\n\t"
        + "var at := scene_tree.root.get_screen_transform() * Vector2(306, 276)\n\t"
        + "for frame in 5:\n\t\t"
        + "await scene_tree.process_frame\n\t"
        + "for pressed: bool in [true, false]:\n\t\t"
        + "var click := InputEventMouseButton.new()\n\t\t"
        + "click.button_index = MOUSE_BUTTON_LEFT\n\t\t"
        + "click.pressed = pressed\n\t\t"
        + "click.button_mask = MOUSE_BUTTON_MASK_LEFT if pressed else 0\n\t\t"
        + "click.position = at\n\t\t"
        + "click.global_position = at\n\t\t"
        + "Input.parse_input_event(click)\n\t\t"
        + "Input.flush_buffered_events()\n\t\t"
        + "await scene_tree.process_frame\n\t"
        + "return [button.get_meta(\"touches\"), button.press_count]";

    // Opens a PopupPanel, an embedded Window, over the Menu, holding a Button named PopupButton; returns the button's centre
    // in viewport coordinates.
    private const string PopupScript =
        "var popup := PopupPanel.new()\n\t"
        + "var button := Button.new()\n\t"
        + "button.name = \"PopupButton\"\n\t"
        + "button.text = \"In the popup\"\n\t"
        + "popup.add_child(button)\n\t"
        + "scene_tree.root.add_child(popup)\n\t"
        + "popup.popup(Rect2i(200, 100, 160, 80))\n\t"
        + "for frame in 3:\n\t\t"
        + "await scene_tree.process_frame\n\t"
        + "return Vector2(popup.position) + button.get_global_rect().get_center()";

    // Opens a PopupMenu holding one item, "Alpha" with the tooltip "Alpha tip", and returns item 0's centre in viewport
    // coordinates: with one item the menu sizes itself to it, the panel's margins around the items control, whose first
    // band is one v_separation plus one item height tall, so the window's centre lies inside that band.
    private const string PopupMenuScript =
        "var menu := PopupMenu.new()\n\t"
        + "menu.add_item(\"Alpha\")\n\t"
        + "menu.set_item_tooltip(0, \"Alpha tip\")\n\t"
        + "scene_tree.root.add_child(menu)\n\t"
        + "menu.popup(Rect2i(200, 100, 160, 0))\n\t"
        + "for frame in 3:\n\t\t"
        + "await scene_tree.process_frame\n\t"
        + "return Vector2(menu.position) + Vector2(menu.size) / 2.0";

    // Adds a drawn MenuBar to the root, holding one PopupMenu whose node name is its title, with that title's tooltip
    // set, and returns the first title's centre in viewport coordinates: titles sit at the bar's top-left, left to right,
    // so the first spans the bar's minimum-size rect, while the bar itself is stretched across the window.
    // prefer_global_menu is off, or on a platform with a global menu the bar would hand its titles to the OS and draw
    // nothing to hover.
    private const string MenuBarScript =
        "var bar := MenuBar.new()\n\t"
        + "bar.name = \"TooltipMenuBar\"\n\t"
        + "bar.prefer_global_menu = false\n\t"
        + "scene_tree.root.add_child(bar)\n\t"
        + "var menu := PopupMenu.new()\n\t"
        + "menu.name = \"File\"\n\t"
        + "bar.add_child(menu)\n\t"
        + "await scene_tree.process_frame\n\t"
        + "bar.set_menu_tooltip(0, \"File tip\")\n\t"
        + "for frame in 3:\n\t\t"
        + "await scene_tree.process_frame\n\t"
        + "return bar.get_global_transform_with_canvas() * (bar.get_minimum_size() / 2.0)";

    // Whether probe_jump is held, and how many presses of it PadProbe's _input has counted.
    private const string ReadJump = "return [Input.is_action_pressed(\"probe_jump\"), scene_tree.root.get_node(\"Main/PadProbe\").jump_count]";

    // The rect {x, y, width, height} of the first tooltip panel (the theme type variation Viewport gives it) seen visible within
    // 2 s, or null when none shows.
    private const string AwaitTooltipScript =
        "var until := Time.get_ticks_msec() + 2000\n\t"
        + "while Time.get_ticks_msec() < until:\n\t\t"
        + "for window in scene_tree.root.get_embedded_subwindows():\n\t\t\t"
        + "if window.visible and window.theme_type_variation == &\"TooltipPanel\":\n\t\t\t\t"
        + "return {\"x\": window.position.x, \"y\": window.position.y, \"width\": window.size.x, \"height\": window.size.y}\n\t\t"
        + "await scene_tree.process_frame\n\t"
        + "return null";

    // Whether a tooltip panel is visible now.
    private const string TooltipShownNow =
        "for window in scene_tree.root.get_embedded_subwindows():\n\t\t"
        + "if window.visible and window.theme_type_variation == &\"TooltipPanel\":\n\t\t\t"
        + "return true\n\t"
        + "return false";
    private const string ReadSmallButtonPresses = "return scene_tree.root.get_node(\"Main/SmallButton\").press_count";
    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DragFromSourceToTargetDrops()
    {
        await _tools.DragAsync(DragSource, DropTarget, 300, "left", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(("dropped:DragSource", 1), await ReadDropAsync(_tools));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DragShorterThanTheThresholdDoesNotDrop()
    {
        // DragSource's centre is (90, 190); 5 px is under the default 10 px drag threshold.
        await _tools.DragAsync(new(null, 90, 190), new(null, 95, 190), 300, "left", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(("drop here", 0), await ReadDropAsync(_tools));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task DragDropsInALetterboxedWindow()
    {
        using ProbeProject probe = new();
        await using SessionHarness harness = new();
        RuntimeTools tools = await LaunchLetterboxedAsync(probe, harness);

        await tools.DragAsync(DragSource, DropTarget, 300, "left", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(("dropped:DragSource", 1), await ReadDropAsync(tools));
        await StopAndCheckCleanAsync(harness, probe);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ClickHitsTheSmallButtonInALetterboxedWindowByElementAndByPoint()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        await using SessionHarness harness = new();
        RuntimeTools tools = await LaunchLetterboxedAsync(probe, harness);

        await tools.ClickAsync(new InputTarget("SmallButton"), "left", false, cancellationToken: cancellation);
        int afterElement = (await RunAsync(tools, ReadSmallButtonPresses)).GetValue<int>();
        JsonNode rect = await FindRectAsync(tools, "Button", "SmallButton");
        double x = rect["x"]!.GetValue<double>() + (rect["width"]!.GetValue<double>() / 2);
        double y = rect["y"]!.GetValue<double>() + (rect["height"]!.GetValue<double>() / 2);
        await tools.ClickAsync(new InputTarget(null, x, y), "left", false, cancellationToken: cancellation);
        int afterPoint = (await RunAsync(tools, ReadSmallButtonPresses)).GetValue<int>();

        Assert.Equal((1, 2), (afterElement, afterPoint));
        await StopAndCheckCleanAsync(harness, probe);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TypeTextKeepsCaseAndSymbols()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await _tools.ClickAsync(new InputTarget("TextInput"), "left", false, cancellationToken: cancellation);
        await _tools.TypeTextAsync("Hello World!", cancellationToken: cancellation);

        Assert.Equal("Hello World!", (await RunAsync("return scene_tree.root.get_node(\"Main/TextInput\").text")).GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task KeyPressHoldsShiftUntilRelease()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await _tools.KeyAsync("Shift", "press", null, cancellationToken: cancellation);
        bool held = (await RunAsync("return Input.is_key_pressed(KEY_SHIFT)")).GetValue<bool>();
        await _tools.KeyAsync("Shift", "release", null, cancellationToken: cancellation);
        bool released = (await RunAsync("return Input.is_key_pressed(KEY_SHIFT)")).GetValue<bool>();

        Assert.Equal((true, false), (held, released));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RawMotionsBetweenAHeldAndAReleasedButtonDrop()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        // From DragSource's centre (90, 190) toward DropTarget's (480, 190), with no explicit button_mask.
        await _tools.MouseButtonAsync(DragSource, "left", "press", cancellationToken: cancellation);
        await _tools.SimulateInputAsync([Motion(200, 190), Motion(340, 190), Motion(470, 190)], cancellationToken: cancellation);
        await _tools.MouseButtonAsync(DropTarget, "left", "release", cancellationToken: cancellation);

        Assert.Equal(("dropped:DragSource", 1), await ReadDropAsync(_tools));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DragDropsWhileTheRealMouseMovesElsewhere()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        Task<string> drag = _tools.DragAsync(DragSource, DropTarget, 1000, "left", cancellationToken: cancellation);
        int strayMotions = (await RunAsync(StrayMotionsScript)).GetValue<int>();
        await drag;

        Assert.True(strayMotions > 10, $"only {strayMotions} stray motions were sent");
        Assert.Equal(("dropped:DragSource", 1), await ReadDropAsync(_tools));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARealClickDuringADragDoesNotReachTheGuiThroughItsTouchTwin()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(CountTouchesScript);

        Task<string> drag = _tools.DragAsync(DragSource, DropTarget, 1000, "left", cancellationToken: cancellation);
        JsonNode during = await RunAsync(RealClickScript);
        await drag;
        JsonNode after = await RunAsync(RealClickScript);
        await _tools.ClickAsync(new InputTarget("SmallButton"), "left", false, cancellationToken: cancellation);
        int touches = (await RunAsync("return scene_tree.root.get_node(\"Main/SmallButton\").get_meta(\"touches\")")).GetValue<int>();

        // During the drag neither the touch twins nor the mouse click reach SmallButton; after it, both twins do, and an
        // injected click's twins pass while its own gesture plays.
        Assert.Equal((0, 0), (during[0]!.GetValue<int>(), during[1]!.GetValue<int>()));
        Assert.Equal((2, 4), (after[0]!.GetValue<int>(), touches));
        Assert.Equal(("dropped:DragSource", 1), await ReadDropAsync(_tools));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ClickReportsTheScriptErrorItsHandlerRaised()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync("scene_tree.root.get_node(\"Main/SmallButton\").fail_on_press = true\n\treturn true");

        string clicked = await _tools.ClickAsync(new InputTarget("SmallButton"), "left", false, cancellationToken: cancellation);
        int pressCount = (await RunAsync(ReadSmallButtonPresses)).GetValue<int>();

        Assert.Equal(1, pressCount);
        JsonNode error = JsonNode.Parse(clicked)!["errors"]![0]!;
        Assert.Equal("res://small_button.gd", error["file"]!.GetValue<string>());
        // small_button.gd line 17: missing.call("free") on a null Object.
        Assert.Equal(17, error["line"]!.GetValue<int>());
        Assert.NotEmpty(error["stack"]!.AsArray());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ClickReportsTheControlItPressedAndReleasedOn()
    {
        JsonNode clicked = JsonNode.Parse(
            await _tools.ClickAsync(new InputTarget("SmallButton"), "left", false, cancellationToken: TestContext.Current.CancellationToken)
        )!;

        AssertHit(clicked, "pressedOn", "SmallButton", "Button");
        AssertHit(clicked, "releasedOn", "SmallButton", "Button");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ClickOnNothingReportsNull()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // (600, 20) is over no Control but Main, which fills the viewport and stops the mouse; with Main ignoring the mouse
        // nothing is under the point.
        InputTarget nowhere = new(null, 600, 20);

        JsonNode overMain = JsonNode.Parse(await _tools.ClickAsync(nowhere, "left", false, cancellationToken: cancellation))!;
        await RunAsync("scene_tree.root.get_node(\"Main\").mouse_filter = Control.MOUSE_FILTER_IGNORE\n\treturn true");
        JsonNode overNothing = JsonNode.Parse(await _tools.ClickAsync(nowhere, "left", false, cancellationToken: cancellation))!;

        AssertHit(overMain, "pressedOn", "Main", "Control");
        AssertHit(overMain, "releasedOn", "Main", "Control");
        AssertNoHit(overNothing, "pressedOn");
        AssertNoHit(overNothing, "releasedOn");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DragReportsTheGuiDragAndTheDrop()
    {
        JsonNode dragged = JsonNode.Parse(
            await _tools.DragAsync(DragSource, DropTarget, 300, "left", cancellationToken: TestContext.Current.CancellationToken)
        )!;

        AssertHit(dragged, "pressedOn", "DragSource", "ColorRect");
        AssertHit(dragged, "releasedOn", "DropTarget", "ColorRect");
        Assert.True(dragged["guiDragStarted"]!.GetValue<bool>(), dragged.ToJsonString());
        Assert.True(dragged["dropAccepted"]!.GetValue<bool>(), dragged.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AShortDragStartsNoGuiDrag()
    {
        // DragSource's centre is (90, 190); 5 px is under the default 10 px drag threshold.
        JsonNode dragged = JsonNode.Parse(
            await _tools.DragAsync(new(null, 90, 190), new(null, 95, 190), 300, "left", cancellationToken: TestContext.Current.CancellationToken)
        )!;

        Assert.False(dragged["guiDragStarted"]!.GetValue<bool>(), dragged.ToJsonString());
        Assert.False(dragged["dropAccepted"]!.GetValue<bool>(), dragged.ToJsonString());
    }

    // A run of its own with the real pads live: the shared run is shut out of them, and shut-out mode's re-sent application
    // focus-out closes a Popup as it opens (popup.cpp L114-120 in 4.7.2).
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ClickReportsAControlInsideAPopup()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        await using SessionHarness harness = new();
        await harness.Sessions.LaunchAsync(new LaunchRequest(probe.Directory, null, [], [], true, false, Prepare: true), null, cancellation);
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        JsonNode centre = await RunAsync(tools, PopupScript);

        JsonNode clicked = JsonNode.Parse(
            await tools.ClickAsync(
                new InputTarget(null, centre["x"]!.GetValue<double>(), centre["y"]!.GetValue<double>()),
                "left",
                false,
                cancellationToken: cancellation
            )
        )!;

        AssertHit(clicked, "pressedOn", "PopupButton", "Button");
        await StopAndCheckCleanAsync(harness, probe);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MouseButtonReportsWhatItPressed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        InputTarget smallButton = new("SmallButton");

        JsonNode pressed = JsonNode.Parse(await _tools.MouseButtonAsync(smallButton, "left", "press", cancellationToken: cancellation))!;
        JsonNode released = JsonNode.Parse(await _tools.MouseButtonAsync(smallButton, "left", "release", cancellationToken: cancellation))!;

        AssertHit(pressed, "pressedOn", "SmallButton", "Button");
        Assert.False(pressed.AsObject().ContainsKey("releasedOn"), pressed.ToJsonString());
        AssertHit(released, "releasedOn", "SmallButton", "Button");
        Assert.False(released.AsObject().ContainsKey("pressedOn"), released.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MouseButtonMoveHoversWithoutPressing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        int before = await PressCountAsync();

        JsonNode moved = JsonNode.Parse(
            await _tools.MouseButtonAsync(new InputTarget("SmallButton"), "left", "move", cancellationToken: cancellation)
        )!;

        AssertHit(moved, "hoveredOn", "SmallButton", "Button");
        Assert.Equal(0, moved["heldButtonMask"]!.GetValue<int>());
        Assert.False(moved.AsObject().ContainsKey("pressedOn"), moved.ToJsonString());
        Assert.False(moved.AsObject().ContainsKey("releasedOn"), moved.ToJsonString());
        Assert.Equal(before, await PressCountAsync());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ScrollMovesAScrollContainerDownThenUp()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonNode down = JsonNode.Parse(await _tools.ScrollAsync(ScrollPoint, "down", 3, cancellationToken: cancellation))!;
        (double Vertical, double Horizontal) afterDown = await ReadScrollAsync();
        await _tools.ScrollAsync(ScrollPoint, "up", 1, cancellationToken: cancellation);
        (double Vertical, double Horizontal) afterUp = await ReadScrollAsync();

        AssertHit(down, "scrolledOn", "ScrollContent", "ColorRect");
        Assert.Equal(0, down["heldButtonMask"]!.GetValue<int>());
        Assert.True(afterDown.Vertical > 0, $"three notches down left ScrollBox at {afterDown}");
        Assert.True(afterUp.Vertical > 0 && afterUp.Vertical < afterDown.Vertical, $"one notch up went from {afterDown} to {afterUp}");
        Assert.Equal(0, afterUp.Horizontal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ScrollViaPanMovesAScrollContainer()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await _tools.ScrollAsync(ScrollPoint, "right", 2, new ScrollOptions(Via: "pan"), cancellationToken: cancellation);
        (double Vertical, double Horizontal) scrolled = await ReadScrollAsync();

        Assert.True(scrolled.Horizontal > 0, $"two pan notches right left ScrollBox at {scrolled}");
        Assert.Equal(0, scrolled.Vertical);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ScrollLeavesTheHeldButtonsAsTheyWere()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _tools.MouseButtonAsync(ScrollPoint, "right", "press", cancellationToken: cancellation);

        JsonNode scrolled = JsonNode.Parse(await _tools.ScrollAsync(ScrollPoint, "down", 2, cancellationToken: cancellation))!;
        JsonNode released = JsonNode.Parse(await _tools.MouseButtonAsync(ScrollPoint, "right", "release", cancellationToken: cancellation))!;

        Assert.Equal(2, scrolled["heldButtonMask"]!.GetValue<int>());
        Assert.Equal(0, released["heldButtonMask"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SimulateInputWheelAndPanEventsScroll()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject wheel = new()
        {
            ["type"] = "mouse_button",
            ["x"] = 590,
            ["y"] = 190,
            ["button"] = "wheel_down",
        };
        JsonObject pan = new()
        {
            ["type"] = "pan_gesture",
            ["x"] = 590,
            ["y"] = 190,
            ["delta_x"] = 2,
        };

        JsonNode wheeled = JsonNode.Parse(await _tools.SimulateInputAsync([wheel], cancellationToken: cancellation))!;
        (double Vertical, double Horizontal) afterWheel = await ReadScrollAsync();
        await _tools.SimulateInputAsync([pan], cancellationToken: cancellation);
        (double Vertical, double Horizontal) afterPan = await ReadScrollAsync();

        Assert.Equal(0, wheeled["heldButtonMask"]!.GetValue<int>());
        Assert.True(afterWheel.Vertical > 0 && afterWheel.Horizontal == 0, $"a wheel_down event left ScrollBox at {afterWheel}");
        Assert.True(afterPan.Horizontal > 0, $"a pan of delta_x 2 left ScrollBox at {afterPan}");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task HoverShowsTheTooltip()
    {
        // ProbeButton spans (20, 70) to (140, 110) and has tooltip_text "Does nothing yet".
        JsonNode hovered = JsonNode.Parse(
            await _tools.HoverAsync(new InputTarget(null, 80, 90), cancellationToken: TestContext.Current.CancellationToken)
        )!;
        bool shownAfter = (await RunAsync(TooltipShownNow)).GetValue<bool>();

        AssertHit(hovered, "hoveredOn", "ProbeButton", "Button");
        Assert.False(hovered.AsObject().ContainsKey("warning"), hovered.ToJsonString());
        Assert.True(hovered["tooltip"] is JsonObject, hovered.ToJsonString());
        JsonNode tooltip = hovered["tooltip"]!;
        Assert.Equal("Does nothing yet", tooltip["text"]!.GetValue<string>());
        (double x, double y) = (tooltip["x"]!.GetValue<double>(), tooltip["y"]!.GetValue<double>());
        (double width, double height) = (tooltip["width"]!.GetValue<double>(), tooltip["height"]!.GetValue<double>());
        JsonNode pointer = hovered["pointer"]!;
        Assert.True(x >= pointer["x"]!.GetValue<double>() && y >= pointer["y"]!.GetValue<double>(), hovered.ToJsonString());
        // The probe's viewport is 640 x 360.
        Assert.True(width > 0 && height > 0 && x + width <= 640 && y + height <= 360, hovered.ToJsonString());
        AssertHit(tooltip, "owner", "ProbeButton", "Button");
        Assert.True(shownAfter, "the TooltipPanel popup is not visible after the hover");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task HoverReportsAnAncestorsTooltip()
    {
        await AddChipAsync("HBoxContainer", "Row", "PASS");

        JsonNode hovered = JsonNode.Parse(
            await _tools.HoverAsync(new InputTarget("Chip"), cancellationToken: TestContext.Current.CancellationToken)
        )!;
        bool shownAfter = (await RunAsync(TooltipShownNow)).GetValue<bool>();

        AssertHit(hovered, "hoveredOn", "Chip/Row", "HBoxContainer");
        Assert.False(hovered.AsObject().ContainsKey("warning"), hovered.ToJsonString());
        Assert.True(hovered["tooltip"] is JsonObject, hovered.ToJsonString());
        JsonNode tooltip = hovered["tooltip"]!;
        Assert.Equal("Reshuffle", tooltip["text"]!.GetValue<string>());
        AssertHit(tooltip, "owner", "Chip", "PanelContainer");
        Assert.True(shownAfter, "the TooltipPanel popup is not visible after the hover");
    }

    // A run of its own, as ClickReportsAControlInsideAPopup: the shared run is shut out of the real pads, and shut-out
    // mode's re-sent application focus-out closes a Popup as it opens (popup.cpp L114-120 in 4.7.2), a PopupMenu included.
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task HoverReportsAPopupMenuItemsTooltip()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        await using SessionHarness harness = new();
        await harness.Sessions.LaunchAsync(new LaunchRequest(probe.Directory, null, [], [], true, false, Prepare: true), null, cancellation);
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        JsonNode centre = await RunAsync(tools, PopupMenuScript);

        JsonNode hovered = JsonNode.Parse(
            await tools.HoverAsync(
                new InputTarget(null, centre["x"]!.GetValue<double>(), centre["y"]!.GetValue<double>()),
                cancellationToken: cancellation
            )
        )!;

        Assert.True(hovered["hoveredOn"] is JsonObject, hovered.ToJsonString());
        Assert.Equal("PopupMenuItems", hovered["hoveredOn"]!["class"]!.GetValue<string>());
        Assert.False(hovered.AsObject().ContainsKey("warning"), hovered.ToJsonString());
        Assert.True(hovered["tooltip"] is JsonObject, hovered.ToJsonString());
        Assert.Equal("Alpha tip", hovered["tooltip"]!["text"]!.GetValue<string>());
        Assert.True(hovered["tooltip"]!["owner"] is JsonObject, hovered.ToJsonString());
        Assert.Equal("PopupMenuItems", hovered["tooltip"]!["owner"]!["class"]!.GetValue<string>());
        await StopAndCheckCleanAsync(harness, probe);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task HoverReportsAMenuBarMenusTooltip()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode point = await RunAsync(MenuBarScript);

        JsonNode hovered = JsonNode.Parse(
            await _tools.HoverAsync(
                new InputTarget(null, point["x"]!.GetValue<double>(), point["y"]!.GetValue<double>()),
                cancellationToken: cancellation
            )
        )!;

        try
        {
            AssertHit(hovered, "hoveredOn", "TooltipMenuBar", "MenuBar");
            Assert.True(hovered["tooltip"] is JsonObject, hovered.ToJsonString());
            Assert.Equal("File tip", hovered["tooltip"]!["text"]!.GetValue<string>());
            Assert.True(hovered["tooltip"]!["owner"] is JsonObject, hovered.ToJsonString());
            Assert.Equal("MenuBar", hovered["tooltip"]!["owner"]!["class"]!.GetValue<string>());
        }
        finally
        {
            await RunAsync("scene_tree.root.get_node(\"TooltipMenuBar\").queue_free()\n\treturn true");
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task HoverStopsAtAStopFilterChild()
    {
        await AddChipAsync("Control", "Blocker", "STOP");

        var clock = Stopwatch.StartNew();
        JsonNode hovered = JsonNode.Parse(
            await _tools.HoverAsync(new InputTarget("Chip"), cancellationToken: TestContext.Current.CancellationToken)
        )!;
        clock.Stop();

        AssertHit(hovered, "hoveredOn", "Chip/Blocker", "Control");
        AssertNoHit(hovered, "tooltip");
        Assert.False(hovered.AsObject().ContainsKey("warning"), hovered.ToJsonString());
        // Under the probe's 0.5 s tooltip delay: a wait would last until a tooltip showed or 1.5 s ran out.
        Assert.True(clock.ElapsedMilliseconds < 450, $"the hover took {clock.ElapsedMilliseconds} ms");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task HoverWhilePausedWarnsTheTooltipNeverStarts()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _tools.FrameControlAsync("pause", cancellationToken: cancellation);
        JsonNode hovered;
        try
        {
            hovered = JsonNode.Parse(await _tools.HoverAsync(new InputTarget("ProbeButton"), cancellationToken: cancellation))!;
        }
        finally
        {
            await _tools.FrameControlAsync("resume", cancellationToken: cancellation);
        }

        AssertHit(hovered, "hoveredOn", "ProbeButton", "Button");
        AssertNoHit(hovered, "tooltip");
        Assert.Equal(
            "the game is paused and /root/Main/ProbeButton cannot process, so its tooltip timer never starts; resume, hover, then pause",
            hovered["warning"]!.GetValue<string>()
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task HoverOverAControlWithoutATooltipAnswersAtOnce()
    {
        var clock = Stopwatch.StartNew();
        JsonNode hovered = JsonNode.Parse(
            await _tools.HoverAsync(new InputTarget("SmallButton"), cancellationToken: TestContext.Current.CancellationToken)
        )!;
        clock.Stop();

        AssertHit(hovered, "hoveredOn", "SmallButton", "Button");
        AssertNoHit(hovered, "tooltip");
        Assert.False(hovered.AsObject().ContainsKey("warning"), hovered.ToJsonString());
        // Under the probe's 0.5 s tooltip delay: a wait would last until a tooltip showed or 1.5 s ran out.
        Assert.True(clock.ElapsedMilliseconds < 450, $"the hover took {clock.ElapsedMilliseconds} ms");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SimulateActionPressesReleasesAndTapsAnInputMapAction()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await _tools.SimulateActionAsync("probe_jump", new ActionOptions("press"), cancellationToken: cancellation);
        JsonNode pressed = await RunAsync(ReadJump);
        await _tools.SimulateActionAsync("probe_jump", new ActionOptions("release"), cancellationToken: cancellation);
        JsonNode released = await RunAsync(ReadJump);
        await _tools.SimulateActionAsync("probe_jump", cancellationToken: cancellation);
        JsonNode tapped = await RunAsync(ReadJump);

        Assert.Equal("""[true,1]""", pressed.ToJsonString());
        Assert.Equal("""[false,1]""", released.ToJsonString());
        Assert.Equal("""[false,2]""", tapped.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SimulateActionRefusesAnActionMissingFromTheInputMap()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SimulateActionAsync("probe_fly", cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Contains("no input action 'probe_fly' in the project's InputMap", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickInsideAShowingTooltipReportsTheControlBeneath()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // A motion with no button over ProbeButton ((20, 70) to (140, 110), tooltip_text set) shows its tooltip after
        // gui/timers/tooltip_delay_sec (0.5 s by default), at the pointer plus display/mouse_cursor/tooltip_position_offset.
        await _tools.SimulateInputAsync([Motion(80, 90)], cancellationToken: cancellation);
        JsonNode? tooltip = await RunAsync(AwaitTooltipScript);
        Assert.True(tooltip is JsonObject, "no tooltip showed over ProbeButton, so the click proves nothing");
        double x = tooltip!["x"]!.GetValue<double>() + 3;
        double y = tooltip["y"]!.GetValue<double>() + 3;
        Assert.True(x < 140 && y < 110, $"({x}, {y}), inside the tooltip {tooltip.ToJsonString()}, is not over ProbeButton");

        JsonNode clicked = JsonNode.Parse(await _tools.ClickAsync(new InputTarget(null, x, y), "left", false, cancellationToken: cancellation))!;
        JsonNode again = JsonNode.Parse(await _tools.ClickAsync(new InputTarget(null, x, y), "left", false, cancellationToken: cancellation))!;
        bool tooltipAfter = (await RunAsync(TooltipShownNow)).GetValue<bool>();

        AssertHit(clicked, "pressedOn", "ProbeButton", "Button");
        AssertHit(again, "pressedOn", "ProbeButton", "Button");
        AssertHit(again, "releasedOn", "ProbeButton", "Button");
        Assert.False(clicked.AsObject().ContainsKey("errors"), clicked.ToJsonString());
        Assert.False(again.AsObject().ContainsKey("errors"), again.ToJsonString());
        Assert.False(tooltipAfter, "a tooltip showed again after the clicks");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickLosesItsPressWhenAPadMovesTheFocusBetweenPressAndRelease()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode rect = await FindRectAsync(_tools, "Button", "SmallButton");
        double x = rect["x"]!.GetValue<double>() + (rect["width"]!.GetValue<double>() / 2);
        double y = rect["y"]!.GetValue<double>() + (rect["height"]!.GetValue<double>() / 2);
        int before = await PressCountAsync();

        await _tools.SimulateInputAsync(
            [
                new JsonObject
                {
                    ["type"] = "mouse_button",
                    ["x"] = x,
                    ["y"] = y,
                    ["button"] = "left",
                    ["pressed"] = true,
                },
                new JsonObject
                {
                    ["type"] = "joypad_motion",
                    ["axis"] = "left_y",
                    ["value"] = -1,
                    ["device"] = 0,
                },
                new JsonObject
                {
                    ["type"] = "mouse_button",
                    ["x"] = x,
                    ["y"] = y,
                    ["button"] = "left",
                    ["pressed"] = false,
                },
            ],
            cancellationToken: cancellation
        );

        // 4.7.2's base_button.cpp L193-197: FOCUS_EXIT clears press_attempt, so the release emits no pressed.
        Assert.Equal(before, await PressCountAsync());
        Assert.Equal("TextInput", await FocusOwnerNameAsync());
    }

    // hand_probe.tscn's CardSlot is hidden, and the first of HandProbe's unnamed cards lies over its place: a click aimed at
    // the hidden CardFace's centre would press that card.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task ClickingAHiddenElementIsRefused()
    {
        await AddHandAsync();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ClickAsync(
                new InputTarget("/root/HandProbe/ViewerMonsters/CardSlot/CardFace"),
                "left",
                false,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Contains(
            "/root/HandProbe/ViewerMonsters/CardSlot/CardFace is hidden; get_ui_elements lists the visible Controls.",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Equal("[]", (await HandPressesAsync()).ToJsonString());
    }

    // Covered spans (230, 300) to (310, 350); Cover, a later sibling, spans (250, 310) to (290, 340) over its centre.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task ClickingACoveredElementIsRefusedNamingTheCoverAndPressesNothing()
    {
        await AddHandAsync();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ClickAsync(new InputTarget("/root/HandProbe/Covered"), "left", false, cancellationToken: TestContext.Current.CancellationToken)
        );

        const string Expected =
            "the centre of /root/HandProbe/Covered (270, 325) lands on /root/HandProbe/Cover, which covers it "
            + "(target rect 230,300,80,50, hit rect 250,310,40,30); click by {x, y} inside the target's visible part, "
            + "or wait until nothing covers it.";
        Assert.True(refused.Message.EndsWith(Expected, StringComparison.Ordinal), refused.Message);
        Assert.Equal("[]", (await HandPressesAsync()).ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ABareNameTwoNodesHaveIsRefusedListingBoth()
    {
        await AddHandAsync();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ClickAsync(new InputTarget("Twin"), "left", false, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Contains(
            "'Twin' names 2 nodes: /root/HandProbe/Left/Twin, /root/HandProbe/Right/Twin; pass the full path.",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Equal("[]", (await HandPressesAsync()).ToJsonString());
    }

    // Main is the probe's scene root; Missing is not one of its children.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnElementNamingNoNodeIsRefusedNamingTheBaseAndItsChildren()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ClickAsync(new InputTarget("Main/Missing"), "left", false, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Contains(
            "No node 'Main/Missing' in the running game: a path is read from /root, and /root/Main has no child 'Missing' ",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("get_ui_elements lists the Controls' paths and names.", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AFullPathClickOnAVisibleCardPressesThatCard()
    {
        await AddHandAsync();
        string card = (await RunAsync("return scene_tree.root.get_node(\"HandProbe\").cards[0]")).GetValue<string>();

        JsonNode clicked = JsonNode.Parse(
            await _tools.ClickAsync(new InputTarget(card), "left", false, cancellationToken: TestContext.Current.CancellationToken)
        )!;

        Assert.Equal(card, clicked["pressedOn"]!["path"]!.GetValue<string>());
        Assert.Equal("PanelContainer", clicked["pressedOn"]!["class"]!.GetValue<string>());
        Assert.Equal(new JsonArray(card[(card.LastIndexOf('/') + 1)..]).ToJsonString(), (await HandPressesAsync()).ToJsonString());
    }

    // PlayLabel ignores the mouse and lies over its Button's centre, so the press goes to Play.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickOnALabelThatIgnoresTheMouseLandsOnItsButton()
    {
        await AddHandAsync();

        JsonNode clicked = JsonNode.Parse(
            await _tools.ClickAsync(
                new InputTarget("/root/HandProbe/Play/PlayLabel"),
                "left",
                false,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )!;

        AssertHit(clicked, "pressedOn", "Play", "Button");
        Assert.Equal("""["Play"]""", (await HandPressesAsync()).ToJsonString());
    }

    // LayerCard spans (10, 230) to (70, 290) on Overlay, a CanvasLayer offset by (560, 0): it shows at (570, 230) to
    // (630, 290), and its rect without the layer's offset would put the click on TextInput.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task AFullPathClickOnACardUnderAnOffsetCanvasLayerPressesIt()
    {
        await AddHandAsync();

        JsonNode clicked = JsonNode.Parse(
            await _tools.ClickAsync(
                new InputTarget("/root/HandProbe/Overlay/LayerCard"),
                "left",
                false,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )!;

        Assert.Equal("/root/HandProbe/Overlay/LayerCard", clicked["pressedOn"]!["path"]!.GetValue<string>());
        Assert.Equal("""["LayerCard"]""", (await HandPressesAsync()).ToJsonString());
    }

    // ClippedCard shows at (570, 60) to (630, 100), wholly outside its clipping parent Tray ((570, 20) to (630, 50)), so the
    // GUI never finds it; with Main ignoring the mouse, no Control at all is under its centre.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task ClickingAnElementWithNoControlUnderItsCentreIsRefusedAsLandingOnNothing()
    {
        await AddHandAsync();
        await RunAsync("scene_tree.root.get_node(\"Main\").mouse_filter = Control.MOUSE_FILTER_IGNORE\n\treturn true");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ClickAsync(
                new InputTarget("/root/HandProbe/Tray/ClippedCard"),
                "left",
                false,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        const string Expected =
            "the centre of /root/HandProbe/Tray/ClippedCard (600, 80) lands on <nothing>, which covers it (target rect 570,60,60,40); "
            + "click by {x, y} inside the target's visible part, or wait until nothing covers it.";
        Assert.True(refused.Message.EndsWith(Expected, StringComparison.Ordinal), refused.Message);
        Assert.Equal("[]", (await HandPressesAsync()).ToJsonString());
    }

    // Only a drag's start is hit-tested: its end, Covered's centre, lies under Cover.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADragWhoseEndIsCoveredStillDrags()
    {
        await AddHandAsync();

        JsonNode dragged = JsonNode.Parse(
            await _tools.DragAsync(
                new InputTarget("/root/HandProbe/DragCard"),
                new InputTarget("/root/HandProbe/Covered"),
                300,
                "left",
                cancellationToken: TestContext.Current.CancellationToken
            )
        )!;

        AssertHit(dragged, "pressedOn", "DragCard", "ColorRect");
        AssertHit(dragged, "releasedOn", "Cover", "ColorRect");
        Assert.True(dragged["guiDragStarted"]!.GetValue<bool>(), dragged.ToJsonString());
    }

    // A hit is {path, class}: the Control's path, which ends with its name, and its engine class.
    private static void AssertHit(JsonNode result, string field, string name, string className)
    {
        JsonNode? hit = result[field];
        Assert.True(hit is JsonObject, $"{field} is not a Control in {result.ToJsonString()}");
        Assert.EndsWith($"/{name}", hit!["path"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(className, hit["class"]!.GetValue<string>());
    }

    private static void AssertNoHit(JsonNode result, string field)
    {
        Assert.True(result.AsObject().ContainsKey(field), $"{field} is missing from {result.ToJsonString()}");
        Assert.Null(result[field]);
    }

    // A test that launches its own game stops it and checks the probe folder is clean, as the shared-session tests do.
    private static async Task StopAndCheckCleanAsync(SessionHarness harness, ProbeProject probe)
    {
        await harness.Sessions.StopAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, Git.Status(probe.Directory));
    }

    // A run of its own in a 1000 x 900 window, checked letterboxed: without the bars the letterboxed tests would prove
    // nothing about mapping viewport points to the window.
    private static async Task<RuntimeTools> LaunchLetterboxedAsync(ProbeProject probe, SessionHarness harness)
    {
        await harness.Sessions.LaunchAsync(
            new LaunchRequest(probe.Directory, null, Letterboxed, [], true, false, Prepare: true),
            null,
            TestContext.Current.CancellationToken
        );
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        JsonNode window = await RunAsync(
            tools,
            "var t := scene_tree.root.get_screen_transform()\n\tvar s := DisplayServer.window_get_size()\n\t"
                + "return {\"width\": s.x, \"height\": s.y, \"origin\": t.origin, \"screen\": str(t)}"
        );
        Assert.Equal((1000, 900), (window["width"]!.GetValue<int>(), window["height"]!.GetValue<int>()));
        Assert.Equal(0.0, window["origin"]!["x"]!.GetValue<double>());
        Assert.True(window["origin"]!["y"]!.GetValue<double>() > 100, window.ToJsonString());
        return tools;
    }

    private static async Task<(string Text, int Count)> ReadDropAsync(RuntimeTools tools)
    {
        JsonNode drop = await RunAsync(
            tools,
            "var target = scene_tree.root.get_node(\"Main/DropTarget\")\n\treturn [target.get_node(\"DropLabel\").text, target.drop_count]"
        );
        return (drop[0]!.GetValue<string>(), drop[1]!.GetValue<int>());
    }

    private static async Task<JsonNode> FindRectAsync(RuntimeTools tools, string classFilter, string name)
    {
        string json = await tools.GetUiElementsAsync(true, classFilter, cancellationToken: TestContext.Current.CancellationToken);
        return Assert.Single(JsonNode.Parse(json)!["elements"]!.AsArray(), element => element!["name"]!.GetValue<string>() == name)!["rect"]!;
    }

    // Adds hand_probe.tscn under the shared run's root as HandProbe, drawn over Main; InitializeAsync has already reset the run.
    private async Task AddHandAsync() =>
        await RunAsync("var hand: Node = load(\"res://hand_probe.tscn\").instantiate()\n\tscene_tree.root.add_child(hand)\n\treturn true");

    // The names of the Controls HandProbe saw a mouse press reach, in order.
    // Adds under Main a PanelContainer Chip (filter Stop, tooltip "Reshuffle") spanning (160, 300) to (260, 340), an empty
    // spot of the probe, filled by one child of the given class, name and mouse filter with no tooltip of its own.
    private async Task AddChipAsync(string childClass, string childName, string childFilter) =>
        await RunAsync(
            "var chip := PanelContainer.new()\n\t"
                + "chip.name = \"Chip\"\n\t"
                + "chip.mouse_filter = Control.MOUSE_FILTER_STOP\n\t"
                + "chip.tooltip_text = \"Reshuffle\"\n\t"
                + $"var child: Control = {childClass}.new()\n\t"
                + $"child.name = \"{childName}\"\n\t"
                + $"child.mouse_filter = Control.MOUSE_FILTER_{childFilter}\n\t"
                + "chip.add_child(child)\n\t"
                + "scene_tree.root.get_node(\"Main\").add_child(chip)\n\t"
                + "chip.position = Vector2(160, 300)\n\t"
                + "chip.size = Vector2(100, 40)\n\t"
                + "await scene_tree.process_frame\n\t"
                + "return true"
        );

    private Task<JsonNode> HandPressesAsync() => RunAsync("return scene_tree.root.get_node(\"HandProbe\").presses");

    private async Task<int> PressCountAsync() => (await RunAsync(ReadSmallButtonPresses)).GetValue<int>();

    private async Task<(double Vertical, double Horizontal)> ReadScrollAsync()
    {
        JsonNode scroll = await RunAsync(ReadScroll);
        return (scroll[0]!.GetValue<double>(), scroll[1]!.GetValue<double>());
    }

    private async Task<string> FocusOwnerNameAsync() => (await RunAsync("return str(scene_tree.root.gui_get_focus_owner().name)")).GetValue<string>();

    private Task<JsonNode> RunAsync(string body) => RunAsync(_tools, body);

    private static async Task<JsonNode> RunAsync(RuntimeTools tools, string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }

    private static JsonObject Motion(double x, double y) =>
        new()
        {
            ["type"] = "mouse_motion",
            ["x"] = x,
            ["y"] = y,
        };
}
