using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Tools that read a project's files in a headless Godot (headless/operations.gd) without running the game: validate and
/// get_scene_file_tree here, the scene edits in HeadlessTools.Scene.cs. Each runs the prep first, as run_project does, and runs
/// beside a live session on the folder.
/// </summary>
[McpServerToolType]
internal sealed partial class HeadlessTools(SessionRegistry sessions)
{
    internal const int MaxTargets = 50;
    private static readonly TimeSpan RunCeiling = TimeSpan.FromSeconds(60);
    internal const int MaxSweep = 500;

    internal const string PrepareDescription =
        "auto (the default): first build the project's C# assembly when it is missing or older than its sources, and run a "
        + "Godot import when imported files are missing, a source asset changed since it was imported, its import settings "
        + "changed since the server last saw them, or a class_name script is newer than Godot's class cache, as run_project "
        + "does. never: use the project as it is.";

    // What a refused import suggests to a tool that takes options.prepare.
    private const string SkipPrepHint = ", or pass options.prepare: \"never\" to skip the prep";

    private const string ProjectPathDescription = "The folder that holds the project's project.godot.";

    private static readonly string[] ValidateExtensions = [".gd", ".cs", ".tscn", ".scn", ".tres", ".res"];
    private static readonly string[] SweepExtensions = [".gd", ".tscn", ".tres"];
    private static readonly string[] SceneExtensions = [".tscn", ".scn"];
    private static readonly string[] BuildStatesChecked = ["built", "up-to-date", "failed"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "validate", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Checks a project's GDScript, scenes and resources without running the game, in a headless Godot: each file is loaded "
            + "(scripts compiled; scenes and resources loaded, never instantiated), and every C# script a scene or resource uses "
            + "is checked for its class. The project's autoloads are freed before they enter the tree (their _init still runs). "
            + "Returns {valid, checked, results: [{path, errors: [{message, file, line}]}], csharp?, prep}: results lists only the "
            + "files with errors, each error under the file it names. Godot's GDScript parser reports only the first parse error "
            + "in a file, so fix it and validate again to see the next. A C# build that fails does not stop the check: it comes "
            + "back as csharp {build: \"failed\", errors} and makes valid false. A .cs target is checked through the C# build and "
            + "through the versioned scenes and resources that attach it (at most 20 each, checked as targets): csharp.files lists, "
            + "per .cs target, {path, errors, warnings, from, scenes}, each diagnostic {file, line, column, code, message, severity}, "
            + "and csharp.otherErrors the errors in the assembly's other files; from says whether they came from this call's build "
            + "or the last saved one. Errors make valid false, warnings never do. Errors Godot logs before the first file is "
            + "checked (an autoload's _init, the project's settings) are listed under the res:// file they name, else in "
            + "engineErrors [{message, file, line}]; either makes valid false."
    )]
    public async Task<string> ValidateAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(
            "1 to 50 files to check: res:// paths or paths relative to the project folder, each a .gd, .cs, .tscn, .scn, .tres or "
                + ".res inside it. A .cs is checked through the C# build and the scenes that attach it, and must be one of the "
                + "Compile items of the project's .csproj. Left out: every git-versioned .gd, .tscn and .tres of the project folder, "
                + "at most 500."
        )]
            string[]? targets = null,
        [Description("{prepare}: " + PrepareDescription)] HeadlessOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        bool prepare = RunOptions.ParsePrepare(options?.Prepare);
        (HeadlessResult run, IReadOnlyList<CsTarget> csTargets) = await RunAsync(async () =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            IReadOnlyList<string> checkedFiles = targets is null ? VersionedTargets(projectDir, sessions.Logger) : CheckTargets(projectDir, targets);
            IReadOnlyList<CsTarget> csTargets = await CSharpTargetsAsync(projectDir, checkedFiles, sessions.Logger, cancellationToken);
            IReadOnlyList<string> loaded = GodotTargets(checkedFiles, csTargets);
            JsonObject parameters = new() { ["targets"] = new JsonArray([.. loaded.Select(path => (JsonNode)path)]) };
            HeadlessRequest request = new(projectDir, "validate", parameters, prepare, RunCeiling)
            {
                ImportSkipHint = SkipPrepHint,
                ReportsBuild = csTargets.Count > 0,
            };
            return (await HeadlessRunner.RunAsync(sessions, request, cancellationToken), csTargets);
        });
        return ShapeValidation(run, csTargets).ToJsonString();
    }

    [McpServerTool(Name = "get_scene_file_tree", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Lists the nodes of a scene file without running it, in a headless Godot: read from the saved scene, never instantiated, "
            + "so no game code runs. Instanced scenes are expanded (their nodes listed under the instancing node) and an inherited "
            + "scene's base is merged in. Each node: path (relative to the scene's root, which is \".\"), name, type, script (its "
            + "resource path), instance (the scene the node instances), groups, and childCount; depth first from root. Returns one "
            + "page, {nodes, total, offset}, plus next, the offset of the following page, while more remain, and errors when Godot "
            + "logged any while reading. A placeholder instance is listed with its instance path, not expanded. For the running "
            + "game's nodes, get_scene_tree."
    )]
    public async Task<string> GetSceneFileTreeAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description("The scene: a res:// path or a path relative to the project folder, ending .tscn or .scn.")] string scenePath,
        [Description(
            "The node to list from, itself included: its path relative to the scene's root (Player/Sprite); the scene's root when left out."
        )]
            string? root = null,
        [Description(
            "{maxDepth, offset, limit, prepare}: how many levels below root to list (all when left out), the page (0 and 100 by "
                + "default, at most 500), and prepare as validate takes it."
        )]
            SceneFileTreeOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        (int maxDepth, int offset, int limit) = RuntimeTools.CheckTreeOptions(new TreeOptions(options?.MaxDepth, options?.Offset, options?.Limit));
        bool prepare = RunOptions.ParsePrepare(options?.Prepare);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            JsonObject parameters = new()
            {
                ["scene"] = CheckScenePath(projectDir, scenePath),
                ["root"] = string.IsNullOrWhiteSpace(root) ? "." : root.Trim(),
                ["maxDepth"] = maxDepth,
            };
            HeadlessRequest request = new(projectDir, "get_scene_file_tree", parameters, prepare, RunCeiling) { ImportSkipHint = SkipPrepHint };
            return HeadlessRunner.RunAsync(sessions, request, cancellationToken);
        });
        return AddBuildAndErrors(RuntimeTools.PageList(run.Result, "nodes", offset, limit), run).ToJsonString();
    }

    /// <summary>The targets as res:// paths, each checked to be an existing file of a kind validate loads, inside the project.</summary>
    /// <exception cref="McpException">There are not 1 to <see cref="MaxTargets"/>, or one is empty, outside, missing or of another kind.</exception>
    internal static IReadOnlyList<string> CheckTargets(string projectDir, IReadOnlyList<string> targets)
    {
        if (targets.Count is < 1 or > MaxTargets)
        {
            throw new McpException(
                $"targets takes 1 to {MaxTargets} paths; got {targets.Count}. Split the check over several calls, or leave targets out "
                    + "to check every versioned script, scene and resource."
            );
        }

        const string refusal = "is not a script, scene or resource: validate checks .gd, .cs, .tscn, .scn, .tres and .res files.";
        return [.. targets.Select(target => ToResPath(projectDir, target, new PathRule("targets", ValidateExtensions, refusal)))];
    }

    /// <exception cref="McpException">The path is empty, outside the project, missing, or not a .tscn or .scn.</exception>
    internal static string CheckScenePath(string projectDir, string scenePath) => ToResPath(projectDir, scenePath, SceneRule("scenePath"));

    private static PathRule SceneRule(string argument) =>
        new(argument, SceneExtensions, "is not a scene: the scene tools take .tscn and .scn files.");

    /// <summary>The project folder's git-versioned scripts, scenes and resources that exist, as res:// paths in ordinal order.</summary>
    /// <exception cref="McpException">The folder is not in a git repository, or it has more than <see cref="MaxSweep"/>.</exception>
    internal static IReadOnlyList<string> VersionedTargets(string projectDir, ILogger logger)
    {
        string listed =
            GitRunner.Run(projectDir, logger, "ls-files", "-z")
            ?? throw new McpException($"git could not list the files of {projectDir} (is it in a git repository?), so validate needs targets.");
        string[] found =
        [
            .. listed
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(path => SweepExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Where(path => File.Exists(Path.Combine(projectDir, path)))
                .Select(path => "res://" + path)
                .Order(StringComparer.Ordinal),
        ];
        return found.Length <= MaxSweep
            ? found
            : throw new McpException(
                $"the project has {found.Length} versioned scripts, scenes and resources; validate takes at most {MaxSweep} at once: pass targets."
            );
    }

    /// <summary>
    /// validate's result: <c>{valid, checked, results, engineErrors?, csharp?, prep}</c>; <c>checked</c> counts the files Godot
    /// checked and the .cs targets.
    /// </summary>
    internal static JsonObject ShapeValidation(HeadlessResult run, IReadOnlyList<CsTarget> csTargets)
    {
        JsonObject reply = run.Result as JsonObject ?? [];
        JsonArray results = ArrayOf(reply, "results");
        // The errors Godot logged before the first file was checked whose file is not a res:// file.
        JsonArray unattributed = ArrayOf(reply, "engineErrors");
        CsReport? report = csTargets.Count == 0 ? null : ReportCSharp(run, csTargets);
        JsonObject shaped = new()
        {
            ["valid"] = results.Count == 0 && unattributed.Count == 0 && !IsBuildRed(run, report),
            ["checked"] = CountOf(reply["checked"]) + csTargets.Count,
            ["results"] = results,
        };
        if (unattributed.Count > 0)
        {
            shaped["engineErrors"] = unattributed;
        }

        if (report is not null || BuildStatesChecked.Contains(run.Prep.Build))
        {
            shaped["csharp"] = ShapeCsharp(run.Prep.Build, run.BuildErrors, report);
        }

        shaped["prep"] = JsonSerializer.SerializeToNode(run.Prep, Json);
        return shaped;
    }

    /// <summary>Whether this call's build failed, or the build the .cs targets are reported from had an error.</summary>
    private static bool IsBuildRed(HeadlessResult run, CsReport? report) => run.Prep.Build == "failed" || report is { HasErrors: true };

    private static JsonArray ArrayOf(JsonObject reply, string key) => reply[key]?.DeepClone() as JsonArray ?? [];

    /// <summary>A count GDScript wrote, which may be a float.</summary>
    private static int CountOf(JsonNode? count) => count?.GetValueKind() == JsonValueKind.Number ? (int)count.GetValue<double>() : 0;

    /// <summary>
    /// validate's <c>csharp</c>: the build's state, a failed build's compiler errors with how many were left out, and the .cs
    /// targets' report.
    /// </summary>
    private static JsonObject ShapeCsharp(string build, CompilerErrorList? errors, CsReport? report)
    {
        JsonObject csharp = new() { ["build"] = build };
        if (errors is not null)
        {
            csharp["errors"] = new JsonArray([.. errors.Errors.Select(error => (JsonNode)error)]);
            if (errors.Total > errors.Errors.Count)
            {
                csharp["errorsOmitted"] = errors.Total - errors.Errors.Count;
            }
        }

        foreach ((string key, JsonNode? value) in report?.Shaped ?? [])
        {
            csharp[key] = value?.DeepClone();
        }

        return csharp;
    }

    /// <summary>
    /// A res:// or project-relative path as the res:// path of an existing file inside the project, of one of the rule's
    /// extensions (in any case) and spelled in the case on disk.
    /// </summary>
    /// <exception cref="McpException">The path breaks the rule, or the file does not exist.</exception>
    internal static string ToResPath(string projectDir, string path, PathRule rule)
    {
        string full = ResolvePath(projectDir, path, rule);
        return File.Exists(full) ? ResOf(projectDir, full) : throw new McpException($"{rule.Argument} '{path}' does not exist: {full}.");
    }

    /// <summary>The res:// path of a full path inside the project.</summary>
    internal static string ResOf(string projectDir, string full) => "res://" + Path.GetRelativePath(projectDir, full).Replace('\\', '/');

    /// <summary>The full path a res:// or project-relative path names, checked to be inside the project and of the rule's kind.</summary>
    /// <exception cref="McpException">The path is empty, outside the project, or of another kind.</exception>
    private static string ResolvePath(string projectDir, string path, PathRule rule)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException($"{rule.Argument} holds an empty path. Pass res:// paths or paths relative to the project folder.");
        }

        string relative = path.StartsWith("res://", StringComparison.Ordinal) ? path["res://".Length..] : path;
        string full = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(projectDir, relative));
        string inProject = Path.GetRelativePath(projectDir, full);
        if (IsOutside(inProject))
        {
            throw new McpException($"{rule.Argument} '{path}' is outside the project folder {projectDir}; pass a res:// path or a path inside it.");
        }

        if (!rule.Extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException($"{rule.Argument} '{path}' {rule.Refusal}");
        }

        // Windows finds a case variant, and Godot would then save the path as it was typed.
        string onDisk = OnDiskCase(projectDir, full);
        return string.Equals(onDisk, full, StringComparison.Ordinal)
            ? full
            : throw new McpException($"{path} differs in case from the file on disk, {ResOf(projectDir, onDisk)}; use the exact case.");
    }

    /// <summary>full, a path inside the project, with each segment that exists spelled as its directory lists it.</summary>
    internal static string OnDiskCase(string projectDir, string full)
    {
        string current = projectDir;
        foreach (string segment in Path.GetRelativePath(projectDir, full).Split(Path.DirectorySeparatorChar))
        {
            string? listed = Directory.Exists(current)
                ? Directory
                    .EnumerateFileSystemEntries(current)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(name => string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                : null;
            current = Path.Combine(current, listed ?? segment);
        }

        return current;
    }

    /// <summary>Whether a path relative to the project folder leaves it: up out of it, or onto another drive.</summary>
    internal static bool IsOutside(string inProject) =>
        inProject == ".." || inProject.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(inProject);

    /// <summary>The logged errors (not warnings) as <c>{message, file, line}</c>.</summary>
    private static JsonArray OnlyErrors(JsonArray engineErrors) =>
        [
            .. engineErrors
                .OfType<JsonObject>()
                .Where(entry => entry["type"]?.GetValue<string>() == "error")
                .Select(entry =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["message"] = entry["message"]?.DeepClone(),
                            ["file"] = entry["file"]?.DeepClone(),
                            ["line"] = entry["line"]?.DeepClone(),
                        }
                ),
        ];

    private static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"A file operation failed: {e.Message}", e);
        }
    }

    /// <summary>How a path argument is checked: its name, the extensions it takes, and what a refusal of another kind says.</summary>
    internal sealed record PathRule(string Argument, string[] Extensions, string Refusal);
}

/// <summary>Whether validate prepares the project first.</summary>
internal sealed record HeadlessOptions([property: Description(HeadlessTools.PrepareDescription)] string? Prepare = null);

/// <summary>How deep get_scene_file_tree lists, which page it returns, and whether it prepares the project first.</summary>
internal sealed record SceneFileTreeOptions(
    [property: Description("How many levels below root to list: 0 lists root alone; every level when left out.")] int? MaxDepth = null,
    [property: Description("How many nodes of the list to skip: 0 (the default), or the next of the previous page.")] int? Offset = null,
    [property: Description("How many nodes to return, 1 to 500; 100 by default.")] int? Limit = null,
    [property: Description(HeadlessTools.PrepareDescription)] string? Prepare = null
);
