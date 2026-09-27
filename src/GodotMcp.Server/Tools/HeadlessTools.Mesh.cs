using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// export_mesh_library (headless/scene_mesh.gd): builds a GridMap's MeshLibrary from a 3D scene file, by the editor's rule
/// (4.7.2 editor/scene/3d/mesh_library_editor_plugin.cpp L515-618) with the navigation mesh placed by the item's transform
/// too, and saves it as a .tres or .res; the scene is only read.
/// </summary>
internal sealed partial class HeadlessTools
{
    internal const int MaxMeshItemNames = 500;

    private static readonly PathRule MeshLibraryRule = new(
        "outputPath",
        [".tres", ".res"],
        "is not a MeshLibrary file: export_mesh_library writes .tres and .res files."
    );

    [McpServerTool(Name = "export_mesh_library", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Builds a MeshLibrary, the item set a GridMap paints with, from a 3D scene file and saves it as a .tres or .res, in a "
            + "headless Godot, without running the game; the scene is only read. By the editor's Import from Scene rule, every "
            + "MeshInstance3D with a mesh below the scene's root becomes an item named after the node (a MeshInstance3D's own "
            + "children are not searched), with its mesh (its surface override materials applied) and its shadow casting. As in "
            + "the editor, the item's transform is the node's own, its parents' left out, and its shapes are the enabled "
            + "collision shapes of its StaticBody3D children, placed by the item's transform, then the body's and the shape's. "
            + "Its navigation mesh is that of its first NavigationRegion3D child with one, placed by the item's transform and "
            + "then the region's, so it lines up with the mesh in a GridMap cell; the editor places it by the region's alone. "
            + "Ids run from 0 in tree order; a later node of an item's name takes the item over, keeping the earlier node's "
            + "navigation mesh when it has none, as in the editor. No previews are made (they need the editor). Meshes and "
            + "shapes built into the scene are written into the library; ones saved in their own files stay references to their "
            + "files, unless the node overrides a surface material, when the library holds a copy of the mesh with it set. A "
            + "file the scene uses is refused as outputPath, whether used directly, through an instanced scene or a resource "
            + "file, or by a node an earlier batch step changed. Missing folders are created; an existing file is refused "
            + "unless options.overwrite, and a replaced .tres "
            + "keeps its uid (a replaced .res gets a new one: a binary file's uid cannot be read outside the editor). Returns "
            + "{outputPath, items: [{id, name, shapes, navigation}], replaced?: [{name, count}], errors?}: shapes counts the "
            + "item's collision shapes, navigation says whether it has a navigation mesh, and replaced lists the item names more "
            + "than one MeshInstance3D carried, count being how many; the last one's meshes make the item. Runs the prep first, "
            + "as run_project does (a C# build when stale, "
            + "an import when needed)."
            + RefusedNote
    )]
    public async Task<string> ExportMeshLibraryAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description("The scene to read: a res:// path or a path relative to the project folder, ending .tscn or .scn.")] string scenePath,
        [Description("The MeshLibrary to write: a res:// path or a path relative to the project folder, ending .tres or .res.")] string outputPath,
        [Description(
            "1 to 500 names of MeshInstance3D nodes to make items of, the rest left out; every one when left out. An empty name "
                + "is refused, and so is a name the scene has no item for, listing the names it has."
        )]
            string[]? meshItemNames = null,
        [Description("{overwrite}: replace the file when one exists at outputPath; false by default.")] SceneWriteOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        // Refused before the project is read; the builder checks them again.
        _ = CheckOptionalMeshItemNames(meshItemNames);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            string scene = CheckScenePath(projectDir, scenePath);
            JsonObject parameters = ExportMeshLibraryParameters(projectDir, outputPath, meshItemNames, options);
            parameters["scene"] = scene;
            return RunWriteAsync(projectDir, "export_mesh_library", parameters, cancellationToken);
        });
        return WithErrors(run);
    }

    /// <summary>export_mesh_library's request parameters but the scene: <c>{output, meshItemNames}</c>, no names meaning every item.</summary>
    /// <exception cref="McpException">As <see cref="CheckMeshItemNames"/> for names given, then <see cref="CheckMeshLibraryPath"/>.</exception>
    internal static JsonObject ExportMeshLibraryParameters(string projectDir, string outputPath, string[]? meshItemNames, SceneWriteOptions? options)
    {
        IReadOnlyList<string> names = CheckOptionalMeshItemNames(meshItemNames);
        return new JsonObject
        {
            ["output"] = CheckMeshLibraryPath(projectDir, outputPath, options?.Overwrite == true),
            ["meshItemNames"] = new JsonArray([.. names.Select(name => (JsonNode)name)]),
        };
    }

    /// <summary>The mesh item names checked as <see cref="CheckMeshItemNames"/> does, or none when left out.</summary>
    private static IReadOnlyList<string> CheckOptionalMeshItemNames(string[]? meshItemNames) =>
        meshItemNames is null ? [] : CheckMeshItemNames(meshItemNames);

    /// <summary>The MeshLibrary file export_mesh_library writes, as a res:// path; its folders need not exist.</summary>
    /// <exception cref="McpException">
    /// The path is empty, outside the project, not a .tres or .res, or names a file and overwrite is false.
    /// </exception>
    internal static string CheckMeshLibraryPath(string projectDir, string outputPath, bool overwrite)
    {
        string full = ResolvePath(projectDir, outputPath, MeshLibraryRule);
        string resPath = ResOf(projectDir, full);
        return overwrite || !File.Exists(full)
            ? resPath
            : throw new McpException($"{resPath} already exists; pass options.overwrite: true to replace it.");
    }

    /// <summary>The mesh item names, each trimmed.</summary>
    /// <exception cref="McpException">There are not 1 to <see cref="MaxMeshItemNames"/>, or one is empty or whitespace.</exception>
    internal static IReadOnlyList<string> CheckMeshItemNames(IReadOnlyList<string> meshItemNames)
    {
        if (meshItemNames.Count is < 1 or > MaxMeshItemNames)
        {
            throw new McpException($"meshItemNames takes 1 to {MaxMeshItemNames} names; got {meshItemNames.Count}.");
        }

        string[] names = [.. meshItemNames.Select(name => name?.Trim() ?? string.Empty)];
        int blank = Array.FindIndex(names, string.IsNullOrEmpty);
        return blank < 0 ? names : throw new McpException($"meshItemNames[{blank}] is empty.");
    }
}
