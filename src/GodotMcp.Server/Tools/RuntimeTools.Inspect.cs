using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The running game's nodes through the bridge's inspector (bridge/godot_mcp_inspect.gd): the scene tree, a node's
/// properties, setting one, and calling a method. Values cross as JSON the way run_script returns them; going in, the
/// bridge converts JSON by the property's or parameter's declared type and reads a set property back.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const int MaxPropertyValueLength = 2000;
    internal const int MaxCallTimeoutMs = 120_000;
    private const int DefaultTreeLimit = 100;
    private const int DefaultCallTimeoutMs = 10_000;

    // Object::callv logs this through ERR_FAIL and returns null when the call itself fails (core/object/object.cpp
    // L750-766 in 4.7.2), so the bridge's reply alone cannot tell a refused call from a method that returned null.
    private const string CallvErrorPrefix = "Error calling method from 'callv'";
    private const string InspectorScript = "godot_mcp_inspect.gd";
    private const string BridgeNote = " The bridge's own nodes (GodotMcpBridge and its children) are out of reach.";
    private const string NodeDescription =
        "The node: an absolute path (/root/Main/Button), a path under the root (Main/Button), or a name, the first node "
        + "of that name breadth first from the root.";
    private const string ValueNote =
        " Values are JSON as run_script returns them: Vector2/3 as {x, y[, z]}, Color as {r, g, b, a}, Rect2 as "
        + "{x, y, width, height}, a Node as its path, another Object as {class, string}.";
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(10);

    [McpServerTool(Name = "get_scene_tree", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Lists the running game's nodes under root, depth first: path, name, class, script (its resource path, when the node "
            + "has one), groups (without Godot's internal ones, which start with _) and childCount. className and group keep "
            + "the nodes of that engine class or a subclass of it, and in that group; the walk still goes through the others. "
            + "Returns one page, {nodes, total, offset}, plus next, the offset of the following page, while more remain."
            + BridgeNote
    )]
    public async Task<string> GetSceneTreeAsync(
        [Description("The node to list from, itself included (a path or a name, as inspect_node takes); the tree's root when left out.")]
            string? root = null,
        [Description("Only nodes of this engine class or a subclass of it (Node.is_class), e.g. Control or Node2D.")] string? className = null,
        [Description("Only nodes in this group (Node.is_in_group).")] string? group = null,
        [Description("{maxDepth, offset, limit}: how many levels below root to walk (all when left out), and the page, 0 and 100 by default.")]
            TreeOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        (int maxDepth, int offset, int limit) = CheckTreeOptions(options);
        JsonObject parameters = new()
        {
            ["root"] = root ?? string.Empty,
            ["class"] = className ?? string.Empty,
            ["group"] = group ?? string.Empty,
            ["maxDepth"] = maxDepth,
        };
        BridgeCall call = new("get_scene_tree", "scene_tree", parameters, InspectTimeout);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        return ErrorReport.AddTo(PageList(result.Reply, "nodes", offset, limit), result.Errors).ToJsonString();
    }

    [McpServerTool(Name = "inspect_node", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Reads a node of the running game: {path, class, script, properties: {name: value}}; script, the script's resource "
            + "path, is left out for a node without one. Without properties, the "
            + "node's script variables and the properties the editor's inspector shows; with them, exactly those, and a name "
            + "the node does not have fails. A value whose JSON is longer than 2000 characters comes back as "
            + "{valuePreview, valueLength}: its first 2000 characters and its length."
            + ValueNote
            + BridgeNote
    )]
    public async Task<string> InspectNodeAsync(
        [Description(NodeDescription)] string node,
        [Description("The property names to read; the script variables and inspector properties when left out.")] string[]? properties = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = new() { ["node"] = CheckNode(node) };
        if (properties is { Length: > 0 })
        {
            parameters["properties"] = new JsonArray([.. properties.Select(name => (JsonNode)CheckPropertyName(name))]);
        }

        BridgeCall call = new("inspect_node", "inspect_node", parameters, InspectTimeout);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        JsonObject shaped = result.Reply?.DeepClone() as JsonObject ?? [];
        if (shaped["properties"] is JsonObject values)
        {
            foreach (string name in values.Select(property => property.Key).ToList())
            {
                values[name] = CutPropertyValue(values[name]);
            }
        }

        return ErrorReport.AddTo(shaped, result.Errors).ToJsonString();
    }

    [McpServerTool(Name = "set_property", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Sets one property of a node in the running game and reads it back: {path, property, before, after}. The JSON "
            + "value is converted by the property's declared type: an int from a whole number, Vector2/Vector2i {x, y}, "
            + "Vector3 {x, y, z}, Rect2 {x, y, width, height}, Color {r, g, b, a?} or \"#rrggbb[aa]\", NodePath and "
            + "StringName from a string, a typed Array or Dictionary (Array[int]) element by element, a packed array "
            + "(PackedVector2Array) from a JSON array, and an untyped property by the type of the value it holds, or as the "
            + "JSON is when it holds null. Fails when the node has no such property, when the value does not convert to its "
            + "type, and when the property reads something else after the set (a read-only property, a setter that "
            + "clamps), which puts the old value back."
            + ValueNote
            + BridgeNote
    )]
    public async Task<string> SetPropertyAsync(
        [Description(NodeDescription)] string node,
        [Description("The property's name, as inspect_node lists it.")] string property,
        [Description("The new value, as JSON.")] JsonElement value,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = new()
        {
            ["node"] = CheckNode(node),
            ["property"] = CheckName(property, "property", "Pass a property name; inspect_node lists a node's properties."),
            ["value"] = JsonSerializer.SerializeToNode(value),
        };
        BridgeCall call = new("set_property", "set_property", parameters, InspectTimeout);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        JsonObject shaped = result.Reply?.DeepClone() as JsonObject ?? [];
        shaped["before"] = CutPropertyValue(shaped["before"]);
        shaped["after"] = CutPropertyValue(shaped["after"]);
        return ErrorReport.AddTo(shaped, result.Errors).ToJsonString();
    }

    [McpServerTool(Name = "call_method", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Calls a method of a node in the running game and returns {path, method, value}, awaiting it when it is a "
            + "coroutine. Each JSON argument is converted by the parameter's declared type, as set_property converts. "
            + "Fails when the node has no such method, when the argument count does not fit, and when Godot refuses the "
            + "call. A value whose JSON is longer than 2000 characters comes back as {valuePreview, valueLength}. Errors the "
            + "method raises come back in errors, with file, line and stack; a GDScript error ends the method with null. Past "
            + "options.timeoutMs the call fails and the method keeps running on its node, which only restart_project stops; "
            + "Engine.time_scale and SceneTree.paused go back to their values at the call's start."
            + ValueNote
            + BridgeNote
    )]
    public async Task<string> CallMethodAsync(
        [Description(NodeDescription)] string node,
        [Description("The method's name; a script's methods and the engine class's alike.")] string method,
        [Description("The arguments, as JSON, in order; none when left out.")] JsonElement[]? args = null,
        [Description("{timeoutMs}: how long to wait for the method, 1 to 120000 ms, load-adjusted; 10000 by default.")] CallOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = new()
        {
            ["node"] = CheckNode(node),
            ["method"] = CheckName(method, "method", "Pass the name of a method the node has."),
            ["args"] = new JsonArray([.. (args ?? []).Select(arg => JsonSerializer.SerializeToNode(arg))]),
        };
        var timeout = TimeSpan.FromMilliseconds(CheckCallTimeout(options?.TimeoutMs));
        GodotSession target = Find(session);
        long mark = target.Errors.Mark();
        JsonNode? reply = await CallStoppableAsync(target, "call_method", parameters, timeout, cancellationToken);
        BridgeResult result = new(reply, target.Errors.ErrorsSince(mark));
        ErrorEntry? refused = result.Errors.FirstOrDefault(IsRefusedCall);
        if (refused is not null)
        {
            throw new McpException($"call_method failed: {refused.Message}");
        }

        JsonObject shaped = result.Reply?.DeepClone() as JsonObject ?? [];
        if (ValuePreview(shaped["value"], MaxPropertyValueLength) is JsonObject preview)
        {
            shaped.Remove("value");
            shaped["valuePreview"] = preview["valuePreview"]!.DeepClone();
            shaped["valueLength"] = preview["valueLength"]!.DeepClone();
        }

        return ErrorReport.AddTo(shaped, result.Errors).ToJsonString();
    }

    /// <summary>
    /// Whether an error is Godot refusing the bridge's own callv: the logger locates an engine error at the most recent script
    /// frame, which for that call is call_method in godot_mcp_inspect.gd. A callv the called method makes itself is located in
    /// that method, and one from another script in its own.
    /// </summary>
    internal static bool IsRefusedCall(ErrorEntry error) =>
        error.Message.StartsWith(CallvErrorPrefix, StringComparison.Ordinal)
        && error.Function == "call_method"
        && error.File.EndsWith(InspectorScript, StringComparison.Ordinal);

    /// <summary>
    /// A property's value, or <c>{valuePreview, valueLength}</c> when its JSON is longer than
    /// <see cref="MaxPropertyValueLength"/> characters: its first <see cref="MaxPropertyValueLength"/> characters and its length.
    /// </summary>
    internal static JsonNode? CutPropertyValue(JsonNode? value) => ValuePreview(value, MaxPropertyValueLength) ?? value?.DeepClone();

    /// <exception cref="McpException">maxDepth or offset is negative, or limit is outside 1 to <see cref="MaxPageSize"/>.</exception>
    internal static (int MaxDepth, int Offset, int Limit) CheckTreeOptions(TreeOptions? options)
    {
        int? maxDepth = options?.MaxDepth;
        if (maxDepth < 0)
        {
            throw new McpException($"maxDepth must be 0 or more; got {maxDepth}.");
        }

        int offset = options?.Offset ?? 0;
        int limit = options?.Limit ?? DefaultTreeLimit;
        CheckPage(offset, limit);
        return (maxDepth ?? -1, offset, limit);
    }

    /// <exception cref="McpException">timeoutMs is outside 1 to <see cref="MaxCallTimeoutMs"/>.</exception>
    internal static int CheckCallTimeout(int? requested)
    {
        int timeoutMs = requested ?? DefaultCallTimeoutMs;
        return timeoutMs is >= 1 and <= MaxCallTimeoutMs
            ? timeoutMs
            : throw new McpException($"timeoutMs must be 1 to {MaxCallTimeoutMs}; got {timeoutMs}.");
    }

    private static string CheckNode(string node) =>
        CheckName(node, "node", "Pass a node's path (/root/Main/Button), a path under the root (Main/Button) or a name; get_scene_tree lists them.");

    private static string CheckPropertyName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new McpException("properties holds an empty name. Pass property names as inspect_node lists them, or leave properties out.")
            : name;

    private static string CheckName(string value, string argument, string hint) =>
        string.IsNullOrWhiteSpace(value) ? throw new McpException($"{argument} is empty. {hint}") : value;
}
