using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Server.Tools;

/// <summary>
/// diff_snapshots' comparison of two snapshots' nodes (path → {property: value}): the paths only one of them has, and every
/// property whose value differs. Values compare as JSON, numbers within <see cref="Tolerance"/> as wait_for compares them
/// (bridge/godot_mcp_time.gd's json_equal), so a float that only round-trips differently is not a change.
/// </summary>
internal static class SnapshotDiff
{
    /// <summary>The most entries each of added, removed and changed lists; the counts are always full.</summary>
    public const int MaxEntries = 200;

    /// <summary>How far apart two numbers may be and still be equal.</summary>
    public const double Tolerance = 1e-6;

    /// <summary>
    /// {added, removed, changed: [{node, property, before, after}], addedCount, removedCount, changedCount}. A property only
    /// one side has is a change whose other side is left out.
    /// </summary>
    public static JsonObject Compare(JsonObject before, JsonObject after)
    {
        List<string> added = [.. after.Where(node => !before.ContainsKey(node.Key)).Select(node => node.Key)];
        List<string> removed = [.. before.Where(node => !after.ContainsKey(node.Key)).Select(node => node.Key)];
        JsonArray changed = [];
        int changedCount = 0;
        foreach ((string path, JsonNode? beforeNode) in before)
        {
            if (after[path] is JsonObject afterProperties && beforeNode is JsonObject beforeProperties)
            {
                changedCount += AddChanges(path, beforeProperties, afterProperties, changed);
            }
        }

        return new JsonObject
        {
            ["added"] = Capped(added),
            ["removed"] = Capped(removed),
            ["changed"] = changed,
            ["addedCount"] = added.Count,
            ["removedCount"] = removed.Count,
            ["changedCount"] = changedCount,
        };
    }

    /// <summary>
    /// Whether two JSON values are equal: numbers within <see cref="Tolerance"/>, arrays element by element, objects by the
    /// same keys with equal values, anything else by kind and value.
    /// </summary>
    public static bool JsonEqual(JsonNode? actual, JsonNode? wanted)
    {
        if (actual is null || wanted is null)
        {
            return actual is null && wanted is null;
        }

        return actual switch
        {
            JsonArray array => wanted is JsonArray other && ArraysEqual(array, other),
            JsonObject obj => wanted is JsonObject other && ObjectsEqual(obj, other),
            _ => wanted is JsonValue other && ValuesEqual(actual.AsValue(), other),
        };
    }

    /// <summary>Adds one node's changed properties to changed while it holds fewer than <see cref="MaxEntries"/>.</summary>
    /// <returns>How many properties changed.</returns>
    private static int AddChanges(string path, JsonObject before, JsonObject after, JsonArray changed)
    {
        IEnumerable<string> names = before.Select(property => property.Key).Concat(after.Select(property => property.Key)).Distinct();
        int count = 0;
        foreach (string name in names)
        {
            bool inBefore = before.TryGetPropertyValue(name, out JsonNode? was);
            bool inAfter = after.TryGetPropertyValue(name, out JsonNode? now);
            if (inBefore == inAfter && JsonEqual(now, was))
            {
                continue;
            }

            count++;
            if (changed.Count < MaxEntries)
            {
                changed.Add(Change(path, name, inBefore ? was : null, inAfter ? now : null, (inBefore, inAfter)));
            }
        }

        return count;
    }

    private static JsonObject Change(string path, string name, JsonNode? was, JsonNode? now, (bool Before, bool After) present)
    {
        JsonObject change = new() { ["node"] = path, ["property"] = name };
        if (present.Before)
        {
            change["before"] = was?.DeepClone();
        }

        if (present.After)
        {
            change["after"] = now?.DeepClone();
        }

        return change;
    }

    private static JsonArray Capped(List<string> paths) => [.. paths.Take(MaxEntries).Select(path => (JsonNode)path)];

    private static bool ArraysEqual(JsonArray actual, JsonArray wanted) =>
        actual.Count == wanted.Count && actual.Zip(wanted).All(pair => JsonEqual(pair.First, pair.Second));

    private static bool ObjectsEqual(JsonObject actual, JsonObject wanted) =>
        actual.Count == wanted.Count
        && wanted.All(property => actual.TryGetPropertyValue(property.Key, out JsonNode? value) && JsonEqual(value, property.Value));

    private static bool ValuesEqual(JsonValue actual, JsonValue wanted)
    {
        if (actual.GetValueKind() == JsonValueKind.Number && wanted.GetValueKind() == JsonValueKind.Number)
        {
            return Math.Abs(Number(actual) - Number(wanted)) <= Tolerance;
        }

        return JsonNode.DeepEquals(actual, wanted);
    }

    private static double Number(JsonValue value) =>
        value.TryGetValue(out double number) ? number : double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture);
}
