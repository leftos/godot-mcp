using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

public sealed class InputTargetTests
{
    [Fact]
    public void AnElementAloneBecomesAnElementTarget()
    {
        JsonObject bridge = InputTarget.ToBridge(new InputTarget("SmallButton"), "target");

        Assert.Equal("{\"element\":\"SmallButton\"}", bridge.ToJsonString());
    }

    [Fact]
    public void XAndYAloneBecomeAPointTarget()
    {
        JsonObject bridge = InputTarget.ToBridge(new InputTarget(null, 12.5, 40), "target");

        Assert.Equal((12.5, 40.0), (bridge["x"]!.GetValue<double>(), bridge["y"]!.GetValue<double>()));
        Assert.False(bridge.ContainsKey("element"));
    }

    [Fact]
    public void AnElementAndAPointTogetherAreRefused() => AssertRefused(new InputTarget("SmallButton", 1, 2), "from");

    [Fact]
    public void NeitherAnElementNorAPointIsRefused() => AssertRefused(new InputTarget(), "to");

    [Fact]
    public void XWithoutYIsRefused() => AssertRefused(new InputTarget(null, 1, null), "target");

    [Fact]
    public void AMissingTargetIsRefused() => AssertRefused(null, "target");

    [Fact]
    public void AWhitespaceElementCountsAsNone() => AssertRefused(new InputTarget("  "), "target");

    [Fact]
    public void AnOffsetPassesThroughWithItsElement()
    {
        JsonObject flat = InputTarget.ToBridge(new InputTarget("Area", Offset: new InputOffset(4, -2.5)), "target");
        JsonObject deep = InputTarget.ToBridge(new InputTarget("Die", Offset: new InputOffset(0, 1, 0.5)), "from");

        Assert.Equal("{\"element\":\"Area\",\"offset\":{\"x\":4,\"y\":-2.5}}", flat.ToJsonString());
        Assert.Equal("{\"element\":\"Die\",\"offset\":{\"x\":0,\"y\":1,\"z\":0.5}}", deep.ToJsonString());
    }

    [Fact]
    public void AnOffsetWithAPointIsRefusedNamingTheKeysGiven() =>
        AssertOffsetRefused(
            new InputTarget(null, 10, 20, new InputOffset(1, 2)),
            "target",
            "{element: null, x: 10, y: 20, offset: {x: 1, y: 2, z: null}}"
        );

    [Fact]
    public void AnOffsetAloneIsRefusedNamingTheKeysGiven() =>
        AssertOffsetRefused(new InputTarget(Offset: new InputOffset(1, 2, 3)), "to", "{element: null, x: null, y: null, offset: {x: 1, y: 2, z: 3}}");

    [Fact]
    public void AnOffsetWithElementAndAPointIsRefused() => AssertRefused(new InputTarget("Area", 1, 2, new InputOffset(1, 2)), "target");

    // Bound as a tool binds it: a missing x must be refused by name, not read as 0.
    [Fact]
    public void AnOffsetMissingXIsRefusedByName()
    {
        InputTarget target = JsonSerializer.Deserialize<InputTarget>("{\"element\":\"Area\",\"offset\":{\"y\":5}}", ToolJson.Options)!;

        McpException refused = Assert.Throws<McpException>(() => InputTarget.ToBridge(target, "target"));

        Assert.Equal("target.offset needs x and y; got {element: Area, x: null, y: null, offset: {x: null, y: 5, z: null}}.", refused.Message);
    }

    [Fact]
    public void ATextAloneBecomesATextTarget()
    {
        JsonObject bridge = InputTarget.ToBridge(new InputTarget(Text: "New Game"), "target");

        Assert.Equal("{\"text\":\"New Game\"}", bridge.ToJsonString());
    }

    [Fact]
    public void ATextWithUnderPassesUnderThrough()
    {
        JsonObject bridge = InputTarget.ToBridge(new InputTarget(Text: "Strike", Under: "Hand/Row"), "to");

        Assert.Equal("{\"text\":\"Strike\",\"under\":\"Hand/Row\"}", bridge.ToJsonString());
    }

    [Fact]
    public void UnderWithoutTextIsRefusedNamingTheKeysGiven()
    {
        McpException refused = Assert.Throws<McpException>(() => InputTarget.ToBridge(new InputTarget("Card", Under: "Hand"), "target"));

        Assert.Equal("target.under narrows a text target, so it needs text; got {element: Card, x: null, y: null, under: 'Hand'}.", refused.Message);
    }

