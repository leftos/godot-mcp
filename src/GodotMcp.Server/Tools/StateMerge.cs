using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Shapes the bridge's state reply into get_game_state's result: keeps the asked keys of each node's state, cuts a long state
/// to a preview, and moves the nodes past the list's size budget into omitted, beside the nodes the bridge left out past
/// maxNodes.
/// </summary>
internal static class StateMerge
{
    /// <summary>The most characters of JSON one node's state keeps before it becomes {valuePreview, valueLength}.</summary>
    internal const int MaxStateLength = 4000;

    /// <summary>The characters of JSON the nodes' entries may take; the entries after the one that crosses it are omitted.</summary>
    internal const int MaxNodesLength = 40_000;

    /// <summary>The most paths omitted names; its count is whole.</summary>
    internal const int MaxOmittedPaths = 20;

    internal const string EmptyHint =
        "No node is in the mcp_state group. A GDScript node joins it and defines _mcp_state() returning a Dictionary; a C# node "
        + "defines _McpState(). Until a game opts in, snapshot_subtree and get_ui_elements read the tree.";

    /// <summary>
    /// {frame, nodes, total, omitted?, hint?} from the bridge's {frame, nodes, total, omitted?}: each state filtered by
    /// <paramref name="keys"/> when given and cut past <see cref="MaxStateLength"/>, the list held to
    /// <see cref="MaxNodesLength"/>, and the hint added when no node is marked and <paramref name="hintWhenEmpty"/>.
    /// </summary>
    public static JsonObject Shape(JsonNode? reply, IReadOnlyList<string>? keys, bool hintWhenEmpty)
    {
        JsonObject fields = reply as JsonObject ?? [];
        (JsonArray nodes, List<string> moved) = Budget(fields["nodes"] as JsonArray ?? [], keys);
        int total = ReadCount(fields["total"]);
        JsonObject result = new()
        {
            ["frame"] = fields["frame"]?.DeepClone(),
            ["nodes"] = nodes,
            ["total"] = total,
        };
        if (Omitted(moved, fields["omitted"] as JsonObject) is JsonObject omitted)
        {
            result["omitted"] = omitted;
        }

        if (total == 0 && hintWhenEmpty)
        {
            result["hint"] = EmptyHint;
        }

        return result;
    }

    /// <summary>
    /// The entries shaped, in order, until their JSON crosses <see cref="MaxNodesLength"/> characters, the one crossing it
    /// kept; the paths of the entries after it.
    /// </summary>
    private static (JsonArray Nodes, List<string> Moved) Budget(JsonArray entries, IReadOnlyList<string>? keys)
    {
        JsonArray nodes = [];
        List<string> moved = [];
        int length = 0;
        foreach (JsonObject entry in entries.OfType<JsonObject>())
        {
            if (length > MaxNodesLength)
            {
                moved.Add(entry["path"]?.ToString() ?? string.Empty);
                continue;
            }

            JsonObject shaped = ShapeEntry(entry, keys);
            length += shaped.ToJsonString().Length;
            nodes.Add(shaped);
        }

        return (nodes, moved);
    }

    /// <summary>A copy of the entry, its state (when it has one) filtered by <paramref name="keys"/> and cut to a preview.</summary>
    internal static JsonObject ShapeEntry(JsonObject entry, IReadOnlyList<string>? keys)
    {
        JsonObject shaped = entry.DeepClone().AsObject();
        if (!shaped.TryGetPropertyValue("state", out JsonNode? state))
        {
            return shaped;
        }

        JsonNode? kept = keys is null ? state : Filter(state, keys);
        shaped["state"] = RuntimeTools.ValuePreview(kept, MaxStateLength) ?? kept?.DeepClone();
        return shaped;
    }

    /// <summary>
    /// {key: value} for each of <paramref name="keys"/> found in <paramref name="state"/>, a key being a name or a dotted path
    /// with list indexes (seats[0].hp); a key the state lacks is left out.
    /// </summary>
    internal static JsonObject Filter(JsonNode? state, IReadOnlyList<string> keys)
    {
        JsonObject kept = [];
        foreach (string key in keys)
        {
            if (TryFind(state, key, out JsonNode? value))
            {
                kept[key] = value?.DeepClone();
            }
        }

        return kept;
    }

    /// <summary>The value at <paramref name="path"/> under <paramref name="root"/>, false when a step of it is missing.</summary>
    internal static bool TryFind(JsonNode? root, string path, out JsonNode? found)
    {
        found = root;
        foreach (string part in path.Split('.'))
        {
            if (IsCutMarker(found))
            {
                return true;
            }

            int bracket = part.IndexOf('[', StringComparison.Ordinal);
            string name = bracket < 0 ? part : part[..bracket];
            if (!TryName(ref found, name, allowEmpty: bracket >= 0) || !TryIndexes(ref found, bracket < 0 ? string.Empty : part[bracket..]))
            {
                found = null;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the value is the bridge's mark for a value it did not write, "&lt;depth limit: Type&gt;" or "&lt;size limit&gt;", which
    /// a path reaching it keeps as its value, so the agent sees the read was cut there.
    /// </summary>
    private static bool IsCutMarker(JsonNode? value) =>
        value is JsonValue text
        && text.GetValueKind() == JsonValueKind.String
        && text.GetValue<string>() is var marker
        && (marker.StartsWith("<depth limit: ", StringComparison.Ordinal) || marker == "<size limit>");

    /// <summary>Steps into the object's member <paramref name="name"/>; an empty name stays put when it comes before an index.</summary>
    private static bool TryName(ref JsonNode? at, string name, bool allowEmpty)
    {
        if (name.Length == 0)
        {
            return allowEmpty;
        }

        return at is JsonObject members && members.TryGetPropertyValue(name, out at);
    }

    /// <summary>Steps through each [n] of <paramref name="indexes"/> into a list; false on a malformed index or one past its end.</summary>
    private static bool TryIndexes(ref JsonNode? at, string indexes)
    {
        string rest = indexes;
        while (rest.Length > 0)
        {
            int close = rest.IndexOf(']', StringComparison.Ordinal);
            if (rest[0] != '[' || close < 0 || !int.TryParse(rest[1..close], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                return false;
            }

            if (at is not JsonArray list || index >= list.Count)
            {
                return false;
            }

            at = list[index];
            rest = rest[(close + 1)..];
        }

        return true;
    }

    /// <summary>
    /// {count, paths}: the nodes the budget moved, then the bridge's omitted past maxNodes (which come later in tree order), the
    /// first <see cref="MaxOmittedPaths"/> paths named; null when neither left a node out.
    /// </summary>
    internal static JsonObject? Omitted(IReadOnlyList<string> moved, JsonObject? fromBridge)
    {
        int bridgeCount = ReadCount(fromBridge?["count"]);
        int count = moved.Count + bridgeCount;
        if (count == 0)
        {
            return null;
        }

        IEnumerable<string> bridgePaths = (fromBridge?["paths"] as JsonArray ?? []).Select(path => path?.ToString() ?? string.Empty);
        JsonArray paths = [.. moved.Concat(bridgePaths).Take(MaxOmittedPaths).Select(path => (JsonNode)path)];
        return new JsonObject { ["count"] = count, ["paths"] = paths };
    }

    /// <summary>A count the bridge wrote as a JSON number; 0 when it is absent or not a number.</summary>
    private static int ReadCount(JsonNode? value) =>
        value is JsonValue number && double.TryParse(number.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double count)
            ? (int)count
            : 0;
}
