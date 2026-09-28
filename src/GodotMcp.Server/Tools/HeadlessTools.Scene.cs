using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The headless scene edits (headless/scene_ops.gd): create_scene, save_scene, delete_nodes, attach_script, duplicate_node and
/// load_sprite. Each always runs the prep, opens
/// the scene as the editor does, and saves it with its uids kept; an edit is all or nothing.
/// </summary>
internal sealed partial class HeadlessTools
{
    internal const int MaxNodePaths = 100;

    private const string WriteNote =
        " Runs the prep first, as run_project does (a C# build when stale, an import when needed). A scene that uses C# scripts is "
        + "not saved while the project's C# build fails. Refused while a session is live on the project (a headless run would "
        + "load the bridge from its override.cfg); stop_project or detach_project it first.";

    private const string EditNote =
        " The file keeps every part the edit does not change as it was; when that cannot be done, it is saved in Godot's full "
        + "form and the result carries a warning saying why.";

    private const string ScenePathDescription =
        "The scene: a res:// path or a path relative to the project folder, ending .tscn (the edit tools write text scenes only).";

    private const string NodePathDescription = "The node, by its path relative to the scene's root (\".\" for the root, Boss/Sprite).";

    private const string RootDuplicateRefusal = "The scene root cannot be duplicated; save_scene with newPath copies the whole scene.";

    // What Godot logs for each C# script and C# autoload while the project assembly is missing (IsMissingAssemblySymptom).
    private const string CSharpClassMissing = "Cannot instantiate C# script because the associated class could not be found";
    private const string AutoloadNotInstantiated = "Failed to instantiate an autoload, script '";
    private const string CSharpAutoloadNotANode = ".cs' does not inherit from 'Node'";

    private static readonly string[] ScriptExtensions = [".gd", ".cs"];

