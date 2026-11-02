namespace GodotMcp.Server.Session;

/// <summary>
/// The files an editor's open changed in a project, from the project's git status read before the open and after it: each
/// line of the later status the earlier one lacks, named by its path, with git's own Godot cache folder left out.
/// </summary>
internal static class EditorOpenChanges
{
    /// <summary>How many changed paths the warning names before it counts the rest.</summary>
    private const int Named = 10;

    /// <summary>The folder Godot's own open always rewrites, which is no change in the project's own files.</summary>
    private const string GodotCache = ".godot/";

    private const string Prefix = "The editor's open changed files in the project: ";
    private const string Suffix = ". Commit or revert them before committing other work.";

    /// <summary>
    /// The warning for what the open changed; null when it changed nothing, or when git answered for the folder on only one
    /// side (<paramref name="before"/> or <paramref name="after"/> null).
    /// </summary>
    /// <param name="before">The project's <c>git status --porcelain --untracked-files=all</c> before the open.</param>
    /// <param name="after">The same status after it.</param>
    public static string? Describe(string? before, string? after)
    {
        if (before is null || after is null)
        {
            return null;
        }

        HashSet<string> known = [.. Lines(before)];
        string[] changed =
        [
            .. Lines(after)
                .Where(line => !known.Contains(line))
                .Select(PathOf)
                .Where(path => !path.StartsWith(GodotCache, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];
        return changed.Length switch
        {
            0 => null,
            <= Named => Prefix + string.Join(", ", changed) + Suffix,
            _ => Prefix + string.Join(", ", changed.Take(Named)) + $" and {changed.Length - Named} more" + Suffix,
        };
    }

    /// <summary>The status's lines, in the order git printed them, without the empty one a trailing newline leaves.</summary>
    private static IEnumerable<string> Lines(string status) => status.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0);

    /// <summary>
    /// The path a porcelain v1 line names, as git prints it: the text after "XY ", and after the arrow of a rename, which
    /// names its new path.
    /// </summary>
    private static string PathOf(string line)
    {
        int arrow = line.LastIndexOf(" -> ", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            return line[(arrow + 4)..];
        }

        return line.Length > 3 ? line[3..] : line;
    }
}
