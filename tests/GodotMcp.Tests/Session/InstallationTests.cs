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