    [Fact]
    public void UnderWithABlankTextIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => InputTarget.ToBridge(new InputTarget(Text: " ", Under: "Hand"), "from"));

        Assert.StartsWith("from.under narrows a text target, so it needs text", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextWithAnElementIsRefusedNamingTheKeysGiven()
    {
        McpException refused = Assert.Throws<McpException>(() => InputTarget.ToBridge(new InputTarget("Card", Text: "Strike"), "to"));

        Assert.Equal(
            "to needs exactly one of element, text, or both x and y; got {element: Card, x: null, y: null, text: 'Strike'}.",
            refused.Message
        );
    }

    [Fact]
    public void ATextWithAPointIsRefused() => AssertRefused(new InputTarget(null, 1, 2, Text: "Strike"), "target");

    [Fact]
    public void ATextWithOnlyXIsRefused() => AssertRefused(new InputTarget(null, 1, null, Text: "Strike"), "target");

    [Fact]
    public void ABlankTextCountsAsNone()
    {
        JsonObject element = InputTarget.ToBridge(new InputTarget("Card", Text: "  "), "target");

        Assert.Equal("{\"element\":\"Card\"}", element.ToJsonString());
        AssertRefused(new InputTarget(Text: " \t"), "target");
    }

    [Fact]
    public void AnOffsetWithATextIsRefused() =>
        AssertOffsetRefused(
            new InputTarget(Offset: new InputOffset(1, 2), Text: "Strike"),
            "target",
            "{element: null, x: null, y: null, text: 'Strike', offset: {x: 1, y: 2, z: null}}"
        );

    [Fact]
    public void AnItemPassesThroughWithAnElement()
    {
        JsonObject byText = InputTarget.ToBridge(new InputTarget("Tabs", Item: new InputItem(Text: "Alex")), "target");
        JsonObject byIndex = InputTarget.ToBridge(new InputTarget("Rows", Item: new InputItem(Index: 0)), "from");

        Assert.Equal("{\"element\":\"Tabs\",\"item\":{\"text\":\"Alex\"}}", byText.ToJsonString());
        Assert.Equal("{\"element\":\"Rows\",\"item\":{\"index\":0}}", byIndex.ToJsonString());
    }

    [Fact]
    public void AnItemPassesThroughWithAText()
    {
        JsonObject bridge = InputTarget.ToBridge(
            new InputTarget(Text: "Inventory", Under: "Panel", Item: new InputItem(Path: ["Weapons", "Sword"], Column: 1)),
            "to"
        );

        Assert.Equal("{\"text\":\"Inventory\",\"under\":\"Panel\",\"item\":{\"path\":[\"Weapons\",\"Sword\"],\"column\":1}}", bridge.ToJsonString());
    }

    [Fact]
    public void AnItemWithAPointIsRefusedNamingTheKeysGiven() =>
        AssertItemRefused(
            new InputTarget(null, 1, 2, Item: new InputItem(Text: "Alex")),
            "target",
            "target.item takes element or text beside it; got {element: null, x: 1, y: 2, "
                + "item: {text: 'Alex', index: null, path: null, column: null}}."
        );

    [Fact]
    public void AnItemAloneIsRefused() =>
        AssertItemRefused(
            new InputTarget(Item: new InputItem(Index: 2)),
            "to",
            "to.item takes element or text beside it; got {element: null, x: null, y: null, item: {text: null, index: 2, path: null, column: null}}."
        );

    [Fact]
    public void AnItemWithTextAndIndexIsRefused() =>
        AssertItemRefused(
            new InputTarget("Rows", Item: new InputItem(Text: "Alex", Index: 1)),
            "target",
            "target.item takes exactly one of text, index or path; got {element: Rows, x: null, y: null, "
                + "item: {text: 'Alex', index: 1, path: null, column: null}}."
        );

    [Fact]
    public void AnItemWithOnlyAColumnIsRefused() =>
        AssertItemRefused(
            new InputTarget("Inventory", Item: new InputItem(Column: 1)),
            "target",
            "target.item takes exactly one of text, index or path; got {element: Inventory, x: null, y: null, "
                + "item: {text: null, index: null, path: null, column: 1}}."
        );

    [Fact]
    public void AnItemWithABlankTextIsRefusedAsBlank() =>
        AssertItemRefused(
            new InputTarget("Rows", Item: new InputItem(Text: " ")),
            "target",
            "target.item.text is blank; give the item's shown text, or index or path; got {element: Rows, x: null, y: null, "
                + "item: {text: ' ', index: null, path: null, column: null}}."
        );

    [Fact]
    public void AnEmptyItemPathIsRefused() =>
        AssertItemRefused(
            new InputTarget("Inventory", Item: new InputItem(Path: [])),
            "from",
            "from.item.path needs at least one non-empty text; got {element: Inventory, x: null, y: null, "
                + "item: {text: null, index: null, path: [], column: null}}."
        );

    [Fact]
    public void AnItemPathWithABlankTextIsRefused() =>
        AssertItemRefused(
            new InputTarget("Inventory", Item: new InputItem(Path: ["Weapons", " "])),
            "target",
            "target.item.path needs at least one non-empty text; got {element: Inventory, x: null, y: null, "
                + "item: {text: null, index: null, path: ['Weapons', ' '], column: null}}."
        );

    [Fact]
    public void ANegativeItemIndexIsRefused() =>
        AssertItemRefused(
            new InputTarget("Rows", Item: new InputItem(Index: -1)),
            "target",
            "target.item.index and item.column must be 0 or more; got {element: Rows, x: null, y: null, "
                + "item: {text: null, index: -1, path: null, column: null}}."
        );

    [Fact]
    public void ANegativeItemColumnIsRefused() =>
        AssertItemRefused(
            new InputTarget(Text: "Inventory", Item: new InputItem(Text: "Sword", Column: -2)),
            "target",
            "target.item.index and item.column must be 0 or more; got {element: null, x: null, y: null, text: 'Inventory', "
                + "item: {text: 'Sword', index: null, path: null, column: -2}}."
        );

    private static void AssertItemRefused(InputTarget target, string parameter, string expected)
    {
        McpException refused = Assert.Throws<McpException>(() => InputTarget.ToBridge(target, parameter));

        Assert.Equal(expected, refused.Message);
    }

    private static void AssertOffsetRefused(InputTarget target, string parameter, string given)
    {
        McpException refused = Assert.Throws<McpException>(() => InputTarget.ToBridge(target, parameter));

        Assert.Equal($"{parameter}.offset aims inside the node element names, so it needs element; got {given}.", refused.Message);
    }

    private static void AssertRefused(InputTarget? target, string parameter)
    {
        McpException refused = Assert.Throws<McpException>(() => InputTarget.ToBridge(target, parameter));

        Assert.StartsWith($"{parameter} needs exactly one of element, text, or both x and y; got ", refused.Message, StringComparison.Ordinal);
    }
}