    private static readonly PathRule TextureRule = new(
        "texturePath",
        [".png", ".jpg", ".jpeg", ".webp", ".svg", ".bmp", ".tga", ".exr", ".hdr", ".dds", ".ktx", ".tres", ".res"],
        "is not a texture: load_sprite takes images (.png, .jpg, .jpeg, .webp, .svg, .bmp, .tga, .exr, .hdr, .dds, .ktx) and .tres or .res textures."
    );

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
            + "the error names every refused path. Returns {deleted, warning?, errors?}: deleted lists the paths as given."
            + WriteNote
            + EditNote
    )]
    public async Task<string> DeleteNodesAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(ScenePathDescription)] string scenePath,
        [Description("1 to 100 nodes to delete, each by its path relative to the scene's root (Boss, Boss/Sprite).")] string[] nodePaths,
        CancellationToken cancellationToken = default
    )
    {
        // Refused before the project is read; the builder checks it again.
        _ = CheckNodePaths(nodePaths);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            string scene = CheckEditableScenePath(projectDir, scenePath);
            JsonObject parameters = DeleteNodesParameters(nodePaths);
            parameters["scene"] = scene;
            return RunWriteAsync(projectDir, "delete_nodes", parameters, cancellationToken);
        });
        return WithErrors(run);
    }

    /// <summary>delete_nodes' request parameters but the scene: <c>{nodePaths}</c>.</summary>
    /// <exception cref="McpException">As <see cref="CheckNodePaths"/>.</exception>
    internal static JsonObject DeleteNodesParameters(string[] nodePaths) =>
        new() { ["nodePaths"] = new JsonArray([.. CheckNodePaths(nodePaths).Select(path => (JsonNode)path)]) };

    [McpServerTool(Name = "attach_script", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Attaches a script to a node of a scene file, replacing the script it had, and saves the scene, in a headless Godot, "
            + "without running the game. The script must compile and extend the node's class or one of its parents. A node inside "
            + "an instanced scene is refused (attach it in that scene's own file); an instance's root and a node an inherited "
            + "scene gets from its base are allowed, saved as overrides. A C# script is refused while the project's C# build "
            + "fails. Returns {path, script: {resource, uid?}, previous?: {resource, uid?}, warning?, errors?}: path is the node's path "
            + "from the scene's root, previous the script it had."
            + WriteNote
            + EditNote
    )]
    public async Task<string> AttachScriptAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(ScenePathDescription)] string scenePath,
        [Description(NodePathDescription)] string nodePath,
        [Description("The script: a res:// path or a path relative to the project folder, ending .gd or .cs.")] string scriptPath,
        CancellationToken cancellationToken = default
    )
    {
        // Refused before the project is read; the builder checks it again.
        _ = CheckNodePath(nodePath);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            string scene = CheckEditableScenePath(projectDir, scenePath);
            JsonObject parameters = AttachScriptParameters(projectDir, nodePath, scriptPath);
            parameters["scene"] = scene;
            return RunWriteAsync(projectDir, "attach_script", parameters, cancellationToken);
        });
        return WithErrors(run);
    }

    /// <summary>attach_script's request parameters but the scene: <c>{nodePath, script}</c>.</summary>
    /// <exception cref="McpException">As <see cref="CheckNodePath"/>, then as <see cref="CheckScriptPath"/>.</exception>
    internal static JsonObject AttachScriptParameters(string projectDir, string nodePath, string scriptPath) =>
        new() { ["nodePath"] = CheckNodePath(nodePath), ["script"] = CheckScriptPath(projectDir, scriptPath) };

    [McpServerTool(Name = "duplicate_node", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Copies a node, with its children, within a scene file and saves the scene, in a headless Godot, without running the "
            + "game. The copy goes right after the node under the same parent, or last under options.parent. Instanced scenes "
            + "in the copy stay instances with their overridden values. Connections between the copied nodes, and from them to "
            + "nodes outside the copy, are kept; connections coming into the copy from outside it are not. The scene's root, a "
            + "node inside an instanced "
            + "scene and a parent inside one are refused (an instance's root is a valid parent). Returns {originalPath, newPath, "
            + "warning?, errors?}, both paths from the scene's root."
            + WriteNote
            + EditNote
    )]
    public async Task<string> DuplicateNodeAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(ScenePathDescription)] string scenePath,
        [Description(NodePathDescription + " Not the root.")] string nodePath,
        [Description(
            "The copy's name; refused when the parent already has a child of that name. Left out, the node's name, or when that "
                + "is taken, as the editor names a duplicate: trailing digits counted up (Sprite gives Sprite2, Sprite2 gives Sprite3)."
        )]
            string? newName = null,
        [Description("{parent}: the node to put the copy under, by its path relative to the scene's root; the node's own parent by default.")]
            DuplicateNodeOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        // Refused before the project is read; the builder checks them again.
        _ = CheckDuplicatedNodePath(nodePath);
        _ = CheckDuplicateParent(options);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            string scene = CheckEditableScenePath(projectDir, scenePath);
            JsonObject parameters = DuplicateNodeParameters(nodePath, newName, options);
            parameters["scene"] = scene;
            return RunWriteAsync(projectDir, "duplicate_node", parameters, cancellationToken);
        });
        return WithErrors(run);
    }

    /// <summary>duplicate_node's request parameters but the scene: <c>{nodePath, newName, parent}</c>.</summary>
    /// <exception cref="McpException">As <see cref="CheckDuplicatedNodePath"/>, then as <see cref="CheckNodePath"/> for the parent.</exception>
    internal static JsonObject DuplicateNodeParameters(string nodePath, string? newName, DuplicateNodeOptions? options) =>
        new()
        {
            ["nodePath"] = CheckDuplicatedNodePath(nodePath),
            ["newName"] = newName?.Trim() ?? string.Empty,
            ["parent"] = CheckDuplicateParent(options),
        };

    /// <summary>duplicate_node's options.parent checked and trimmed, or "" for the node's own parent.</summary>
    private static string CheckDuplicateParent(DuplicateNodeOptions? options) =>
        options?.Parent is null ? string.Empty : CheckNodePath(options.Parent);

    [McpServerTool(Name = "load_sprite", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Sets the texture of a node in a scene file (a Sprite2D, Sprite3D, TextureRect, NinePatchRect, Polygon2D, or any node "
            + "whose texture property takes a Texture2D) and saves the scene, in a headless Godot, without running the game. An "
            + "image that was never imported (no .import file beside it, or its imported file is missing) is imported first, by "
            + "the editor's full scan of the project: it writes an .import file beside every asset never imported and a .uid file "
            + "beside every script. An image in a folder Godot does not scan (a name starting with \".\", or holding a .gdignore) "
            + "is refused. A node inside an instanced scene is "
            + "refused. Returns {path, texture: {resource, uid?}, warning?, errors?}: path is the node's path from the scene's root."
            + WriteNote
            + EditNote
    )]
    public async Task<string> LoadSpriteAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(ScenePathDescription)] string scenePath,
        [Description(NodePathDescription)] string nodePath,
        [Description(
            "The texture: a res:// path or a path relative to the project folder, an image (.png, .jpg, .svg...) or a .tres or .res texture."
        )]
            string texturePath,
        CancellationToken cancellationToken = default
    )
    {
        // Refused before the project is read; the builder checks it again.
        _ = CheckNodePath(nodePath);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            JsonObject parameters = LoadSpriteParameters(projectDir, nodePath, texturePath);
            parameters["scene"] = CheckEditableScenePath(projectDir, scenePath);
            HeadlessRequest request = new(projectDir, "load_sprite", parameters, Prepare: true, RunCeiling)
            {
                ImportAssets = LoadSpriteImportAssets(projectDir, texturePath),
            };
            return HeadlessRunner.RunAsync(sessions, request, cancellationToken);
        });
        return WithErrors(run);
    }

    /// <summary>load_sprite's request parameters but the scene: <c>{nodePath, texture}</c>, the texture as a res:// path.</summary>
    /// <exception cref="McpException">As <see cref="CheckNodePath"/>, then as <see cref="CheckTexturePath"/>.</exception>
    internal static JsonObject LoadSpriteParameters(string projectDir, string nodePath, string texturePath) =>
        new() { ["nodePath"] = CheckNodePath(nodePath), ["texture"] = ResOf(projectDir, CheckTexturePath(projectDir, texturePath)) };

    /// <summary>The files load_sprite loads, which the prep imports first when they need it: the texture's full path.</summary>
    /// <exception cref="McpException">As <see cref="CheckTexturePath"/>.</exception>
    internal static IReadOnlyList<string> LoadSpriteImportAssets(string projectDir, string texturePath) =>
        [CheckTexturePath(projectDir, texturePath)];

    /// <summary>A script attach_script takes, as a res:// path.</summary>
    /// <exception cref="McpException">The path is empty, outside the project, missing, or not a .gd or .cs.</exception>
    internal static string CheckScriptPath(string projectDir, string scriptPath) =>
        ToResPath(projectDir, scriptPath, new PathRule("scriptPath", ScriptExtensions, "is not a script: attach_script takes .gd and .cs files."));

    /// <summary>The full path of a texture load_sprite takes.</summary>
    /// <exception cref="McpException">The path is empty, outside the project, of another kind, or missing.</exception>
    internal static string CheckTexturePath(string projectDir, string texturePath)
    {
        string full = ResolvePath(projectDir, texturePath, TextureRule);
        if (!File.Exists(full))
        {
            throw new McpException($"{ResOf(projectDir, full)} does not exist.");
        }

        string? unscanned = UnscannedFolder(projectDir, full);
        return unscanned is null
            ? full
            : throw new McpException($"{ResOf(projectDir, full)} is in {unscanned}, which Godot does not scan; move the asset out of it.");
    }

    /// <summary>
    /// The res:// path of the outermost folder holding the file that Godot's scan skips (a name starting with "." or a folder
    /// holding a .gdignore), or null. An import never reaches such a file, so it would be due on every request.
    /// </summary>
    private static string? UnscannedFolder(string projectDir, string full)
    {
        string folder = projectDir;
        string relative = Path.GetRelativePath(projectDir, Path.GetDirectoryName(full)!);
        foreach (string name in relative.Split(Path.DirectorySeparatorChar).Where(name => name != "."))
        {
            folder = Path.Combine(folder, name);
            if (name.StartsWith('.') || File.Exists(Path.Combine(folder, ".gdignore")))
            {
                return ResOf(projectDir, folder);
            }
        }

        return null;
    }

    /// <summary>The node path duplicate_node copies, checked as <see cref="CheckNodePath"/> does.</summary>
    /// <exception cref="McpException">The path breaks <see cref="CheckNodePath"/>'s rules, or names the scene's root.</exception>
    internal static string CheckDuplicatedNodePath(string nodePath)
    {
        string path = CheckNodePath(nodePath);
        return path.Split('/').All(part => part is "" or ".") ? throw new McpException(RootDuplicateRefusal) : path;
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

    /// <summary>The operation's result, with <see cref="AddBuildAndErrors"/> applied.</summary>
    private static string WithErrors(HeadlessResult run) => AddBuildAndErrors(run.Result?.DeepClone() as JsonObject ?? [], run).ToJsonString();

    /// <summary>
    /// result with <c>csharp</c> (<see cref="ShapeCsharp"/>: the state and the compiler errors) added when the prep's C# build
    /// failed, and <c>errors</c> when Godot logged any. While the build failed, the errors a missing project assembly causes
    /// (<see cref="IsMissingAssemblySymptom"/>) are left out: <c>csharp</c> reports their cause.
    /// </summary>
    private static JsonObject AddBuildAndErrors(JsonObject result, HeadlessResult run)
    {
        JsonArray engineErrors = run.EngineErrors;
        if (run.Prep.Build == "failed")
        {
            result["csharp"] = ShapeCsharp(run.Prep.Build, run.BuildErrors, null);
            engineErrors = [.. engineErrors.OfType<JsonObject>().Where(entry => !IsMissingAssemblySymptom(entry)).Select(entry => entry.DeepClone())];
        }

        JsonArray errors = OnlyErrors(engineErrors);
        if (errors.Count > 0)
        {
            result["errors"] = errors;
        }

        return result;
    }

    /// <summary>
    /// Whether Godot logged the entry because the project assembly did not load: a C# autoload that "does not inherit from
    /// 'Node'" (Godot 4.7.2 <c>main.cpp</c>), or a C# script whose class could not be found (<c>csharp_script.cpp</c>).
    /// </summary>
    private static bool IsMissingAssemblySymptom(JsonObject entry)
    {
        string message = entry["message"] is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;
        return message.Contains(CSharpClassMissing, StringComparison.Ordinal)
            || (
                message.Contains(AutoloadNotInstantiated, StringComparison.Ordinal)
                && message.Contains(CSharpAutoloadNotANode, StringComparison.Ordinal)
            );
    }
}

/// <summary>Where duplicate_node puts the copy.</summary>
internal sealed record DuplicateNodeOptions(
    [property: Description("The node to put the copy under, by its path relative to the scene's root; the node's own parent by default.")]
        string? Parent = null
);

/// <summary>Whether a scene tool may replace the file it writes.</summary>
internal sealed record SceneWriteOptions(
    [property: Description("Replace the file at the path being written when one exists; false by default.")] bool? Overwrite = null
);
