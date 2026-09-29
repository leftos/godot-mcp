using GodotMcp.Server.Session;
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

    [Fact]
    public void AttachScriptRefusesANonScriptExtension()
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckScriptPath(_project, "levels/a.tscn"));

        Assert.Equal("scriptPath 'levels/a.tscn' is not a script: attach_script takes .gd and .cs files.", refused.Message);
        Assert.Equal("res://main.gd", HeadlessTools.CheckScriptPath(_project, "res://main.gd"));
    }

    [Fact]
    public void AttachScriptRefusesAPathOutsideTheProject()
    {
        foreach (string path in new[] { "../other.gd", "res://../other.gd", _temp.Combine("other.gd") })
        {
            McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckScriptPath(_project, path));

            Assert.Equal($"scriptPath '{path}' is outside the project folder {_project}; pass a res:// path or a path inside it.", refused.Message);
        }
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData(" . ")]
    public void DuplicateNodeRefusesTheRootPath(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckDuplicatedNodePath(path));

        Assert.Equal("The scene root cannot be duplicated; save_scene with newPath copies the whole scene.", refused.Message);
        Assert.Equal("Boss/Sprite", HeadlessTools.CheckDuplicatedNodePath("Boss/Sprite"));
    }

    [Fact]
    public void LoadSpriteRefusesATexturePathOutsideTheProject()
    {
        foreach (string path in new[] { "../t.png", "res://../t.png", _temp.Combine("t.png") })
        {
            McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckTexturePath(_project, path));

            Assert.Equal($"texturePath '{path}' is outside the project folder {_project}; pass a res:// path or a path inside it.", refused.Message);
        }
    }

    [Fact]
    public void AnImageWithoutImportSidecarNeedsAnImport()
    {
        string image = Path.Combine(_project, "art.png");
        File.WriteAllText(image, string.Empty);

        Assert.True(PrepScan.AssetNeedsImport(_project, image));
        File.WriteAllText(image + ".import", "[remap]\n");
        Assert.False(PrepScan.AssetNeedsImport(_project, image));
    }

    [Fact]
    public void AnImageWhoseImportedFileIsMissingNeedsAnImport()
    {
        string image = Path.Combine(_project, "art.png");
        File.WriteAllText(image, string.Empty);
        File.WriteAllText(image + ".import", "[remap]\n\n[deps]\n\ndest_files=[\"res://.godot/imported/art.png-1.ctex\"]\n");

        Assert.True(PrepScan.AssetNeedsImport(_project, image));
        Directory.CreateDirectory(Path.Combine(_project, ".godot", "imported"));
        File.WriteAllText(Path.Combine(_project, ".godot", "imported", "art.png-1.ctex"), string.Empty);
        Assert.False(PrepScan.AssetNeedsImport(_project, image));
    }

    [Theory]
    [InlineData("gradient.tres")]
    [InlineData("gradient.res")]
    [InlineData("GRADIENT.TRES")]
    [InlineData("atlas.dds")]
    [InlineData("atlas.ktx")]
    public void AFileThatLoadsWithoutAnImportNeverNeedsOne(string name)
    {
        string resource = Path.Combine(_project, name);
        File.WriteAllText(resource, string.Empty);

        Assert.False(PrepScan.AssetNeedsImport(_project, resource));
    }

    [Theory]
    [InlineData(".hidden/art", "res://.hidden")]
    [InlineData("ignored/deep", "res://ignored")]
    public void LoadSpriteRefusesATextureInAFolderGodotDoesNotScan(string folder, string unscanned)
    {
        Directory.CreateDirectory(Path.Combine(_project, folder));
        Directory.CreateDirectory(Path.Combine(_project, "ignored"));
        File.WriteAllText(Path.Combine(_project, "ignored", ".gdignore"), string.Empty);
        File.WriteAllText(Path.Combine(_project, folder, "t.png"), string.Empty);

        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckTexturePath(_project, $"{folder}/t.png"));

        Assert.Equal($"res://{folder}/t.png is in {unscanned}, which Godot does not scan; move the asset out of it.", refused.Message);
    }

    [Fact]
    public void LoadSpriteRefusesATextureInANestedProjectFolder()
    {
        Directory.CreateDirectory(Path.Combine(_project, "nested", "art"));
        File.WriteAllText(Path.Combine(_project, "nested", "project.godot"), "config_version=5\n");
        File.WriteAllText(Path.Combine(_project, "nested", "art", "t.png"), string.Empty);

        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckTexturePath(_project, "nested/art/t.png"));

        Assert.Equal("res://nested/art/t.png is in res://nested, which Godot does not scan; move the asset out of it.", refused.Message);
    }

    [Fact]
    public void ACaseVariantOfAFileOnDiskIsRefused()
    {
        McpException script = Assert.Throws<McpException>(() => HeadlessTools.CheckScriptPath(_project, "res://MAIN.gd"));
        McpException scene = Assert.Throws<McpException>(() => HeadlessTools.CheckEditableScenePath(_project, "Levels/A.tscn"));

        Assert.Equal("res://MAIN.gd differs in case from the file on disk, res://main.gd; use the exact case.", script.Message);
        Assert.Equal("Levels/A.tscn differs in case from the file on disk, res://levels/a.tscn; use the exact case.", scene.Message);
        Assert.Equal("res://levels/new.tscn", HeadlessTools.CheckNewScenePath(_project, "levels/new.tscn", "newPath", overwrite: false));
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
