using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// get_scene_file_tree's scene check, which reads a model a scene importer brings in where every scene edit tool refuses it; no
/// Godot runs here.
/// </summary>
public sealed class ImportedScenePathTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _project;

    public ImportedScenePathTests()
    {
        _project = _temp.Combine("game");
        Directory.CreateDirectory(Path.Combine(_project, "models"));
        Directory.CreateDirectory(Path.Combine(_project, "upper"));
        File.WriteAllText(Path.Combine(_project, "project.godot"), "config_version=5\n");
        File.WriteAllText(Path.Combine(_project, "level.tscn"), string.Empty);
        foreach (string name in new[] { "model.glb", "model.gltf", "model.fbx", "model.blend", "model.dae" })
        {
            File.WriteAllText(Path.Combine(_project, "models", name), string.Empty);
        }

        File.WriteAllText(Path.Combine(_project, "upper", "MODEL.GLB"), string.Empty);
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("models/model.glb", "res://models/model.glb")]
    [InlineData("models/model.gltf", "res://models/model.gltf")]
    [InlineData("models/model.fbx", "res://models/model.fbx")]
    [InlineData("models/model.blend", "res://models/model.blend")]
    [InlineData("models/model.dae", "res://models/model.dae")]
    [InlineData("res://models/model.gltf", "res://models/model.gltf")]
    [InlineData("upper/MODEL.GLB", "res://upper/MODEL.GLB")]
    [InlineData("level.tscn", "res://level.tscn")]
    public void AnImportedModelOrASceneIsReadable(string path, string resPath) =>
        Assert.Equal(resPath, HeadlessTools.CheckReadableScenePath(_project, path));

    [Fact]
    public void AFileOfAnotherKindIsRefusedWithTheNewMessage()
    {
        string refusal =
            "is not a scene: get_scene_file_tree takes .tscn, .scn and .xscn files and imported .glb, .gltf, .fbx, .blend and .dae models.";
        McpException obj = Assert.Throws<McpException>(() => HeadlessTools.CheckReadableScenePath(_project, "models/model.obj"));
        McpException png = Assert.Throws<McpException>(() => HeadlessTools.CheckReadableScenePath(_project, "icon.png"));

        Assert.Equal($"scenePath 'models/model.obj' {refusal}", obj.Message);
        Assert.Equal($"scenePath 'icon.png' {refusal}", png.Message);
    }

    [Fact]
    public void AModelInAFolderGodotSkipsIsRefused()
    {
        Directory.CreateDirectory(Path.Combine(_project, "skipped"));
        File.WriteAllText(Path.Combine(_project, "skipped", ".gdignore"), string.Empty);
        File.WriteAllText(Path.Combine(_project, "skipped", "model.glb"), string.Empty);

        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.SceneImportAssets(_project, "res://skipped/model.glb"));

        Assert.Equal("res://skipped/model.glb is in res://skipped, which Godot does not scan; move the asset out of it.", refused.Message);
    }

    [Theory]
    [InlineData("res://upper/MODEL.GLB", "upper")]
    [InlineData("res://level.tscn", null)]
    public void SceneImportAssetsTakesAnImportedModelsFullPathAndNothingElse(string scenePath, string? folder)
    {
        IReadOnlyList<string> assets = HeadlessTools.SceneImportAssets(_project, scenePath);

        if (folder is null)
        {
            Assert.Empty(assets);
            return;
        }

        Assert.Equal([Path.Combine(_project, folder, "MODEL.GLB")], assets);
    }

    [Fact]
    public void TheSceneToolsStillRefuseAnImportedModel()
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckScenePath(_project, "models/model.glb"));

        Assert.Equal("scenePath 'models/model.glb' is not a scene: the scene tools take .tscn, .scn and .xscn files.", refused.Message);
        Assert.Equal("res://level.tscn", HeadlessTools.CheckScenePath(_project, "level.tscn"));
    }
}
