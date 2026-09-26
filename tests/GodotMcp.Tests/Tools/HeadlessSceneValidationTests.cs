using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>The scene tools' argument checks, which refuse before a headless Godot starts; no Godot runs here.</summary>
public sealed class HeadlessSceneValidationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _project;

    public HeadlessSceneValidationTests()
    {
        _project = _temp.Combine("game");
        Directory.CreateDirectory(Path.Combine(_project, "levels"));
        File.WriteAllText(Path.Combine(_project, "project.godot"), "config_version=5\n");
        File.WriteAllText(Path.Combine(_project, "levels", "a.tscn"), string.Empty);
        File.WriteAllText(Path.Combine(_project, "levels", "c.scn"), string.Empty);
        File.WriteAllText(Path.Combine(_project, "main.gd"), string.Empty);
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("/root/Level/Boss")]
    [InlineData("/Boss")]
    public void NodePathWithLeadingSlashIsRefused(string path) => AssertNodePathRefused(path);

    [Theory]
    [InlineData("root/Level")]
    [InlineData("root/")]
    public void NodePathStartingWithRootIsRefused(string path) => AssertNodePathRefused(path);

    [Theory]
    [InlineData("..")]
    [InlineData("Boss/../Btn")]
    [InlineData("../Other")]
    public void NodePathWithDotDotIsRefused(string path) => AssertNodePathRefused(path);

    [Theory]
    [InlineData("Boss:position")]
    [InlineData("Boss/Sprite:texture:size")]
    public void NodePathWithSubnameIsRefused(string path) => AssertNodePathRefused(path);

    [Fact]
    public void DotAndRelativeNodePathsAreAccepted()
    {
        IReadOnlyList<string> accepted = HeadlessTools.CheckNodePaths([".", "Boss", "Boss/Sprite", "./Btn", " Btn "]);

        Assert.Equal([".", "Boss", "Boss/Sprite", "./Btn", "Btn"], accepted);
        Assert.Equal("Rooted/Child", HeadlessTools.CheckNodePath("Rooted/Child"));
    }

    [Fact]
    public void NewPathOutsideProjectIsRefused()
    {
        foreach (string path in new[] { "../copy.tscn", "res://../copy.tscn", _temp.Combine("copy.tscn") })
        {
            McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckNewScenePath(_project, path, "newPath", overwrite: false));

            Assert.Equal($"newPath '{path}' is outside the project folder {_project}; pass a res:// path or a path inside it.", refused.Message);
        }
    }

    [Fact]
    public void ExistingNewPathWithoutOverwriteIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            HeadlessTools.CheckNewScenePath(_project, "levels/a.tscn", "newPath", overwrite: false)
        );

        Assert.Equal("res://levels/a.tscn already exists; pass options.overwrite: true to replace it.", refused.Message);
        Assert.Equal("res://levels/a.tscn", HeadlessTools.CheckNewScenePath(_project, "levels/a.tscn", "newPath", overwrite: true));
        Assert.Equal("res://new/b.tscn", HeadlessTools.CheckNewScenePath(_project, "res://new/b.tscn", "scenePath", overwrite: false));
    }

    [Fact]
    public void DeleteNodesRejectsEmptyAndOverHundredLists()
    {
        McpException none = Assert.Throws<McpException>(() => HeadlessTools.CheckNodePaths([]));
        McpException missing = Assert.Throws<McpException>(() => HeadlessTools.CheckNodePaths(null));
        McpException many = Assert.Throws<McpException>(() => HeadlessTools.CheckNodePaths([.. Enumerable.Repeat("Boss", 101)]));

        Assert.Equal("nodePaths takes 1 to 100 node paths; got 0.", none.Message);
        Assert.Equal("nodePaths takes 1 to 100 node paths; got 0.", missing.Message);
        Assert.Equal("nodePaths takes 1 to 100 node paths; got 101.", many.Message);
        Assert.Equal(100, HeadlessTools.CheckNodePaths([.. Enumerable.Repeat("Boss", 100)]).Count);
    }

    [Fact]
    public void SceneExtensionMustBeTscnOrScn()
    {
        McpException newPath = Assert.Throws<McpException>(() =>
            HeadlessTools.CheckNewScenePath(_project, "levels/b.tres", "scenePath", overwrite: false)
        );
        McpException existing = Assert.Throws<McpException>(() => HeadlessTools.CheckScenePath(_project, "res://main.gd"));

        Assert.Equal("scenePath 'levels/b.tres' is not a scene: the scene tools take .tscn and .scn files.", newPath.Message);
        Assert.Equal("scenePath 'res://main.gd' is not a scene: the scene tools take .tscn and .scn files.", existing.Message);
    }

    [Fact]
    public void BinaryScenesAreRefusedForWriting()
    {
        McpException created = Assert.Throws<McpException>(() =>
            HeadlessTools.CheckNewScenePath(_project, "levels/b.scn", "newPath", overwrite: false)
        );
        McpException edited = Assert.Throws<McpException>(() => HeadlessTools.CheckEditableScenePath(_project, "res://levels/c.SCN"));

        Assert.Equal("newPath 'levels/b.scn' is a binary scene; the scene edit tools write .tscn only.", created.Message);
        Assert.Equal("scenePath 'res://levels/c.SCN' is a binary scene; the scene edit tools write .tscn only.", edited.Message);
        Assert.Equal("res://levels/a.tscn", HeadlessTools.CheckEditableScenePath(_project, "levels/a.tscn"));
        Assert.Equal("res://levels/c.scn", HeadlessTools.CheckScenePath(_project, "levels/c.scn"));
    }

    private static void AssertNodePathRefused(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckNodePaths(["Boss", path]));

        Assert.Equal(
            $"Node paths are relative to the scene root: use \".\" for the root and \"Boss/Sprite\" for a child, not \"{path}\".",
            refused.Message
        );
    }
}
