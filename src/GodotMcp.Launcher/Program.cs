namespace GodotMcp.Launcher;

/// <summary>
/// Starts the server the pointer file beside this exe names, with this process's arguments and standard handles, and
/// exits with its exit code. It never writes to stdout or reads stdin: the server speaks the protocol on them directly.
/// </summary>
internal static class Program
{
    /// <summary>The variable the server reads to name the launcher, not its version folder, in what it tells the user.</summary>
    public const string LauncherVariable = "GODOT_MCP_LAUNCHER";

    private static int Main(string[] args)
    {
        try
        {
            string launcher = Environment.ProcessPath ?? throw new LauncherException("its own executable's path could not be read.");
            string server = ServerPointer.Resolve(Path.GetDirectoryName(launcher)!);
            Environment.SetEnvironmentVariable(LauncherVariable, launcher);
            return ServerProcess.Run(server, CommandLine.Build(server, args));
        }
        catch (LauncherException refused)
        {
            Console.Error.WriteLine($"godot-mcp launcher: {refused.Message}");
            return 1;
        }
    }
}
