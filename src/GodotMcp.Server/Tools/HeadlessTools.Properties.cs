using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The headless property tools (headless/scene_props.gd): add_node, set_node_properties and get_node_properties. A value is JSON
/// converted by the property's declared type and read back, as set_property does in the running game; a node reads and is
/// written as its path from the scene's root.
/// </summary>
internal sealed partial class HeadlessTools
{
    internal const int MaxPropertyUpdates = 100;
    internal const int MaxNodeQueries = 50;

    private const string ValuesNote =
        " Values are JSON, converted by the property's declared type as set_property converts them: vectors as {x, y}, colours "
        + "as {r, g, b, a} or \"#rrggbb\", enums as integers, a resource as a res:// or uid:// path, {resource: path}, "
        + "{type, ...properties} or null, and a Node-typed export as a node path from the scene's root. A NodePath-typed "
        + "property is relative to the node that holds it, as written.";

    [McpServerTool(Name = "add_node", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Adds a node to a scene file and saves it, in a headless Godot, without running the game. nodeType is a Godot class "
            + "derived from Node, a script's class_name whose base is one, or a scene (.tscn or .scn in the project), which is "
            + "added as an instance of that scene. options.properties are set on the new node before it is added: when any does "
            + "not take, nothing is added and the error names every failing property. Refused: a name a sibling already has, and "
            + "a parent inside an instanced scene (an instance's own root may be the parent). Returns {path, type, instance?, "
            + "errors?}: path is the new node's path from the scene's root."
            + ValuesNote
            + WriteNote
    )]
    public async Task<string> AddNodeAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(ScenePathDescription)] string scenePath,
        [Description(
            "A Node class (Sprite2D), a script's class_name, or a scene to instance: a res:// path or a path relative to the project folder."
        )]
            string nodeType,
        [Description("The new node's name; it may not hold . : @ / \" or %.")] string nodeName,
        [Description("{parent, properties}: the parent's path from the scene's root (\".\", the root, by default), and {name: value} to set.")]
            AddNodeOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        // Refused before the project is read; the builder checks them again.
        _ = CheckNewNode(nodeName, options?.Parent);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            string scene = CheckEditableScenePath(projectDir, scenePath);
            JsonObject parameters = AddNodeParameters(projectDir, scene, nodeType, nodeName, options);
            parameters["scene"] = scene;
            return RunWriteAsync(projectDir, "add_node", parameters, cancellationToken);
        });
        return WithErrors(run);
    }

    /// <summary>
    /// add_node's request parameters but the scene: <c>{nodeType, nodeName, parent, properties}</c>. scene is the res:// path of
    /// the scene being edited, which nodeType may not name.
    /// </summary>
    /// <exception cref="McpException">As <see cref="CheckNewNode"/>, then as <see cref="CheckNodeType"/>.</exception>
    internal static JsonObject AddNodeParameters(string projectDir, string scene, string nodeType, string nodeName, AddNodeOptions? options)
    {
        (string name, string parent) = CheckNewNode(nodeName, options?.Parent);
        return new JsonObject
        {
            ["nodeType"] = CheckNodeType(projectDir, nodeType, scene),
            ["nodeName"] = name,
            ["parent"] = parent,
            ["properties"] = new JsonObject([.. (options?.Properties ?? []).Select(entry => KeyValuePair.Create(entry.Key, ToNode(entry.Value)))]),
        };
    }

    [McpServerTool(Name = "set_node_properties", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Sets properties on a scene file's nodes and saves it, in a headless Godot, without running the game. Each value is "
            + "set and read back; a read-back that differs fails. A node the scene inherits from its base scene may be set (the "
            + "override is saved in this scene); a node inside an instanced scene is refused unless the instance is an editable "
            + "instance. All or nothing: when any entry fails, nothing is saved and the error names every failing entry. Returns "
            + "{results: [{nodePath, property, before, after}], errors?}; a value whose JSON is longer than 2000 characters comes "
            + "back as {valuePreview, valueLength}."
            + ValuesNote
            + WriteNote
    )]
    public async Task<string> SetNodePropertiesAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(ScenePathDescription)] string scenePath,
        [Description("1 to 100 {nodePath, property, value}: the node's path from the scene's root, the property's name, and its new value.")]
            PropertyUpdate[] updates,
        CancellationToken cancellationToken = default
    )
    {
        // Refused before the project is read; the builder checks them again.
        _ = CheckPropertyUpdates(updates);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            string scene = CheckEditableScenePath(projectDir, scenePath);
            JsonObject parameters = SetNodePropertiesParameters(projectDir, updates);
            parameters["scene"] = scene;
            return RunWriteAsync(projectDir, "set_node_properties", parameters, cancellationToken);
        });
        return ShapeResults(
                run,
                entry =>
                {
                    entry["before"] = RuntimeTools.CutPropertyValue(entry["before"]);
                    entry["after"] = RuntimeTools.CutPropertyValue(entry["after"]);
                }
            )
            .ToJsonString();
    }

    /// <summary>set_node_properties' request parameters but the scene: <c>{updates}</c>.</summary>
    /// <exception cref="McpException">As <see cref="CheckPropertyUpdates"/>.</exception>
    internal static JsonObject SetNodePropertiesParameters(string projectDir, PropertyUpdate[] updates) =>
        new() { ["updates"] = CheckPropertyUpdates(updates) };

    [McpServerTool(Name = "get_node_properties", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Reads the properties of a scene file's nodes, in a headless Godot, without running the game; nothing is saved. The "
            + "scene is instantiated to read it, so its scripts' _init runs (never _ready). By default a node reads as inspect_node "
            + "reads one: its script variables and the properties the editor's inspector shows; properties narrows that to a list, "
            + "and changedOnly to the properties the scene file stores for the node (with those of the scenes it instances or "
            + "inherits). A node reads as its path from the scene's root, a resource saved in its own file as {resource, uid?, "
            + "class}, a built-in one as {class, subResource?, properties}. Returns {results: [{nodePath, type, script?, "
            + "properties} or {nodePath, error}], errors?}: a missing node or property fails its own entry alone. A value whose "
            + "JSON is longer than 2000 characters comes back as {valuePreview, valueLength}."
            + RefusedNote
    )]
    public async Task<string> GetNodePropertiesAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description("The scene: a res:// path or a path relative to the project folder, ending .tscn or .scn.")] string scenePath,
        [Description(
            "1 to 50 {nodePath, properties?, changedOnly?}: the node's path from the scene's root, the property names to read "
                + "(all shown when left out), and whether to read only the values the scene file stores."
        )]
            NodePropertyQuery[] nodes,
        [Description("{prepare}: " + PrepareDescription)] HeadlessOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonArray queries = CheckNodeQueries(nodes);
        bool prepare = RunOptions.ParsePrepare(options?.Prepare);
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            JsonObject parameters = new() { ["scene"] = CheckScenePath(projectDir, scenePath), ["nodes"] = queries };
            HeadlessRequest request = new(projectDir, "get_node_properties", parameters, prepare, RunCeiling);
            return HeadlessRunner.RunAsync(sessions, request, cancellationToken);
        });
        return ShapeResults(run, entry => CutValues(entry["properties"] as JsonObject)).ToJsonString();
    }

    /// <summary>add_node's name and parent path, each trimmed.</summary>
    /// <exception cref="McpException">The name is empty, or the parent path is not relative to the scene root.</exception>
    internal static (string Name, string Parent) CheckNewNode(string nodeName, string? parent)
    {
        string name = nodeName?.Trim() ?? string.Empty;
        return name.Length == 0 ? throw new McpException("nodeName is empty; give the new node a name.") : (name, CheckNodePath(parent ?? "."));
    }

    /// <summary>set_node_properties' updates as the request's <c>[{nodePath, property, value}]</c>.</summary>
    /// <exception cref="McpException">
    /// There are not 1 to <see cref="MaxPropertyUpdates"/>, or one has a node path not relative to the scene root, no property
    /// name, or no value.
    /// </exception>
    internal static JsonArray CheckPropertyUpdates(IReadOnlyList<PropertyUpdate>? updates)
    {
        int count = updates?.Count ?? 0;
        if (updates is null || count is < 1 or > MaxPropertyUpdates)
        {
            throw new McpException($"updates takes 1 to {MaxPropertyUpdates} entries; got {count}.");
        }

        return [.. updates.Select((update, index) => (JsonNode)CheckPropertyUpdate(update, index))];
    }

    /// <summary>get_node_properties' queries as the request's <c>[{nodePath, properties, changedOnly}]</c>.</summary>
    /// <exception cref="McpException">
    /// There are not 1 to <see cref="MaxNodeQueries"/>, or one has a node path not relative to the scene root.
    /// </exception>
    internal static JsonArray CheckNodeQueries(IReadOnlyList<NodePropertyQuery>? nodes)
    {
        int count = nodes?.Count ?? 0;
        if (nodes is null || count is < 1 or > MaxNodeQueries)
        {
            throw new McpException($"nodes takes 1 to {MaxNodeQueries} entries; got {count}.");
        }

        return
        [
            .. nodes.Select(query =>
                (JsonNode)
                    new JsonObject
                    {
                        ["nodePath"] = CheckNodePath(query.NodePath),
                        ["properties"] = new JsonArray([.. (query.Properties ?? []).Select(name => (JsonNode)name.Trim())]),
                        ["changedOnly"] = query.ChangedOnly == true,
                    }
            ),
        ];
    }

    private static JsonObject CheckPropertyUpdate(PropertyUpdate update, int index)
    {
        string nodePath = CheckNodePath(update.NodePath);
        string property = update.Property?.Trim() ?? string.Empty;
        if (property.Length == 0)
        {
            throw new McpException($"updates[{index}] has no property name.");
        }

        return update.Value.ValueKind == JsonValueKind.Undefined
            ? throw new McpException($"updates[{index}] has no value; pass null to clear the property.")
            : new JsonObject
            {
                ["nodePath"] = nodePath,
                ["property"] = property,
                ["value"] = ToNode(update.Value),
            };
    }

    /// <summary>add_node's nodeType: a res:// path for a scene (checked to exist in the project), else the class name trimmed.</summary>
    /// <exception cref="McpException">
    /// The type is empty, or it names a scene that is outside the project, missing, or the scene being edited (scene, a res://
    /// path), compared ignoring case since the file systems the server runs on mostly do.
    /// </exception>
    internal static string CheckNodeType(string projectDir, string nodeType, string scene)
    {
        string type = nodeType?.Trim() ?? string.Empty;
        if (type.Length == 0)
        {
            throw new McpException("nodeType is empty; pass a Node class (Sprite2D), a script's class_name or a scene (res://enemy.tscn).");
        }

        bool isScene =
            type.StartsWith("res://", StringComparison.Ordinal)
            || SceneExtensions.Contains(Path.GetExtension(type), StringComparer.OrdinalIgnoreCase);
        if (!isScene)
        {
            return type;
        }

        string resolved = ToResPath(projectDir, type, SceneRule("nodeType"));
        return string.Equals(resolved, scene, StringComparison.OrdinalIgnoreCase)
            ? throw new McpException($"nodeType '{type}' is the scene being edited; a scene cannot instance itself.")
            : resolved;
    }

    private static JsonNode? ToNode(JsonElement value) => JsonSerializer.SerializeToNode(value);

    /// <summary>
    /// The run's result with <c>shape</c> applied to each entry of its <c>results</c>, and <c>errors</c> added when Godot logged any.
    /// </summary>
    private static JsonObject ShapeResults(HeadlessResult run, Action<JsonObject> shape)
    {
        JsonObject result = run.Result?.DeepClone() as JsonObject ?? [];
        foreach (JsonObject entry in (result["results"] as JsonArray ?? []).OfType<JsonObject>())
        {
            shape(entry);
        }

        JsonArray errors = OnlyErrors(run.EngineErrors);
        if (errors.Count > 0)
        {
            result["errors"] = errors;
        }

        return result;
    }

    private static void CutValues(JsonObject? properties)
    {
        if (properties is null)
        {
            return;
        }

        foreach (string name in properties.Select(entry => entry.Key).ToList())
        {
            properties[name] = RuntimeTools.CutPropertyValue(properties[name]);
        }
    }
}

/// <summary>Where add_node puts the new node, and what it sets on it.</summary>
internal sealed record AddNodeOptions(
    [property: Description("The parent's path from the scene's root; the root (\".\") when left out.")] string? Parent = null,
    [property: Description("{name: value}: properties to set on the new node before it is added (position, modulate, a script variable...).")]
        Dictionary<string, JsonElement>? Properties = null
);

/// <summary>One property set_node_properties sets.</summary>
internal sealed record PropertyUpdate(
    [property: Description("The node's path from the scene's root: \".\" for the root, Boss/Sprite for a child.")] string NodePath,
    [property: Description("The property's name, as the inspector's tooltip or get_node_properties gives it.")] string Property,
    [property: Description("The new value, as JSON; null clears a resource or node property.")] JsonElement Value
);

/// <summary>One node get_node_properties reads.</summary>
internal sealed record NodePropertyQuery(
    [property: Description("The node's path from the scene's root: \".\" for the root, Boss/Sprite for a child.")] string NodePath,
    [property: Description("The property names to read; every property the inspector shows when left out.")] string[]? Properties = null,
    [property: Description("Read only the values the scene file stores for the node; false by default.")] bool? ChangedOnly = null
);
