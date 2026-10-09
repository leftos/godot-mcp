using System.Diagnostics;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// save_screenshot's path rules, checked before any capture is taken, and the copy that writes the capture to its path;
/// no Godot runs here.
/// </summary>
public sealed class ScreenshotPathValidationTests : IDisposable
{
    private static readonly byte[] PngBytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
    private readonly TempDirectory _temp = new();

    private string Project => _temp.Path;

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("shot.png", "shot.png")]
    [InlineData("docs/screenshots/showcase/class.png", "docs/screenshots/showcase/class.png")]
    [InlineData("docs\\screenshots\\class.png", "docs/screenshots/class.png")]
    [InlineData("docs/../shot.png", "shot.png")]
    [InlineData("res://docs/class.png", "docs/class.png")]
    [InlineData("Shot.PNG", "Shot.PNG")]
    public void AProjectRelativePathResolvesUnderTheProject(string path, string expected) =>
        Assert.Equal(Path.Combine([Project, .. expected.Split('/')]), ScreenshotTarget.Resolve(Project, path, overwrite: false));

    [Fact]
    public void AnAbsolutePathInsideTheProjectIsAccepted()
    {
        string absolute = Path.Combine(Project, "docs", "shot.png");

        Assert.Equal(absolute, ScreenshotTarget.Resolve(Project, absolute, overwrite: false));
    }

    [Fact]
    public void AnAbsolutePathInsideTheProjectInAnotherCaseIsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows paths are case-insensitive");
        string absolute = Path.Combine(Project.ToUpperInvariant(), "docs", "shot.png");

        Assert.Equal(absolute, ScreenshotTarget.Resolve(Project, absolute, overwrite: false));
    }

    [Theory]
    [InlineData("../shot.png")]
    [InlineData("docs/../../shot.png")]
    [InlineData("res://../shot.png")]
    [InlineData(@"\\server\share\shot.png")]
    public void ARelativePathLeavingTheProjectIsRefused(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(Project, path, overwrite: false));

        Assert.Equal($"path {path} resolves outside the project {Project}; save inside the project", refused.Message);
    }

    [Fact]
    public void AnAbsolutePathOutsideTheProjectIsRefused()
    {
        string outside = Path.Combine(Path.GetDirectoryName(Project)!, "elsewhere", "shot.png");

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(Project, outside, overwrite: false));

        Assert.Equal($"path {outside} resolves outside the project {Project}; save inside the project", refused.Message);
    }

    [Fact]
    public void AnExtendedLengthPathIsRefusedAsOutside()
    {
        string extended = @"\\?\" + Path.Combine(Project, "shot.png");

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(Project, extended, overwrite: false));

        Assert.Equal($"path {extended} resolves outside the project {Project}; save inside the project", refused.Message);
    }

    [Theory]
    [InlineData(".godot/shot.png")]
    [InlineData(".godot/godot-mcp/screenshots/shot.png")]
    [InlineData(".GODOT/shot.png")]
    [InlineData("docs/../.godot/shot.png")]
    [InlineData(".godot./shot.png")]
    public void APathUnderDotGodotIsRefused(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(Project, path, overwrite: false));

        Assert.Equal($"path {path} is under .godot/, which the editor owns; pick a tracked folder", refused.Message);
    }

    [Theory]
    [InlineData("README.md:x.png")]
    [InlineData(".godot:x.png")]
    [InlineData("docs/shot.png:x.png")]
    public void APathNamingAStreamIsRefused(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(Project, path, overwrite: false));

        Assert.Equal($"path must not contain ':' after the drive: {path}", refused.Message);
    }

    [Theory]
    [InlineData("GODOT~1/shot.png")]
    [InlineData("docs/SCREEN~2/shot.png")]
    public void APathWithAShortNameIsRefused(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(Project, path, overwrite: false));

        Assert.Equal($"path {path} uses a short (8.3) name; use the folder's full name", refused.Message);
    }

    [Fact]
    public void AJunctionLeadingOutsideTheProjectIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        using TempDirectory outside = new();
        MakeJunction(Path.Combine(Project, "addons"), outside.Path);

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(Project, "addons/shot.png", overwrite: false));

        Assert.Equal($"path addons/shot.png leads through a link to {outside.Path}, outside the project; save inside the project", refused.Message);
    }

    [Fact]
    public void AJunctionLeadingUnderDotGodotIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string screenshots = Directory.CreateDirectory(Path.Combine(Project, ".godot", "godot-mcp")).FullName;
        MakeJunction(Path.Combine(Project, "shots"), screenshots);

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(Project, "shots/shot.png", overwrite: false));

        Assert.Equal(
            $"path shots/shot.png leads through a link to {screenshots}, under .godot/, which the editor owns; pick a tracked folder",
            refused.Message
        );
    }

    [Fact]
    public void AJunctionLeadingInsideTheProjectIsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string docs = Directory.CreateDirectory(Path.Combine(Project, "docs")).FullName;
        MakeJunction(Path.Combine(Project, "shots"), docs);

        Assert.Equal(Path.Combine(Project, "shots", "shot.png"), ScreenshotTarget.Resolve(Project, "shots/shot.png", overwrite: false));
    }

    [Fact]
    public void AnExistingFileThatLinksOutsideIsRefusedEvenWithOverwrite()
    {
        using TempDirectory outside = new();
        string target = Path.Combine(outside.Path, "elsewhere.png");
        File.WriteAllBytes(target, PngBytes);
        try
        {
            File.CreateSymbolicLink(Path.Combine(Project, "shot.png"), target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"creating a file symbolic link needs a privilege this run lacks: {e.Message}");
        }

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(Project, "shot.png", overwrite: true));

        Assert.Equal($"path shot.png leads through a link to {target}, outside the project; save inside the project", refused.Message);
    }

    [Fact]
    public void AFolderMerelyNamedLikeDotGodotIsAccepted()
    {
        string expected = Path.Combine(Project, ".godot-shots", "shot.png");

        Assert.Equal(expected, ScreenshotTarget.Resolve(Project, ".godot-shots/shot.png", overwrite: false));
    }

    [Theory]
    [InlineData("shot.jpg")]
    [InlineData("shot")]
    [InlineData("shot.png.txt")]
    [InlineData("")]
    [InlineData("docs/")]
    public void APathNotEndingInPngIsRefused(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(Project, path, overwrite: false));

        Assert.Equal($"path must end in .png: {path}", refused.Message);
    }

    [Fact]
    public void AnExistingFileIsRefusedWithoutOverwriteAndAcceptedWithIt()
    {
        string existing = Path.Combine(Project, "shot.png");
        File.WriteAllBytes(existing, PngBytes);

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(Project, "shot.png", overwrite: false));

        Assert.Equal("shot.png exists; pass options.overwrite true to replace it", refused.Message);
        Assert.Equal(existing, ScreenshotTarget.Resolve(Project, "shot.png", overwrite: true));
    }

    [Fact]
    public void SavingCreatesTheMissingFoldersAndCopiesTheCapture()
    {
        string capture = WriteCapture();
        string destination = Path.Combine(Project, "docs", "screenshots", "showcase", "class.png");

        ScreenshotTarget.Save(capture, destination, overwrite: false);

        Assert.Equal(PngBytes, File.ReadAllBytes(destination));
        Assert.Equal(PngBytes, File.ReadAllBytes(capture));
    }

    [Fact]
    public void SavingWithOverwriteReplacesTheFile()
    {
        string capture = WriteCapture();
        string destination = Path.Combine(Project, "shot.png");
        File.WriteAllBytes(destination, [1, 2]);

        ScreenshotTarget.Save(capture, destination, overwrite: true);

        Assert.Equal(PngBytes, File.ReadAllBytes(destination));
    }

    [Fact]
    public void SavingOverAFileThatAppearedSinceTheCheckIsRefused()
    {
        string capture = WriteCapture();
        string destination = Path.Combine(Project, "shot.png");
        File.WriteAllBytes(destination, [1, 2]);

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Save(capture, destination, overwrite: false));

        Assert.StartsWith($"saving the screenshot to {destination} failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2], File.ReadAllBytes(destination));
    }

    // A junction needs no privilege, unlike a directory symbolic link, and .NET has no API that makes one.
    private static void MakeJunction(string link, string target)
    {
        ProcessStartInfo start = new("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using Process mklink = Process.Start(start)!;
        mklink.StandardInput.Close();
        string output = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0, $"mklink /J {link} {target} failed: {output}");
    }

    private string WriteCapture()
    {
        string folder = Path.Combine(Project, ".godot", "godot-mcp", "screenshots");
        Directory.CreateDirectory(folder);
        string capture = Path.Combine(folder, "capture.png");
        File.WriteAllBytes(capture, PngBytes);
        return capture;
    }
}
