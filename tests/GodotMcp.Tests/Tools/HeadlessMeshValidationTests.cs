using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>export_mesh_library's argument checks, which refuse before a headless Godot starts; no Godot runs here.</summary>
public sealed class HeadlessMeshValidationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _project;

    public HeadlessMeshValidationTests()
    {
        _project = _temp.Combine("game");
        Directory.CreateDirectory(Path.Combine(_project, "levels"));
        File.WriteAllText(Path.Combine(_project, "project.godot"), "config_version=5\n");
        File.WriteAllText(Path.Combine(_project, "levels", "tiles.tres"), string.Empty);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void OutputPathMustBeTresOrRes()
    {
        foreach (string path in new[] { "levels/tiles.tscn", "res://tiles.meshlib", "tiles" })
        {
            McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckMeshLibraryPath(_project, path, overwrite: false));

            Assert.Equal($"outputPath '{path}' is not a MeshLibrary file: export_mesh_library writes .tres and .res files.", refused.Message);
        }

        Assert.Equal("res://new/tiles.tres", HeadlessTools.CheckMeshLibraryPath(_project, "new/tiles.tres", overwrite: false));
        Assert.Equal("res://new/tiles.res", HeadlessTools.CheckMeshLibraryPath(_project, "res://new/tiles.res", overwrite: false));
    }

    [Fact]
    public void OutputPathOutsideTheProjectIsRefused()
    {
        foreach (string path in new[] { "../tiles.tres", "res://../tiles.tres", _temp.Combine("tiles.tres") })
        {
            McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckMeshLibraryPath(_project, path, overwrite: false));

            Assert.Equal($"outputPath '{path}' is outside the project folder {_project}; pass a res:// path or a path inside it.", refused.Message);
        }
    }

    [Fact]
    public void ExistingOutputWithoutOverwriteIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckMeshLibraryPath(_project, "levels/tiles.tres", overwrite: false));

        Assert.Equal("res://levels/tiles.tres already exists; pass options.overwrite: true to replace it.", refused.Message);
        Assert.Equal("res://levels/tiles.tres", HeadlessTools.CheckMeshLibraryPath(_project, "res://levels/tiles.tres", overwrite: true));
    }

    [Fact]
    public void MeshItemNamesRejectsEmptyAndOverFiveHundred()
    {
        McpException none = Assert.Throws<McpException>(() => HeadlessTools.CheckMeshItemNames([]));
        McpException many = Assert.Throws<McpException>(() => HeadlessTools.CheckMeshItemNames([.. Enumerable.Repeat("Crate", 501)]));

        Assert.Equal("meshItemNames takes 1 to 500 names; got 0.", none.Message);
        Assert.Equal("meshItemNames takes 1 to 500 names; got 501.", many.Message);
        Assert.Equal(500, HeadlessTools.CheckMeshItemNames([.. Enumerable.Repeat("Crate", 500)]).Count);
        Assert.Equal(["Crate", "Ball"], HeadlessTools.CheckMeshItemNames([" Crate ", "Ball"]));
    }

    [Fact]
    public void BlankMeshItemNameIsRefused()
    {
        McpException blank = Assert.Throws<McpException>(() => HeadlessTools.CheckMeshItemNames(["Crate", "  "]));
        McpException empty = Assert.Throws<McpException>(() => HeadlessTools.CheckMeshItemNames([""]));

        Assert.Equal("meshItemNames[1] is empty.", blank.Message);
        Assert.Equal("meshItemNames[0] is empty.", empty.Message);
    }

    [Fact]
    public void OutputPathDifferingInCaseFromTheFileOnDiskIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckMeshLibraryPath(_project, "levels/Tiles.tres", overwrite: true));
        McpException folder = Assert.Throws<McpException>(() =>
            HeadlessTools.CheckMeshLibraryPath(_project, "res://Levels/new.tres", overwrite: false)
        );

        Assert.Equal("levels/Tiles.tres differs in case from the file on disk, res://levels/tiles.tres; use the exact case.", refused.Message);
        Assert.Equal("res://Levels/new.tres differs in case from the file on disk, res://levels/new.tres; use the exact case.", folder.Message);
    }
}
