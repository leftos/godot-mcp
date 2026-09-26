using System.Text.Json;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>The property tools' argument checks, which refuse before a headless Godot starts; no Godot runs here.</summary>
public sealed class HeadlessPropertyValidationTests : IDisposable
{
    private static readonly JsonElement One = JsonDocument.Parse("1").RootElement;
    private readonly TempDirectory _temp = new();
    private readonly string _project;

    public HeadlessPropertyValidationTests()
    {
        _project = _temp.Combine("game");
        Directory.CreateDirectory(_project);
        File.WriteAllText(Path.Combine(_project, "project.godot"), "config_version=5\n");
        File.WriteAllText(Path.Combine(_project, "level.tscn"), string.Empty);
        File.WriteAllText(Path.Combine(_project, "enemy.tscn"), string.Empty);
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddNodeRefusesAnEmptyName(string name)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckNewNode(name, null));

        Assert.Equal("nodeName is empty; give the new node a name.", refused.Message);
        Assert.Equal(("Hat", "."), HeadlessTools.CheckNewNode(" Hat ", null));
    }

    [Theory]
    [InlineData("/root/Level")]
    [InlineData("root/Level")]
    [InlineData("../Other")]
    [InlineData("Boss:position")]
    public void AddNodeRefusesAParentPathOutsideTheRule(string parent)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckNewNode("Hat", parent));

        Assert.Equal(
            $"Node paths are relative to the scene root: use \".\" for the root and \"Boss/Sprite\" for a child, not \"{parent}\".",
            refused.Message
        );
        Assert.Equal(("Hat", "Boss"), HeadlessTools.CheckNewNode("Hat", " Boss "));
    }

    [Theory]
    [InlineData("level.tscn")]
    [InlineData("LEVEL.tscn")]
    [InlineData("res://Level.TSCN")]
    public void AddNodeRefusesTheSceneItselfInAnyCase(string nodeType)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckNodeType(_project, nodeType, "res://level.tscn"));

        Assert.Equal($"nodeType '{nodeType}' is the scene being edited; a scene cannot instance itself.", refused.Message);
        Assert.Equal("res://enemy.tscn", HeadlessTools.CheckNodeType(_project, "enemy.tscn", "res://level.tscn"));
        Assert.Equal("Sprite2D", HeadlessTools.CheckNodeType(_project, " Sprite2D ", "res://level.tscn"));
    }

    [Fact]
    public void SetNodePropertiesRejectsEmptyAndOverHundredLists()
    {
        McpException none = Assert.Throws<McpException>(() => HeadlessTools.CheckPropertyUpdates([]));
        McpException missing = Assert.Throws<McpException>(() => HeadlessTools.CheckPropertyUpdates(null));
        McpException many = Assert.Throws<McpException>(() => HeadlessTools.CheckPropertyUpdates([.. Updates(101)]));

        Assert.Equal("updates takes 1 to 100 entries; got 0.", none.Message);
        Assert.Equal("updates takes 1 to 100 entries; got 0.", missing.Message);
        Assert.Equal("updates takes 1 to 100 entries; got 101.", many.Message);
        Assert.Equal(100, HeadlessTools.CheckPropertyUpdates([.. Updates(100)]).Count);
    }

    [Fact]
    public void SetNodePropertiesRefusesAnUpdateWithNoValue()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            HeadlessTools.CheckPropertyUpdates([new PropertyUpdate("Box", "visible", One), new PropertyUpdate("Box", "position", default)])
        );

        Assert.Equal("updates[1] has no value; pass null to clear the property.", refused.Message);
    }

    [Fact]
    public void GetNodePropertiesRejectsEmptyAndOverFiftyLists()
    {
        NodePropertyQuery box = new("Box");

        McpException none = Assert.Throws<McpException>(() => HeadlessTools.CheckNodeQueries([]));
        McpException missing = Assert.Throws<McpException>(() => HeadlessTools.CheckNodeQueries(null));
        McpException many = Assert.Throws<McpException>(() => HeadlessTools.CheckNodeQueries([.. Enumerable.Repeat(box, 51)]));

        Assert.Equal("nodes takes 1 to 50 entries; got 0.", none.Message);
        Assert.Equal("nodes takes 1 to 50 entries; got 0.", missing.Message);
        Assert.Equal("nodes takes 1 to 50 entries; got 51.", many.Message);
        Assert.Equal(50, HeadlessTools.CheckNodeQueries([.. Enumerable.Repeat(box, 50)]).Count);
    }

    [Theory]
    [InlineData("/root/Level/Box")]
    [InlineData("root/Box")]
    [InlineData("Box/../Btn")]
    [InlineData("")]
    public void SetNodePropertiesRefusesABadNodePath(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckPropertyUpdates([new PropertyUpdate(path, "position", One)]));

        Assert.Equal(
            $"Node paths are relative to the scene root: use \".\" for the root and \"Boss/Sprite\" for a child, not \"{path}\".",
            refused.Message
        );
    }

    private static IEnumerable<PropertyUpdate> Updates(int count) => Enumerable.Repeat(new PropertyUpdate("Box", "visible", One), count);
}
