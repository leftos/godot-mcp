using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The game's own state: the nodes in the mcp_state group, each read through its _mcp_state() in one frame by the bridge's
/// state reader (bridge/godot_mcp_state.gd), then filtered by key and cut to size here (<see cref="StateMerge"/>).
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const string GetGameStateToolName = "get_game_state";
    internal const int DefaultMaxStateNodes = 50;
    internal const int MaxStateNodes = 500;
    internal const int DefaultMaxStateDepth = 4;
    internal const int MaxStateDepth = 8;

    [McpServerTool(Name = GetGameStateToolName, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Reads the running game's own state: every node in the mcp_state group, in tree order, with the value its "
            + "_mcp_state() returns, all in one frame. Returns {frame, nodes: [{path, class, state} | {path, class, error}], total, "
            + "omitted?: {count, paths}, hint?}: frame is the engine's process frame count at the read; total counts every marked "
            + "node; omitted counts the marked nodes past maxNodes or past the 40000-character budget of the nodes list and "
            + "names the first 20. A state longer than 4000 characters of JSON comes back as {valuePreview, valueLength}. A "
            + "node's error says why it has no state: no _mcp_state method, the method raised (the error's text), or it "
            + "returned a coroutine, which is never awaited. With no marked node, hint says how a game opts in. The state "
            + "methods are game code and run at every read."
            + ValueNote
            + BridgeNote
    )]
    public async Task<string> GetGameStateAsync(
        [Description(NodeDescription + " Only the marked nodes at or under it are read; every marked node when left out.")] string? node = null,
        [Description(
            "{keys, maxNodes, maxDepth}: only these keys or dotted paths of each state, the most nodes to read (50 by "
                + "default, up to 500), and the levels of nested values to write (4 by default, up to 8)."
        )]
            StateOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        StateRequest request = CheckStateRequest(node, options);
        JsonObject parameters = new()
        {
            ["node"] = request.Node ?? string.Empty,
            ["maxNodes"] = request.MaxNodes,
            ["maxDepth"] = request.MaxDepth,
        };
        BridgeCall call = new(GetGameStateToolName, "state", parameters, InspectTimeout);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        JsonObject shaped = StateMerge.Shape(result.Reply, request.Keys, hintWhenEmpty: request.Node is null);
        return ErrorReport.AddTo(shaped, result.Errors).ToJsonString();
    }

    /// <exception cref="McpException">node or a key is empty, or maxNodes or maxDepth is out of its range.</exception>
    internal static StateRequest CheckStateRequest(string? node, StateOptions? options)
    {
        if (node is not null)
        {
            CheckNode(node);
        }

        int maxNodes = CheckStateRange(options?.MaxNodes ?? DefaultMaxStateNodes, MaxStateNodes, "maxNodes");
        int maxDepth = CheckStateRange(options?.MaxDepth ?? DefaultMaxStateDepth, MaxStateDepth, "maxDepth");
        return new StateRequest(node, CheckStateKeys(options?.Keys), maxNodes, maxDepth);
    }

    /// <exception cref="McpException">value is outside 1 to max.</exception>
    private static int CheckStateRange(int value, int max, string argument) =>
        value >= 1 && value <= max ? value : throw new McpException($"{argument} must be 1 to {max}; got {value}.");

    /// <summary>The keys, null when none are given (every key).</summary>
    /// <exception cref="McpException">A key is empty.</exception>
    private static string[]? CheckStateKeys(string[]? keys)
    {
        if (keys is not null && keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new McpException(
                "keys holds an empty key. Pass keys of the nodes' state or dotted paths into it (seats[0].hp), or leave keys out."
            );
        }

        return keys is { Length: > 0 } ? keys : null;
    }

    /// <summary>What a state read takes: the node to read under (null: the whole tree), the keys (null: every key) and the caps.</summary>
    internal sealed record StateRequest(string? Node, IReadOnlyList<string>? Keys, int MaxNodes, int MaxDepth);
}
