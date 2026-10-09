using System.Text.Json.Nodes;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The fired list a click or a mouse_button answers: the bridge's raw entries <c>[frame, node, signal, args, listeners]</c>
/// as <c>{node, signal, args?, frame, listeners, count?}</c>. A run of the same signal on the same node in a row folds into
/// one entry with the run's first frame, its last args and its count; the first <see cref="MaxEntries"/> entries are kept,
/// and firedDropped counts the rest with the bridge's own overflow (firedOverflow, removed) and any raw entry that is not
/// an array; an argument over <see cref="MaxArgLength"/> characters of JSON becomes <c>{valuePreview, valueLength}</c>, as
/// the watch cuts one.
/// </summary>
internal static class FiredSignals
{
    /// <summary>The most entries a fired list keeps, after folding.</summary>
    internal const int MaxEntries = 50;

    /// <summary>The most characters of JSON an argument keeps before its preview.</summary>
    internal const int MaxArgLength = 200;

    private const int FrameIndex = 0;
    private const int NodeIndex = 1;
    private const int SignalIndex = 2;
    private const int ArgsIndex = 3;
    private const int ListenersIndex = 4;

    /// <summary>
    /// The result with its fired list shaped and firedDropped set when an entry was left out; a result without a fired
    /// list (every gesture but click and mouse_button) comes back as it is. The other keys pass through.
    /// </summary>
    internal static JsonObject Shape(JsonObject result)
    {
        if (result["fired"] is not JsonArray raw)
        {
            return result;
        }

        List<Run> runs = Fold(raw, out int malformed);
        int dropped = Math.Max(runs.Count - MaxEntries, 0) + malformed + Count(result["firedOverflow"]);
        result.Remove("firedOverflow");
        JsonArray fired = [];
        foreach (Run run in runs.Take(MaxEntries))
        {
            fired.Add(Entry(run));
        }

        result["fired"] = fired;
        if (dropped > 0)
        {
            result["firedDropped"] = dropped;
        }

        return result;
    }

    /// <summary>
    /// The raw entries as runs of the same signal on the same node in a row, in the bridge's order; <paramref name="malformed"/>
    /// counts the entries that are not arrays, which firedDropped counts as left out.
    /// </summary>
    private static List<Run> Fold(JsonArray raw, out int malformed)
    {
        List<Run> runs = [];
        malformed = 0;
        foreach (JsonNode? node in raw)
        {
            if (node is not JsonArray item)
            {
                malformed++;
                continue;
            }

            if (runs.Count > 0 && runs[^1].Continues(item))
            {
                runs[^1] = runs[^1] with { Last = item, Count = runs[^1].Count + 1 };
                continue;
            }

            runs.Add(new Run(item, item, 1));
        }

        return runs;
    }

    private static JsonObject Entry(Run run)
    {
        JsonObject entry = new() { ["node"] = At(run.First, NodeIndex)?.DeepClone(), ["signal"] = At(run.First, SignalIndex)?.DeepClone() };
        if (At(run.Last, ArgsIndex) is JsonArray { Count: > 0 } args)
        {
            entry["args"] = Cut(args);
        }

        entry["frame"] = At(run.First, FrameIndex)?.DeepClone();
        entry["listeners"] = At(run.Last, ListenersIndex)?.DeepClone();
        if (run.Count > 1)
        {
            entry["count"] = run.Count;
        }

        return entry;
    }

    private static JsonArray Cut(JsonArray args)
    {
        JsonArray cut = [];
        foreach (JsonNode? arg in args)
        {
            cut.Add(RuntimeTools.ValuePreview(arg, MaxArgLength) ?? arg?.DeepClone());
        }

        return cut;
    }

    private static JsonNode? At(JsonArray item, int index) => index < item.Count ? item[index] : null;

    private static string? Text(JsonArray item, int index) => At(item, index) is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    // GDScript's JSON may write an integer as a float, so a count is read as a double.
    private static int Count(JsonNode? node) =>
        node is null ? 0
        : node.AsValue().TryGetValue(out int count) ? count
        : (int)node.GetValue<double>();

    /// <summary>A run of raw entries in a row with the same node and signal: its first entry, its last, and how many.</summary>
    private sealed record Run(JsonArray First, JsonArray Last, int Count)
    {
        public bool Continues(JsonArray item) =>
            Text(item, NodeIndex) == Text(First, NodeIndex) && Text(item, SignalIndex) == Text(First, SignalIndex);
    }
}
