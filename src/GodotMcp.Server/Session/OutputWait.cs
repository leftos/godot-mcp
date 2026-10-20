using System.Text.RegularExpressions;

namespace GodotMcp.Server.Session;

/// <summary>
/// A line an output wait matched: the stream, its number in that stream, the raw line, its capture groups, and the drive time
/// the server received it (null when the page carried no times).
/// </summary>
internal readonly record struct OutputMatch(string Stream, long LineNumber, string Line, IReadOnlyList<string?> Groups, double? AtMs = null);

/// <summary>The line scan behind wait_for's output condition: lines numbered after a mark, stdout first when the stream is any.</summary>
internal static class OutputWait
{
    /// <summary>How often a waiting output wait looks at the captured lines again.</summary>
    internal static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    internal const string TimeoutError = "condition.output took over 1 s to match a line; simplify the pattern.";

    /// <summary>
    /// The first line above its stream's mark that <paramref name="regex"/> matches, or the error of a match that took over
    /// its one-second timeout. Lines are matched as printed, uncut.
    /// </summary>
    internal static (OutputMatch? Match, string? Error) Scan(
        OutputPage stdout,
        OutputPage stderr,
        string stream,
        Regex regex,
        long stdoutAfter,
        long stderrAfter
    )
    {
        try
        {
            if (stream is "any" or "stdout" && ScanPage(stdout, "stdout", regex, stdoutAfter) is { } outMatch)
            {
                return (outMatch, null);
            }

            if (stream is "any" or "stderr" && ScanPage(stderr, "stderr", regex, stderrAfter) is { } errMatch)
            {
                return (errMatch, null);
            }

            return (null, null);
        }
        catch (RegexMatchTimeoutException)
        {
            return (null, TimeoutError);
        }
    }

    private static OutputMatch? ScanPage(OutputPage page, string stream, Regex regex, long after)
    {
        if (page.FirstLine is not { } first)
        {
            return null;
        }

        for (int index = 0; index < page.Lines.Count; index++)
        {
            long number = first + index;
            if (number <= after)
            {
                continue;
            }

            Match match = regex.Match(page.Lines[index]);
            if (match.Success)
            {
                List<string?> groups = [];
                for (int group = 1; group < match.Groups.Count; group++)
                {
                    groups.Add(Captured(match.Groups[group]));
                }

                return new OutputMatch(stream, number, page.Lines[index], groups, ReceivedAt(page, index));
            }
        }

        return null;
    }

    /// <summary>The receipt time of <paramref name="page"/>'s line at <paramref name="index"/>, or null when it carries none.</summary>
    private static double? ReceivedAt(OutputPage page, int index) => page.Times is { } times && index < times.Count ? times[index] : null;

    private static string? Captured(Group group) => group.Success ? group.Value : null;
}
