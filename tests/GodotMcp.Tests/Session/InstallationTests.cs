using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

public sealed class InstallationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void FindsTheDotnetExtensionBesideTheServerThenInTheCheckout()
    {
        string checkout = CreateCheckout();
        string server = CreateServerDirectory(checkout);
        string inCheckout = WriteFile(checkout, "bin", "dotnet", Installation.DotnetExtensionFileName);

        Assert.Equal(inCheckout, Installation.FindDotnetExtension(server));

        string besideServer = WriteFile(server, "dotnet", Installation.DotnetExtensionFileName);

        Assert.Equal(besideServer, Installation.FindDotnetExtension(server));
    }

    [Fact]
    public void FindsNoDotnetExtensionWhenNeitherPlaceHasIt()
    {
        string checkout = CreateCheckout();
        string server = CreateServerDirectory(checkout);
        WriteFile(checkout, "dotnet", Installation.DotnetExtensionFileName);
        WriteFile(_temp.Path, "bin", "dotnet", Installation.DotnetExtensionFileName);

        Assert.Null(Installation.FindDotnetExtension(server));
    }

    private const string ServerExe = @"C:\opt\godot-mcp\godot-mcp.exe";

    [Fact]
    public void GodotPathVariableWinsOverAPathMatch()
    {
        string configured = WriteFile(_temp.Path, "configured", "Godot_console.exe");
        string folder = TempFolder("onpath");
        WriteFile(folder, "Godot_v4.7.2-stable_win64_console.exe");

        Assert.Equal(configured, Installation.FindGodot(configured, folder, ServerExe));
    }

    [Fact]
    public void AGodotPathNamingNoFileIsRefusedWithItsText()
    {
        string missing = _temp.Combine("gone", "Godot_console.exe");
        string folder = TempFolder("onpath");
        WriteFile(folder, "Godot_v4.7.2-stable_win64_console.exe");

        SessionException refused = Assert.Throws<SessionException>(() => Installation.FindGodot(missing, folder, ServerExe));

        Assert.Equal($"GODOT_PATH is '{missing}', which does not exist. Point it at the Godot 4.7 console executable.", refused.Message);
    }

    [Fact]
    public void ABlankGodotPathFallsThroughToPath()
    {
        string folder = TempFolder("onpath");
        string match = WriteFile(folder, "Godot_v4.7.2-stable_win64_console.exe");

        Assert.Equal(match, Installation.FindGodot("  ", folder, ServerExe));
    }

    [Fact]
    public void TheFirstPathFolderWithAMatchWins()
    {
        string first = TempFolder("first");
        string firstMatch = WriteFile(first, "Godot_v4.6.1-stable_win64_console.exe");
        string second = TempFolder("second");
        WriteFile(second, "Godot_v4.7.2-stable_win64_console.exe");
        string path = string.Join(Path.PathSeparator, _temp.Combine("gone"), first, second);

        Assert.Equal(firstMatch, Installation.FindGodot(null, path, ServerExe));
    }

    [Fact]
    public void TheLastNameInAFolderWins()
    {
        string folder = TempFolder("builds");
        WriteFile(folder, "Godot_v4.6.1-stable_win64_console.exe");
        string newest = WriteFile(folder, "Godot_v4.7.2-stable_win64_console.exe");

        Assert.Equal(newest, Installation.FindGodot(null, folder, ServerExe));
    }

    [Fact]
    public void AMatchIgnoresTheNamesCasing()
    {
        string folder = TempFolder("lowercase");
        string match = WriteFile(folder, "godot_console.EXE");

        Assert.Equal(match, Installation.FindGodot(null, folder, ServerExe));
    }

    [Fact]
    public void ANonConsoleBuildAloneDoesNotMatch()
    {
        string folder = TempFolder("editor");
        WriteFile(folder, "Godot_v4.7.2-stable_win64.exe");

        SessionException refused = Assert.Throws<SessionException>(() => Installation.FindGodot(null, folder, ServerExe));

        Assert.Contains("no Godot*console*.exe is on PATH", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingFoundIsRefusedWithTheRegisterLine()
    {
        SessionException refused = Assert.Throws<SessionException>(() => Installation.FindGodot(null, TempFolder("empty"), ServerExe));

        Assert.Equal(
            "Godot was not found: GODOT_PATH is not set and no Godot*console*.exe is on PATH. Set GODOT_PATH to the Godot 4.7 "
                + "console executable; with Claude Code, register this server with: claude mcp add godot -s local -e "
                + $"GODOT_PATH=<path to the Godot console exe> -- \"{ServerExe}\"",
            refused.Message
        );
    }

    private string TempFolder(string name)
    {
        string folder = _temp.Combine(name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private string CreateCheckout()
    {
        string checkout = _temp.Combine("checkout");
        WriteFile(checkout, Installation.SolutionFileName);
        return checkout;
    }

    private static string CreateServerDirectory(string checkout)
    {
        string server = Path.Combine(checkout, "src", "GodotMcp.Server", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(server);
        return server;
    }

    private static string WriteFile(string directory, params string[] parts)
    {
        string path = Path.Combine([directory, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
