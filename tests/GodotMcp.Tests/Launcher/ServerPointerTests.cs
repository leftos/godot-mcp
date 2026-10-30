using GodotMcp.Launcher;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Launcher;

/// <summary>How the launcher reads the pointer file beside it and which server it names.</summary>
public sealed class ServerPointerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Pointer => Path.Combine(_temp.Path, "current");

    [Theory]
    [InlineData("A\n")]
    [InlineData("  A  \r\nB\r\n")]
    [InlineData("\uFEFFA")]
    public void AValidNameGivesThatVersionsServer(string content)
    {
        string server = WriteServer("A");
        File.WriteAllText(Pointer, content);

        Assert.Equal(server, ServerPointer.Resolve(_temp.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \nA\n")]
    public void ABlankFirstLineIsRefused(string content)
    {
        WriteServer("A");
        File.WriteAllText(Pointer, content);

        LauncherException refused = Assert.Throws<LauncherException>(() => ServerPointer.Resolve(_temp.Path));

        Assert.Equal($"{Pointer} names no version folder: its first line is blank.", refused.Message);
    }

    [Theory]
    [InlineData(@"A\B")]
    [InlineData("A/B")]
    [InlineData(@"..\A")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(@"C:\A")]
    public void ANameThatIsNotOneFolderIsRefused(string name)
    {
        WriteServer("A");
        File.WriteAllText(Pointer, name);

        LauncherException refused = Assert.Throws<LauncherException>(() => ServerPointer.Resolve(_temp.Path));

        Assert.Equal($"{Pointer} names '{name}', which is not a single folder name.", refused.Message);
    }

    [Fact]
    public void AVersionWithoutItsServerIsRefused()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "versions", "B"));
        File.WriteAllText(Pointer, "B");
        string missing = Path.Combine(_temp.Path, "versions", "B", "godot-mcp.exe");

        LauncherException refused = Assert.Throws<LauncherException>(() => ServerPointer.Resolve(_temp.Path));

        Assert.Equal($"{Pointer} names version 'B', but {missing} does not exist.", refused.Message);
    }

    [Fact]
    public void AMissingPointerIsRefused()
    {
        LauncherException refused = Assert.Throws<LauncherException>(() => ServerPointer.Resolve(_temp.Path));

        Assert.Equal($"{Pointer} does not exist.", refused.Message);
    }

    [Fact]
    public void APointerHeldOpenForWriteAndDeleteIsStillRead()
    {
        string server = WriteServer("A");
        File.WriteAllText(Pointer, "A");
        using FileStream writer = new(Pointer, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(server, ServerPointer.Resolve(_temp.Path));
    }

    private string WriteServer(string version)
    {
        string folder = Path.Combine(_temp.Path, "versions", version);
        Directory.CreateDirectory(folder);
        string server = Path.Combine(folder, "godot-mcp.exe");
        File.WriteAllText(server, "stand-in");
        return server;
    }
}
