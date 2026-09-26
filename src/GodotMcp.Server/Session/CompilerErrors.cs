using System.Text.RegularExpressions;

namespace GodotMcp.Server.Session;

/// <summary>A failed build's errors, each as <c>file:line: CODE message</c>, at most <see cref="CompilerErrors.Limit"/>, and how many there were.</summary>
internal sealed record CompilerErrorList(IReadOnlyList<string> Errors, int Total);

/// <summary>Reads the errors out of an MSBuild console log.</summary>
internal static partial class CompilerErrors
{
    public const int Limit = 20;

    /// <summary>
    /// Every <c>file(line,col): error CODE: message [project]</c> line of the log, deduplicated (MSBuild repeats each error
    /// in its closing summary), in the order they first appear. An error without a position reads <c>file: CODE message</c>,
    /// and one without a file (<c>error MSB1009: ...</c>) reads <c>CODE message</c>.
    /// </summary>
    public static CompilerErrorList Parse(string log)
    {
        List<string> errors = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string line in log.Split('\n'))
        {
            Match match = ErrorLine().Match(line.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            string formatted = Format(match);
            if (seen.Add(formatted))
            {
                errors.Add(formatted);
            }
        }

        return new CompilerErrorList([.. errors.Take(Limit)], errors.Count);
    }

    private static string Format(Match match)
    {
        string what = $"{match.Groups["code"].Value} {match.Groups["message"].Value}";
        if (!match.Groups["file"].Success)
        {
            return what;
        }

        string where = match.Groups["line"].Success ? $"{match.Groups["file"].Value}:{match.Groups["line"].Value}" : match.Groups["file"].Value;
        return $"{where}: {what}";
    }

    [GeneratedRegex(
        @"^\s*(?:(?<file>.+?)(?:\((?<line>\d+)(?:,\d+)*\))?\s*:\s*)?error (?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?\s*$"
    )]
    private static partial Regex ErrorLine();
}
