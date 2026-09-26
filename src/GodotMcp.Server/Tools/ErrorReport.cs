using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Server.Tools;

/// <summary>
/// How the errors a game raised appear in a tool's result: identical ones (same message, file and line) collapsed into one
/// with a count, at most <see cref="MaxPerResult"/> of them, each message cut to <see cref="MaxMessageLength"/> characters.
/// </summary>
internal static class ErrorReport
{
    public const int MaxPerResult = 20;
    public const int MaxMessageLength = 2000;

    /// <summary>
    /// Adds <c>errors</c> to <paramref name="result"/>, and <c>errorsOmitted</c> (how many collapsed errors did not fit) when
    /// there are more than <see cref="MaxPerResult"/>; with no errors the result is left as it is.
    /// </summary>
    /// <returns><paramref name="result"/>.</returns>
    public static JsonObject AddTo(JsonObject result, IReadOnlyList<ErrorEntry> errors)
    {
        if (errors.Count == 0)
        {
            return result;
        }

        List<Collapsed> collapsed = Collapse(errors);
        JsonArray shown = [];
        foreach (Collapsed error in collapsed.Take(MaxPerResult))
        {
            JsonObject item = Describe(error.First, withType: false);
            if (error.Count > 1)
            {
                item["count"] = error.Count;
            }

            shown.Add(item);
        }

        result["errors"] = shown;
        if (collapsed.Count > MaxPerResult)
        {
            result["errorsOmitted"] = collapsed.Count - MaxPerResult;
        }

        return result;
    }

    /// <summary>One entry as a result shows it, {seq, type?, message, file, line, function, engine, stack}, leaving out empty fields.</summary>
    public static JsonObject Describe(ErrorEntry entry, bool withType)
    {
        JsonObject item = new() { ["seq"] = entry.Seq };
        if (withType)
        {
            item["type"] = entry.Type;
        }

        item["message"] = OutputBuffer.Cut(entry.Message, MaxMessageLength);
        if (entry.File.Length > 0)
        {
            item["file"] = entry.File;
        }

        if (entry.Line > 0)
        {
            item["line"] = entry.Line;
        }

        if (entry.Function.Length > 0)
        {
            item["function"] = entry.Function;
        }

        if (entry.Engine.Length > 0)
        {
            item["engine"] = entry.Engine;
        }

        if (entry.Stack.Count > 0)
        {
            item["stack"] = new JsonArray([.. entry.Stack.Select(frame => JsonValue.Create(frame))]);
        }

        return item;
    }

    /// <summary>The errors as lines for an exception's message: each collapsed error, where it was raised, and its stack.</summary>
    public static string Summarise(IReadOnlyList<ErrorEntry> errors)
    {
        List<Collapsed> collapsed = Collapse(errors);
        StringBuilder text = new();
        foreach ((ErrorEntry first, int count) in collapsed.Take(MaxPerResult))
        {
            string times = count > 1 ? $" (x{count})" : string.Empty;
            text.Append(OutputBuffer.Cut(first.Message, MaxMessageLength)).Append(times).Append(" at ").Append(Locate(first)).Append('\n');
            foreach (string frame in first.Stack)
            {
                text.Append("    ").Append(frame).Append('\n');
            }
        }

        if (collapsed.Count > MaxPerResult)
        {
            text.Append(CultureInfo.InvariantCulture, $"... and {collapsed.Count - MaxPerResult} more; get_errors lists them.\n");
        }

        return text.ToString().TrimEnd('\n');
    }

    private static string Locate(ErrorEntry entry)
    {
        string place = entry.Line > 0 ? $"{entry.File}:{entry.Line}" : entry.File;
        return entry.Function.Length > 0 ? $"{place} in {entry.Function}" : place;
    }

    /// <summary>The errors grouped by message, file and line, in the order each first appeared.</summary>
    private static List<Collapsed> Collapse(IReadOnlyList<ErrorEntry> errors)
    {
        Dictionary<(string Message, string File, int Line), int> positions = [];
        List<Collapsed> collapsed = [];
        foreach (ErrorEntry error in errors)
        {
            (string, string, int) key = (error.Message, error.File, error.Line);
            if (positions.TryGetValue(key, out int position))
            {
                collapsed[position] = collapsed[position] with { Count = collapsed[position].Count + 1 };
                continue;
            }

            positions[key] = collapsed.Count;
            collapsed.Add(new Collapsed(error, 1));
        }

        return collapsed;
    }

    private sealed record Collapsed(ErrorEntry First, int Count);
}
