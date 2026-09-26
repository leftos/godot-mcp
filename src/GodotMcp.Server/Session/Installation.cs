namespace GodotMcp.Server.Session;

/// <summary>Finds the Godot executable and the bridge script a run needs.</summary>
internal static class Installation
{
    public const string GodotPathVariable = "GODOT_PATH";
    public const string DefaultGodotPath = @"F:\Godot\Godot_console.exe";
    public const string SolutionFileName = "GodotMcp.slnx";

    public static readonly string BridgeRelativePath = Path.Combine("bridge", "godot_mcp_bridge.gd");

    /// <summary><c>GODOT_PATH</c> when set, else <see cref="DefaultGodotPath"/>.</summary>
    /// <exception cref="SessionException">Neither names an existing file.</exception>
    public static string FindGodot()
    {
        string? configured = Environment.GetEnvironmentVariable(GodotPathVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? configured
                : throw new SessionException(
                    $"{GodotPathVariable} is '{configured}', which does not exist. Point it at the Godot 4.7 console executable."
                );
        }

        return File.Exists(DefaultGodotPath)
            ? DefaultGodotPath
            : throw new SessionException(
                $"Godot was not found: {GodotPathVariable} is not set and {DefaultGodotPath} does not exist. "
                    + $"Set {GodotPathVariable} to the Godot 4.7 console executable."
            );
    }

    public static string FindBridgeScript() => FindBridgeScript(AppContext.BaseDirectory);

    /// <summary>
    /// The bridge published beside the server (<c>bridge/</c> next to the exe), else the <c>bridge/</c> of the
    /// godot-mcp checkout the server was built in.
    /// </summary>
    /// <exception cref="SessionException">Neither exists.</exception>
    public static string FindBridgeScript(string serverDirectory)
    {
        string besideServer = Path.Combine(serverDirectory, BridgeRelativePath);
        if (File.Exists(besideServer))
        {
            return besideServer;
        }

        for (DirectoryInfo? directory = new(serverDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, BridgeRelativePath);
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new SessionException(
            $"The bridge script was not found beside the server ({besideServer}) or in a godot-mcp checkout above it. "
                + "Reinstall the server with 'pwsh run.ps1 publish'."
        );
    }
}
