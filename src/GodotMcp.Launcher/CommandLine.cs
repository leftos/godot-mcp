using System.Text;

namespace GodotMcp.Launcher;

/// <summary>The command line the server is started with.</summary>
internal static class CommandLine
{
    /// <summary>
    /// The file quoted, then each argument quoted by the MSVCRT rules <c>ProcessStartInfo.ArgumentList</c> uses, so the
    /// server's own parse gives the launcher's arguments back unchanged.
    /// </summary>
    public static string Build(string fileName, IEnumerable<string> arguments)
    {
        StringBuilder line = new();
        line.Append('"').Append(fileName).Append('"');
        foreach (string argument in arguments)
        {
            line.Append(' ');
            AppendArgument(line, argument);
        }

        return line.ToString();
    }

    private static void AppendArgument(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes before a quote are doubled and the quote escaped; anywhere else they stand as they are.
            line.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled, so they do not escape it.
        line.Append('\\', backslashes * 2).Append('"');
    }
}
