using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The game's own state: the nodes in the mcp_state group, each read in one frame by the bridge's state reader
/// (bridge/godot_mcp_state.gd), a GDScript node through its _mcp_state() and, in a project with a built C# assembly, a C#
/// node through its _McpState() by the C# helper the reader calls; then merged, filtered by key and cut to size here
/// (<see cref="StateMerge"/>).
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
        "Reads the running game's own state: every node in the mcp_state group, in tree order, with the value its state "
            + "method returns, all in one frame. A GDScript node's is _mcp_state(); a C# node's is _McpState(), an instance "
            + "method with no parameters and any accessibility returning any value (a record, an anonymous type, a "
            + "Dictionary), read by the C# helper, or a Godot-visible _mcp_state() when it has no _McpState. Returns {frame, "
            + "nodes: [{path, class, state, warning?} | {path, class, error}], total, omitted?: {count, paths}, hint?}: frame "
            + "is the engine's process frame count at the read; total counts every marked node; omitted counts the marked "
            + "nodes past maxNodes or past the 40000-character budget of the nodes list and names the first 20. A state "
            + "longer than 4000 characters of JSON comes back as {valuePreview, valueLength}. A node's error says why it has "
            + "no state: no state method, the method raised or threw (the error's text), or it returned a coroutine or a "
            + "Task, which is never awaited. warning says a C# node's _mcp_state was not read beside its _McpState. With no "
            + "marked node, hint says how a game opts in. The state methods are game code and run at every read. With keep, "
            + "the read is also held in the session beside snapshot_subtree's snapshots and the result gains stateId, which "
            + "diff_snapshots compares with a later kept read or with the live game read again, one value per leaf of each "
            + "state (seats[1].hp)."
            + ValueNote
            + BridgeNote
    )]
    public async Task<string> GetGameStateAsync(
        [Description(NodeDescription + " Only the marked nodes at or under it are read; every marked node when left out.")] string? node = null,
        [Description(
            "{keys, maxNodes, maxDepth, keep}: only these keys or dotted paths of each state, the most nodes to read (50 by "
                + "default, up to 500), the levels of nested values to write (4 by default, up to 8), and whether to hold the "
                + "read for diff_snapshots (false by default)."
        )]
            StateOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        StateRequest request = CheckStateRequest(node, options);
        GodotSession game = Find(session);
        (JsonObject shaped, IReadOnlyList<ErrorEntry> errors) = await ReadStateAsync(game, request, cancellationToken);
        if (options?.Keep == true)
        {
            shaped["stateId"] = game.Snapshots.Add(KeptState(request, shaped));
        }

        return ErrorReport.AddTo(shaped, errors).ToJsonString();
    }

    /// <summary>
    /// One state read: the bridge's state command, with the C# helper's extension when the project has a built assembly, shaped
    /// into get_game_state's result; diff_snapshots re-reads a kept read through it.
    /// </summary>
    private async Task<(JsonObject Shaped, IReadOnlyList<ErrorEntry> Errors)> ReadStateAsync(
        GodotSession game,
        StateRequest request,
        CancellationToken cancellationToken
    )
    {
        JsonObject parameters = StateParameters(request, csharp.PrepareForState(game.ProjectDir));
        BridgeCall call = new(GetGameStateToolName, "state", parameters, InspectTimeout);
        BridgeResult result = await CallWithErrorsAsync(game, call, cancellationToken);
        return (StateMerge.Shape(result.Reply, request.Keys, hintWhenEmpty: request.Node is null), result.Errors);
    }

    /// <summary>The read as the snapshot store holds it: flattened, with the options that read it again.</summary>
    internal static Snapshot KeptState(StateRequest request, JsonObject shaped) =>
        new(SnapshotKind.State, request.Node, request.MaxNodes, StateFlatten.Nodes(shaped)) { Keys = request.Keys, MaxDepth = request.MaxDepth };

    /// <summary>
    /// The bridge's state params: node, maxNodes and maxDepth, with extension when the C# helper reads the C# nodes, or
    /// csharpError, why it cannot, which the bridge puts in each C# node's error that has no Godot-visible _mcp_state.
    /// </summary>
    internal static JsonObject StateParameters(StateRequest request, StateHelper helper)
    {
        JsonObject parameters = new()
        {
            ["node"] = request.Node ?? string.Empty,
            ["maxNodes"] = request.MaxNodes,
            ["maxDepth"] = request.MaxDepth,
        };
        if (helper.Extension is { } extension)
        {
            parameters["extension"] = extension;
        }

        if (helper.Error is { } error)
        {
            parameters["csharpError"] = error;
        }

        return parameters;
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
