namespace GodotMcp.TestSupport;

/// <summary>Paths inside the godot-mcp checkout the tests run from.</summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string InputProbe => Path.Combine(Root, "tests", "fixtures", "InputProbe");

    public static string BridgeScript => Path.Combine(Root, "bridge", "godot_mcp_bridge.gd");

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GodotMcp.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No GodotMcp.slnx above {AppContext.BaseDirectory}; the tests must run from a godot-mcp checkout.");
    }
}
