using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// Element targets on 2D and 3D world nodes, and on nodes inside SubViewportContainers and embedded windows, against the
/// InputProbe running in the real Godot: one shared run, reset before each test, the nodes built by run_script. The
/// fixture's Main fills the viewport and stops the mouse, so every test that needs it out of the way hides it first.
/// </summary>
public sealed class WorldTargetTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;

    // The probe's viewport is 640 x 360. A Camera2D at (1000, 500) zoomed 2x puts world (1040, 535) at
    // ((1040 - 1000) * 2 + 320, (535 - 500) * 2 + 180) = (400, 250), a spot over Main alone.
    private const string Camera2DBlock =
        "var camera := Camera2D.new()\n\t"
        + "camera.position = Vector2(1000, 500)\n\t"
        + "camera.zoom = Vector2(2, 2)\n\t"
        + "world.add_child(camera)\n\t"
        + "camera.make_current()\n\t";

    // A Camera3D at (0, 0, 5) looking down -z with the default 75 degree vertical fov: x = 1 at depth 5 lands at
    // (1 / (5 tan 37.5 * 640 / 360) / 2 + 0.5) * 640 = 366.9 px, the vertical centre at 180.
    private const string Camera3DBlock =
        "var camera := Camera3D.new()\n\t"
        + "camera.name = \"Camera\"\n\t"
        + "camera.position = Vector3(0, 0, 5)\n\t"
        + "world.add_child(camera)\n\t"
        + "camera.make_current()\n\t";

    private const string HideMain = "scene_tree.root.get_node(\"Main\").visible = false\n\t";

    private const string World2DBlock = "var world := Node2D.new()\n\tworld.name = \"World2D\"\n\tscene_tree.root.add_child(world)\n\t";

    private const string World3DBlock = "var world := Node3D.new()\n\tworld.name = \"World3D\"\n\tscene_tree.root.add_child(world)\n\t";

    // A Stop ColorRect named Cover over the SubViewport's (20, 10) to (80, 60), root (140, 60) to (260, 160) in Stretched.
    private const string SubCoverBlock =
        "var cover := ColorRect.new()\n\tcover.name = \"Cover\"\n\tcover.position = Vector2(20, 10)\n\t"
        + "cover.size = Vector2(60, 50)\n\tworld.add_child(cover)\n\t";

    // A Button named SubButton at the SubViewport's (100, 60), 40 x 20, counting its presses: its centre (120, 70) is
    // (340, 180) in the root in Stretched.
    private const string SubButtonBlock =
        "var button := Button.new()\n\t"
        + "button.name = \"SubButton\"\n\t"
        + "button.position = Vector2(100, 60)\n\t"
        + "button.size = Vector2(40, 20)\n\t"
        + "button.set_meta(\"presses\", 0)\n\t"
        + "button.pressed.connect(func() -> void: button.set_meta(\"presses\", int(button.get_meta(\"presses\")) + 1))\n\t"
        + "world.add_child(button)\n\t";

    // Inside Stretched's World: a SubViewportContainer named Inner at (20, 10), 100 x 60, unstretched, over a 100 x 60
    // SubViewport named Deep (the script's deep variable) with physics picking on.
    private const string NestedBlock =
        "var inner := SubViewportContainer.new()\n\t"
        + "inner.name = \"Inner\"\n\t"
        + "world.add_child(inner)\n\t"
        + "var deep := SubViewport.new()\n\t"
        + "deep.name = \"Deep\"\n\t"
        + "deep.physics_object_picking = true\n\t"
        + "deep.size = Vector2i(100, 60)\n\t"
        + "inner.add_child(deep)\n\t"
        + "inner.position = Vector2(20, 10)\n\t"
        + "inner.size = Vector2(100, 60)\n\t";

    // Waits a few physics ticks, so the shapes are in their spaces before a click picks them.
    private const string Settle = "for tick in 3:\n\t\tawait scene_tree.physics_frame\n\treturn true";

    // A SubViewportContainer at (100, 40), 320 x 200, stretched with shrink 2 over a 160 x 100 SubViewport: a SubViewport
    // point p lands at (100, 40) + 2p in the root.
    private static readonly string Stretched = Container(true, 320, 200);

    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea2DUnderAZoomedOffsetCamera2DIsClickedByElement()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + World2DBlock + Camera2DBlock + Area2D("world", "Target2D", 1040, 535, 20) + Settle, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Target2D"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "node2d", "/root/World2D/Target2D", "Area2D", 400, 250);
        Assert.False(clicked["aimedAt"]!.AsObject().ContainsKey("viewport"), clicked.ToJsonString());
        Assert.Equal(1, await PressesAsync("World2D/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnOffsetMovesTheAimInsideTheArea2D()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + World2DBlock + Camera2DBlock + Area2D("world", "Target2D", 1040, 535, 20) + Settle, cancellation);

        // (5, -5) world pixels from the origin, doubled by the zoom.
        JsonNode clicked = await ClickAsync(new InputTarget("Target2D", Offset: new InputOffset(5, -5)), cancellation);

        AssertAimed(clicked["aimedAt"]!, "node2d", "/root/World2D/Target2D", "Area2D", 410, 240);
        Assert.Equal(1, await PressesAsync("World2D/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea2DOnAScaledOffsetCanvasLayerIsClickedWithAnOffset()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // The layer scales by 2 and moves by (50, 20): local (100, 80) plus offset (5, 5) lands at (260, 190).
        await RunAsync(
            HideMain
                + "var world := CanvasLayer.new()\n\tworld.name = \"Layer\"\n\tworld.offset = Vector2(50, 20)\n\t"
                + "world.scale = Vector2(2, 2)\n\tscene_tree.root.add_child(world)\n\t"
                + Area2D("world", "Target2D", 100, 80, 20)
                + Settle,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Target2D", Offset: new InputOffset(5, 5)), cancellation);

        AssertAimed(clicked["aimedAt"]!, "node2d", "/root/Layer/Target2D", "Area2D", 260, 190);
        Assert.Equal(1, await PressesAsync("Layer/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea3DUnderACamera3DInTheRootIsClickedByElement()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + World3DBlock + Camera3DBlock + Area3D("world", "Vector3(1, 0, 0)") + Settle, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Target3D"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "node3d", "/root/World3D/Target3D", "Area3D", 366.9, 180);
        Assert.Equal(1, await PressesAsync("World3D/Target3D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea2DInASubViewportContainerIsClickedThroughTheShrink()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + Stretched + Area2D("world", "Target2D", 40, 30, 10) + Settle, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Target2D"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "node2d", "/root/Screen/World/Target2D", "Area2D", 180, 100);
        Assert.Equal("/root/Screen/World", clicked["aimedAt"]!["viewport"]!.GetValue<string>());
        Assert.Equal(1, await PressesAsync("Screen/World/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea2DInAnUnstretchedContainerIsClickedWithoutTheShrink()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + Container(false, 320, 200) + Area2D("world", "Target2D", 40, 30, 10) + Settle, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Target2D"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "node2d", "/root/Screen/World/Target2D", "Area2D", 140, 70);
        Assert.Equal(1, await PressesAsync("Screen/World/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea2DInNestedContainersIsClickedThroughBoth()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // Deep's (30, 20) is Inner's local (30, 20), World's (50, 30), and the root's (100, 40) + 2 * (50, 30).
        await RunAsync(HideMain + Stretched + NestedBlock + Area2D("deep", "Target2D", 30, 20, 10) + Settle, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Target2D"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "node2d", "/root/Screen/World/Inner/Deep/Target2D", "Area2D", 200, 100);
        Assert.Equal("/root/Screen/World/Inner/Deep", clicked["aimedAt"]!["viewport"]!.GetValue<string>());
        Assert.Equal(1, await PressesAsync("Screen/World/Inner/Deep/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHoverAimsIntoNestedContainersWhoseOuterSubViewportIgnoresInput()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            HideMain + Stretched + NestedBlock + "world.gui_disable_input = true\n\t" + Area2D("deep", "Target2D", 30, 20, 10) + Settle,
            cancellation
        );

        JsonNode hovered = await HoverAsync(new InputTarget("Target2D"), cancellation);

        AssertAimed(hovered["aimedAt"]!, "node2d", "/root/Screen/World/Inner/Deep/Target2D", "Area2D", 200, 100);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea3DInASubViewportContainerIsClickedThroughItsCamera()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // The SubViewport's camera centres the area at (80, 50) of its 160 x 100.
        await RunAsync(HideMain + Stretched + Camera3DBlock + Area3D("world", "Vector3.ZERO") + Settle, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Target3D"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "node3d", "/root/Screen/World/Target3D", "Area3D", 260, 140);
        Assert.Equal("/root/Screen/World", clicked["aimedAt"]!["viewport"]!.GetValue<string>());
        Assert.Equal(1, await PressesAsync("Screen/World/Target3D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AControlInASubViewportContainerIsClickedAtItsCentreInTheRoot()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + Stretched + SubButtonBlock + Settle, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("SubButton"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "control", "/root/Screen/World/SubButton", "Button", 340, 180);
        Assert.EndsWith("/Screen", clicked["pressedOn"]!["path"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, await PressesAsync("Screen/World/SubButton", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AControlInAWindowOpenedFromInsideAContainerIsClickedWhereTheRootShowsIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // The window's position is in its embedder's coordinates, the root's, not the SubViewport's it hangs under.
        JsonNode expected = await RunAsync(
            HideMain
                + Stretched
                + "var opener := Control.new()\n\topener.name = \"Opener\"\n\tworld.add_child(opener)\n\t"
                + "var panel := Window.new()\n\tpanel.name = \"Panel\"\n\tpanel.position = Vector2i(200, 100)\n\t"
                + "panel.size = Vector2i(160, 80)\n\topener.add_child(panel)\n\t"
                + "var button := Button.new()\n\tbutton.name = \"PanelButton\"\n\tbutton.position = Vector2(10, 10)\n\t"
                + "button.size = Vector2(60, 30)\n\tbutton.set_meta(\"presses\", 0)\n\t"
                + "button.pressed.connect(func() -> void: button.set_meta(\"presses\", int(button.get_meta(\"presses\")) + 1))\n\t"
                + "panel.add_child(button)\n\t"
                + "for frame in 3:\n\t\tawait scene_tree.process_frame\n\t"
                + "return [panel.is_embedded(), Vector2(panel.position) + button.get_global_rect().get_center()]",
            cancellation
        );
        Assert.True(expected[0]!.GetValue<bool>(), expected.ToJsonString());

        JsonNode clicked = await ClickAsync(new InputTarget("PanelButton"), cancellation);

        double x = expected[1]!["x"]!.GetValue<double>();
        double y = expected[1]!["y"]!.GetValue<double>();
        AssertAimed(clicked["aimedAt"]!, "control", "/root/Screen/World/Opener/Panel/PanelButton", "Button", x, y);
        Assert.Equal(1, await PressesAsync("Screen/World/Opener/Panel/PanelButton", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AControlInARootAndAPointTargetReportAimedAtOnlyForTheElement()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonNode byElement = await ClickAsync(new InputTarget("SmallButton"), cancellation);
        JsonNode byPoint = await ClickAsync(new InputTarget(null, 306, 276), cancellation);

        AssertAimed(byElement["aimedAt"]!, "control", "/root/Main/SmallButton", "Button", 306, 276);
        Assert.False(byElement["aimedAt"]!.AsObject().ContainsKey("viewport"), byElement.ToJsonString());
        Assert.False(byPoint.AsObject().ContainsKey("aimedAt"), byPoint.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADragBetweenTwoWorldNodesReportsBothAims()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            HideMain + World2DBlock + Area2D("world", "Source", 100, 100, 20) + Area2D("world", "Sink", 300, 100, 20) + Settle,
            cancellation
        );

        JsonNode dragged = JsonNode.Parse(
            await _tools.DragAsync(new InputTarget("Source"), new InputTarget("Sink"), 100, "left", cancellationToken: cancellation)
        )!;

        AssertAimed(dragged["aimedAt"]!["from"]!, "node2d", "/root/World2D/Source", "Area2D", 100, 100);
        AssertAimed(dragged["aimedAt"]!["to"]!, "node2d", "/root/World2D/Sink", "Area2D", 300, 100);
        Assert.Equal(1, await PressesAsync("World2D/Source", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task HoverAndMouseButtonAimAtAWorldNode()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + World2DBlock + Area2D("world", "Target2D", 400, 250, 20) + Settle, cancellation);
        InputTarget target = new("Target2D");

        JsonNode hovered = await HoverAsync(target, cancellation);
        JsonNode pressed = JsonNode.Parse(await _tools.MouseButtonAsync(target, "left", "press", cancellationToken: cancellation))!;
        await _tools.MouseButtonAsync(target, "left", "release", cancellationToken: cancellation);

        AssertAimed(hovered["aimedAt"]!, "node2d", "/root/World2D/Target2D", "Area2D", 400, 250);
        AssertAimed(pressed["aimedAt"]!, "node2d", "/root/World2D/Target2D", "Area2D", 400, 250);
        Assert.Equal(1, await PressesAsync("World2D/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AScrollReachesAWorldNodeUnderAStopControlThatPassesScrolls()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // Main is shown and stops clicks at (400, 250), but passes wheel events, as every Control does by default.
        await RunAsync(World2DBlock + Area2D("world", "Target2D", 400, 250, 20) + Settle, cancellation);

        JsonNode scrolled = JsonNode.Parse(await _tools.ScrollAsync(new InputTarget("Target2D"), "down", 1, cancellationToken: cancellation))!;

        AssertAimed(scrolled["aimedAt"]!, "node2d", "/root/World2D/Target2D", "Area2D", 400, 250);
        Assert.Equal(1, await PressesAsync("World2D/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWorldNodeUnderAStopControlIsRefusedNamingTheControl()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // No camera: world (400, 250) is viewport (400, 250), over Main alone, which stops the mouse.
        await RunAsync(World2DBlock + Area2D("world", "Target2D", 400, 250, 20) + Settle, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Target2D"),
            "/root/World2D/Target2D projects to (400, 250), where /root/Main (Control, mouse_filter Stop, rect 0,0,640,360) "
                + "takes the click before physics picking; aim elsewhere with offset, or at that Control.",
            cancellation
        );
        Assert.Equal(0, await PressesAsync("World2D/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWorldNodeUnderAStopControlInsideASubViewportIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + Stretched + Area2D("world", "Target2D", 40, 30, 10) + SubCoverBlock + Settle, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Target2D"),
            "/root/Screen/World/Target2D projects to (180, 100), where /root/Screen/World/Cover (ColorRect, mouse_filter Stop, "
                + "rect 140,60,120,100) takes the click before physics picking; aim elsewhere with offset, or at that Control.",
            cancellation
        );
        Assert.Equal(0, await PressesAsync("Screen/World/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWorldNodeUnderAnEmbeddedWindowIsRefusedNamingTheWindow()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            HideMain
                + World2DBlock
                + Area2D("world", "Target2D", 400, 250, 20)
                + "var cover := Window.new()\n\tcover.name = \"Cover\"\n\tcover.position = Vector2i(380, 230)\n\t"
                + "cover.size = Vector2i(60, 60)\n\tscene_tree.root.add_child(cover)\n\t"
                + Settle,
            cancellation
        );

        await AssertRefusedAsync(
            new InputTarget("Target2D"),
            "/root/World2D/Target2D projects to (400, 250), inside the embedded window /root/Cover, which takes the event before "
                + "physics picking.",
            cancellation
        );
        Assert.Equal(0, await PressesAsync("World2D/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWorldNodeWhosePointerLandsBesideItsContainerIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            HideMain
                + Stretched
                + Area2D("world", "Target2D", 40, 30, 10)
                + "var blocker := ColorRect.new()\n\tblocker.name = \"Blocker\"\n\tblocker.position = Vector2(100, 40)\n\t"
                + "blocker.size = Vector2(320, 200)\n\tscene_tree.root.add_child(blocker)\n\t"
                + Settle,
            cancellation
        );

        await AssertRefusedAsync(
            new InputTarget("Target2D"),
            "/root/Screen/World/Target2D projects to (180, 100), where the pointer lands on /root/Blocker, not on /root/Screen, "
                + "the SubViewportContainer that forwards input to /root/Screen/World.",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickIntoASubViewportWhoseInputIsDisabledIsRefusedAndAHoverAims()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + Stretched + "world.gui_disable_input = true\n\t" + Area2D("world", "Target2D", 40, 30, 10) + Settle, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Target2D"),
            "/root/Screen/World/Target2D is in SubViewport /root/Screen/World, whose gui_disable_input is on, so no input reaches "
                + "it; hover and mouse_button move can still aim at a world node there.",
            cancellation
        );
        JsonNode hovered = await HoverAsync(new InputTarget("Target2D"), cancellation);

        AssertAimed(hovered["aimedAt"]!, "node2d", "/root/Screen/World/Target2D", "Area2D", 180, 100);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHoverOnAControlInASubViewportWhoseInputIsDisabledIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + Stretched + "world.gui_disable_input = true\n\t" + SubButtonBlock + Settle, cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.HoverAsync(new InputTarget("SubButton"), new HoverOptions(false), cancellationToken: cancellation)
        );

        Assert.EndsWith(
            "/root/Screen/World/SubButton is in SubViewport /root/Screen/World, whose gui_disable_input is on, so no input reaches it.",
            refused.Message,
            StringComparison.Ordinal
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea2DInAnEmbeddedWindowAimedOutsideTheWindowIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // Panel spans (200, 100) to (360, 180), borderless; the area's (80, 40) plus (200, 0) is (480, 140) in the root.
        await RunAsync(HideMain + WindowBlock("panel", "Panel", 200, 100, 160, 80) + Area2D("panel", "Target2D", 80, 40, 20) + Settle, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Target2D", Offset: new InputOffset(200, 0)),
            "/root/Panel/Target2D projects to (480, 140), outside the embedded window /root/Panel (rect 200,100,160,80).",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea2DInAnEmbeddedWindowAboveAnotherWindowIsClicked()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // Over, added last, sits above Under, and both hold the area's point (280, 140).
        await RunAsync(
            HideMain
                + WindowBlock("under", "Under", 150, 80, 250, 150)
                + WindowBlock("over", "Over", 200, 100, 160, 80)
                + Area2D("over", "Target2D", 80, 40, 20)
                + Settle,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Target2D"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "node2d", "/root/Over/Target2D", "Area2D", 280, 140);
        Assert.Equal("/root/Over", clicked["aimedAt"]!["viewport"]!.GetValue<string>());
        Assert.Equal(1, await PressesAsync("Over/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea3DBehindTheCameraIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + World3DBlock + Camera3DBlock + Area3D("world", "Vector3(0, 0, 10)") + Settle, cancellation);

        await AssertRefusedAsync(new InputTarget("Target3D"), "/root/World3D/Target3D is behind the camera /root/World3D/Camera.", cancellation);
        Assert.Equal(0, await PressesAsync("World3D/Target3D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea3DProjectedOffScreenIsRefusedWithThePointAndTheViewport()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // x = 10 at depth 5 lands near 789 px, right of the 640 px viewport.
        await RunAsync(HideMain + World3DBlock + Camera3DBlock + Area3D("world", "Vector3(10, 0, 0)") + Settle, cancellation);

        McpException refused = await RefusedAsync(new InputTarget("Target3D"), cancellation);

        Assert.Contains("/root/World3D/Target3D projects to (789.", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(", 180), outside the 640x360 viewport.", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await PressesAsync("World3D/Target3D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea2DOffScreenIsRefusedWithThePointAndTheViewport()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + World2DBlock + Area2D("world", "Target2D", 700, 100, 20) + Settle, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Target2D"),
            "/root/World2D/Target2D projects to (700, 100), outside the 640x360 viewport.",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea2DOutsideItsSubViewportIsRefusedWithTheSubViewportsRect()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + Stretched + Area2D("world", "Target2D", 200, 30, 10) + Settle, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Target2D"),
            "/root/Screen/World/Target2D projects to (200, 30) in /root/Screen/World, outside its 160x100 rect.",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea3DWithNoCameraIsRefusedNamingTheViewport()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + World3DBlock + Area3D("world", "Vector3.ZERO") + Settle, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Target3D"),
            "/root/World3D/Target3D is in viewport /root, which has no current Camera3D.",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnArea3DInASubViewportWithNoCameraIsRefusedNamingTheSubViewport()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + Stretched + Area3D("world", "Vector3.ZERO") + Settle, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Target3D"),
            "/root/Screen/World/Target3D is in viewport /root/Screen/World, which has no current Camera3D.",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ANodeInASubViewportWithoutAContainerIsRefusedNamingTheSubViewport()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            HideMain
                + "var loose := Node.new()\n\tloose.name = \"Loose\"\n\tscene_tree.root.add_child(loose)\n\t"
                + "var hidden := SubViewport.new()\n\thidden.name = \"Hidden\"\n\tloose.add_child(hidden)\n\t"
                + "var thing := Node2D.new()\n\tthing.name = \"Thing\"\n\thidden.add_child(thing)\n\t"
                + Settle,
            cancellation
        );

        await AssertRefusedAsync(
            new InputTarget("Thing"),
            "/root/Loose/Hidden/Thing is in SubViewport /root/Loose/Hidden, which has no SubViewportContainer parent; only a "
                + "SubViewportContainer forwards input to a SubViewport.",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AWorldNodeInANativeWindowIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode embedded = await RunAsync(
            HideMain
                + "scene_tree.root.gui_embed_subwindows = false\n\t"
                + "var native := Window.new()\n\tnative.name = \"Native\"\n\t"
                + "native.size = Vector2i(100, 100)\n\tscene_tree.root.add_child(native)\n\t"
                + "var thing := Node2D.new()\n\tthing.name = \"NativeThing\"\n\tnative.add_child(thing)\n\t"
                + "await scene_tree.process_frame\n\treturn native.is_embedded()",
            cancellation
        );
        try
        {
            Assert.False(embedded.GetValue<bool>());

            await AssertRefusedAsync(
                new InputTarget("NativeThing"),
                "/root/Native/NativeThing is in native window /root/Native; input reaches only the root and its embedded windows.",
                cancellation
            );
        }
        finally
        {
            // The root takes embedding back only once the native window is gone, and the reset does not restore it.
            JsonNode restored = await RunAsync(
                "scene_tree.root.get_node(\"Native\").free()\n\tscene_tree.root.gui_embed_subwindows = true\n\t"
                    + "return scene_tree.root.gui_embed_subwindows",
                cancellation
            );
            Assert.True(restored.GetValue<bool>());
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHiddenWorldNodeIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + World2DBlock + Area2D("world", "Target2D", 400, 250, 20) + "target2d.visible = false\n\t" + Settle, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Target2D"),
            "/root/World2D/Target2D is hidden; get_ui_elements lists the visible Controls, get_scene_tree every node.",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnOffsetWithZOnA2DNodeIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(HideMain + World2DBlock + Camera2DBlock + Area2D("world", "Target2D", 1040, 535, 20) + Settle, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Target2D", Offset: new InputOffset(1, 2, 3)),
            "/root/World2D/Target2D is a Area2D; offset takes x and y.",
            cancellation
        );
        Assert.Equal(0, await PressesAsync("World2D/Target2D", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnOffsetOnAControlAndAPlainNodeAreRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        await AssertRefusedAsync(
            new InputTarget("SmallButton", Offset: new InputOffset(1, 2)),
            "offset aims inside a world node; /root/Main/SmallButton is a Control.",
            cancellation
        );
        await AssertRefusedAsync(
            new InputTarget("PadProbe"),
            "'PadProbe' is a Node, neither a Control nor a 2D or 3D node, so it has no point to aim at.",
            cancellation
        );
    }

    // A SubViewportContainer named Screen at (100, 40), width x height, over a SubViewport named World (bound to the script's
    // world variable) with its own 3D world and physics picking on: stretched with shrink 2, or unstretched over a 320 x 200
    // SubViewport.
    private static string Container(bool stretch, int width, int height) =>
        "var container := SubViewportContainer.new()\n\t"
        + "container.name = \"Screen\"\n\t"
        + $"container.stretch = {(stretch ? "true" : "false")}\n\t"
        + "container.stretch_shrink = 2\n\t"
        + "scene_tree.root.add_child(container)\n\t"
        + "var world := SubViewport.new()\n\t"
        + "world.name = \"World\"\n\t"
        + "world.own_world_3d = true\n\t"
        + "world.physics_object_picking = true\n\t"
        + "container.add_child(world)\n\t"
        + "container.position = Vector2(100, 40)\n\t"
        + $"container.size = Vector2({width}, {height})\n\t"
        + (stretch ? "" : "world.size = Vector2i(320, 200)\n\t");

    // An Area2D named name under the script's parent variable at (x, y) with a size x size square shape, counting the
    // mouse presses its input_event delivers in its "presses" meta; the script variable named name in lower case holds it.
    private static string Area2D(string parent, string name, int x, int y, int size)
    {
        string area = name.ToLowerInvariant();
        return $"var {area} := Area2D.new()\n\t"
            + $"{area}.name = \"{name}\"\n\t"
            + $"{area}.position = Vector2({x}, {y})\n\t"
            + $"var {area}_shape := CollisionShape2D.new()\n\t"
            + $"var {area}_square := RectangleShape2D.new()\n\t"
            + $"{area}_square.size = Vector2({size}, {size})\n\t"
            + $"{area}_shape.shape = {area}_square\n\t"
            + $"{area}.add_child({area}_shape)\n\t"
            + $"{area}.set_meta(\"presses\", 0)\n\t"
            + $"{area}.input_event.connect(func(_viewport: Node, event: InputEvent, _shape: int) -> void:\n\t\t"
            + "if event is InputEventMouseButton and event.pressed:\n\t\t\t"
            + $"{area}.set_meta(\"presses\", int({area}.get_meta(\"presses\")) + 1))\n\t"
            + $"{parent}.add_child({area})\n\t";
    }

    // An Area3D named Target3D under the script's parent variable at position with a unit box shape, counting presses as
    // Area2D does.
    private static string Area3D(string parent, string position) =>
        "var area := Area3D.new()\n\t"
        + "area.name = \"Target3D\"\n\t"
        + $"area.position = {position}\n\t"
        + "var shape := CollisionShape3D.new()\n\t"
        + "shape.shape = BoxShape3D.new()\n\t"
        + "area.add_child(shape)\n\t"
        + "area.set_meta(\"presses\", 0)\n\t"
        + "area.input_event.connect(func(_camera: Node, event: InputEvent, _at: Vector3, _normal: Vector3, _shape: int) -> void:\n\t\t"
        + "if event is InputEventMouseButton and event.pressed:\n\t\t\t"
        + "area.set_meta(\"presses\", int(area.get_meta(\"presses\")) + 1))\n\t"
        + $"{parent}.add_child(area)\n\t";

    // A borderless embedded Window named name under the root at (x, y), width x height, with physics picking on; the
    // script variable named variable holds it.
    private static string WindowBlock(string variable, string name, int x, int y, int width, int height) =>
        $"var {variable} := Window.new()\n\t"
        + $"{variable}.name = \"{name}\"\n\t"
        + $"{variable}.borderless = true\n\t"
        + $"{variable}.physics_object_picking = true\n\t"
        + $"{variable}.position = Vector2i({x}, {y})\n\t"
        + $"{variable}.size = Vector2i({width}, {height})\n\t"
        + $"scene_tree.root.add_child({variable})\n\t";

    private static void AssertAimed(JsonNode aimed, string kind, string path, string className, double x, double y)
    {
        Assert.Equal(kind, aimed["kind"]!.GetValue<string>());
        Assert.Equal(path, aimed["path"]!.GetValue<string>());
        Assert.Equal(className, aimed["class"]!.GetValue<string>());
        Assert.Equal(x, aimed["x"]!.GetValue<double>(), 0.5);
        Assert.Equal(y, aimed["y"]!.GetValue<double>(), 0.5);
    }

    private async Task AssertRefusedAsync(InputTarget target, string expected, CancellationToken cancellation)
    {
        McpException refused = await RefusedAsync(target, cancellation);
        Assert.True(refused.Message.EndsWith(expected, StringComparison.Ordinal), refused.Message);
    }

    private async Task<JsonNode> ClickAsync(InputTarget target, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.ClickAsync(target, "left", false, cancellationToken: cancellation))!;

    private async Task<JsonNode> HoverAsync(InputTarget target, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.HoverAsync(target, new HoverOptions(false), cancellationToken: cancellation))!;

    private Task<McpException> RefusedAsync(InputTarget target, CancellationToken cancellation) =>
        Assert.ThrowsAsync<McpException>(() => _tools.ClickAsync(target, "left", false, cancellationToken: cancellation));

    // The presses a node under the root counted, read after a physics tick, since picking runs on the physics step.
    private async Task<int> PressesAsync(string path, CancellationToken cancellation) =>
        (
            await RunAsync($"await scene_tree.physics_frame\n\treturn int(scene_tree.root.get_node(\"{path}\").get_meta(\"presses\"))", cancellation)
        ).GetValue<int>();

    private async Task<JsonNode> RunAsync(string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
        return JsonNode.Parse(json)!["value"]!;
    }
}
