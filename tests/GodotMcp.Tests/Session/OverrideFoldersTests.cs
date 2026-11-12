using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The file the servers' shared override-folders list is read from: an environment variable chooses it, else LocalAppData.</summary>
public sealed class OverrideFoldersTests
{
    private static readonly string LocalList = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "godot-mcp-cache",
        "override-folders.txt"
    );

    [Fact]
    public void TheEnvironmentVariableChoosesTheListPath()
    {
        string chosen = Path.Combine(Path.GetTempPath(), "owner-chosen-override-folders.txt");

        Assert.Equal(chosen, OverrideFolders.ResolvePath(name => name == OverrideFolders.PathVariable ? chosen : null));
        Assert.Equal(LocalList, OverrideFolders.ResolvePath(_ => null));
        Assert.Equal(LocalList, OverrideFolders.ResolvePath(_ => "  "));
    }
}
