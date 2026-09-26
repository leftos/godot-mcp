using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The headless scene edits (headless/scene_ops.gd): create_scene, save_scene and delete_nodes. Each always runs the prep, opens
/// the scene as the editor does, and saves it with its uids kept; an edit is all or nothing.
/// </summary>
internal sealed partial class HeadlessTools
{
    internal const int MaxNodePaths = 100;

    private const string WriteNote =
        " Runs the prep first, as run_project does (a C# build when stale, an import when needed). A scene that uses C# scripts is "
        + "not saved while the project's C# build fails. Refused while a session is live on the project (a headless run would "
        + "load the bridge from its override.cfg); stop_project or detach_project it first.";

    private const string ScenePathDescription =
        "The scene: a res:// path or a path relative to the project folder, ending .tscn (the edit tools write text scenes only).";

    [McpServerTool(Name = "create_scene", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Creates a scene file holding one root node, in a headless Godot, without running the game. Missing folders are "
            + "created; an existing file is refused unless options.overwrite. The new scene gets a new uid; a replaced file keeps "
            + "its own, so what referred to it finds the new scene. Returns {scenePath, root: {name, type}, uid, errors?}: errors "
            + "lists what Godot logged as errors while it worked."
            + WriteNote
    )]
    public async Task<string> CreateSceneAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description("The scene to write: a res:// path or a path relative to the project folder, ending .tscn.")] string scenePath,
        [Description("The root's type: a Godot class derived from Node (Node2D, Node3D, Control...), or a script's class_name whose base is one.")]
            string rootType = "Node2D",
        [Description("The root's name; left out, the file's name in PascalCase (player_ship.tscn gives PlayerShip).")] string? rootName = null,
        [Description("{overwrite}: replace the file when one exists at scenePath; false by default.")] SceneWriteOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            JsonObject parameters = new()
            {
                ["scene"] = CheckNewScenePath(projectDir, scenePath, "scenePath", options?.Overwrite == true),
                ["rootType"] = rootType.Trim(),
                ["rootName"] = rootName?.Trim() ?? string.Empty,
            };
            return RunWriteAsync(projectDir, "create_scene", parameters, cancellationToken);
        });
        return WithErrors(run);
    }

    [McpServerTool(Name = "save_scene", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Opens a scene file and saves it again, in a headless Godot, without running the game: in place, or to newPath (a "
            + "save-as). It is saved as the editor saves it: instanced and inherited scenes stay references to their files, and the "
            + "uids of the scene and of the files it uses are kept. Saved in place, the scene keeps its uid; saved as a new file, "
            + "the copy gets a new uid; saved over an existing file (options.overwrite), it takes that file's uid. Returns "
            + "{scenePath, savedTo, uid, errors?}."
            + WriteNote
    )]
    public async Task<string> SaveSceneAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(ScenePathDescription)] string scenePath,
        [Description("Where to save instead: a res:// path or a path relative to the project folder, ending .tscn; in place when left out.")]
            string? newPath = null,
        [Description("{overwrite}: replace the file when one exists at newPath; false by default.")] SceneWriteOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            string scene = CheckEditableScenePath(projectDir, scenePath);
            JsonObject parameters = new() { ["scene"] = scene, ["target"] = SaveTarget(projectDir, scene, newPath, options?.Overwrite == true) };
            return RunWriteAsync(projectDir, "save_scene", parameters, cancellationToken);
        });
        return WithErrors(run);
    }

    [McpServerTool(Name = "delete_nodes", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Deletes nodes, with their children, from a scene file and saves it, in a headless Godot, without running the game. "
            + "The scene's root cannot be deleted, nor a node inside an instanced scene (edit that scene's own file instead); the "
            + "root of an instance can, which removes the instance. All or nothing: when any path is refused, nothing is saved and "
            + "the error names every refused path. Returns {deleted, errors?}: deleted lists the paths as given."
            + WriteNote
    )]
    public async Task<string> DeleteNodesAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(ScenePathDescription)] string scenePath,
        [Description("1 to 100 nodes to delete, each by its path relative to the scene's root (Boss, Boss/Sprite).")] string[] nodePaths,
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<string> paths = CheckNodePaths(nodePaths);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            JsonObject parameters = new()
            {
                ["scene"] = CheckEditableScenePath(projectDir, scenePath),
                ["nodePaths"] = new JsonArray([.. paths.Select(path => (JsonNode)path)]),
            };
            return RunWriteAsync(projectDir, "delete_nodes", parameters, cancellationToken);
        });
        return WithErrors(run);
    }

    /// <summary>The node paths, each checked and trimmed.</summary>
    /// <exception cref="McpException">There are not 1 to <see cref="MaxNodePaths"/>, or one is not relative to the scene root.</exception>
    internal static IReadOnlyList<string> CheckNodePaths(IReadOnlyList<string>? nodePaths)
    {
        int count = nodePaths?.Count ?? 0;
        if (nodePaths is null || count is < 1 or > MaxNodePaths)
        {
            throw new McpException($"nodePaths takes 1 to {MaxNodePaths} node paths; got {count}.");
        }

        return [.. nodePaths.Select(CheckNodePath)];
    }

    /// <summary>The node path trimmed: "." or a path relative to the scene root.</summary>
    /// <exception cref="McpException">
    /// The path is empty, absolute, starts at root/, climbs with "..", or names a property with ":" (which no node name holds).
    /// </exception>
    internal static string CheckNodePath(string nodePath)
    {
        string trimmed = nodePath?.Trim() ?? string.Empty;
        bool refused =
            trimmed.Length == 0
            || trimmed.StartsWith('/')
            || trimmed.StartsWith("root/", StringComparison.Ordinal)
            || trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.Contains(':', StringComparison.Ordinal);
        return refused
            ? throw new McpException(
                $"Node paths are relative to the scene root: use \".\" for the root and \"Boss/Sprite\" for a child, not \"{nodePath}\"."
            )
            : trimmed;
    }

    /// <summary>A scene path to write, as a res:// path; its folders need not exist.</summary>
    /// <exception cref="McpException">The path is empty, outside the project, not a .tscn, or names a file and overwrite is false.</exception>
    internal static string CheckNewScenePath(string projectDir, string path, string argument, bool overwrite)
    {
        RefuseBinaryScene(path, argument);
        string full = ResolvePath(projectDir, path, SceneRule(argument));
        string resPath = ResOf(projectDir, full);
        return overwrite || !File.Exists(full)
            ? resPath
            : throw new McpException($"{resPath} already exists; pass options.overwrite: true to replace it.");
    }

    /// <summary>An existing scene an edit tool opens and saves, as a res:// path.</summary>
    /// <exception cref="McpException">The path is empty, outside the project, missing, or not a .tscn.</exception>
    internal static string CheckEditableScenePath(string projectDir, string scenePath)
    {
        RefuseBinaryScene(scenePath, "scenePath");
        return CheckScenePath(projectDir, scenePath);
    }

    /// <summary>
    /// Refuses a binary scene: outside the editor its uid cannot be read back, since ResourceLoader.get_resource_uid only asks
    /// the uid cache (4.7.2 core/io/resource_loader.cpp L1412-1416), so a save would lose it. get_scene_file_tree still reads one.
    /// </summary>
    /// <exception cref="McpException">The path ends .scn.</exception>
    private static void RefuseBinaryScene(string path, string argument)
    {
        if (Path.GetExtension(path?.Trim() ?? string.Empty).Equals(".scn", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException($"{argument} '{path}' is a binary scene; the scene edit tools write .tscn only.");
        }
    }

    /// <summary>Where save_scene writes: the scene itself when newPath is left out or names it, else newPath checked as a new file.</summary>
    private static string SaveTarget(string projectDir, string scene, string? newPath, bool overwrite)
    {
        if (newPath is null)
        {
            return scene;
        }

        string target = ResOf(projectDir, ResolvePath(projectDir, newPath, SceneRule("newPath")));
        return string.Equals(target, scene, StringComparison.OrdinalIgnoreCase)
            ? scene
            : CheckNewScenePath(projectDir, newPath, "newPath", overwrite);
    }

    private Task<HeadlessResult> RunWriteAsync(string projectDir, string operation, JsonObject parameters, CancellationToken cancellationToken) =>
        HeadlessRunner.RunAsync(sessions, new HeadlessRequest(projectDir, operation, parameters, Prepare: true, RunCeiling), cancellationToken);

    /// <summary>The operation's result, with <c>errors</c> added when Godot logged any while it ran.</summary>
    private static string WithErrors(HeadlessResult run)
    {
        JsonObject result = run.Result?.DeepClone() as JsonObject ?? [];
        JsonArray errors = OnlyErrors(run.EngineErrors);
        if (errors.Count > 0)
        {
            result["errors"] = errors;
        }

        return result.ToJsonString();
    }
}

/// <summary>Whether a scene tool may replace the file it writes.</summary>
internal sealed record SceneWriteOptions(
    [property: Description("Replace the file at the path being written when one exists; false by default.")] bool? Overwrite = null
);
