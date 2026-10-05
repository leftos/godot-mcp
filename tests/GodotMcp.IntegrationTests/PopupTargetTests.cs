using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// Popup item targets against the InputProbe running in the real Godot: one shared run with the real pads shut out, reset
/// before each test, the widgets built by run_script under the root, above the fixture's Main, and drawn twice before the
/// first gesture.
/// </summary>
public sealed class PopupTargetTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Drawn = "await scene_tree.process_frame\n\tawait scene_tree.process_frame\n\treturn true";
    private const string ReadSelected = "return int(scene_tree.root.get_node(\"Weapon\").get_meta(\"selected\"))";
    private const string ReadOpens = "return int(scene_tree.root.get_node(\"Weapon\").get_meta(\"opens\"))";
    private const string WeaponShown = "return scene_tree.root.get_node(\"Weapon\").get_popup().visible";
    private const string ReadPicked = "return int(scene_tree.root.get_node(\"FileMenu\").get_meta(\"picked\"))";

    private static readonly InputTarget Recent = new("FileMenu", Item: new InputItem(Text: "Recent"));

    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickByTextOrIndexOpensTheOptionButtonAndSelectsTheItem()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Weapon("") + Drawn, cancellation);

        JsonNode byText = await ClickAsync(new InputTarget("Weapon", Item: new InputItem(Text: "Axe")), cancellation);

        Assert.Equal(3, await ReadIntAsync(ReadSelected, cancellation));
        JsonNode aimed = byText["aimedAt"]!;
        AssertAimed(aimed, "/root/Weapon", "OptionButton", 3, "Axe");
        Assert.True(aimed["opened"]!.GetValue<bool>(), aimed.ToJsonString());
        Assert.Equal("PopupMenuItems", byText["pressedOn"]!["class"]!.GetValue<string>());
        // The opening press set Input's mouse button mask before the popup showed, which PopupMenu's grabbed-click guard
        // reads at NOTIFICATION_POST_POPUP (scene/gui/popup_menu.cpp L1566-1568 in 4.7.2): MOUSE_BUTTON_MASK_LEFT.
        Assert.Equal(1, await ReadIntAsync("return int(scene_tree.root.get_node(\"Weapon\").get_meta(\"mask\"))", cancellation));
        Assert.False((await RunAsync(WeaponShown, cancellation)).GetValue<bool>());

        JsonNode byIndex = await ClickAsync(new InputTarget("Weapon", Item: new InputItem(Index: 4)), cancellation);

        Assert.Equal(4, await ReadIntAsync(ReadSelected, cancellation));
        AssertAimed(byIndex["aimedAt"]!, "/root/Weapon", "OptionButton", 4, "Spear");
        Assert.True(byIndex["aimedAt"]!["opened"]!.GetValue<bool>(), byIndex.ToJsonString());
        Assert.Equal(2, await ReadIntAsync(ReadOpens, cancellation));
    }

    // An embedded popup taking focus fires the root Window's focus_exited (scene/main/viewport.cpp L465-470 in 4.7.2), with
    // no change of application focus; a shut-out re-assert answering it sends an application focus-out, on which a Popup
    // hides itself (scene/gui/popup.cpp L114-121).
    [Fact(Timeout = TestTimeoutMs)]
    public async Task APopupMenuStaysOpenUnderShutOut()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Weapon("") + Drawn, cancellation);
        Assert.True(
            (await RunAsync("return scene_tree.root.get_node(\"GodotMcpBridge/Gamepad\").real_pads_shut_out", cancellation)).GetValue<bool>()
        );

        await ClickAsync(new InputTarget("Weapon"), cancellation);
        await RunAsync(Drawn, cancellation);

        Assert.True((await RunAsync(WeaponShown, cancellation)).GetValue<bool>(), "Weapon's popup closed after the click opened it");
        Assert.Equal(1, await ReadIntAsync(ReadOpens, cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickIntoAnOpenPopupDoesNotOpenItAgain()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Weapon("") + "weapon.show_popup()\n\t" + Drawn, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Weapon", Item: new InputItem(Text: "Bow")), cancellation);

        Assert.Equal(1, await ReadIntAsync(ReadSelected, cancellation));
        AssertAimed(clicked["aimedAt"]!, "/root/Weapon", "OptionButton", 1, "Bow");
        Assert.False(clicked["aimedAt"]!.AsObject().ContainsKey("opened"), clicked.ToJsonString());
        Assert.Equal(1, await ReadIntAsync(ReadOpens, cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AMenuButtonsSubmenuItemIsReachedInTwoCalls()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(FileMenu("") + Drawn, cancellation);

        JsonNode held = await ClickAsync(Recent, cancellation);

        JsonNode aimed = held["aimedAt"]!;
        AssertAimed(aimed, "/root/FileMenu", "MenuButton", 1, "Recent");
        Assert.True(aimed["opened"]!.GetValue<bool>(), aimed.ToJsonString());
        string submenu = await AssertSubmenuShownAsync(aimed, cancellation);
        Assert.Equal(-1, await ReadIntAsync(ReadPicked, cancellation));

        JsonNode picked = await ClickAsync(new InputTarget(submenu, Item: new InputItem(Text: "Two")), cancellation);

        Assert.Equal(22, await ReadIntAsync(ReadPicked, cancellation));
        AssertAimed(picked["aimedAt"]!, submenu, "PopupMenu", 1, "Two");
        Assert.False(picked["aimedAt"]!.AsObject().ContainsKey("viewport"), picked.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHoverOnASubmenuItemOpensTheSubmenuAndPressesNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(FileMenu("menu.show_popup()\n\t") + Drawn, cancellation);

        JsonNode hovered = JsonNode.Parse(await _tools.HoverAsync(Recent, cancellationToken: cancellation))!;

        JsonNode aimed = hovered["aimedAt"]!;
        AssertAimed(aimed, "/root/FileMenu", "MenuButton", 1, "Recent");
        await AssertSubmenuShownAsync(aimed, cancellation);
        Assert.False(hovered.AsObject().ContainsKey("pressedOn"), hovered.ToJsonString());
        Assert.Equal(0, hovered["heldButtonMask"]!.GetValue<int>());
        Assert.Equal(-1, await ReadIntAsync(ReadPicked, cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AReleaseOnASubmenuItemIsSentSoNoButtonStaysHeld()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(FileMenu("menu.show_popup()\n\t") + Drawn, cancellation);

        JsonNode pressed = await MouseButtonAsync(new InputTarget("FileMenu", Item: new InputItem(Text: "New")), "press", cancellation);
        Assert.Equal(1, pressed["heldButtonMask"]!.GetValue<int>());

        JsonNode released = await MouseButtonAsync(Recent, "release", cancellation);

        Assert.Equal(0, released["heldButtonMask"]!.GetValue<int>());
        Assert.Equal("PopupMenuItems", released["releasedOn"]!["class"]!.GetValue<string>());
        Assert.Equal(0, await ReadIntAsync("return Input.get_mouse_button_mask()", cancellation));
        JsonNode next = await MouseButtonAsync(new InputTarget(null, 500, 300), "move", cancellation);
        Assert.Equal(0, next["heldButtonMask"]!.GetValue<int>());
        Assert.Equal(-1, await ReadIntAsync(ReadPicked, cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APressThenAReleaseOnAPopupItemSelectsItOnTheRelease()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Weapon("") + Drawn, cancellation);
        InputTarget bow = new("Weapon", Item: new InputItem(Text: "Bow"));

        JsonNode pressed = await MouseButtonAsync(bow, "press", cancellation);

        Assert.True(pressed["aimedAt"]!["opened"]!.GetValue<bool>(), pressed.ToJsonString());
        Assert.Equal(-1, await ReadIntAsync(ReadSelected, cancellation));

        JsonNode released = await MouseButtonAsync(bow, "release", cancellation);

        Assert.Equal(1, await ReadIntAsync(ReadSelected, cancellation));
        AssertAimed(released["aimedAt"]!, "/root/Weapon", "OptionButton", 1, "Bow");
        Assert.Equal(0, released["heldButtonMask"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnItemTheProbeCannotReachSaysThePopupItOpenedIsOpen()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Weapon("for index in 80:\n\t\tweapon.add_item(\"Extra %d\" % index)\n\t") + Drawn, cancellation);

        McpException refused = await RefusedAsync(new InputTarget("Weapon", Item: new InputItem(Text: "Extra 79")), cancellation);

        Assert.Contains("could not find 'Extra 79' in the popup of /root/Weapon within", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("scroll the popup first; the popup is open now", refused.Message, StringComparison.Ordinal);
        Assert.True((await RunAsync(WeaponShown, cancellation)).GetValue<bool>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADisabledButtonsClosedPopupIsRefusedWithoutAClick()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Weapon("weapon.disabled = true\n\t") + Drawn, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Weapon", Item: new InputItem(Text: "Bow")),
            "/root/Weapon is disabled; it cannot open its popup",
            cancellation
        );
        Assert.Equal(0, await ReadIntAsync(ReadOpens, cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AVisiblePopupPanelWithPopupWindowOffDoesNotBlockTheOpeningClick()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Weapon("")
                + "var panel := PopupPanel.new()\n\tpanel.name = \"Panel\"\n\tpanel.popup_window = false\n\t"
                + "panel.position = Vector2i(480, 250)\n\tpanel.size = Vector2i(100, 60)\n\t"
                + "scene_tree.root.add_child(panel)\n\tpanel.show()\n\t"
                + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Weapon", Item: new InputItem(Text: "Bow")), cancellation);

        Assert.Equal(1, await ReadIntAsync(ReadSelected, cancellation));
        Assert.True(clicked["aimedAt"]!["opened"]!.GetValue<bool>(), clicked.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TheLastOfThirtyItemsIsFoundWithinTheProbesBudget()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode height = await RunAsync(
            "var long := PopupMenu.new()\n\tlong.name = \"Long\"\n\t"
                + "long.add_theme_font_size_override(\"font_size\", 6)\n\tlong.add_theme_constant_override(\"v_separation\", 0)\n\t"
                + "for index in 30:\n\t\tlong.add_item(\"Item %d\" % index)\n\t"
                + "long.set_meta(\"pressed\", -1)\n\t"
                + "long.index_pressed.connect(func(index: int) -> void: long.set_meta(\"pressed\", index))\n\t"
                + "scene_tree.root.add_child(long)\n\t"
                + "long.popup(Rect2i(300, 0, 160, 0))\n\t"
                + "await scene_tree.process_frame\n\tawait scene_tree.process_frame\n\t"
                + "return long.position.y + long.size.y",
            cancellation
        );
        Assert.InRange(height.GetValue<double>(), 1, 360);

        JsonNode clicked = await ClickAsync(new InputTarget("Long", Item: new InputItem(Text: "Item 29")), cancellation);

        Assert.Equal(29, await ReadIntAsync("return int(scene_tree.root.get_node(\"Long\").get_meta(\"pressed\"))", cancellation));
        AssertAimed(clicked["aimedAt"]!, "/root/Long", "PopupMenu", 29, "Item 29");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ItemsThatTakeNoPressAndGesturesThatCannotReachThemAreRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Weapon("weapon.set_item_disabled(5, true)\n\t")
                + "var loose := PopupMenu.new()\n\tloose.name = \"Loose\"\n\tloose.add_item(\"A\")\n\tloose.add_item(\"B\")\n\t"
                + "scene_tree.root.add_child(loose)\n\t"
                + Drawn,
            cancellation
        );
        InputTarget bow = new("Weapon", Item: new InputItem(Text: "Bow"));

        await AssertRefusedAsync(
            new InputTarget("Weapon", Item: new InputItem(Text: "Club")),
            "'Club' in the popup of /root/Weapon is disabled; Godot ignores a press on it",
            cancellation
        );
        await AssertRefusedAsync(
            new InputTarget("Weapon", Item: new InputItem(Index: 2)),
            "item 2 in the popup of /root/Weapon is a separator; Godot ignores a press on it",
            cancellation
        );
        await AssertRefusedAsync(new InputTarget("Loose", Item: new InputItem(Text: "A")), "/root/Loose is not open; open it first", cancellation);
        McpException hover = await Assert.ThrowsAsync<McpException>(() => _tools.HoverAsync(bow, cancellationToken: cancellation));
        Assert.EndsWith("the popup of /root/Weapon is closed; click the button first", hover.Message, StringComparison.Ordinal);
        McpException from = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DragAsync(bow, new InputTarget(null, 500, 300), 300, "left", cancellationToken: cancellation)
        );
        Assert.EndsWith("from: an item in a popup cannot start or end a drag; click it instead", from.Message, StringComparison.Ordinal);
        McpException to = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DragAsync(new InputTarget(null, 500, 300), bow, 300, "left", cancellationToken: cancellation)
        );
        Assert.EndsWith("to: an item in a popup cannot start or end a drag; click it instead", to.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ReadIntAsync(ReadOpens, cancellation));

        await RunAsync("scene_tree.root.get_node(\"Loose\").popup(Rect2i(400, 200, 120, 0))\n\t" + Drawn, cancellation);

        await AssertRefusedAsync(
            bow,
            "the popup of /root/Weapon is closed and /root/Loose is open, so the press that would open it only closes that one; " + "close it first",
            cancellation
        );
        Assert.True((await RunAsync("return scene_tree.root.get_node(\"Loose\").visible", cancellation)).GetValue<bool>());
        Assert.Equal(0, await ReadIntAsync(ReadOpens, cancellation));
        Assert.Equal(-1, await ReadIntAsync(ReadSelected, cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ANativePopupAndAFilteredOneAreRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode barShown = await RunAsync(
            Weapon("weapon.get_popup().force_native = true\n\t")
                + "var finder := PopupMenu.new()\n\tfinder.name = \"Finder\"\n\tfinder.search_bar_enabled = true\n\t"
                + "for fruit: String in [\"Apple\", \"Banana\", \"Cherry\"]:\n\t\tfinder.add_item(fruit)\n\t"
                + "var dried := PopupMenu.new()\n\tdried.name = \"Dried\"\n\tdried.add_item(\"Fig\")\n\t"
                + "finder.add_submenu_node_item(\"Dried\", dried)\n\t"
                + "scene_tree.root.add_child(finder)\n\t"
                + "finder.popup(Rect2i(300, 40, 160, 0))\n\t"
                + "await scene_tree.process_frame\n\tawait scene_tree.process_frame\n\t"
                + "var bar: LineEdit = null\n\tvar stack: Array[Node] = [finder]\n\t"
                + "while not stack.is_empty():\n\t\tvar node: Node = stack.pop_back()\n\t\t"
                + "if node is LineEdit:\n\t\t\tbar = node\n\t\tstack.append_array(node.get_children(true))\n\t"
                + "bar.text = \"an\"\n\t"
                + "return bar.visible",
            cancellation
        );
        Assert.True(barShown.GetValue<bool>());

        McpException native = await RefusedAsync(new InputTarget("Weapon", Item: new InputItem(Text: "Bow")), cancellation);
        Assert.Contains("the popup of /root/Weapon is a native menu", native.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ReadIntAsync(ReadOpens, cancellation));

        await AssertRefusedAsync(
            new InputTarget("Finder", Item: new InputItem(Text: "Banana")),
            "/root/Finder filters its items by the search 'an'; hidden items cannot be told apart, clear the search first",
            cancellation
        );
        // A search filters every submenu below its popup with its own query (scene/gui/popup_menu.cpp L1143-1147 in 4.7.2).
        await AssertRefusedAsync(
            new InputTarget("Dried", Item: new InputItem(Text: "Fig")),
            "/root/Finder filters its items by the search 'an'; hidden items cannot be told apart, clear the search first",
            cancellation
        );
    }

    // An OptionButton named Weapon at (40, 40), 160 x 30, held by the script variable weapon, with extra run before it is
    // added: Sword, Bow, a separator (index 2), Axe with a red icon, Spear and Club. Its "selected" meta keeps the index its
    // item_selected last sent (-1 before any), "opens" counts its popup's about_to_popup and "mask" keeps Input's mouse
    // button mask at the last.
    private static string Weapon(string extra) =>
        "var weapon := OptionButton.new()\n\tweapon.name = \"Weapon\"\n\t"
        + "weapon.position = Vector2(40, 40)\n\tweapon.size = Vector2(160, 30)\n\t"
        + "weapon.add_item(\"Sword\")\n\tweapon.add_item(\"Bow\")\n\tweapon.add_separator()\n\t"
        + "var image := Image.create(16, 16, false, Image.FORMAT_RGBA8)\n\timage.fill(Color.RED)\n\t"
        + "weapon.add_icon_item(ImageTexture.create_from_image(image), \"Axe\")\n\t"
        + "weapon.add_item(\"Spear\")\n\tweapon.add_item(\"Club\")\n\t"
        + "weapon.set_meta(\"selected\", -1)\n\tweapon.set_meta(\"opens\", 0)\n\t"
        + "weapon.item_selected.connect(func(index: int) -> void: weapon.set_meta(\"selected\", index))\n\t"
        + "weapon.get_popup().about_to_popup.connect(func() -> void: weapon.set_meta(\"mask\", Input.get_mouse_button_mask()))\n\t"
        + "weapon.get_popup().about_to_popup.connect(func() -> void: weapon.set_meta(\"opens\", int(weapon.get_meta(\"opens\")) + 1))\n\t"
        + extra
        + "scene_tree.root.add_child(weapon)\n\t";

    // A MenuButton named FileMenu at (300, 40), 100 x 30, held by the script variable menu, with extra run before it is added:
    // New (id 10) and Recent (id 11), whose submenu Recent holds One (21) and Two (22). Its "picked" meta keeps the id either
    // popup's id_pressed last sent (-1 before any).
    private static string FileMenu(string extra) =>
        "var menu := MenuButton.new()\n\tmenu.name = \"FileMenu\"\n\tmenu.text = \"File\"\n\t"
        + "menu.position = Vector2(300, 40)\n\tmenu.size = Vector2(100, 30)\n\t"
        + "menu.get_popup().add_item(\"New\", 10)\n\t"
        + "var recent := PopupMenu.new()\n\trecent.name = \"Recent\"\n\t"
        + "recent.add_item(\"One\", 21)\n\trecent.add_item(\"Two\", 22)\n\t"
        + "menu.get_popup().add_submenu_node_item(\"Recent\", recent, 11)\n\t"
        + "menu.set_meta(\"picked\", -1)\n\t"
        + "menu.get_popup().id_pressed.connect(func(id: int) -> void: menu.set_meta(\"picked\", id))\n\t"
        + "recent.id_pressed.connect(func(id: int) -> void: menu.set_meta(\"picked\", id))\n\t"
        + "scene_tree.root.add_child(menu)\n\t"
        + extra;

    private async Task<string> AssertSubmenuShownAsync(JsonNode aimed, CancellationToken cancellation)
    {
        string submenu = aimed["item"]!["submenu"]!.GetValue<string>();
        Assert.EndsWith("/Recent", submenu, StringComparison.Ordinal);
        Assert.True((await RunAsync($"return scene_tree.root.get_node(\"{submenu}\").visible", cancellation)).GetValue<bool>());
        return submenu;
    }

    private async Task<JsonNode> MouseButtonAsync(InputTarget target, string action, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.MouseButtonAsync(target, "left", action, cancellationToken: cancellation))!;

    private static void AssertAimed(JsonNode aimed, string path, string className, int index, string text)
    {
        Assert.Equal("item", aimed["kind"]!.GetValue<string>());
        Assert.Equal(path, aimed["path"]!.GetValue<string>());
        Assert.Equal(className, aimed["class"]!.GetValue<string>());
        Assert.Equal(index, aimed["item"]!["index"]!.GetValue<int>());
        Assert.Equal(text, aimed["item"]!["text"]!.GetValue<string>());
    }

    private async Task AssertRefusedAsync(InputTarget target, string expected, CancellationToken cancellation)
    {
        McpException refused = await RefusedAsync(target, cancellation);
        Assert.True(refused.Message.EndsWith(expected, StringComparison.Ordinal), refused.Message);
    }

    private async Task<JsonNode> ClickAsync(InputTarget target, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.ClickAsync(target, "left", false, cancellationToken: cancellation))!;

    private Task<McpException> RefusedAsync(InputTarget target, CancellationToken cancellation) =>
        Assert.ThrowsAsync<McpException>(() => _tools.ClickAsync(target, "left", false, cancellationToken: cancellation));

    private async Task<int> ReadIntAsync(string body, CancellationToken cancellation) => (await RunAsync(body, cancellation)).GetValue<int>();

    private async Task<JsonNode> RunAsync(string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
        return JsonNode.Parse(json)!["value"]!;
    }
}
