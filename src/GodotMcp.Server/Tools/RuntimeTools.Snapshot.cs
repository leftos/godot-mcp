using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Snapshots of a live subtree and their diff: the bridge's inspector captures every node's properties
/// (bridge/godot_mcp_inspect.gd's snapshot), the session holds the capture (<see cref="SnapshotStore"/>), and the diff is
/// computed here (<see cref="SnapshotDiff"/>).
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const int DefaultMaxSnapshotNodes = 2000;
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(30);

    [McpServerTool(Name = "snapshot_subtree", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Captures a subtree of the running game and holds it in the session for diff_snapshots: for the node and every "
            + "descendant, the properties the inspector shows (as inspect_node lists them) and its groups as the property "
            + "groups. Returns {snapshotId, node, nodeCount}, not the data. The session holds its 16 most recently used "
            + "snapshots, until the game stops or restarts. A subtree of more than maxNodes nodes is refused with its count."
            + BridgeNote
    )]
    public async Task<string> SnapshotSubtreeAsync(
        [Description("The subtree's root (a path or a name, as inspect_node takes); the current scene's root when left out.")] string? node = null,
        [Description(
            "{properties, ignore, maxNodes}: only these property names, these names left out, and the most nodes the subtree "
                + "may hold (2000 by default)."
        )]
            SnapshotOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        SnapshotRequest request = CheckSnapshotRequest(node, options);
        GodotSession target = Find(session);
        (Snapshot snapshot, IReadOnlyList<ErrorEntry> errors) = await CaptureSnapshotAsync(target, request, cancellationToken);
        JsonObject result = new()
        {
            ["snapshotId"] = target.Snapshots.Add(snapshot),
            ["node"] = snapshot.Node,
            ["nodeCount"] = snapshot.Nodes.Count,
        };
        return ErrorReport.AddTo(result, errors).ToJsonString();
    }

    [McpServerTool(Name = "diff_snapshots", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Compares two snapshots snapshot_subtree took, or one with the live game: without afterId, the before snapshot's "
            + "node is captured again with the same options. Nodes match by their path from the snapshot's node. Returns "
            + "{added, removed, changed: [{node, property, before, after}], addedCount, removedCount, changedCount}: added and "
            + "removed are paths, and each list holds at most 200 entries while the counts are full. Values compare as JSON, "
            + "numbers within 1e-6; a property only one side has leaves the other side's value out."
    )]
    public async Task<string> DiffSnapshotsAsync(
        [Description("The earlier snapshot's snapshotId.")] string beforeId,
        [Description("The later snapshot's snapshotId; the live game, captured now, when left out.")] string? afterId = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        CheckName(beforeId, "beforeId", "Pass a snapshotId snapshot_subtree returned.");
        if (afterId is not null)
        {
            CheckName(afterId, "afterId", "Pass a snapshotId snapshot_subtree returned, or leave afterId out to compare with the live game.");
        }

        GodotSession target = Find(session);
        Snapshot before = HeldSnapshot(target, beforeId);
        if (afterId is not null)
        {
            return SnapshotDiff.Compare(before.Nodes, HeldSnapshot(target, afterId).Nodes).ToJsonString();
        }

        SnapshotRequest again = new(before.Node, before.Properties, before.Ignore, before.MaxNodes);
        (Snapshot live, IReadOnlyList<ErrorEntry> errors) = await CaptureSnapshotAsync(target, again, cancellationToken);
        return ErrorReport.AddTo(SnapshotDiff.Compare(before.Nodes, live.Nodes), errors).ToJsonString();
    }

    /// <exception cref="McpException">node or a property name is empty, or maxNodes is under 1.</exception>
    internal static SnapshotRequest CheckSnapshotRequest(string? node, SnapshotOptions? options)
    {
        if (node is not null)
        {
            CheckNode(node);
        }

        int maxNodes = options?.MaxNodes ?? DefaultMaxSnapshotNodes;
        if (maxNodes < 1)
        {
            throw new McpException($"maxNodes must be at least 1; got {maxNodes}.");
        }

        return new SnapshotRequest(node, CheckNames(options?.Properties, "properties"), CheckNames(options?.Ignore, "ignore"), maxNodes);
    }

    private static string[]? CheckNames(string[]? names, string argument) =>
        names is not null && names.Any(string.IsNullOrWhiteSpace)
            ? throw new McpException($"{argument} holds an empty name. Pass property names as inspect_node lists them, or leave {argument} out.")
            : names;

    /// <exception cref="McpException">The session does not hold the snapshot.</exception>
    private static Snapshot HeldSnapshot(GodotSession target, string id) =>
        target.Snapshots.Find(id)
        ?? throw new McpException(
            $"snapshot {id} is not held (evicted, or from a run that stopped or restarted, or an attached game that has gone); "
                + "take a new one with snapshot_subtree"
        );

    private static async Task<(Snapshot Snapshot, IReadOnlyList<ErrorEntry> Errors)> CaptureSnapshotAsync(
        GodotSession target,
        SnapshotRequest request,
        CancellationToken cancellationToken
    )
    {
        BridgeCall call = new("snapshot_subtree", "snapshot", SnapshotParameters(request), SnapshotTimeout);
        BridgeResult result = await CallWithErrorsAsync(target, call, cancellationToken);
        return (ReadSnapshot(result.Reply, request), result.Errors);
    }

    /// <summary>The bridge's snapshot parameters: the node ("" for the current scene's root), maxNodes, and the filters given.</summary>
    private static JsonObject SnapshotParameters(SnapshotRequest request)
    {
        JsonObject parameters = new() { ["node"] = request.Node ?? string.Empty, ["maxNodes"] = request.MaxNodes };
        if (request.Properties is { Count: > 0 } properties)
        {
            parameters["properties"] = new JsonArray([.. properties.Select(name => (JsonNode)name)]);
        }

        if (request.Ignore is { Count: > 0 } ignore)
        {
            parameters["ignore"] = new JsonArray([.. ignore.Select(name => (JsonNode)name)]);
        }

        return parameters;
    }

    /// <exception cref="McpException">The reply names no node.</exception>
    private static Snapshot ReadSnapshot(JsonNode? reply, SnapshotRequest request)
    {
        JsonObject fields = reply as JsonObject ?? [];
        string node =
            HandshakeExpectation.ReadString(fields, "node")
            ?? throw new McpException($"The bridge's snapshot reply names no node: {reply?.ToJsonString() ?? "null"}.");
        JsonObject nodes = fields["nodes"] is JsonObject captured ? captured.DeepClone().AsObject() : [];
        return new Snapshot(node, request.Properties, request.Ignore, request.MaxNodes, nodes);
    }

    /// <summary>What a snapshot captures: its root as given (null: the current scene's), the property filters and the node cap.</summary>
    internal sealed record SnapshotRequest(string? Node, IReadOnlyList<string>? Properties, IReadOnlyList<string>? Ignore, int MaxNodes);
}
