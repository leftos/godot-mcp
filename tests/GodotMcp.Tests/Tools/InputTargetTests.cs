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

    private static void AssertOffsetRefused(InputTarget target, string parameter, string given)
    {
        McpException refused = Assert.Throws<McpException>(() => InputTarget.ToBridge(target, parameter));

        Assert.Equal($"{parameter}.offset aims inside the node element names, so it needs element; got {given}.", refused.Message);
    }

    private static void AssertRefused(InputTarget? target, string parameter)
    {
        McpException refused = Assert.Throws<McpException>(() => InputTarget.ToBridge(target, parameter));

        Assert.StartsWith($"{parameter} needs either element, or both x and y", refused.Message, StringComparison.Ordinal);
    }
}
