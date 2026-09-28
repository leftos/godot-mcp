using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>move_node's argument checks, and add_node's position, which refuse before a headless Godot starts; no Godot runs here.</summary>
public sealed class MoveNodeValidationTests
{
    private const string OneKey = "position takes exactly one of index, before or after; got ";

    [Fact]
    public void NoParentAndNoPositionIsRefused()
    {
        McpException noOptions = Assert.Throws<McpException>(() => HeadlessTools.MoveNodeParameters("Box", null));
        McpException emptyOptions = Assert.Throws<McpException>(() => HeadlessTools.MoveNodeParameters("Box", new MoveNodeOptions()));
        McpException onlyTransform = Assert.Throws<McpException>(() =>
            HeadlessTools.MoveNodeParameters("Box", new MoveNodeOptions(KeepGlobalTransform: false))
        );

        Assert.Equal("move_node needs options.parent, options.position or both.", noOptions.Message);
        Assert.Equal(noOptions.Message, emptyOptions.Message);
        Assert.Equal(noOptions.Message, onlyTransform.Message);
    }

    [Fact]
    public void APositionTakesExactlyOneKey()
    {
        McpException none = Assert.Throws<McpException>(() =>
            HeadlessTools.MoveNodeParameters("Box", new MoveNodeOptions(Position: new NodePosition()))
        );
        McpException two = Assert.Throws<McpException>(() =>
            HeadlessTools.MoveNodeParameters("Box", new MoveNodeOptions(Position: new NodePosition(Before: "A", After: "B")))
        );

        Assert.Equal(OneKey + "none.", none.Message);
        Assert.Equal(OneKey + "before, after.", two.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/root/Level/Box")]
    [InlineData("../Box")]
    public void ABadNodePathIsRefused(string nodePath)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.MoveNodeParameters(nodePath, new MoveNodeOptions(Parent: ".")));

        Assert.StartsWith("Node paths are relative to the scene root", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheParentIsCheckedAsAddNodesParentIs()
    {
        McpException moved = Assert.Throws<McpException>(() => HeadlessTools.MoveNodeParameters("Box", new MoveNodeOptions(Parent: "root/Level")));
        McpException added = Assert.Throws<McpException>(() => HeadlessTools.CheckNewNode("Hat", "root/Level"));

        Assert.Equal(added.Message, moved.Message);
    }

    [Fact]
    public void TheRequestCarriesTheNodeTheParentAndTheOnePositionKey()
    {
        JsonObject built = HeadlessTools.MoveNodeParameters(" Box ", new MoveNodeOptions(" Holder ", new NodePosition(Index: -1)));
        JsonObject reorder = HeadlessTools.MoveNodeParameters(
            "Box",
            new MoveNodeOptions(Position: new NodePosition(After: "Hat"), KeepGlobalTransform: false)
        );

        Assert.Equal("""{"nodePath":"Box","keepGlobalTransform":true,"parent":"Holder","position":{"index":-1}}""", built.ToJsonString());
        Assert.Equal("""{"nodePath":"Box","keepGlobalTransform":false,"position":{"after":"Hat"}}""", reorder.ToJsonString());
    }

    [Fact]
    public void AddNodesPositionIsCheckedAndPassedOn()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            HeadlessTools.AddNodeParameters(".", "res://level.tscn", "Node2D", "Hat", new AddNodeOptions(Position: new NodePosition(0, "A")))
        );
        JsonObject built = HeadlessTools.AddNodeParameters(
            ".",
            "res://level.tscn",
            "Node2D",
            "Hat",
            new AddNodeOptions(Position: new NodePosition(Before: "B"))
        );
        JsonObject last = HeadlessTools.AddNodeParameters(".", "res://level.tscn", "Node2D", "Hat", null);

        Assert.Equal(OneKey + "index, before.", refused.Message);
        Assert.Equal("""{"before":"B"}""", built["position"]?.ToJsonString());
        Assert.False(last.ContainsKey("position"));
    }
}
