using System.Text.RegularExpressions;

namespace GodotMcp.Server.Agents;

/// <summary>What a sweep does with one agent file.</summary>
internal enum AgentFileState
{
    /// <summary>No marker and no godot tool: not the sweep's business.</summary>
    Ignored,

    /// <summary>No marker, but its tools line names a godot tool.</summary>
    Unmarked,

    /// <summary>Marked, and its godot tools are already its classes' tools.</summary>
    Unchanged,

    /// <summary>Marked, and its tools line is rewritten.</summary>
    Changed,

    /// <summary>Marked, but the marker or the tools line cannot be read.</summary>
    Error,
}

/// <summary>The outcome of syncing one agent file's text.</summary>
/// <param name="State">What the sweep does with the file.</param>
/// <param name="Text">The file's new text when <see cref="AgentFileState.Changed"/>, else its text as it was.</param>
/// <param name="Added">The godot tools added, by their short names.</param>
/// <param name="Removed">The godot tools removed, by their short names.</param>
/// <param name="Error">Why the file cannot be synced, when <see cref="AgentFileState.Error"/>.</param>
internal sealed record AgentFileSync(AgentFileState State, string Text, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, string Error);

/// <summary>
/// Syncs the godot tools of a Claude Code agent file. The file's YAML front matter carries its tools as one comma-separated
/// line (<c>tools: Read, Grep, mcp__godot__click</c>), and a body line <c>&lt;!-- godot-mcp tool classes: read, drive --&gt;</c>
/// marks the file as one whose <c>mcp__godot__</c> entries are exactly the tools of those classes. Only the tools line's
/// text after <c>tools:</c> is ever rewritten; every other byte, line endings included, is kept.
/// </summary>
internal static partial class AgentFile
{
    /// <summary>The prefix of every godot-mcp tool's name in an agent's tools list.</summary>
    public const string ToolPrefix = "mcp__godot__";

    private const string FallbackAnchor = "SendMessage";

    /// <summary>Syncs <paramref name="text"/> against the served tools and their classes.</summary>
    /// <param name="text">The agent file's text.</param>
    /// <param name="catalog">Every served tool's short name mapped to its class.</param>
    public static AgentFileSync Sync(string text, IReadOnlyDictionary<string, string> catalog)
    {
        (Group? toolsList, List<string> entries, Match marker) = Parse(text);
        if (!marker.Success)
        {
            return Outcome(entries.Exists(IsGodotTool) ? AgentFileState.Unmarked : AgentFileState.Ignored, text);
        }

        if (toolsList is null || IsUnsupportedList(toolsList.Value))
        {
            return Failure(text, "it is marked for godot-mcp tool classes but its front matter has no one-line, comma-separated tools: list");
        }

        HashSet<string> classes = ParseClasses(marker.Groups["classes"].Value, out string classError);
        return classError.Length > 0 ? Failure(text, classError) : Rewrite(text, toolsList, entries, Wanted(classes, catalog));
    }

    // The tools line's list (the text after "tools:"), its entries, and the marker searched for after the front matter.
    private static (Group? ToolsList, List<string> Entries, Match Marker) Parse(string text)
    {
        Match frontMatter = FrontMatterPattern().Match(text);
        if (!frontMatter.Success)
        {
            return (null, [], MarkerPattern().Match(text));
        }

        Group? toolsList = FindToolsList(text, frontMatter.Groups["body"]);
        List<string> entries = toolsList is null ? [] : SplitEntries(toolsList.Value);
        return (toolsList, entries, MarkerPattern().Match(text, frontMatter.Index + frontMatter.Length));
    }

    private static List<string> Wanted(HashSet<string> classes, IReadOnlyDictionary<string, string> catalog) =>
        [.. catalog.Where(p => classes.Contains(p.Value)).Select(p => ToolPrefix + p.Key).Order(StringComparer.Ordinal)];

    private static AgentFileSync Rewrite(string text, Group toolsList, List<string> entries, List<string> wanted)
    {
        List<string> updated = Place(entries, wanted);
        if (updated.SequenceEqual(entries, StringComparer.Ordinal))
        {
            return Outcome(AgentFileState.Unchanged, text);
        }

        string line = " " + string.Join(", ", updated);
        string rewritten = string.Concat(text.AsSpan(0, toolsList.Index), line, text.AsSpan(toolsList.Index + toolsList.Length));
        List<string> current = entries.FindAll(IsGodotTool);
        return new AgentFileSync(AgentFileState.Changed, rewritten, ShortNames(wanted.Except(current)), ShortNames(current.Except(wanted)), "");
    }

    private static bool IsGodotTool(string entry) => entry.StartsWith(ToolPrefix, StringComparison.Ordinal);

    // A YAML flow sequence or a quoted scalar would need a YAML parser to rewrite; no agent file uses either.
    private static bool IsUnsupportedList(string list)
    {
        string value = list.Trim();
        return value.Length == 0 || value[0] is '[' or '"' or '\'';
    }

    private static Group? FindToolsList(string text, Group body)
    {
        Match tools = ToolsLinePattern().Match(text, body.Index, body.Length);
        return tools.Success ? tools.Groups["list"] : null;
    }

    private static List<string> SplitEntries(string list) =>
        [.. list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    private static HashSet<string> ParseClasses(string list, out string error)
    {
        HashSet<string> classes = new(
            list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal
        );
        List<string> unknown = [.. classes.Where(c => !ToolClasses.All.Contains(c))];
        string known = string.Join(", ", ToolClasses.All);
        error =
            unknown.Count > 0 ? $"unknown tool class {string.Join(", ", unknown)} in its marker; the classes are {known}"
            : classes.Count == 0 ? $"its marker names no tool class; the classes are {known}"
            : "";
        return classes;
    }

    // The godot entries go, sorted, where the first one stood; with none, just before SendMessage, else at the end.
    private static List<string> Place(List<string> entries, List<string> wanted)
    {
        int first = entries.FindIndex(IsGodotTool);
        List<string> kept = entries.FindAll(e => !IsGodotTool(e));
        int anchor = kept.IndexOf(FallbackAnchor);
        int at =
            first >= 0 ? first
            : anchor >= 0 ? anchor
            : kept.Count;
        kept.InsertRange(at, wanted);
        return kept;
    }

    private static List<string> ShortNames(IEnumerable<string> tools) =>
        [.. tools.Select(t => t[ToolPrefix.Length..]).Distinct().Order(StringComparer.Ordinal)];

    private static AgentFileSync Outcome(AgentFileState state, string text) => new(state, text, [], [], "");

    private static AgentFileSync Failure(string text, string error) => new(AgentFileState.Error, text, [], [], error);

    [GeneratedRegex(@"\A---[ \t]*\r?\n(?<body>.*?)^---[ \t]*\r?$", RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FrontMatterPattern();

    [GeneratedRegex(@"^tools:(?<list>[^\r\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ToolsLinePattern();

    [GeneratedRegex(
        @"^[ \t]*<!--[ \t]*godot-mcp tool classes:(?<classes>[^\r\n]*?)-->[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant
    )]
    private static partial Regex MarkerPattern();
}
