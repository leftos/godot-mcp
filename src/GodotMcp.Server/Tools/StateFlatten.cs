using System.Globalization;
using System.Text.Json.Nodes;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Flattens a get_game_state result for the snapshot store, so diff_snapshots compares one value per leaf: each node entry
/// becomes its path → {leaf key: value}. Object members join with ".", list elements with "[i]", the syntax keys takes
/// (seats[1].hp); a member name is written as it is, so a keys-filtered state's keys ("seats[0].hp") stay whole and a name
/// holding "." or "[" reads like a path. A state that is itself a non-empty list is keyed from "[0]"; any other state that is not
/// a non-empty Dictionary (a scalar, null, an empty one) is the one key <see cref="RootKey"/>. An empty object or list below
/// the root is one leaf holding {} or [], so filling it shows. A cut mark ("&lt;depth limit: Type&gt;", "&lt;size limit&gt;")
/// is a string leaf, and a state cut to a preview flattens to its valuePreview and valueLength. An errored node is the one
/// leaf error. Only the entries in nodes are flattened: omitted is not.
/// </summary>
internal static class StateFlatten
{
    /// <summary>The key of a whole state that is not a non-empty Dictionary or Array.</summary>
    internal const string RootKey = "$";

    /// <summary>The leaf that holds an errored node's error.</summary>
    internal const string ErrorKey = "error";

    /// <summary>path → {leaf key: value} for each node entry of a get_game_state result; empty when it read none.</summary>
    public static JsonObject Nodes(JsonObject result)
    {
        JsonObject flat = [];
        foreach (JsonObject entry in (result["nodes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            flat[entry["path"]?.ToString() ?? string.Empty] = Entry(entry);
        }

        return flat;
    }

    /// <summary>One node entry's leaves: its error as the one leaf <see cref="ErrorKey"/>, else its state's leaves.</summary>
    internal static JsonObject Entry(JsonObject entry)
    {
        if (entry.TryGetPropertyValue(ErrorKey, out JsonNode? error))
        {
            return new JsonObject { [ErrorKey] = error?.DeepClone() };
        }

        JsonObject leaves = [];
        JsonNode? state = entry["state"];
        if (IsFilled(state))
        {
            AddLeaves(leaves, string.Empty, state);
        }
        else
        {
            leaves[RootKey] = state?.DeepClone();
        }

        return leaves;
    }

    /// <summary>Adds the value under <paramref name="key"/>: a filled object or list by its leaves below it, anything else whole.</summary>
    private static void AddLeaves(JsonObject leaves, string key, JsonNode? value)
    {
        switch (value)
        {
            case JsonObject { Count: > 0 } members:
                AddMembers(leaves, key, members);
                break;
            case JsonArray { Count: > 0 } list:
                AddElements(leaves, key, list);
                break;
            default:
                leaves[key] = value?.DeepClone();
                break;
        }
    }

    private static void AddMembers(JsonObject leaves, string prefix, JsonObject members)
    {
        foreach ((string name, JsonNode? value) in members)
        {
            AddLeaves(leaves, prefix.Length == 0 ? name : $"{prefix}.{name}", value);
        }
    }

    private static void AddElements(JsonObject leaves, string prefix, JsonArray list)
    {
        for (int index = 0; index < list.Count; index++)
        {
            AddLeaves(leaves, string.Create(CultureInfo.InvariantCulture, $"{prefix}[{index}]"), list[index]);
        }
    }

    private static bool IsFilled(JsonNode? value) => value is JsonObject { Count: > 0 } or JsonArray { Count: > 0 };
}
