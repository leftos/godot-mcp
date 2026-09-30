using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// Item targets against the InputProbe running in the real Godot: one shared run, reset before each test, the lists built by
/// run_script under the root, above the fixture's Main, which fills the viewport, and drawn twice before the first gesture.
/// </summary>
public sealed class ItemTargetTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Drawn = "await scene_tree.process_frame\n\tawait scene_tree.process_frame\n\treturn true";

    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickByTextOrIndexSelectsTheItemListItem()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(ItemList("Rows", 400, 40, 200, 150, "Alex", "Sam", "Kit") + Drawn, cancellation);

        JsonNode byText = await ClickAsync(new InputTarget("Rows", Item: new InputItem(Text: " Sam ")), cancellation);

        Assert.Equal(1, await ReadIntAsync("return int(scene_tree.root.get_node(\"Rows\").get_meta(\"selected\"))", cancellation));
        JsonNode aimed = byText["aimedAt"]!;
        AssertAimed(aimed, "/root/Rows", "ItemList", 1, "Sam");
        Assert.False(aimed["item"]!.AsObject().ContainsKey("path"), aimed.ToJsonString());
        Assert.False(aimed["item"]!.AsObject().ContainsKey("disabled"), aimed.ToJsonString());
        JsonNode centre = await RunAsync(
            "var rows: ItemList = scene_tree.root.get_node(\"Rows\")\n\t"
                + "var point: Vector2 = rows.get_global_transform_with_canvas() * rows.get_item_rect(1).get_center()\n\t"
                + "return [point.x, point.y]",
            cancellation
        );
        Assert.Equal(centre[0]!.GetValue<double>(), aimed["x"]!.GetValue<double>(), 0.5);
        Assert.Equal(centre[1]!.GetValue<double>(), aimed["y"]!.GetValue<double>(), 0.5);

        JsonNode byIndex = await ClickAsync(new InputTarget("Rows", Item: new InputItem(Index: 2)), cancellation);

        Assert.Equal(2, await ReadIntAsync("return int(scene_tree.root.get_node(\"Rows\").get_meta(\"selected\"))", cancellation));
        AssertAimed(byIndex["aimedAt"]!, "/root/Rows", "ItemList", 2, "Kit");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickByTextChangesTheTabOfATabBarAndATabContainer()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            TabBar("Seats", 20, 20, 300, "Alex", "Sam", "Kit")
                + "var book := TabContainer.new()\n\tbook.name = \"Book\"\n\tbook.position = Vector2(20, 100)\n\t"
                + "book.size = Vector2(300, 200)\n\tscene_tree.root.add_child(book)\n\t"
                + "for page_name: String in [\"First\", \"Second\"]:\n\t\tvar page := Control.new()\n\t\tpage.name = page_name\n\t\t"
                + "book.add_child(page)\n\t"
                + Drawn,
            cancellation
        );

        JsonNode tab = await ClickAsync(new InputTarget("Seats", Item: new InputItem(Text: "Kit")), cancellation);
        JsonNode page = await ClickAsync(new InputTarget("Book", Item: new InputItem(Text: "Second")), cancellation);

        Assert.Equal(2, await ReadIntAsync("return scene_tree.root.get_node(\"Seats\").current_tab", cancellation));
        Assert.Equal(1, await ReadIntAsync("return scene_tree.root.get_node(\"Book\").current_tab", cancellation));
        AssertAimed(tab["aimedAt"]!, "/root/Seats", "TabBar", 2, "Kit");
        AssertAimed(page["aimedAt"]!, "/root/Book", "TabContainer", 1, "Second");
        Assert.InRange(page["aimedAt"]!["y"]!.GetValue<double>(), 100, 140);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickByPathOrByTextInAColumnSelectsTheTreeItem()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Tree("") + Drawn, cancellation);

        JsonNode byPath = await ClickAsync(new InputTarget("Inv", Item: new InputItem(Path: ["Weapons", "Bow"])), cancellation);

        Assert.Equal("Bow", (await RunAsync("return scene_tree.root.get_node(\"Inv\").get_selected().get_text(0)", cancellation)).GetValue<string>());
        AssertAimed(byPath["aimedAt"]!, "/root/Inv", "Tree", 1, "Bow");
        AssertPath(byPath["aimedAt"]!, "Weapons", "Bow");

        JsonNode byText = await ClickAsync(new InputTarget("Inv", Item: new InputItem(Text: "Sharp", Column: 1)), cancellation);

        JsonNode selected = await RunAsync(
            "var inv: Tree = scene_tree.root.get_node(\"Inv\")\n\treturn [inv.get_selected().get_text(0), inv.get_selected_column()]",
            cancellation
        );
        Assert.Equal("Sword", selected[0]!.GetValue<string>());
        Assert.Equal(1, selected[1]!.GetValue<int>());
        AssertAimed(byText["aimedAt"]!, "/root/Inv", "Tree", 0, "Sharp");
        AssertPath(byText["aimedAt"]!, "Weapons", "Sword");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ItemsReadingTheSameTextAreRefusedListingEach()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(ItemList("Dup", 400, 40, 200, 150, "Deal", "Pass", "Deal") + Drawn, cancellation);

        McpException refused = await RefusedAsync(new InputTarget("Dup", Item: new InputItem(Text: "Deal")), cancellation);

        Assert.Contains("/root/Dup has 2 items reading 'Deal': index 0 at 4", refused.Message, StringComparison.Ordinal);
        Assert.Contains(", index 2 at 4", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("; narrow with item.index", refused.Message, StringComparison.Ordinal);
        Assert.Equal(-1, await ReadIntAsync("return int(scene_tree.root.get_node(\"Dup\").get_meta(\"selected\"))", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHiddenTabIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(TabBar("Seats", 20, 20, 300, "Alex", "Sam", "Kit") + "bar.set_tab_hidden(1, true)\n\t" + Drawn, cancellation);

        await AssertRefusedAsync(new InputTarget("Seats", Item: new InputItem(Text: "Sam")), "item 'Sam' of /root/Seats is hidden", cancellation);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATabBeyondTheDrawnRangeIsRefusedAsNotDrawn()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(TabBar("Narrow", 20, 20, 150, "Tab 0", "Tab 1", "Tab 2", "Tab 3", "Tab 4", "Tab 5", "Tab 6", "Tab 7") + Drawn, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Narrow", Item: new InputItem(Text: "Tab 7")),
            "item 'Tab 7' of /root/Narrow has no drawn rect; it may be outside the tab bar's drawn range (scroll the tabs) or not laid out yet",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnItemUnderACollapsedItemIsRefusedNamingIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Tree("weapons.collapsed = true\n\t") + Drawn, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Inv", Item: new InputItem(Path: ["Weapons", "Sword"])),
            "item 'Sword' of /root/Inv is under the collapsed item [\"Weapons\"]; expand it first",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnItemScrolledOutIsRefusedUntilTheListScrolls()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string[] texts = [.. Enumerable.Range(0, 12).Select(index => $"Item {index}")];
        await RunAsync(ItemList("Short", 400, 40, 200, 60, texts) + Drawn, cancellation);

        McpException refused = await RefusedAsync(new InputTarget("Short", Item: new InputItem(Text: "Item 11")), cancellation);

        Assert.Contains("refused 'input': item 'Item 11' of /root/Short is at 4", refused.Message, StringComparison.Ordinal);
        Assert.Contains(", outside the list's visible rect 400,40,", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(",60; scroll the list first", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("visible rect 400,40,200,60", refused.Message, StringComparison.Ordinal);

        await RunAsync(
            "var bar: VScrollBar = scene_tree.root.get_node(\"Short\").get_v_scroll_bar()\n\tbar.value = bar.max_value\n\t" + Drawn,
            cancellation
        );
        JsonNode clicked = await ClickAsync(new InputTarget("Short", Item: new InputItem(Text: "Item 11")), cancellation);

        Assert.Equal(11, await ReadIntAsync("return int(scene_tree.root.get_node(\"Short\").get_meta(\"selected\"))", cancellation));
        AssertAimed(clicked["aimedAt"]!, "/root/Short", "ItemList", 11, "Item 11");
        Assert.InRange(clicked["aimedAt"]!["y"]!.GetValue<double>(), 40, 100);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADisabledTabIsAimedAtAndReportedDisabled()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(TabBar("Seats", 20, 20, 300, "Alex", "Sam", "Kit") + "bar.set_tab_disabled(2, true)\n\t" + Drawn, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Seats", Item: new InputItem(Text: "Kit")), cancellation);

        AssertAimed(clicked["aimedAt"]!, "/root/Seats", "TabBar", 2, "Kit");
        Assert.True(clicked["aimedAt"]!["item"]!["disabled"]!.GetValue<bool>(), clicked.ToJsonString());
        Assert.Equal(0, await ReadIntAsync("return scene_tree.root.get_node(\"Seats\").current_tab", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APathOnAnItemListIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(ItemList("Rows", 400, 40, 200, 150, "Alex", "Sam") + Drawn, cancellation);

        await AssertRefusedAsync(
            new InputTarget("Rows", Item: new InputItem(Path: ["Alex"])),
            "/root/Rows is a ItemList; item.path is for a Tree only",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHoverOnAnItemReportsTheItemAimedAt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(ItemList("Rows", 400, 40, 200, 150, "Alex", "Sam", "Kit") + Drawn, cancellation);

        JsonNode hovered = JsonNode.Parse(
            await _tools.HoverAsync(new InputTarget("Rows", Item: new InputItem(Text: "Kit")), cancellationToken: cancellation)
        )!;

        AssertAimed(hovered["aimedAt"]!, "/root/Rows", "ItemList", 2, "Kit");
        Assert.Equal(-1, await ReadIntAsync("return int(scene_tree.root.get_node(\"Rows\").get_meta(\"selected\"))", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATreeRowUnderAVisibleScrollBarIsRefusedAsScrolledOut()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Rows("Wide", "wide.set_column_expand(0, false)\n\twide.set_column_custom_minimum_width(0, 260)\n\t")
                + Drawn.Replace("return true", "", StringComparison.Ordinal)
                + "var row: TreeItem = wide.get_root().get_child(10)\n\t"
                + "vbar.value += wide.get_item_area_rect(row, 0).get_center().y - (hbar.position.y + hbar.size.y / 2.0)\n\t"
                + Drawn,
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget("Wide", Item: new InputItem(Text: "Row 10")), cancellation);

        Assert.Contains("item 'Row 10' of /root/Wide is at ", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("; scroll the list first", refused.Message, StringComparison.Ordinal);
        Assert.True((await RunAsync("return scene_tree.root.get_node(\"Wide\").get_selected() == null", cancellation)).GetValue<bool>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATreeRowUnderTheColumnTitlesIsRefusedAsScrolledOut()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Rows("Titled", "wide.column_titles_visible = true\n\twide.set_column_title(0, \"Name\")\n\t")
                + Drawn.Replace("return true", "", StringComparison.Ordinal)
                + "var top: float = wide.get_theme_stylebox(\"panel\").get_offset().y\n\t"
                + "var first: float = wide.get_item_area_rect(wide.get_root().get_child(0), 0).position.y\n\t"
                + "vbar.value = wide.get_item_area_rect(wide.get_root().get_child(10), 0).get_center().y - (top + first) / 2.0\n\t"
                + Drawn,
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget("Titled", Item: new InputItem(Text: "Row 10")), cancellation);

        Assert.Contains("item 'Row 10' of /root/Titled is at ", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("; scroll the list first", refused.Message, StringComparison.Ordinal);
        Assert.True((await RunAsync("return scene_tree.root.get_node(\"Titled\").get_selected() == null", cancellation)).GetValue<bool>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARightToLeftMultiColumnItemListSelectsTheItemClicked()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            ItemList("Rtl", 300, 40, 240, 150, "A1", "B1", "A2", "B2")
                + "list.layout_direction = Control.LAYOUT_DIRECTION_RTL\n\tlist.max_columns = 2\n\tlist.same_column_width = true\n\t"
                + Drawn,
            cancellation
        );

        JsonNode first = await ClickAsync(new InputTarget("Rtl", Item: new InputItem(Text: "A1")), cancellation);

        Assert.Equal(0, await ReadIntAsync("return int(scene_tree.root.get_node(\"Rtl\").get_meta(\"selected\"))", cancellation));
        Assert.InRange(first["aimedAt"]!["x"]!.GetValue<double>(), 300, 540);

        await ClickAsync(new InputTarget("Rtl", Item: new InputItem(Text: "B2")), cancellation);

        Assert.Equal(3, await ReadIntAsync("return int(scene_tree.root.get_node(\"Rtl\").get_meta(\"selected\"))", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADeepTreeItemWithChildrenInANarrowColumnIsSelectedNotFolded()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            "var deep := Tree.new()\n\tdeep.name = \"Deep\"\n\tdeep.hide_root = true\n\tdeep.columns = 2\n\t"
                + "deep.position = Vector2(20, 20)\n\tdeep.size = Vector2(300, 200)\n\t"
                + "deep.set_column_expand(0, false)\n\tdeep.set_column_custom_minimum_width(0, 90)\n\t"
                + "var parent: TreeItem = deep.create_item()\n\t"
                + "for step: String in [\"A\", \"B\", \"C\", \"D\"]:\n\t\tparent = deep.create_item(parent)\n\t\tparent.set_text(0, step)\n\t"
                + "scene_tree.root.add_child(deep)\n\t"
                + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Deep", Item: new InputItem(Path: ["A", "B", "C"])), cancellation);

        JsonNode state = await RunAsync(
            "var deep: Tree = scene_tree.root.get_node(\"Deep\")\n\tvar selected: TreeItem = deep.get_selected()\n\t"
                + "return [selected.get_text(0) if selected != null else \"\", "
                + "deep.get_root().get_child(0).get_child(0).get_child(0).collapsed]",
            cancellation
        );
        Assert.Equal("C", state[0]!.GetValue<string>());
        Assert.False(state[1]!.GetValue<bool>(), clicked.ToJsonString());
        AssertPath(clicked["aimedAt"]!, "A", "B", "C");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnItemListInAShrunkSubViewportIsAimedThroughItsContainer()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            "var box := SubViewportContainer.new()\n\tbox.name = \"Box\"\n\tbox.position = Vector2(300, 20)\n\t"
                + "box.size = Vector2(300, 300)\n\tbox.stretch = true\n\tbox.stretch_shrink = 2\n\tscene_tree.root.add_child(box)\n\t"
                + "var port := SubViewport.new()\n\tport.name = \"Port\"\n\tbox.add_child(port)\n\t"
                + ItemList("Rows", 10, 10, 120, 100, "Alex", "Sam", "Kit")
                    .Replace("scene_tree.root.add_child(list)", "port.add_child(list)", StringComparison.Ordinal)
                + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Rows", Item: new InputItem(Text: "Sam")), cancellation);

        Assert.Equal(1, await ReadIntAsync("return int(scene_tree.root.get_node(\"Box/Port/Rows\").get_meta(\"selected\"))", cancellation));
        JsonNode aimed = clicked["aimedAt"]!;
        AssertAimed(aimed, "/root/Box/Port/Rows", "ItemList", 1, "Sam");
        Assert.Equal("/root/Box/Port", aimed["viewport"]!.GetValue<string>());
        JsonNode centre = await RunAsync(
            "var box: SubViewportContainer = scene_tree.root.get_node(\"Box\")\n\t"
                + "var rows: ItemList = box.get_node(\"Port/Rows\")\n\t"
                + "var inner: Vector2 = rows.get_global_transform_with_canvas() * rows.get_item_rect(1).get_center()\n\t"
                + "var point: Vector2 = box.get_global_transform_with_canvas() * (inner * 2.0)\n\treturn [point.x, point.y]",
            cancellation
        );
        Assert.Equal(centre[0]!.GetValue<double>(), aimed["x"]!.GetValue<double>(), 0.5);
        Assert.Equal(centre[1]!.GetValue<double>(), aimed["y"]!.GetValue<double>(), 0.5);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATabIsMatchedByItsTranslatedTitle()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            "var messages := Translation.new()\n\tmessages.locale = TranslationServer.get_locale()\n\t"
                + "messages.add_message(\"SEAT_ALEX\", \"Alex\")\n\tTranslationServer.add_translation(messages)\n\t"
                + "scene_tree.root.set_meta(\"item_targets_translation\", messages)\n\t"
                + TabBar("Seats", 20, 20, 300, "Sam", "SEAT_ALEX")
                + Drawn,
            cancellation
        );

        try
        {
            JsonNode clicked = await ClickAsync(new InputTarget("Seats", Item: new InputItem(Text: "Alex")), cancellation);

            Assert.Equal(1, await ReadIntAsync("return scene_tree.root.get_node(\"Seats\").current_tab", cancellation));
            AssertAimed(clicked["aimedAt"]!, "/root/Seats", "TabBar", 1, "Alex");
            await AssertRefusedAsync(
                new InputTarget("Seats", Item: new InputItem(Text: "SEAT_ALEX")),
                "/root/Seats has no item 'SEAT_ALEX'; items: 'Sam', 'Alex'",
                cancellation
            );
        }
        finally
        {
            await RunAsync(
                "TranslationServer.remove_translation(scene_tree.root.get_meta(\"item_targets_translation\"))\n\t"
                    + "scene_tree.root.remove_meta(\"item_targets_translation\")\n\treturn true",
                cancellation
            );
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATreePathSkipsAHiddenItemOfTheSameText()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Tree("var ghost: TreeItem = inv.create_item(root, 0)\n\tghost.set_text(0, \"Weapons\")\n\tghost.visible = false\n\t") + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Inv", Item: new InputItem(Path: ["Weapons", "Bow"])), cancellation);

        Assert.Equal("Bow", (await RunAsync("return scene_tree.root.get_node(\"Inv\").get_selected().get_text(0)", cancellation)).GetValue<string>());
        AssertPath(clicked["aimedAt"]!, "Weapons", "Bow");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TreeItemsReadingTheSameTextAreRefusedWithTheirPathsAndRects()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Tree(
                "var twin: TreeItem = inv.create_item(weapons)\n\ttwin.set_text(0, \"Sword\")\n\ttwin.set_text(1, \"Blunt\")\n\t"
                    + "helm.set_text(1, \"Sharp\")\n\t"
            ) + Drawn,
            cancellation
        );

        McpException shared = await RefusedAsync(new InputTarget("Inv", Item: new InputItem(Path: ["Weapons", "Sword"])), cancellation);
        McpException apart = await RefusedAsync(new InputTarget("Inv", Item: new InputItem(Text: "Sharp", Column: 1)), cancellation);

        Assert.Contains("/root/Inv has 2 items reading 'Sword': path [\"Weapons\", \"Sword\"] at 3", shared.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            "; they share the path [\"Weapons\", \"Sword\"]; aim with {x, y} inside one of the rects listed",
            shared.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("/root/Inv has 2 items reading 'Sharp': path [\"Weapons\", \"Sword\"] at ", apart.Message, StringComparison.Ordinal);
        Assert.Contains(", path [\"Armour\", \"Helm\"] at ", apart.Message, StringComparison.Ordinal);
        Assert.EndsWith("; narrow with item.path", apart.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APathFollowsEveryBranchReadingAStep()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Tree(
                "var other: TreeItem = inv.create_item(root, 0)\n\tother.set_text(0, \"Weapons\")\n\tinv.create_item(other).set_text(0, \"Axe\")\n\t"
            ) + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Inv", Item: new InputItem(Path: ["Weapons", "Sword"])), cancellation);

        Assert.Equal(
            "Sword",
            (await RunAsync("return scene_tree.root.get_node(\"Inv\").get_selected().get_text(0)", cancellation)).GetValue<string>()
        );
        AssertPath(clicked["aimedAt"]!, "Weapons", "Sword");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task APathWhoseBranchesBothHoldTheLastStepIsRefusedListingTheLastMatches()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Tree("var other: TreeItem = inv.create_item(root)\n\tother.set_text(0, \"Weapons\")\n\tinv.create_item(other).set_text(0, \"Sword\")\n\t")
                + Drawn,
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget("Inv", Item: new InputItem(Path: ["Weapons", "Sword"])), cancellation);

        Assert.Contains("/root/Inv has 2 items reading 'Sword': path [\"Weapons\", \"Sword\"] at 3", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            "; they share the path [\"Weapons\", \"Sword\"]; aim with {x, y} inside one of the rects listed",
            refused.Message,
            StringComparison.Ordinal
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADeepLeafInANarrowColumnIsPressedAtItsCellCentre()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            "var leafy := Tree.new()\n\tleafy.name = \"Leafy\"\n\tleafy.hide_root = true\n\tleafy.columns = 2\n\t"
                + "leafy.position = Vector2(20, 20)\n\tleafy.size = Vector2(300, 200)\n\t"
                + "leafy.set_column_expand(0, false)\n\tleafy.set_column_custom_minimum_width(0, 60)\n\t"
                + "var parent: TreeItem = leafy.create_item()\n\t"
                + "for step: String in [\"A\", \"B\", \"C\", \"D\"]:\n\t\tparent = leafy.create_item(parent)\n\t\tparent.set_text(0, step)\n\t"
                + "scene_tree.root.add_child(leafy)\n\t"
                + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Leafy", Item: new InputItem(Path: ["A", "B", "C", "D"])), cancellation);

        Assert.Equal("D", (await RunAsync("return scene_tree.root.get_node(\"Leafy\").get_selected().get_text(0)", cancellation)).GetValue<string>());
        AssertPath(clicked["aimedAt"]!, "A", "B", "C", "D");
    }

    // A one-column Tree named name at (20, 20), 200 x 150, its root hidden, holding "Row 0" to "Row 19", held by the script
    // variable wide with extra run before it is added; after Drawn without its return, hbar and vbar hold its scroll bars.
    private static string Rows(string name, string extra) =>
        $"var wide := Tree.new()\n\twide.name = \"{name}\"\n\twide.hide_root = true\n\t"
        + "wide.position = Vector2(20, 20)\n\twide.size = Vector2(200, 150)\n\t"
        + "var rows_root: TreeItem = wide.create_item()\n\t"
        + "for index in 20:\n\t\twide.create_item(rows_root).set_text(0, \"Row %d\" % index)\n\t"
        + extra
        + "scene_tree.root.add_child(wide)\n\t"
        + "var hbar: HScrollBar = null\n\tvar vbar: VScrollBar = null\n\t"
        + "for child: Node in wide.get_children(true):\n\t\tif child is HScrollBar:\n\t\t\thbar = child\n\t\t"
        + "elif child is VScrollBar:\n\t\t\tvbar = child\n\t";

    // An ItemList named name at (x, y), width x height, holding texts, held by the script variable list, keeping the index its
    // item_selected last sent in its "selected" meta (-1 before any).
    private static string ItemList(string name, int x, int y, int width, int height, params string[] texts) =>
        "var list := ItemList.new()\n\t"
        + $"list.name = \"{name}\"\n\t"
        + $"list.position = Vector2({x}, {y})\n\t"
        + $"list.size = Vector2({width}, {height})\n\t"
        + string.Concat(texts.Select(text => $"list.add_item(\"{text}\")\n\t"))
        + "list.set_meta(\"selected\", -1)\n\t"
        + "list.item_selected.connect(func(index: int) -> void: list.set_meta(\"selected\", index))\n\t"
        + "scene_tree.root.add_child(list)\n\t";

    // A 40 px high TabBar named name at (x, y), width wide, with a tab per title, held by the script variable bar.
    private static string TabBar(string name, int x, int y, int width, params string[] titles) =>
        "var bar := TabBar.new()\n\t"
        + $"bar.name = \"{name}\"\n\t"
        + $"bar.position = Vector2({x}, {y})\n\t"
        + $"bar.size = Vector2({width}, 40)\n\t"
        + string.Concat(titles.Select(title => $"bar.add_tab(\"{title}\")\n\t"))
        + "scene_tree.root.add_child(bar)\n\t";

    // Inv, a two-column Tree at (360, 20), 260 x 300, its root hidden: Weapons ("2") holding Sword ("Sharp") and Bow ("Long"),
    // and Armour ("1") holding Helm ("Iron"); weapons holds the Weapons item for extra.
    private static string Tree(string extra) =>
        "var inv := Tree.new()\n\tinv.name = \"Inv\"\n\tinv.columns = 2\n\tinv.hide_root = true\n\t"
        + "inv.position = Vector2(360, 20)\n\tinv.size = Vector2(260, 300)\n\t"
        + "var root: TreeItem = inv.create_item()\n\t"
        + "var weapons: TreeItem = inv.create_item(root)\n\tweapons.set_text(0, \"Weapons\")\n\tweapons.set_text(1, \"2\")\n\t"
        + "var sword: TreeItem = inv.create_item(weapons)\n\tsword.set_text(0, \"Sword\")\n\tsword.set_text(1, \"Sharp\")\n\t"
        + "var bow: TreeItem = inv.create_item(weapons)\n\tbow.set_text(0, \"Bow\")\n\tbow.set_text(1, \"Long\")\n\t"
        + "var armour: TreeItem = inv.create_item(root)\n\tarmour.set_text(0, \"Armour\")\n\tarmour.set_text(1, \"1\")\n\t"
        + "var helm: TreeItem = inv.create_item(armour)\n\thelm.set_text(0, \"Helm\")\n\thelm.set_text(1, \"Iron\")\n\t"
        + extra
        + "scene_tree.root.add_child(inv)\n\t";

    private static void AssertAimed(JsonNode aimed, string path, string className, int index, string text)
    {
        Assert.Equal("item", aimed["kind"]!.GetValue<string>());
        Assert.Equal(path, aimed["path"]!.GetValue<string>());
        Assert.Equal(className, aimed["class"]!.GetValue<string>());
        Assert.Equal(index, aimed["item"]!["index"]!.GetValue<int>());
        Assert.Equal(text, aimed["item"]!["text"]!.GetValue<string>());
    }

    private static void AssertPath(JsonNode aimed, params string[] path) =>
        Assert.Equal(path, aimed["item"]!["path"]!.AsArray().Select(step => step!.GetValue<string>()));

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
