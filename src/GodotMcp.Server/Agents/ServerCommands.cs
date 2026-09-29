using System.Text;
using System.Text.Json;

namespace GodotMcp.Server.Agents;

/// <summary>Where the command-line commands find the user's folders: the home folder and the value of the roots variable.</summary>
/// <param name="Home">The user's home folder.</param>
/// <param name="SweepRoots">The value of <see cref="AgentSweep.RootsVariable"/>, or null when it is not set.</param>
internal sealed record CommandEnvironment(string Home, string? SweepRoots);

/// <summary>
/// The server exe's commands, which print to stdout and exit without starting the MCP server: <c>--list-tools</c> prints
/// every tool with its class as JSON, and <c>--sweep-agents [--dry-run] [--root &lt;folder&gt;]...</c> runs
/// <see cref="AgentSweep"/>.
/// </summary>
internal static class ServerCommands
{
    public const string ListTools = "--list-tools";
    public const string SweepAgents = "--sweep-agents";

    private const string Usage = "usage: godot-mcp --list-tools | godot-mcp --sweep-agents [--dry-run] [--root <folder>]...";

    /// <summary>
    /// Runs the command <paramref name="args"/> names and returns its exit status: 0 on success, 1 when a sweep failed, 2
    /// for arguments it cannot read. Returns null, running nothing, when the first argument names no command.
    /// </summary>
    /// <param name="args">The exe's arguments.</param>
    /// <param name="output">Where the command's report goes.</param>
    /// <param name="error">Where a usage error goes.</param>
    /// <param name="environment">The user's folders.</param>
    public static int? Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, CommandEnvironment environment)
    {
        if (args.Count == 0)
        {
            return null;
        }

        return args[0] switch
        {
            ListTools when args.Count == 1 => PrintTools(output),
            ListTools => Refuse(error, $"{ListTools} takes no arguments."),
            SweepAgents => Sweep(args, output, error, environment),
            // An option the server does not know would otherwise start the MCP server on the terminal.
            string option when option.StartsWith("--", StringComparison.Ordinal) => Refuse(error, $"'{option}' is not a command."),
            _ => null,
        };
    }

    private static int PrintTools(TextWriter output)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach ((string name, string toolClass) in ToolClasses.ByTool)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("class", toolClass);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        output.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        return 0;
    }

    private static int Sweep(IReadOnlyList<string> args, TextWriter output, TextWriter error, CommandEnvironment environment)
    {
        bool dryRun = false;
        List<string> roots = [];
        for (int i = 1; i < args.Count; i++)
        {
            if (args[i] == "--dry-run")
            {
                dryRun = true;
            }
            else if (args[i] == "--root" && i + 1 < args.Count)
            {
                roots.Add(args[++i]);
            }
            else
            {
                return Refuse(error, $"{SweepAgents} does not take '{args[i]}'.");
            }
        }

        roots.AddRange((environment.SweepRoots ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return new AgentSweep(output).Run(new AgentSweepRequest(environment.Home, roots, dryRun, ToolClasses.ByTool));
    }

    private static int Refuse(TextWriter error, string message)
    {
        error.WriteLine($"godot-mcp: {message} {Usage}");
        return 2;
    }
}
