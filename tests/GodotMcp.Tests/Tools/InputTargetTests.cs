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

    private static void AssertRefused(InputTarget? target, string parameter)
    {
        McpException refused = Assert.Throws<McpException>(() => InputTarget.ToBridge(target, parameter));

        Assert.StartsWith($"{parameter} needs either element, or both x and y", refused.Message, StringComparison.Ordinal);
    }
}
