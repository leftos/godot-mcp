using System.Collections.Frozen;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// batch_scene_operations (headless/scene_ops.gd): several scene edits applied in order to one open scene in a single headless
/// Godot, which saves the scene once when every one passes. Each step's args are bound to its tool's own parameters and
/// checked by that tool's own parameter builder before Godot starts, so a step is refused as the tool alone refuses it.
/// </summary>
internal sealed partial class HeadlessTools
{
    internal const int MaxSceneBatchSteps = 100;

    internal const string SceneBatchTools =
        "delete_nodes, attach_script, duplicate_node, move_node, load_sprite, add_node, set_node_properties, connect_signal, "
        + "disconnect_signal, export_mesh_library";

    private static readonly TimeSpan BatchCeiling = TimeSpan.FromSeconds(120);

    private static readonly FrozenDictionary<string, SceneStepKind> SceneStepKinds = new Dictionary<string, SceneStepKind>
    {
        ["delete_nodes"] = SceneStepKind.Of<DeleteNodesArgs>((_, args) => DeleteNodesParameters(args.NodePaths)),
        ["attach_script"] = SceneStepKind.Of<AttachScriptArgs>(
            (target, args) => AttachScriptParameters(target.ProjectDir, args.NodePath, args.ScriptPath)
        ),
        ["duplicate_node"] = SceneStepKind.Of<DuplicateNodeArgs>((_, args) => DuplicateNodeParameters(args.NodePath, args.NewName, args.Options)),
        ["move_node"] = SceneStepKind.Of<MoveNodeArgs>((_, args) => MoveNodeParameters(args.NodePath, args.Options)),
        ["load_sprite"] = SceneStepKind.Of<LoadSpriteArgs>(
            (target, args) => LoadSpriteParameters(target.ProjectDir, args.NodePath, args.TexturePath),
            (target, args) => LoadSpriteImportAssets(target.ProjectDir, args.TexturePath)
        ),
        ["add_node"] = SceneStepKind.Of<AddNodeArgs>(
            (target, args) => AddNodeParameters(target.ProjectDir, target.Scene, args.NodeType, args.NodeName, args.Options)
        ),
        ["set_node_properties"] = SceneStepKind.Of<SetNodePropertiesArgs>((_, args) => SetNodePropertiesParameters(args.Updates)),
        ["connect_signal"] = SceneStepKind.Of<ConnectSignalArgs>((_, args) => ConnectSignalParameters(args.NodePath, args.Signal, args.Target)),
        ["disconnect_signal"] = SceneStepKind.Of<DisconnectSignalArgs>(
            (_, args) => DisconnectSignalParameters(args.NodePath, args.Signal, args.Target)
        ),
        ["export_mesh_library"] = SceneStepKind.Of<ExportMeshLibraryArgs>(
            (target, args) => ExportMeshLibraryParameters(target.ProjectDir, args.OutputPath, args.MeshItemNames, args.Options)
        ),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    [McpServerTool(Name = "batch_scene_operations", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Runs several scene edits on one scene file in a single headless Godot and saves the scene once, without running the "
            + "game. Each step is {tool, args}: tool is one of delete_nodes, attach_script, duplicate_node, move_node, load_sprite, "
            + "add_node, set_node_properties, connect_signal, disconnect_signal or export_mesh_library, and args are that tool's own "
            + "arguments without projectPath and scenePath. Every step is checked before Godot starts. The steps run in order on "
            + "the open scene, each seeing the ones before it; at the first step that fails nothing is saved and no mesh library "
            + "is written. When all pass, the scene is saved once (unless every step was export_mesh_library), then each mesh "
            + "library is written. Returns {passed, steps: [{index, tool, ok, result | error}], failedAt?: {index, tool, error}, "
            + "uid?, warning?, errors?}: each result is what that tool returns on its own, and uid is the saved scene's."
            + WriteNote
            + EditNote
    )]
    public async Task<string> BatchSceneOperationsAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(ScenePathDescription)] string scenePath,
        [Description(
            "1 to 100 edits, in order, each {tool, args}: a scene edit tool's name and its own arguments without projectPath and scenePath."
        )]
            SceneBatchStep[] steps,
        CancellationToken cancellationToken = default
    )
    {
        CheckSceneBatchCount(steps);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            string scene = CheckEditableScenePath(projectDir, scenePath);
            (JsonArray built, IReadOnlyList<string> imports) = BuildSceneSteps(new SceneTarget(projectDir, scene), steps);
            JsonObject parameters = new() { ["scene"] = scene, ["steps"] = built };
            HeadlessRequest request = new(projectDir, "batch_scene_operations", parameters, Prepare: true, BatchCeiling) { ImportAssets = imports };
            return HeadlessRunner.RunAsync(sessions, request, cancellationToken);
        });
        return ShapeSceneBatch(run);
    }

    /// <summary>The JSON names of a batchable tool's args, in its parameters' order: its MCP schema's but projectPath and scenePath.</summary>
    internal static IReadOnlyList<string> SceneStepArgumentNames(string tool) => SceneStepKinds[tool].Names;

    /// <summary>The args a batchable tool's step must give: its parameters that have no default.</summary>
    internal static IReadOnlyList<string> SceneStepRequiredArguments(string tool) => SceneStepKinds[tool].Required;

    /// <exception cref="McpException">There are not 1 to <see cref="MaxSceneBatchSteps"/> steps.</exception>
    private static void CheckSceneBatchCount(SceneBatchStep[]? steps)
    {
        if (steps is null || steps.Length == 0)
        {
            throw new McpException("steps is empty; a batch needs at least one step.");
        }

        if (steps.Length > MaxSceneBatchSteps)
        {
            throw new McpException($"a batch takes at most {MaxSceneBatchSteps} steps; split it.");
        }
    }

    /// <summary>The request's steps, each checked in order, and the files they load, each once.</summary>
    /// <exception cref="McpException">
    /// A step is refused as <see cref="BuildSceneStep"/> refuses it, or an export_mesh_library step writes the file an earlier one writes.
    /// </exception>
    private static (JsonArray Steps, IReadOnlyList<string> Loads) BuildSceneSteps(SceneTarget target, SceneBatchStep[] steps)
    {
        JsonArray built = [];
        List<string> loads = [];
        // Each library's res:// output and the step that writes it; case is ignored, as the other path checks ignore it.
        Dictionary<string, int> outputs = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < steps.Length; index++)
        {
            (JsonObject step, IReadOnlyList<string> stepLoads) = BuildSceneStep(target, index, steps[index]);
            if (step["op"]!.GetValue<string>() == "export_mesh_library")
            {
                string output = step["params"]!["output"]!.GetValue<string>();
                if (outputs.TryGetValue(output, out int earlier))
                {
                    throw new McpException(
                        $"step {index} (export_mesh_library): outputPath {output} is also step {earlier}'s; each library needs its own file."
                    );
                }

                outputs[output] = index;
            }

            built.Add(step);
            loads.AddRange(stepLoads);
        }

        return (built, [.. loads.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// A step as the request's <c>{op, params}</c>, params built by its tool's own builder with the batch's scene as its scene, and
    /// the files it loads.
    /// </summary>
    /// <exception cref="McpException">The step is refused: <c>step &lt;index&gt; (&lt;tool&gt;): </c> and why.</exception>
    private static (JsonObject Step, IReadOnlyList<string> Loads) BuildSceneStep(SceneTarget target, int index, SceneBatchStep? step)
    {
        string tool = step?.Tool ?? string.Empty;
        try
        {
            if (!SceneStepKinds.TryGetValue(tool, out SceneStepKind? kind))
            {
                throw new McpException($"'{tool}' cannot run in a scene batch; tool is one of: {SceneBatchTools}.");
            }

            object args = BindSceneStepArgs(tool, kind, step?.Args);
            JsonObject parameters = kind.Build(target, args);
            return (new JsonObject { ["op"] = tool, ["params"] = parameters }, kind.Loads(target, args));
        }
        catch (McpException e)
        {
            throw new McpException($"step {index} ({tool}): {e.Message}", e);
        }
    }

    /// <summary>
    /// A step's args as its tool's args record, bound with the serializer options the SDK binds the tool's own arguments with
    /// (<see cref="McpJsonUtilities.DefaultOptions"/>), so a nested object takes what it takes in the single tool.
    /// </summary>
    /// <exception cref="McpException">A key the tool does not take, a missing required one, or a value that does not fit its type.</exception>
    private static object BindSceneStepArgs(string tool, SceneStepKind kind, JsonObject? args)
    {
        JsonObject given = args ?? [];
        CheckSceneStepKeys(tool, kind, given);
        try
        {
            return given.Deserialize(kind.ArgsType, McpJsonUtilities.DefaultOptions)
                ?? throw new McpException($"args do not fit {tool}'s parameters.");
        }
        catch (JsonException e)
        {
            // The serializer's own message names .NET types; its path names the key.
            string where = e.Path?.TrimStart('$').TrimStart('.') ?? string.Empty;
            throw new McpException($"args.{where} has the wrong type for {tool}; see the tool's schema for what it takes.", e);
        }
    }

    /// <exception cref="McpException">A key the tool does not take (projectPath and scenePath among them), or a required one missing.</exception>
    private static void CheckSceneStepKeys(string tool, SceneStepKind kind, JsonObject given)
    {
        foreach (string key in given.Select(pair => pair.Key))
        {
            if (key is "projectPath" or "scenePath")
            {
                throw new McpException($"args.{key} is not taken: the batch's projectPath and scenePath are every step's.");
            }

            if (!kind.Names.Contains(key, StringComparer.Ordinal))
            {
                throw new McpException($"args.{key} is not an argument of {tool}; it takes {string.Join(", ", kind.Names)}.");
            }
        }

        string? missing = kind.Required.FirstOrDefault(name => !given.ContainsKey(name));
        if (missing is not null)
        {
            throw new McpException($"args.{missing} is missing; {tool} requires it.");
        }
    }

    /// <summary>The batch's result with each set_node_properties step's values cut as that tool cuts them, and <c>errors</c> added.</summary>
    private static string ShapeSceneBatch(HeadlessResult run)
    {
        JsonObject result = run.Result?.DeepClone() as JsonObject ?? [];
        IEnumerable<JsonObject> propertySteps = (result["steps"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(entry => entry["tool"]?.GetValue<string>() == "set_node_properties");
        foreach (JsonObject update in propertySteps.SelectMany(entry => (entry["result"]?["results"] as JsonArray ?? []).OfType<JsonObject>()))
        {
            update["before"] = RuntimeTools.CutPropertyValue(update["before"]);
            update["after"] = RuntimeTools.CutPropertyValue(update["after"]);
        }

        return WithErrors(run with { Result = result });
    }

    /// <summary>The project and the res:// scene every step of a batch edits.</summary>
    private sealed record SceneTarget(string ProjectDir, string Scene);

    /// <summary>
    /// How one tool's step binds: its args record, whose constructor parameters are the tool's parameters but projectPath and
    /// scenePath, the builder of its request parameters, and the files it loads.
    /// </summary>
    private sealed record SceneStepKind(
        Type ArgsType,
        Func<SceneTarget, object, JsonObject> Build,
        Func<SceneTarget, object, IReadOnlyList<string>> Loads
    )
    {
        /// <summary>The args' JSON names, camelCase as the MCP schema names the tool's parameters.</summary>
        public IReadOnlyList<string> Names { get; } =
        [.. ArgsType.GetConstructors().Single().GetParameters().Select(parameter => JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!))];

        /// <summary>The args with no default, which the tool's schema requires.</summary>
        public IReadOnlyList<string> Required { get; } =
        [
            .. ArgsType
                .GetConstructors()
                .Single()
                .GetParameters()
                .Where(parameter => !parameter.HasDefaultValue)
                .Select(parameter => JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!)),
        ];

        public static SceneStepKind Of<TArgs>(Func<SceneTarget, TArgs, JsonObject> build) => Of<TArgs>(build, (_, _) => []);

        public static SceneStepKind Of<TArgs>(Func<SceneTarget, TArgs, JsonObject> build, Func<SceneTarget, TArgs, IReadOnlyList<string>> loads) =>
            new(typeof(TArgs), (target, args) => build(target, (TArgs)args), (target, args) => loads(target, (TArgs)args));
    }

    private sealed record DeleteNodesArgs(string[] NodePaths);

    private sealed record AttachScriptArgs(string NodePath, string ScriptPath);

    private sealed record DuplicateNodeArgs(string NodePath, string? NewName = null, DuplicateNodeOptions? Options = null);

    private sealed record MoveNodeArgs(string NodePath, MoveNodeOptions? Options = null);

    private sealed record LoadSpriteArgs(string NodePath, string TexturePath);

    private sealed record AddNodeArgs(string NodeType, string NodeName, AddNodeOptions? Options = null);

    private sealed record SetNodePropertiesArgs(PropertyUpdate[] Updates);

    private sealed record ConnectSignalArgs(string NodePath, string Signal, ConnectTarget Target);

    private sealed record DisconnectSignalArgs(string NodePath, string Signal, DisconnectTarget Target);

    private sealed record ExportMeshLibraryArgs(string OutputPath, string[]? MeshItemNames = null, SceneWriteOptions? Options = null);
}
