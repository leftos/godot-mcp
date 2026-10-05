using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// Text targets against the InputProbe running in the real Godot: one shared run, reset before each test, the Controls built
/// by run_script under the root, above the fixture's Main, which fills the viewport.
/// </summary>
public sealed class TextTargetTests(SharedProbeSession shared) : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;

    // Card (mouse-ignoring) at (400, 200), 160 x 100, as an instanced scene holds it: Hit, a text-less Button filling the
    // card, and Title, a titleType reading "Strike" at the card's (10, 10), 140 x 30, both owned by Card. Title's centre
    // is (480, 225).
    private static string Card(string titleType) =>
        "var card := Control.new()\n\t"
        + "card.name = \"Card\"\n\t"
        + "card.mouse_filter = Control.MOUSE_FILTER_IGNORE\n\t"
        + "card.position = Vector2(400, 200)\n\t"
        + "card.size = Vector2(160, 100)\n\t"
        + "scene_tree.root.add_child(card)\n\t"
        + "var hit := Button.new()\n\t"
        + "hit.name = \"Hit\"\n\t"
        + "hit.size = Vector2(160, 100)\n\t"
        + "hit.set_meta(\"presses\", 0)\n\t"
        + "hit.pressed.connect(func() -> void: hit.set_meta(\"presses\", int(hit.get_meta(\"presses\")) + 1))\n\t"
        + "card.add_child(hit)\n\t"
        + "hit.owner = card\n\t"
        + $"var title := {titleType}.new()\n\t"
        + "title.name = \"Title\"\n\t"
        + "title.text = \"Strike\"\n\t"
        + "title.position = Vector2(10, 10)\n\t"
        + "title.size = Vector2(140, 30)\n\t"
        + "card.add_child(title)\n\t"
        + "title.owner = card\n\t";

    private static readonly string CardBlock = Card("Label");
    private static readonly string RichCardBlock = Card("RichTextLabel");

    // TextMenu (mouse-ignoring) holds Host, reading "New Game", at (400, 250) and Join, reading "Join Game", at (400, 300),
    // each 120 x 40.
    private static readonly string Menu =
        Box("menu", "TextMenu") + Button("host", "menu", "Host", "New Game", 400, 250) + Button("join", "menu", "Join", "Join Game", 400, 300);

    private readonly SharedProbeSession _shared = shared;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AClickByTextPressesTheButtonShowingIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Menu + "return true", cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget(Text: " New Game "), cancellation);

        JsonNode aimed = clicked["aimedAt"]!;
        AssertAimed(aimed, "/root/TextMenu/Host", "Button", 460, 270);
        AssertMatched(aimed, "New Game");
        Assert.Equal(1, await PressesAsync("TextMenu/Host", cancellation));
        Assert.Equal(0, await PressesAsync("TextMenu/Join", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AMissNamesTheNodeOfThatNameAndTheNearMisses()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Menu + "return true", cancellation);

        await AssertRefusedAsync(
            new InputTarget(Text: "Host"),
            "no visible Control shows 'Host'; /root/TextMenu/Host is named 'Host' and shows 'New Game'.",
            cancellation
        );
        await AssertRefusedAsync(
            new InputTarget(Text: "join game"),
            "no visible Control shows 'join game'; near misses: /root/TextMenu/Join shows 'Join Game'.",
            cancellation
        );
        Assert.Equal(0, await PressesAsync("TextMenu/Join", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TwoControlsShowingTheTextAreRefusedAndUnderNarrowsThem()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Box("left", "LeftBox")
                + Button("left_same", "left", "Same", "Deal", 20, 300)
                + Box("right", "RightBox")
                + Button("right_same", "right", "Same", "Deal", 500, 300)
                + "return true",
            cancellation
        );

        await AssertRefusedAsync(
            new InputTarget(Text: "Deal"),
            "'Deal' is shown by 2 Controls: /root/LeftBox/Same at 20,300,120,40, /root/RightBox/Same at 500,300,120,40; narrow it with under.",
            cancellation
        );
        JsonNode clicked = await ClickAsync(new InputTarget(Text: "Deal", Under: "RightBox"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "/root/RightBox/Same", "Button", 560, 320);
        Assert.Equal(1, await PressesAsync("RightBox/Same", cancellation));
        Assert.Equal(0, await PressesAsync("LeftBox/Same", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUnknownUnderIsRefusedAsAnElement()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Menu + "return true", cancellation);

        McpException refused = await RefusedAsync(new InputTarget(Text: "New Game", Under: "NoSuchBox"), cancellation);

        Assert.Contains("NoSuchBox", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await PressesAsync("TextMenu/Host", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHiddenUnderIsRefusedAsAnElement()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Menu + "menu.visible = false\n\treturn true", cancellation);

        await AssertRefusedAsync(
            new InputTarget(Text: "New Game", Under: "TextMenu"),
            "/root/TextMenu is hidden; get_ui_elements lists the visible Controls, get_scene_tree every node.",
            cancellation
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TheUnderNodeItselfCanBeTheMatch()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Menu + "return true", cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget(Text: "New Game", Under: "Host"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "/root/TextMenu/Host", "Button", 460, 270);
        AssertMatched(clicked["aimedAt"]!, "New Game");
        Assert.Equal(1, await PressesAsync("TextMenu/Host", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnElementTargetCarriesNoMatched()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(Menu + "return true", cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget("Host"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "/root/TextMenu/Host", "Button", 460, 270);
        Assert.False(clicked["aimedAt"]!.AsObject().ContainsKey("matched"), clicked.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHiddenControlShowingTheTextIsNotMatched()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            Box("box", "GhostBox") + Button("ghost", "box", "Ghost", "Boo", 400, 250) + "ghost.visible = false\n\treturn true",
            cancellation
        );

        await AssertRefusedAsync(new InputTarget(Text: "Boo"), "no visible Control shows 'Boo'.", cancellation);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetUiElementsReportsTheTextATargetMatches()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            "scene_tree.root.get_node(\"Main/TextInput\").placeholder_text = \"Your name\"\n\t"
                + "var rich := RichTextLabel.new()\n\trich.name = \"Rich\"\n\trich.bbcode_enabled = true\n\t"
                + "rich.text = \"[b]Bold[/b] move\"\n\trich.position = Vector2(400, 250)\n\trich.size = Vector2(160, 40)\n\t"
                + "scene_tree.root.add_child(rich)\n\treturn true",
            cancellation
        );

        JsonNode placeholder = await ClickAsync(new InputTarget(Text: "Your name"), cancellation);
        JsonNode rich = await ClickAsync(new InputTarget(Text: "Bold move"), cancellation);

        AssertAimed(placeholder["aimedAt"]!, "/root/Main/TextInput", "LineEdit", 140, 276);
        AssertMatched(placeholder["aimedAt"]!, "Your name");
        AssertAimed(rich["aimedAt"]!, "/root/Rich", "RichTextLabel", 480, 270);
        AssertMatched(rich["aimedAt"]!, "Bold move");
        JsonArray elements = JsonNode.Parse(await _tools.GetUiElementsAsync(true, null, limit: 500, cancellationToken: cancellation))![
            "elements"
        ]!.AsArray();
        Assert.Equal("Your name", TextOf(elements, "/root/Main/TextInput"));
        Assert.Equal("Bold move", TextOf(elements, "/root/Rich"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ALabelMatchIsPressedThroughAButtonOfItsOwnSceneInstance()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(CardBlock + "return true", cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget(Text: "Strike"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "/root/Card/Title", "Label", 480, 225);
        AssertMatched(clicked["aimedAt"]!, "Strike");
        Assert.Equal("/root/Card/Hit", clicked["pressedOn"]!["path"]!.GetValue<string>());
        Assert.Equal(1, await PressesAsync("Card/Hit", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStopLabelMatchIsPressedThroughAButtonOfItsOwnSceneInstance()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(RichCardBlock + "card.move_child(hit, -1)\n\treturn true", cancellation);

        JsonNode clicked = await ClickAsync(new InputTarget(Text: "Strike"), cancellation);

        AssertAimed(clicked["aimedAt"]!, "/root/Card/Title", "RichTextLabel", 480, 225);
        AssertMatched(clicked["aimedAt"]!, "Strike");
        Assert.Equal("/root/Card/Hit", clicked["pressedOn"]!["path"]!.GetValue<string>());
        Assert.Equal(1, await PressesAsync("Card/Hit", cancellation));

        JsonNode under = await ClickAsync(new InputTarget(Text: "Strike", Under: "Card"), cancellation);

        AssertAimed(under["aimedAt"]!, "/root/Card/Title", "RichTextLabel", 480, 225);
        AssertMatched(under["aimedAt"]!, "Strike");
        Assert.Equal("/root/Card/Hit", under["pressedOn"]!["path"]!.GetValue<string>());
        Assert.Equal(2, await PressesAsync("Card/Hit", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AButtonMatchCoveredByItsOwnSceneIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            CardBlock
                + "var play := Button.new()\n\t"
                + "play.name = \"Play\"\n\t"
                + "play.text = \"Play\"\n\t"
                + "play.position = Vector2(10, 10)\n\t"
                + "play.size = Vector2(140, 30)\n\t"
                + "play.set_meta(\"presses\", 0)\n\t"
                + "play.pressed.connect(func() -> void: play.set_meta(\"presses\", int(play.get_meta(\"presses\")) + 1))\n\t"
                + "card.add_child(play)\n\t"
                + "play.owner = card\n\t"
                + "var quit := Panel.new()\n\t"
                + "quit.name = \"ConfirmQuit\"\n\t"
                + "quit.position = Vector2(10, 10)\n\t"
                + "quit.size = Vector2(140, 30)\n\t"
                + "card.add_child(quit)\n\t"
                + "quit.owner = card\n\treturn true",
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget(Text: "Play"), cancellation);

        Assert.Contains("the centre of /root/Card/Play", refused.Message, StringComparison.Ordinal);
        Assert.Contains("lands on /root/Card/ConfirmQuit, which covers it", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await PressesAsync("Card/Play", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnEmbeddedPopupCoveringALabelMatchIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            CardBlock
                + "var popup := PopupPanel.new()\n\t"
                + "popup.name = \"Popup\"\n\t"
                + "popup.popup_window = false\n\t"
                + "popup.position = Vector2i(400, 200)\n\t"
                + "popup.size = Vector2i(160, 100)\n\t"
                + "card.add_child(popup)\n\t"
                + "popup.owner = card\n\t"
                + "var cover := Control.new()\n\t"
                + "cover.name = \"Cover\"\n\t"
                + "cover.size = Vector2(160, 100)\n\t"
                + "popup.add_child(cover)\n\t"
                + "cover.owner = card\n\t"
                + "popup.show()\n\treturn true",
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget(Text: "Strike"), cancellation);

        Assert.Contains("lands on /root/Card/Popup/Cover, which covers it", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await PressesAsync("Card/Hit", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnOwnerlessSiblingOverALabelMatchIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            CardBlock
                + "var slot := Control.new()\n\t"
                + "slot.name = \"Slot\"\n\t"
                + "slot.position = Vector2(400, 200)\n\t"
                + "slot.size = Vector2(160, 100)\n\t"
                + "scene_tree.root.add_child(slot)\n\treturn true",
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget(Text: "Strike"), cancellation);

        Assert.Contains("the centre of /root/Card/Title (480, 225) lands on /root/Slot, which covers it", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await PressesAsync("Card/Hit", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ALabelMatchUnderAModalIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(
            CardBlock
                + "var modal := ColorRect.new()\n\tmodal.name = \"Modal\"\n\tmodal.size = Vector2(640, 360)\n\t"
                + "scene_tree.root.add_child(modal)\n\treturn true",
            cancellation
        );

        McpException refused = await RefusedAsync(new InputTarget(Text: "Strike"), cancellation);

        Assert.Contains("the centre of /root/Card/Title (480, 225) lands on /root/Modal, which covers it", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await PressesAsync("Card/Hit", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AControlWithItsCentreOutsideTheViewportIsRefusedAsOffScreen()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(RootButton("far", "Far", 700) + "return true", cancellation);
        const string expected =
            "/root/Far is at 700,100,80,40, whose centre (740, 120) lies outside the 640x360 viewport; "
            + "bring it into view first, or click by {x, y} inside its visible part.";

        foreach (InputTarget target in new[] { new InputTarget("Far"), new InputTarget(Text: "Far") })
        {
            McpException refused = await RefusedAsync(target, cancellation);

            Assert.True(refused.Message.EndsWith(expected, StringComparison.Ordinal), refused.Message);
            Assert.DoesNotContain("covers it", refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, await PressesAsync("Far", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AControlStraddlingTheViewportEdgeWithItsCentreInsideIsClicked()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await RunAsync(RootButton("edge", "Edge", 590) + "return true", cancellation);

        JsonNode byElement = await ClickAsync(new InputTarget("Edge"), cancellation);
        JsonNode byText = await ClickAsync(new InputTarget(Text: "Edge"), cancellation);

        AssertAimed(byElement["aimedAt"]!, "/root/Edge", "Button", 630, 120);
        AssertAimed(byText["aimedAt"]!, "/root/Edge", "Button", 630, 120);
        Assert.Equal(2, await PressesAsync("Edge", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ADragToATextTargetDropsAndReportsTheMatch()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;

        JsonNode dragged = JsonNode.Parse(
            await _tools.DragAsync(new InputTarget("DragSource"), new InputTarget(Text: "drop here"), 300, "left", cancellationToken: cancellation)
        )!;

        Assert.True(dragged["dropAccepted"]!.GetValue<bool>(), dragged.ToJsonString());
        JsonNode to = dragged["aimedAt"]!["to"]!;
        AssertAimed(to, "/root/Main/DropTarget/DropLabel", "Label", 480, 190);
        AssertMatched(to, "drop here");
        Assert.False(dragged["aimedAt"]!["from"]!.AsObject().ContainsKey("matched"), dragged.ToJsonString());
    }

    // A mouse-ignoring Control named name under the root, held by the script variable named variable.
    private static string Box(string variable, string name) =>
        $"var {variable} := Control.new()\n\t"
        + $"{variable}.name = \"{name}\"\n\t"
        + $"{variable}.mouse_filter = Control.MOUSE_FILTER_IGNORE\n\t"
        + $"scene_tree.root.add_child({variable})\n\t";

    // A 120 x 40 Button named name reading text at (x, y) under the script's parent variable, held by variable, counting its
    // presses in its "presses" meta.
    private static string Button(string variable, string parent, string name, string text, int x, int y) =>
        $"var {variable} := Button.new()\n\t"
        + $"{variable}.name = \"{name}\"\n\t"
        + $"{variable}.text = \"{text}\"\n\t"
        + $"{variable}.position = Vector2({x}, {y})\n\t"
        + $"{variable}.size = Vector2(120, 40)\n\t"
        + $"{variable}.set_meta(\"presses\", 0)\n\t"
        + $"{variable}.pressed.connect(func() -> void: {variable}.set_meta(\"presses\", int({variable}.get_meta(\"presses\")) + 1))\n\t"
        + $"{parent}.add_child({variable})\n\t";

    // An 80 x 40 Button named name reading name at (x, 100) under the root, held by variable, counting its presses in its
    // "presses" meta.
    private static string RootButton(string variable, string name, int x) =>
        Button(variable, "scene_tree.root", name, name, x, 100) + $"{variable}.size = Vector2(80, 40)\n\t";

    private static void AssertAimed(JsonNode aimed, string path, string className, double x, double y)
    {
        Assert.Equal("control", aimed["kind"]!.GetValue<string>());
        Assert.Equal(path, aimed["path"]!.GetValue<string>());
        Assert.Equal(className, aimed["class"]!.GetValue<string>());
        Assert.Equal(x, aimed["x"]!.GetValue<double>(), 0.5);
        Assert.Equal(y, aimed["y"]!.GetValue<double>(), 0.5);
    }

    private static void AssertMatched(JsonNode aimed, string text)
    {
        Assert.Equal("text", aimed["matched"]!["by"]!.GetValue<string>());
        Assert.Equal(text, aimed["matched"]!["text"]!.GetValue<string>());
    }

    private static string? TextOf(JsonArray elements, string path) =>
        Assert.Single(elements, element => element!["path"]!.GetValue<string>() == path)!["text"]?.GetValue<string>();

    private async Task AssertRefusedAsync(InputTarget target, string expected, CancellationToken cancellation)
    {
        McpException refused = await RefusedAsync(target, cancellation);
        Assert.True(refused.Message.EndsWith(expected, StringComparison.Ordinal), refused.Message);
    }

    private async Task<JsonNode> ClickAsync(InputTarget target, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.ClickAsync(target, "left", false, cancellationToken: cancellation))!;

    private Task<McpException> RefusedAsync(InputTarget target, CancellationToken cancellation) =>
        Assert.ThrowsAsync<McpException>(() => _tools.ClickAsync(target, "left", false, cancellationToken: cancellation));

    private async Task<int> PressesAsync(string path, CancellationToken cancellation) =>
        (await RunAsync($"return int(scene_tree.root.get_node(\"{path}\").get_meta(\"presses\"))", cancellation)).GetValue<int>();

    private async Task<JsonNode> RunAsync(string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
        return JsonNode.Parse(json)!["value"]!;
    }
}
