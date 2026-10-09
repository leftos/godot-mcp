using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// Item targets against the InputProbe running in the real Godot: one shared run, reset before each test, the lists built by
/// run_script under the root, above the fixture's Main, which fills the viewport, and drawn twice before the first gesture.
/// </summary>
public sealed partial class ItemTargetTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Drawn = "await scene_tree.process_frame\n\tawait scene_tree.process_frame\n\treturn true";

    // A ScrollContainer Box at (20, 20), 600 x 300, holding Log, a fit_content RichTextLabel of 120 lines as wide as Box:
    // line 1 ends in the span "near", line 100, scrolled out of Box, in the span "far".
    private const string LongLog =
        "var box := ScrollContainer.new()\n\tbox.name = \"Box\"\n\tbox.position = Vector2(20, 20)\n\tbox.size = Vector2(600, 300)\n\t"
        + "scene_tree.root.add_child(box)\n\tvar rows := PackedStringArray()\n\t"
        + "for row in 120:\n\t\trows.append(\"plain text on row %d that runs on long enough to fill most of the width\" % row)\n\t"
        + "rows[1] += \" [hint=near]here[/hint]\"\n\trows[100] += \" [hint=far]there[/hint]\"\n\t"
        + "var label := RichTextLabel.new()\n\tlabel.name = \"Log\"\n\tlabel.bbcode_enabled = true\n\tlabel.fit_content = true\n\t"
        + "label.autowrap_mode = TextServer.AUTOWRAP_OFF\n\tlabel.size_flags_horizontal = Control.SIZE_EXPAND_FILL\n\t"
        + "label.text = \"\\n\".join(rows)\n\tbox.add_child(label)\n\tawait scene_tree.process_frame\n\t"
        + Drawn;

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
    public async Task ATabBeyondTheDrawnRangeIsRefusedAsScrolledOut()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(TabBar("Narrow", 20, 20, 150, Tabs(8)) + Drawn, cancellation);

        JsonNode state = await RunAsync(
            "var bar: TabBar = scene_tree.root.get_node(\"Narrow\")\n\t"
                + "var last: int = -1\n\t"
                + "for index in bar.tab_count:\n\t\t"
                + "var rect: Rect2 = bar.get_tab_rect(index)\n\t\t"
                + "if rect.has_area() and bar.get_tab_idx_at_point(rect.get_center()) == index:\n\t\t\t"
                + "last = index\n\t"
                + "var point: Vector2 = bar.get_global_transform_with_canvas() * (bar.size / 2.0)\n\t"
                + "return [bar.get_tab_offset(), last, point.x, point.y]",
            cancellation
        );
        int offset = state[0]!.GetValue<int>();
        int last = state[1]!.GetValue<int>();
        string x = Num(state[2]!.GetValue<double>());
        string y = Num(state[3]!.GetValue<double>());

        await AssertRefusedAsync(
            new InputTarget("Narrow", Item: new InputItem(Text: "Tab 7")),
            $"item 'Tab 7' of /root/Narrow is scrolled out of the tab bar: tab 7 lies after the drawn tabs ({offset} to {last}); "
                + "scroll over the tab strip, scroll "
                + $"{{target: {{x: {x}, y: {y}}}, direction: \"down\", notches: {7 - last}}}, then try again "
                + "(a notch moves the tabs by one; repeat while the tab is not drawn)",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AScrolledOutTabIsReachedByTheScrollItsRefusalNames()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(TabBar("Narrow", 20, 20, 150, Tabs(8)) + Drawn, cancellation);

        int rounds = 0;
        while (true)
        {
            try
            {
                await ClickAsync(new InputTarget("Narrow", Item: new InputItem(Text: "Tab 7")), cancellation);
                break;
            }
            catch (McpException refused)
            {
                rounds++;
                Assert.True(rounds <= 10, refused.Message);
                (InputTarget target, string direction, int notches) = ScrollNamedBy(refused.Message);
                await _tools.ScrollAsync(target, direction, notches, cancellationToken: cancellation);
            }
        }

        Assert.True(rounds is >= 1 and <= 10, $"rounds {rounds}");
        Assert.Equal(7, await ReadIntAsync("return scene_tree.root.get_node(\"Narrow\").current_tab", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATabBeforeTheOffsetNamesAScrollUp()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(TabBar("Narrow", 20, 20, 150, Tabs(8)) + Drawn, cancellation);

        await _tools.ScrollAsync(new InputTarget(null, 95, 40), "down", 4, cancellationToken: cancellation);

        Assert.True(await ReadIntAsync("return scene_tree.root.get_node(\"Narrow\").get_tab_offset()", cancellation) > 0, "the offset moved");
        McpException refused = await RefusedAsync(new InputTarget("Narrow", Item: new InputItem(Text: "Tab 0")), cancellation);

        Assert.Contains("tab 0 lies before the drawn tabs", refused.Message, StringComparison.Ordinal);
        Assert.Contains("direction: \"up\"", refused.Message, StringComparison.Ordinal);
        (InputTarget target, string direction, int notches) = ScrollNamedBy(refused.Message);
        await _tools.ScrollAsync(target, direction, notches, cancellationToken: cancellation);
        await ClickAsync(new InputTarget("Narrow", Item: new InputItem(Text: "Tab 0")), cancellation);

        Assert.Equal(0, await ReadIntAsync("return scene_tree.root.get_node(\"Narrow\").current_tab", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATabContainerTabNamesItsTabStripPoint()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(TabContainer("Book", 20, 20, 150, 200, Tabs(8)) + Drawn, cancellation);

        JsonNode boxes = await RunAsync(
            "var book: TabContainer = scene_tree.root.get_node(\"Book\")\n\t"
                + "var strip: Rect2 = book.get_tab_bar().get_global_transform_with_canvas() * Rect2(Vector2.ZERO, book.get_tab_bar().size)\n\t"
                + "var whole: Rect2 = book.get_global_rect()\n\t"
                + "return [strip.position.x, strip.position.y, strip.end.x, strip.end.y, whole.get_center().x, whole.get_center().y]",
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget("Book", Item: new InputItem(Text: "Tab 7")), cancellation);

        (InputTarget target, string direction, int notches) = ScrollNamedBy(refused.Message);
        double x = target.X!.Value;
        double y = target.Y!.Value;
        Assert.InRange(x, boxes[0]!.GetValue<double>(), boxes[2]!.GetValue<double>());
        Assert.InRange(y, boxes[1]!.GetValue<double>(), boxes[3]!.GetValue<double>());
        Assert.True(
            x != boxes[4]!.GetValue<double>() || y != boxes[5]!.GetValue<double>(),
            $"the point ({x}, {y}) is the container's centre, not its tab strip's: {refused.Message}"
        );

        await _tools.ScrollAsync(target, direction, notches, cancellationToken: cancellation);
        await ClickAsync(new InputTarget("Book", Item: new InputItem(Text: "Tab 7")), cancellation);

        Assert.Equal(7, await ReadIntAsync("return scene_tree.root.get_node(\"Book\").current_tab", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATabBarWithScrollingOffNamesItsArrow()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(TabBar("Locked", 20, 20, 150, Tabs(8)) + "bar.scrolling_enabled = false\n\t" + Drawn, cancellation);

        int rounds = 0;
        while (true)
        {
            try
            {
                await ClickAsync(new InputTarget("Locked", Item: new InputItem(Text: "Tab 7")), cancellation);
                break;
            }
            catch (McpException refused)
            {
                rounds++;
                Assert.True(rounds <= 10, refused.Message);
                Assert.Contains("and the bar takes no wheel (scrolling_enabled is off)", refused.Message, StringComparison.Ordinal);
                (InputTarget arrow, string name, int clicks) = ArrowNamedBy(refused.Message);
                Assert.Equal("increment", name);
                for (int click = 0; click < clicks; click++)
                {
                    await ClickAsync(arrow, cancellation);
                }
            }
        }

        Assert.True(rounds is >= 1 and <= 10, $"rounds {rounds}");
        Assert.Equal(7, await ReadIntAsync("return scene_tree.root.get_node(\"Locked\").current_tab", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATabBarWithClipTabsOffClicksATabPastItsNominalWidth()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode last = await RunAsync(
            TabBar("Open", 20, 20, 150, "Tab 0", "Tab 1", "Tab 2", "Tab 3", "Tab 4", "Tab 5", "Tab 6", "Tab 7")
                + "bar.clip_tabs = false\n\t"
                + Drawn.Replace(
                    "return true",
                    "var rect: Rect2 = bar.get_tab_rect(7)\n\treturn [rect.position.x, rect.end.x]",
                    StringComparison.Ordinal
                ),
            cancellation
        );

        Assert.True(last[0]!.GetValue<double>() >= 150, last.ToJsonString());
        Assert.True(20 + last[1]!.GetValue<double>() <= 640, last.ToJsonString());
        JsonNode clicked = await ClickAsync(new InputTarget("Open", Item: new InputItem(Text: "Tab 7")), cancellation);

        Assert.Equal(7, await ReadIntAsync("return scene_tree.root.get_node(\"Open\").current_tab", cancellation));
        AssertAimed(clicked["aimedAt"]!, "/root/Open", "TabBar", 7, "Tab 7");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATabBarWithClipTabsOffRefusesATabPastTheViewport()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string[] titles = [.. Enumerable.Range(0, 16).Select(index => $"Tab {index}")];
        JsonNode last = await RunAsync(
            TabBar("Open", 20, 20, 150, titles)
                + "bar.clip_tabs = false\n\t"
                + Drawn.Replace("return true", "return bar.get_tab_rect(15).get_center().x", StringComparison.Ordinal),
            cancellation
        );

        Assert.True(20 + last.GetValue<double>() > 640, last.ToJsonString());
        McpException refused = await RefusedAsync(new InputTarget("Open", Item: new InputItem(Text: titles[^1])), cancellation);

        Assert.True(
            refused.Message.StartsWith(
                $"click failed: The bridge refused 'input': item '{titles[^1]}' of /root/Open is at ",
                StringComparison.Ordinal
            ),
            refused.Message
        );
        Assert.True(
            refused.Message.Contains(") lies outside the 640x360 viewport; bring the item into view first", StringComparison.Ordinal),
            refused.Message
        );
        Assert.DoesNotContain("covers it", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ReadIntAsync("return scene_tree.root.get_node(\"Open\").current_tab", cancellation));
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
    public async Task HoverOnATooltipSpanItemShowsItsTooltip()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(RichLabel("Log", 100, 100, 400, 60, "3 × 1.5 + [hint=bonus]2[/hint] = 6.") + Drawn, cancellation);

        JsonNode hovered = JsonNode.Parse(
            await _tools.HoverAsync(new InputTarget("Log", Item: new InputItem(Text: "bonus")), cancellationToken: cancellation)
        )!;

        JsonNode aimed = hovered["aimedAt"]!;
        AssertAimed(aimed, "/root/Log", "RichTextLabel", 0, "bonus");
        JsonNode rect = Assert.Single(aimed["item"]!["rects"]!.AsArray())!;
        Assert.True(JsonNode.DeepEquals(rect, aimed["item"]!["rect"]), aimed.ToJsonString());
        double x = rect["x"]!.GetValue<double>();
        double width = rect["width"]!.GetValue<double>();
        Assert.True(x > 100 && width > 0 && x + width < 500, aimed.ToJsonString());
        Assert.Equal(x + width / 2, aimed["x"]!.GetValue<double>(), 0.5);
        Assert.False(hovered.AsObject().ContainsKey("warning"), hovered.ToJsonString());
        Assert.True(hovered["tooltip"] is JsonObject, hovered.ToJsonString());
        Assert.Equal("bonus", hovered["tooltip"]!["text"]!.GetValue<string>());
        Assert.Equal("/root/Log", hovered["tooltip"]!["owner"]!["path"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATooltipSpanRefusalListsTheSpansInReadingOrder()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            RichLabel(
                "Log",
                100,
                100,
                400,
                100,
                "a [hint=one]11[/hint] b [hint=two]22[/hint]\\n[hint=three]33[/hint] [url href=go tooltip=Open]link[/url] "
                    + "[url href=bare]bare[/url]"
            )
                + "label.tooltip_text = \"own\"\n\t"
                + Drawn,
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget("Log", Item: new InputItem(Text: "nope")), cancellation);

        Match listed = SpansListed().Match(refused.Message);
        Assert.True(listed.Success, refused.Message);
        Assert.Equal(["one", "two", "three", "Open"], listed.Groups["text"].Captures.Select(capture => capture.Value));
        double[] xs = [.. listed.Groups["x"].Captures.Select(capture => Number(capture.Value))];
        double[] ys = [.. listed.Groups["y"].Captures.Select(capture => Number(capture.Value))];
        Assert.True(xs[0] < xs[1] && ys[0] == ys[1], refused.Message);
        Assert.True(ys[2] > ys[0] && ys[2] == ys[3] && xs[2] < xs[3], refused.Message);
        bool fact = (
            await RunAsync(
                "var label: RichTextLabel = scene_tree.root.get_node(\"Log\")\n\t"
                    + "var own: String = label.get_tooltip(Vector2(1, 1))\n\t"
                    + $"var hint: String = label.get_tooltip(Vector2({Num(xs[0] - 100 + 1)}, label.get_line_height(0) / 2.0))\n\t"
                    + "return own == \"own\" and hint == \"one\"",
                cancellation
            )
        ).GetValue<bool>();
        Assert.True(fact, "get_tooltip answers a [hint]'s description over its glyph and tooltip_text off it");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATooltipSpanThatWrapsIsOneSpanWithARectPerLine()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(RichLabel("Log", 100, 100, 90, 200, "[hint=wrap]one two three four five six seven[/hint]") + Drawn, cancellation);
        int lines = await ReadIntAsync("return scene_tree.root.get_node(\"Log\").get_line_count()", cancellation);

        JsonNode hovered = JsonNode.Parse(
            await _tools.HoverAsync(new InputTarget("Log", Item: new InputItem(Index: 0)), cancellationToken: cancellation)
        )!;

        Assert.True(lines >= 2, $"the hint wraps onto {lines} lines");
        JsonNode aimed = hovered["aimedAt"]!;
        AssertAimed(aimed, "/root/Log", "RichTextLabel", 0, "wrap");
        JsonArray rects = aimed["item"]!["rects"]!.AsArray();
        Assert.Equal(lines, rects.Count);
        for (int line = 1; line < rects.Count; line++)
        {
            Assert.True(rects[line]!["y"]!.GetValue<double>() > rects[line - 1]!["y"]!.GetValue<double>(), aimed.ToJsonString());
        }
        Assert.Equal("wrap", hovered["tooltip"]!["text"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ATooltipSpanOnAScrolledLabelIsAimedAtItsScrolledPosition()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rows = string.Join("\\n", Enumerable.Range(0, 20).Select(row => $"row {row} [hint=t{row}]x[/hint]"));
        await RunAsync(
            RichLabel("Log", 100, 100, 300, 100, rows)
                + "await scene_tree.process_frame\n\tawait scene_tree.process_frame\n\t"
                + "label.scroll_to_line(10)\n\t"
                + Drawn,
            cancellation
        );
        double lineHeight = (await RunAsync("return scene_tree.root.get_node(\"Log\").get_line_height(10)", cancellation)).GetValue<double>();

        JsonNode hovered = JsonNode.Parse(
            await _tools.HoverAsync(new InputTarget("Log", Item: new InputItem(Text: "t10")), cancellationToken: cancellation)
        )!;

        JsonNode aimed = hovered["aimedAt"]!;
        AssertAimed(aimed, "/root/Log", "RichTextLabel", 0, "t10");
        double y = aimed["y"]!.GetValue<double>();
        Assert.True(y > 100 && y < 100 + lineHeight, aimed.ToJsonString());
        Assert.Equal("t10", hovered["tooltip"]!["text"]!.GetValue<string>());
        McpException refused = await RefusedAsync(new InputTarget("Log", Item: new InputItem(Text: "t0")), cancellation);
        Assert.Contains("/root/Log has no tooltip span 't0'; spans: index 0 't10' at ", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickOnATooltipSpanItemLandsOnTheLabel()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(RichLabel("Log", 100, 100, 400, 60, "3 × 1.5 + [hint=bonus]2[/hint] = 6.") + Drawn, cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Log", Item: new InputItem(Text: "bonus")), cancellation);

        AssertAimed(clicked["aimedAt"]!, "/root/Log", "RichTextLabel", 0, "bonus");
        Assert.Equal("/root/Log", clicked["pressedOn"]!["path"]!.GetValue<string>());
        Assert.Equal("/root/Log", clicked["releasedOn"]!["path"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TheProbeFindsASpanOnEachLineOfA600PxWide20LineLabel()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string rows = string.Join(
            "\\n",
            Enumerable.Range(0, 20).Select(row => $"plain text before the hint on row {row} [hint=r{row}]here[/hint] and after")
        );
        await RunAsync(RichLabel("Log", 20, 0, 600, 600, rows) + "label.autowrap_mode = TextServer.AUTOWRAP_OFF\n\t" + Drawn, cancellation);

        int shown = await ReadIntAsync(
            "var label: RichTextLabel = scene_tree.root.get_node(\"Log\")\n\t"
                + "var bottom: float = scene_tree.root.get_visible_rect().end.y\n\tvar shown := 0\n\t"
                + "for line in label.get_line_count():\n\t\t"
                + "if label.get_line_offset(line) + label.get_line_height(line) / 2.0 < bottom:\n\t\t\tshown += 1\n\t"
                + "return shown",
            cancellation
        );

        var clock = Stopwatch.StartNew();
        McpException refused = await RefusedAsync(new InputTarget("Log", Item: new InputItem(Index: 20)), cancellation);
        clock.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"a refused span click on 20 lines x 600 px took {clock.ElapsedMilliseconds} ms");
        Assert.True(shown is > 10 and < 20, $"the viewport shows {shown} of the 20 lines");
        Assert.Contains($"/root/Log has no tooltip span 20; it has {shown}: index 0 'r0' at ", refused.Message, StringComparison.Ordinal);
        Assert.True(clock.ElapsedMilliseconds < 150, $"the probe took {clock.ElapsedMilliseconds} ms");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHoverOffASpanOfALongLogInAScrollContainerAnswersWithinASecond()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(LongLog, cancellation);
        JsonNode onSpan = JsonNode.Parse(
            await _tools.HoverAsync(
                new InputTarget("Box/Log", Item: new InputItem(Text: "near")),
                new HoverOptions(Tooltip: false),
                cancellationToken: cancellation
            )
        )!;
        JsonNode rect = onSpan["aimedAt"]!["item"]!["rect"]!;
        double x = rect["x"]!.GetValue<double>() - 3;
        double y = rect["y"]!.GetValue<double>() + rect["height"]!.GetValue<double>() / 2;

        var clock = Stopwatch.StartNew();
        JsonNode hovered = JsonNode.Parse(await _tools.HoverAsync(new InputTarget(null, x, y), cancellationToken: cancellation))!;
        clock.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"a hover off a span of 120 fit_content lines took {clock.ElapsedMilliseconds} ms");
        string warning = hovered["warning"]!.GetValue<string>();
        Assert.Contains("on /root/Box/Log; its nearest tooltip span is index 0 'near' at ", warning, StringComparison.Ordinal);
        Assert.EndsWith(", 3 px away; aim at it with item {index: 0}", warning, StringComparison.Ordinal);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"the hover took {clock.ElapsedMilliseconds} ms");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASpanScrolledOutOfAScrollContainerIsNotListed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(LongLog, cancellation);

        McpException refused = await RefusedAsync(new InputTarget("Box/Log", Item: new InputItem(Text: "far")), cancellation);

        Assert.Equal(["near"], ListedSpans(refused.Message));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASpanATypewriterHasNotRevealedIsNotListed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // After shaping, the hidden text keeps its glyphs, so get_tooltip still answers over them.
        await RunAsync(
            RichLabel("Log", 100, 100, 400, 100, "[hint=shown]aa[/hint]\\n[hint=hidden]bb[/hint]")
                + "label.visible_characters_behavior = TextServer.VC_CHARS_AFTER_SHAPING\n\tlabel.visible_characters = 2\n\t"
                + Drawn,
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget("Log", Item: new InputItem(Text: "hidden")), cancellation);

        Assert.Equal(["shown"], ListedSpans(refused.Message));
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData("label.vertical_alignment = VERTICAL_ALIGNMENT_FILL\n\t")]
    [InlineData("label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER\n\t")]
    [InlineData("label.vertical_alignment = VERTICAL_ALIGNMENT_BOTTOM\n\t")]
    [InlineData("label.add_theme_constant_override(\"paragraph_separation\", 16)\n\t")]
    public async Task TheProbeFindsASpanOnEachOfThreeLinesHoweverTheyArePlaced(string setup)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            RichLabel("Log", 100, 20, 400, 300, "[hint=a]aaa[/hint]\\n[hint=b]bbb[/hint]\\n[hint=c]ccc[/hint]") + setup + Drawn,
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget("Log", Item: new InputItem(Text: "nope")), cancellation);

        Assert.Equal(["a", "b", "c"], ListedSpans(refused.Message));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASmallHintBesideALargeFontIsASpanAHoverShows()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(RichLabel("Log", 100, 100, 400, 100, "[font_size=48]Gold[/font_size] [hint=x]+2[/hint]") + Drawn, cancellation);

        McpException refused = await RefusedAsync(new InputTarget("Log", Item: new InputItem(Text: "nope")), cancellation);
        JsonNode hovered = JsonNode.Parse(
            await _tools.HoverAsync(new InputTarget("Log", Item: new InputItem(Text: "x")), cancellationToken: cancellation)
        )!;

        Assert.Equal(["x"], ListedSpans(refused.Message));
        Assert.Equal("x", hovered["tooltip"]!["text"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnImageTooltipIsASpan()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // [img tooltip=...] loads its texture from a path, so the image is added with add_image's tooltip instead.
        await RunAsync(
            RichLabel("Log", 100, 100, 400, 100, "before ")
                + "var texture := ImageTexture.create_from_image(Image.create_empty(24, 24, false, Image.FORMAT_RGBA8))\n\t"
                + "label.add_image(texture, 24, 24, Color.WHITE, INLINE_ALIGNMENT_CENTER, Rect2(), null, false, \"pic\")\n\t"
                + Drawn,
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget("Log", Item: new InputItem(Text: "nope")), cancellation);

        Assert.Equal(["pic"], ListedSpans(refused.Message));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AProbeThatRunsOutOfSamplesSaysWhereItStopped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // Scaled to half, the label shows 1280 x 720 of its own pixels in the 640 x 360 viewport: more than 20000 samples.
        string rows = string.Join("\\n", Enumerable.Range(0, 40).Select(row => $"row {row} " + string.Concat(Enumerable.Repeat("words ", 30))));
        await RunAsync(
            RichLabel("Log", 0, 0, 1280, 720, "[hint=first]x[/hint] " + rows)
                + "label.autowrap_mode = TextServer.AUTOWRAP_OFF\n\tlabel.scale = Vector2(0.5, 0.5)\n\t"
                + Drawn,
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget("Log", Item: new InputItem(Index: 999)), cancellation);

        Assert.Contains("/root/Log has no tooltip span 999; it has 1: index 0 'first' at ", refused.Message, StringComparison.Ordinal);
        Assert.Matches(
            @"; the tooltip span probe of /root/Log stopped after \d+ samples in \d+ ms, so spans past \([\d.-]+, [\d.-]+\) were not looked for\.$",
            refused.Message
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHoverOffASpanDeepInA3000LineLogAnswersWithinASecondAndAHalf()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // Box scrolled to its bottom: every sample walks the label's paragraphs from line 0, so the time budget stops the probe.
        await RunAsync(
            "var box := ScrollContainer.new()\n\tbox.name = \"Box\"\n\tbox.position = Vector2(20, 20)\n\tbox.size = Vector2(600, 300)\n\t"
                + "scene_tree.root.add_child(box)\n\tvar rows := PackedStringArray()\n\t"
                + "for row in 3000:\n\t\trows.append(\"row %d\" % row)\n\trows[2995] += \" [hint=deep]here[/hint]\"\n\t"
                + "var label := RichTextLabel.new()\n\tlabel.name = \"Log\"\n\tlabel.bbcode_enabled = true\n\tlabel.fit_content = true\n\t"
                + "label.size_flags_horizontal = Control.SIZE_EXPAND_FILL\n\tlabel.text = \"\\n\".join(rows)\n\tbox.add_child(label)\n\t"
                + "await scene_tree.process_frame\n\tawait scene_tree.process_frame\n\t"
                + "box.scroll_vertical = int(box.get_v_scroll_bar().max_value)\n\t"
                + Drawn,
            cancellation
        );
        JsonNode point = await RunAsync(
            "var label: RichTextLabel = scene_tree.root.get_node(\"Box/Log\")\n\t"
                + "var local := Vector2(label.get_line_width(2995) + 4, label.get_line_offset(2995) + label.get_line_height(2995) / 2.0)\n\t"
                + "var at: Vector2 = label.get_global_transform_with_canvas() * local\n\treturn [at.x, at.y]",
            cancellation
        );

        var clock = Stopwatch.StartNew();
        JsonNode hovered = JsonNode.Parse(
            await _tools.HoverAsync(
                new InputTarget(null, point[0]!.GetValue<double>(), point[1]!.GetValue<double>()),
                cancellationToken: cancellation
            )
        )!;
        clock.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"a hover off a span on line 2995 of 3000 took {clock.ElapsedMilliseconds} ms");
        Assert.Equal("/root/Box/Log", hovered["hoveredOn"]!["path"]!.GetValue<string>());
        string warning = hovered["warning"]!.GetValue<string>();
        bool named = warning.Contains("its nearest tooltip span is index 0 'deep' at ", StringComparison.Ordinal);
        bool stopped = ProbeStopped().IsMatch(warning) && warning.Contains("probe of /root/Box/Log ", StringComparison.Ordinal);
        Assert.True(named || stopped, warning);
        Assert.True(clock.ElapsedMilliseconds < 1500, $"the hover took {clock.ElapsedMilliseconds} ms");
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
    public async Task ARightToLeftTabBarClicksTheTabByTextAndIndex()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonNode drawnAt = await RunAsync(
            TabBar("Seats", 20, 20, 300, "Alex", "Sam", "Kit")
                + "bar.layout_direction = Control.LAYOUT_DIRECTION_RTL\n\t"
                + Drawn.Replace(
                    "return true",
                    "var rect: Rect2 = bar.get_global_rect()\n\treturn [rect.position.x, rect.end.x]",
                    StringComparison.Ordinal
                ),
            cancellation
        );
        double left = drawnAt[0]!.GetValue<double>();
        double right = drawnAt[1]!.GetValue<double>();

        JsonNode byText = await ClickAsync(new InputTarget("Seats", Item: new InputItem(Text: "Kit")), cancellation);

        Assert.Equal(2, await ReadIntAsync("return scene_tree.root.get_node(\"Seats\").current_tab", cancellation));
        AssertAimed(byText["aimedAt"]!, "/root/Seats", "TabBar", 2, "Kit");

        JsonNode byIndex = await ClickAsync(new InputTarget("Seats", Item: new InputItem(Index: 0)), cancellation);

        Assert.Equal(0, await ReadIntAsync("return scene_tree.root.get_node(\"Seats\").current_tab", cancellation));
        AssertAimed(byIndex["aimedAt"]!, "/root/Seats", "TabBar", 0, "Alex");
        double first = byIndex["aimedAt"]!["x"]!.GetValue<double>();
        Assert.InRange(first, (left + right) / 2, right);
        Assert.True(byText["aimedAt"]!["x"]!.GetValue<double>() < first, byText.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARightToLeftTreeSelectsADeepItemWithChildrenRatherThanFoldingIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Tree("inv.layout_direction = Control.LAYOUT_DIRECTION_RTL\n\tinv.create_item(sword).set_text(0, \"Edge\")\n\t") + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("Inv", Item: new InputItem(Path: ["Weapons", "Sword"])), cancellation);

        JsonNode state = await RunAsync(
            "var inv: Tree = scene_tree.root.get_node(\"Inv\")\n\tvar selected: TreeItem = inv.get_selected()\n\t"
                + "return [selected.get_text(0) if selected != null else \"\", inv.get_root().get_child(0).get_child(0).collapsed]",
            cancellation
        );
        Assert.Equal("Sword", state[0]!.GetValue<string>());
        Assert.False(state[1]!.GetValue<bool>(), clicked.ToJsonString());
        AssertPath(clicked["aimedAt"]!, "Weapons", "Sword");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARightToLeftItemListScrolledSelectsTheItemClicked()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string[] texts = [.. Enumerable.Range(0, 12).Select(index => $"Item {index}")];
        await RunAsync(
            ItemList("RtlShort", 400, 40, 200, 60, texts)
                + "list.layout_direction = Control.LAYOUT_DIRECTION_RTL\n\t"
                + Drawn.Replace("return true", "", StringComparison.Ordinal)
                + "var bar: VScrollBar = list.get_v_scroll_bar()\n\tbar.value = bar.max_value\n\t"
                + Drawn,
            cancellation
        );

        JsonNode clicked = await ClickAsync(new InputTarget("RtlShort", Item: new InputItem(Text: "Item 11")), cancellation);

        Assert.Equal(11, await ReadIntAsync("return int(scene_tree.root.get_node(\"RtlShort\").get_meta(\"selected\"))", cancellation));
        AssertAimed(clicked["aimedAt"]!, "/root/RtlShort", "ItemList", 11, "Item 11");
        Assert.InRange(clicked["aimedAt"]!["y"]!.GetValue<double>(), 40, 100);
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

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetUiElementsListsTheItemsAnItemTargetMatches()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            TabBar("Seats", 20, 20, 300, "Alex", "Sam", "Kit")
                + "bar.set_tab_hidden(1, true)\n\tbar.set_tab_disabled(2, true)\n\t"
                + ItemList("Rows", 20, 100, 200, 100, "One", "Two")
                + Tree("weapons.collapsed = true\n\t")
                + Drawn,
            cancellation
        );

        JsonArray elements = JsonNode.Parse(await _tools.GetUiElementsAsync(true, null, limit: 500, cancellationToken: cancellation))![
            "elements"
        ]!.AsArray();

        AssertItems(
            elements,
            "/root/Seats",
            """[{"index": 0, "text": "Alex"}, {"index": 1, "text": "Sam", "hidden": true}, {"index": 2, "text": "Kit", "disabled": true}]"""
        );
        AssertItems(elements, "/root/Rows", """[{"index": 0, "text": "One"}, {"index": 1, "text": "Two"}]""");
        AssertItems(
            elements,
            "/root/Inv",
            """[{"path": ["Weapons"], "text": "Weapons"}, {"path": ["Armour"], "text": "Armour"}, {"path": ["Armour", "Helm"], "text": "Helm"}]"""
        );
        Assert.Equal(
            ["/root/Inv", "/root/Rows", "/root/Seats"],
            elements.Where(element => element!.AsObject().ContainsKey("items")).Select(element => element!["path"]!.GetValue<string>()).Order()
        );

        await ClickAsync(new InputTarget("Rows", Item: new InputItem(Text: "Two")), cancellation);
        await ClickAsync(new InputTarget("Inv", Item: new InputItem(Path: ["Armour", "Helm"])), cancellation);

        Assert.Equal(1, await ReadIntAsync("return int(scene_tree.root.get_node(\"Rows\").get_meta(\"selected\"))", cancellation));
        Assert.Equal(
            "Helm",
            (await RunAsync("return scene_tree.root.get_node(\"Inv\").get_selected().get_text(0)", cancellation)).GetValue<string>()
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetUiElementsListsTabContainerTabsAndAShownTreeRoot()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            "for spec: Array in [[\"Book\", 20, [\"First\", \"Second\", \"Third\"]], [\"Folded\", 200, [\"One\", \"Two\"]]]:\n\t\t"
                + "var book := TabContainer.new()\n\t\tbook.name = spec[0]\n\t\tbook.position = Vector2(20, spec[1])\n\t\t"
                + "book.size = Vector2(300, 120)\n\t\tscene_tree.root.add_child(book)\n\t\t"
                + "for page_name: String in spec[2]:\n\t\t\tvar page := Control.new()\n\t\t\tpage.name = page_name\n\t\t\tbook.add_child(page)\n\t"
                + "scene_tree.root.get_node(\"Book\").set_tab_hidden(1, true)\n\t"
                + "scene_tree.root.get_node(\"Folded\").tabs_visible = false\n\t"
                + "var shown := Tree.new()\n\tshown.name = \"Shown\"\n\tshown.position = Vector2(360, 20)\n\tshown.size = Vector2(260, 150)\n\t"
                + "var top: TreeItem = shown.create_item()\n\ttop.set_text(0, \"Root\")\n\tshown.create_item(top).set_text(0, \"Leaf\")\n\t"
                + "scene_tree.root.add_child(shown)\n\t"
                + Drawn,
            cancellation
        );

        JsonArray elements = JsonNode.Parse(await _tools.GetUiElementsAsync(true, null, limit: 500, cancellationToken: cancellation))![
            "elements"
        ]!.AsArray();

        AssertItems(
            elements,
            "/root/Book",
            """[{"index": 0, "text": "First"}, {"index": 1, "text": "Second", "hidden": true}, {"index": 2, "text": "Third"}]"""
        );
        AssertItems(elements, "/root/Folded", """[{"index": 0, "text": "One", "hidden": true}, {"index": 1, "text": "Two", "hidden": true}]""");
        AssertItems(elements, "/root/Shown", """[{"path": ["Root"], "text": "Root"}, {"path": ["Root", "Leaf"], "text": "Leaf"}]""");

        await ClickAsync(new InputTarget("Book", Item: new InputItem(Text: "Third")), cancellation);
        await ClickAsync(new InputTarget("Shown", Item: new InputItem(Path: ["Root", "Leaf"])), cancellation);

        Assert.Equal(2, await ReadIntAsync("return scene_tree.root.get_node(\"Book\").current_tab", cancellation));
        Assert.Equal(
            "Leaf",
            (await RunAsync("return scene_tree.root.get_node(\"Shown\").get_selected().get_text(0)", cancellation)).GetValue<string>()
        );
        await AssertRefusedAsync(new InputTarget("Folded", Item: new InputItem(Text: "One")), "item 'One' of /root/Folded is hidden", cancellation);
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

    // A TabContainer named name at (x, y), width x height, with a Control page per title, held by the script variable book;
    // each tab's title is its page's name, so titles hold no character a node name refuses.
    private static string TabContainer(string name, int x, int y, int width, int height, params string[] titles) =>
        "var book := TabContainer.new()\n\t"
        + $"book.name = \"{name}\"\n\t"
        + $"book.position = Vector2({x}, {y})\n\t"
        + $"book.size = Vector2({width}, {height})\n\t"
        + "scene_tree.root.add_child(book)\n\t"
        + $"for page_name: String in [{string.Join(", ", titles.Select(title => $"\"{title}\""))}]:\n\t\t"
        + "var page := Control.new()\n\t\tpage.name = page_name\n\t\tbook.add_child(page)\n\t";

    // A RichTextLabel, label in the script, named name under the root at (x, y), width x height, showing bbcode.
    private static string RichLabel(string name, int x, int y, int width, int height, string bbcode) =>
        "var label := RichTextLabel.new()\n\t"
        + $"label.name = \"{name}\"\n\t"
        + "label.bbcode_enabled = true\n\t"
        + $"label.text = \"{bbcode}\"\n\t"
        + $"label.position = Vector2({x}, {y})\n\t"
        + $"label.size = Vector2({width}, {height})\n\t"
        + "scene_tree.root.add_child(label)\n\t";

    // A tooltip span refusal's listing: each span's index, text and first rect.
    [GeneratedRegex(@"spans: (?:index \d+ '(?<text>[^']*)' at (?<x>[\d.-]+),(?<y>[\d.-]+),[\d.]+,[\d.]+(?:, |$))+")]
    private static partial Regex SpansListed();

    // The sentence a tooltip span probe that ran out of samples or time ends a refusal or warning with.
    [GeneratedRegex(@"the tooltip span probe of \S+ stopped after \d+ samples in \d+ ms, so spans past \([\d.-]+, [\d.-]+\) were not looked for\.$")]
    private static partial Regex ProbeStopped();

    // The texts of the spans a tooltip span refusal lists, in order.
    private static string[] ListedSpans(string refusal)
    {
        Match listed = SpansListed().Match(refusal);
        Assert.True(listed.Success, refusal);
        return [.. listed.Groups["text"].Captures.Select(capture => capture.Value)];
    }

    // A bar's titles "Tab 0" to "Tab count-1": one 150 px wide draws only some of them.
    private static string[] Tabs(int count) => [.. Enumerable.Range(0, count).Select(index => $"Tab {index}")];

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

    // The wheel scroll a scrolled-out tab's refusal names: its point, direction and notches.
    [GeneratedRegex("""scroll \{target: \{x: (?<x>[-\d.]+), y: (?<y>[-\d.]+)\}, direction: "(?<direction>\w+)", notches: (?<notches>\d+)\}""")]
    private static partial Regex ScrollNamed();

    // The arrow click a scrolled-out tab's refusal names when its bar takes no wheel: its point, name and count.
    [GeneratedRegex("""click its (?<arrow>\w+) arrow at \((?<x>[-\d.]+), (?<y>[-\d.]+)\) (?<count>\d+) times""")]
    private static partial Regex ArrowNamed();

    private static (InputTarget Target, string Direction, int Notches) ScrollNamedBy(string refusal)
    {
        Match named = ScrollNamed().Match(refusal);
        Assert.True(named.Success, refusal);
        return (Point(named), named.Groups["direction"].Value, Count(named, "notches"));
    }

    private static (InputTarget Target, string Name, int Clicks) ArrowNamedBy(string refusal)
    {
        Match named = ArrowNamed().Match(refusal);
        Assert.True(named.Success, refusal);
        return (Point(named), named.Groups["arrow"].Value, Count(named, "count"));
    }

    private static InputTarget Point(Match named) => new(null, Number(named.Groups["x"].Value), Number(named.Groups["y"].Value));

    private static int Count(Match named, string group) => int.Parse(named.Groups[group].Value, CultureInfo.InvariantCulture);

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    // A coordinate as the bridge prints it (its num): at most one decimal, none when whole.
    private static string Num(double value)
    {
        double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded == Math.Floor(rounded)
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static void AssertAimed(JsonNode aimed, string path, string className, int index, string text)
    {
        Assert.Equal("item", aimed["kind"]!.GetValue<string>());
        Assert.Equal(path, aimed["path"]!.GetValue<string>());
        Assert.Equal(className, aimed["class"]!.GetValue<string>());
        Assert.Equal(index, aimed["item"]!["index"]!.GetValue<int>());
        Assert.Equal(text, aimed["item"]!["text"]!.GetValue<string>());
    }

    // The element at path has exactly the items expected, a JSON array, and no itemsTotal.
    private static void AssertItems(JsonArray elements, string path, string expected)
    {
        JsonNode element = elements.Single(candidate => candidate!["path"]!.GetValue<string>() == path)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), element["items"]), element.ToJsonString());
        Assert.False(element.AsObject().ContainsKey("itemsTotal"), element.ToJsonString());
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
