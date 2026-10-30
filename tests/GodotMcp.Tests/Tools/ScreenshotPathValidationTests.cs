using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly TempDirectory _main = new();

    private string Project => _temp.Path;

    public void Dispose()
    {
        _temp.Dispose();
        _main.Dispose();
    }

    [Theory]
    [InlineData("shot.png", "shot.png")]
    [InlineData("docs/screenshots/showcase/class.png", "docs/screenshots/showcase/class.png")]
    [InlineData("docs\\screenshots\\class.png", "docs/screenshots/class.png")]
    [InlineData("docs/../shot.png", "shot.png")]
    [InlineData("res://docs/class.png", "docs/class.png")]
    [InlineData("Shot.PNG", "Shot.PNG")]
    public void AProjectRelativePathResolvesUnderTheProject(string path, string expected) =>
        Assert.Equal(Path.Combine([Project, .. expected.Split('/')]), Resolve(Project, path, overwrite: false));

    [Fact]
    public void AnAbsolutePathInsideTheProjectIsAccepted()
    {
        string absolute = Path.Combine(Project, "docs", "shot.png");

        Assert.Equal(absolute, Resolve(Project, absolute, overwrite: false));
    }

    [Fact]
    public void AnAbsolutePathInsideTheProjectInAnotherCaseIsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows paths are case-insensitive");
        string absolute = Path.Combine(Project.ToUpperInvariant(), "docs", "shot.png");

        Assert.Equal(absolute, Resolve(Project, absolute, overwrite: false));
    }

    [Theory]
    [InlineData("../shot.png")]
    [InlineData("docs/../../shot.png")]
    [InlineData("res://../shot.png")]
    [InlineData(@"\\server\share\shot.png")]
    public void ARelativePathLeavingTheProjectIsRefused(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => Resolve(Project, path, overwrite: false));

        Assert.Equal($"path {path} resolves outside the project {Project}; save inside the project", refused.Message);
    }

    [Fact]
    public void AnAbsolutePathOutsideTheProjectIsRefused()
    {
        string outside = Path.Combine(Path.GetDirectoryName(Project)!, "elsewhere", "shot.png");

        McpException refused = Assert.Throws<McpException>(() => Resolve(Project, outside, overwrite: false));

        Assert.Equal($"path {outside} resolves outside the project {Project}; save inside the project", refused.Message);
    }

    [Fact]
    public void AnExtendedLengthPathIsRefusedAsOutside()
    {
        string extended = @"\\?\" + Path.Combine(Project, "shot.png");

        McpException refused = Assert.Throws<McpException>(() => Resolve(Project, extended, overwrite: false));

        Assert.Equal($"path {extended} resolves outside the project {Project}; save inside the project", refused.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnAbsolutePathInTheCheckoutsTmpFolderIsAccepted(bool dotGitIsFile)
    {
        string project = ProjectUnderACheckout(dotGitIsFile);
        string tmp = Path.Combine(CheckoutTmp, "dim", "dimmed.png");

        Assert.Equal(tmp, Resolve(project, tmp, overwrite: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AProjectRelativePathIntoTheCheckoutsTmpFolderIsAccepted(bool dotGitIsFile)
    {
        string project = ProjectUnderACheckout(dotGitIsFile);
        string tmp = Path.Combine(CheckoutTmp, "dim", "dimmed.png");

        Assert.Equal(tmp, Resolve(project, "../../.tmp/dim/dimmed.png", overwrite: false));
    }

    [Fact]
    public void AnAbsolutePathOutsideTheProjectAndTheCheckoutsTmpFolderIsRefused()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string elsewhere = Path.Combine(_temp.Path, "elsewhere", "shot.png");

        McpException refused = Assert.Throws<McpException>(() => Resolve(project, elsewhere, overwrite: false));

        Assert.Equal($"path {elsewhere} resolves outside {WorktreePlaces(project)}", refused.Message);
    }

    [Fact]
    public void AResPathLeavingTheProjectStaysRefusedUnderACheckout()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);

        McpException refused = Assert.Throws<McpException>(() => Resolve(project, "res://../../.tmp/x.png", overwrite: false));

        Assert.Equal($"path res://../../.tmp/x.png resolves outside the project {project}; save inside the project", refused.Message);
    }

    [Fact]
    public void APathNamingAStreamUnderTheCheckoutsTmpFolderIsRefused()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);

        McpException refused = Assert.Throws<McpException>(() => Resolve(project, "../../.tmp/a.txt:x.png", overwrite: false));

        Assert.Equal("path must not contain ':' after the drive: ../../.tmp/a.txt:x.png", refused.Message);
    }

    [Fact]
    public void AnExistingFileInTheCheckoutsTmpFolderIsRefusedWithoutOverwriteAndAcceptedWithIt()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string existing = Path.Combine(CheckoutTmp, "dim", "dimmed.png");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllBytes(existing, PngBytes);

        McpException refused = Assert.Throws<McpException>(() => Resolve(project, existing, overwrite: false));

        Assert.Equal($"{existing} exists; pass options.overwrite true to replace it", refused.Message);
        Assert.Equal(existing, Resolve(project, existing, overwrite: true));
    }

    [Fact]
    public void AJunctionUnderTheCheckoutsTmpFolderLeadingOutsideBothIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string elsewhere = Directory.CreateDirectory(Path.Combine(_temp.Path, "elsewhere")).FullName;
        Directory.CreateDirectory(CheckoutTmp);
        MakeJunction(Path.Combine(CheckoutTmp, "out"), elsewhere);

        McpException refused = Assert.Throws<McpException>(() => Resolve(project, "../../.tmp/out/shot.png", overwrite: false));

        Assert.Equal($"path ../../.tmp/out/shot.png leads through a link to {elsewhere}, outside {WorktreePlaces(project)}", refused.Message);
    }

    [Fact]
    public void AJunctionUnderTheCheckoutsTmpFolderLeadingInsideTheProjectIsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string docs = Directory.CreateDirectory(Path.Combine(project, "docs")).FullName;
        Directory.CreateDirectory(CheckoutTmp);
        MakeJunction(Path.Combine(CheckoutTmp, "docs"), docs);

        Assert.Equal(Path.Combine(CheckoutTmp, "docs", "shot.png"), Resolve(project, "../../.tmp/docs/shot.png", overwrite: false));
    }

    [Fact]
    public void AJunctionTmpFolderLeadingOutsideBothIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string elsewhere = Directory.CreateDirectory(Path.Combine(_temp.Path, "elsewhere")).FullName;
        MakeJunction(CheckoutTmp, elsewhere);

        McpException refused = Assert.Throws<McpException>(() => Resolve(project, "../../.tmp/x.png", overwrite: false));

        Assert.Equal($"path ../../.tmp/x.png leads through a link to {elsewhere}, outside {WorktreePlaces(project)}", refused.Message);
    }

    [Fact]
    public void AJunctionUnderTmpToTheProjectsDotGodotIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string dotGodot = Directory.CreateDirectory(Path.Combine(project, ".godot")).FullName;
        Directory.CreateDirectory(CheckoutTmp);
        MakeJunction(Path.Combine(CheckoutTmp, "g"), dotGodot);

        McpException refused = Assert.Throws<McpException>(() => Resolve(project, "../../.tmp/g/x.png", overwrite: false));

        Assert.Equal(
            $"path ../../.tmp/g/x.png leads through a link to {dotGodot}, under .godot/, which the editor owns; pick a tracked folder",
            refused.Message
        );
    }

    [Fact]
    public void AJunctionUnderTmpToTheProjectRefusesAPathIntoDotGodot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        Directory.CreateDirectory(CheckoutTmp);
        MakeJunction(Path.Combine(CheckoutTmp, "p"), project);

        McpException refused = Assert.Throws<McpException>(() => Resolve(project, "../../.tmp/p/.godot/x.png", overwrite: false));

        Assert.Equal(
            $"path ../../.tmp/p/.godot/x.png leads through a link to {Path.Combine(project, ".godot")}, under .godot/, "
                + "which the editor owns; pick a tracked folder",
            refused.Message
        );
    }

    [Fact]
    public void AJunctionUnderTmpToTheProjectAcceptsAPathInsideTheProject()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        Directory.CreateDirectory(Path.Combine(project, "docs"));
        Directory.CreateDirectory(CheckoutTmp);
        MakeJunction(Path.Combine(CheckoutTmp, "p"), project);

        Assert.Equal(Path.Combine(CheckoutTmp, "p", "docs", "x.png"), Resolve(project, "../../.tmp/p/docs/x.png", overwrite: false));
    }

    [Fact]
    public void AJunctionUnderTmpLeadingInsideTmpIsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string real = Directory.CreateDirectory(Path.Combine(CheckoutTmp, "real")).FullName;
        MakeJunction(Path.Combine(CheckoutTmp, "inner"), real);

        Assert.Equal(Path.Combine(CheckoutTmp, "inner", "x.png"), Resolve(project, "../../.tmp/inner/x.png", overwrite: false));
    }

    [Fact]
    public void TheNearestGitEntryAboveTheProjectFixesTheTmpFolder()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: false);
        string inner = Path.Combine(_temp.Path, "src");
        Git.Run(inner, "init", "--quiet", "--initial-branch=main");
        string innerTmp = Path.Combine(inner, ".tmp");

        Assert.Equal(Path.Combine(innerTmp, "x.png"), Resolve(project, "../.tmp/x.png", overwrite: false));

        string outer = Path.Combine(CheckoutTmp, "x.png");
        McpException refused = Assert.Throws<McpException>(() => Resolve(project, outer, overwrite: false));

        Assert.Equal($"path {outer} resolves outside {UntrackedPlaces(project, inner, main: null)}", refused.Message);
    }

    [Fact]
    public void APathWithAShortNameOnTheCheckoutsTmpRouteIsRefused()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        const string path = "../../.tmp/SCREEN~2/shot.png";

        McpException refused = Assert.Throws<McpException>(() => Resolve(project, path, overwrite: false));

        Assert.Equal($"path {path} uses a short (8.3) name; use the folder's full name", refused.Message);
    }

    [Fact]
    public void ADotGodotFolderInsideTheCheckoutsTmpIsAccepted()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);

        Assert.Equal(Path.Combine(CheckoutTmp, ".godot", "x.png"), Resolve(project, "../../.tmp/.godot/x.png", overwrite: false));
    }

    [Theory]
    [InlineData(".godot/shot.png")]
    [InlineData(".godot/godot-mcp/screenshots/shot.png")]
    [InlineData(".GODOT/shot.png")]
    [InlineData("docs/../.godot/shot.png")]
    [InlineData(".godot./shot.png")]
    public void APathUnderDotGodotIsRefused(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => Resolve(Project, path, overwrite: false));

        Assert.Equal($"path {path} is under .godot/, which the editor owns; pick a tracked folder", refused.Message);
    }

    [Theory]
    [InlineData("README.md:x.png")]
    [InlineData(".godot:x.png")]
    [InlineData("docs/shot.png:x.png")]
    public void APathNamingAStreamIsRefused(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => Resolve(Project, path, overwrite: false));

        Assert.Equal($"path must not contain ':' after the drive: {path}", refused.Message);
    }

    [Theory]
    [InlineData("GODOT~1/shot.png")]
    [InlineData("docs/SCREEN~2/shot.png")]
    public void APathWithAShortNameIsRefused(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => Resolve(Project, path, overwrite: false));

        Assert.Equal($"path {path} uses a short (8.3) name; use the folder's full name", refused.Message);
    }

    [Fact]
    public void AJunctionLeadingOutsideTheProjectIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        using TempDirectory outside = new();
        MakeJunction(Path.Combine(Project, "addons"), outside.Path);

        McpException refused = Assert.Throws<McpException>(() => Resolve(Project, "addons/shot.png", overwrite: false));

        Assert.Equal($"path addons/shot.png leads through a link to {outside.Path}, outside the project; save inside the project", refused.Message);
    }

    [Fact]
    public void AJunctionLeadingUnderDotGodotIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string screenshots = Directory.CreateDirectory(Path.Combine(Project, ".godot", "godot-mcp")).FullName;
        MakeJunction(Path.Combine(Project, "shots"), screenshots);

        McpException refused = Assert.Throws<McpException>(() => Resolve(Project, "shots/shot.png", overwrite: false));

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

        Assert.Equal(Path.Combine(Project, "shots", "shot.png"), Resolve(Project, "shots/shot.png", overwrite: false));
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

        McpException refused = Assert.Throws<McpException>(() => Resolve(Project, "shot.png", overwrite: true));

        Assert.Equal($"path shot.png leads through a link to {target}, outside the project; save inside the project", refused.Message);
    }

    [Fact]
    public void AFolderMerelyNamedLikeDotGodotIsAccepted()
    {
        string expected = Path.Combine(Project, ".godot-shots", "shot.png");

        Assert.Equal(expected, Resolve(Project, ".godot-shots/shot.png", overwrite: false));
    }

    [Theory]
    [InlineData("shot.jpg")]
    [InlineData("shot")]
    [InlineData("shot.png.txt")]
    [InlineData("")]
    [InlineData("docs/")]
    public void APathNotEndingInPngIsRefused(string path)
    {
        McpException refused = Assert.Throws<McpException>(() => Resolve(Project, path, overwrite: false));

        Assert.Equal($"path must end in .png: {path}", refused.Message);
    }

    [Fact]
    public void AnExistingFileIsRefusedWithoutOverwriteAndAcceptedWithIt()
    {
        string existing = Path.Combine(Project, "shot.png");
        File.WriteAllBytes(existing, PngBytes);

        McpException refused = Assert.Throws<McpException>(() => Resolve(Project, "shot.png", overwrite: false));

        Assert.Equal("shot.png exists; pass options.overwrite true to replace it", refused.Message);
        Assert.Equal(existing, Resolve(Project, "shot.png", overwrite: true));
    }

    [Fact]
    public void SavingCreatesTheMissingFoldersAndCopiesTheCapture()
    {
        string capture = WriteCapture();
        string destination = Path.Combine(Project, "docs", "screenshots", "showcase", "class.png");

        ScreenshotTarget.Save(capture, Destination(Project, destination, overwrite: false), overwrite: false);

        Assert.Equal(PngBytes, File.ReadAllBytes(destination));
        Assert.Equal(PngBytes, File.ReadAllBytes(capture));
    }

    [Fact]
    public void SavingWithOverwriteReplacesTheFile()
    {
        string capture = WriteCapture();
        string destination = Path.Combine(Project, "shot.png");
        File.WriteAllBytes(destination, [1, 2]);

        ScreenshotTarget.Save(capture, Destination(Project, destination, overwrite: true), overwrite: true);

        Assert.Equal(PngBytes, File.ReadAllBytes(destination));
    }

    [Fact]
    public void SavingOverAFileThatAppearedSinceTheCheckIsRefused()
    {
        string capture = WriteCapture();
        string destination = Path.Combine(Project, "shot.png");
        OutputTarget target = Destination(Project, destination, overwrite: false);
        File.WriteAllBytes(destination, [1, 2]);

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Save(capture, target, overwrite: false));

        Assert.StartsWith($"saving the screenshot to {destination} failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2], File.ReadAllBytes(destination));
    }

    [Theory]
    [InlineData("take_screenshot")]
    [InlineData("wait_for")]
    [InlineData("capture_frames")]
    public void AFrameToolsPathOutsideATmpFolderIsRefused(string tool)
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);

        McpException refused = Assert.Throws<McpException>(() => InTmp(project, "docs/shot.png", overwrite: false, tool));

        Assert.Equal(TmpOnlyRefusal(tool), refused.Message);
    }

    [Fact]
    public void AFrameToolsPathInTheCheckoutsTmpFolderIsAccepted()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);

        Assert.Equal(Path.Combine(CheckoutTmp, "shots", "a.png"), InTmp(project, "../../.tmp/shots/a.png", overwrite: false, "take_screenshot"));
    }

    [Fact]
    public void AFrameToolsPathInTheMainCheckoutsTmpFolderIsAccepted()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string main = Path.Combine(_main.Path, ".tmp", "a.png");

        Assert.Equal(main, InTmp(project, main, overwrite: false, "wait_for"));
    }

    [Fact]
    public void AFrameToolsPathInATrackedCheckoutOutsideItsTmpFolderIsRefused()
    {
        Git.Run(_temp.Path, "init", "--quiet", "--initial-branch=main");
        File.WriteAllText(Path.Combine(_temp.Path, "project.godot"), "");
        Git.Run(_temp.Path, "add", "project.godot");

        McpException refused = Assert.Throws<McpException>(() => InTmp(_temp.Path, "shot.png", overwrite: false, "take_screenshot"));

        Assert.Equal(TmpOnlyRefusal("take_screenshot"), refused.Message);
        Assert.Equal(Path.Combine(_temp.Path, ".tmp", "shot.png"), InTmp(_temp.Path, ".tmp/shot.png", overwrite: false, "take_screenshot"));
    }

    [Fact]
    public void AnExistingFileOutsideATmpFolderGetsTheTmpRefusal()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        File.WriteAllBytes(Path.Combine(project, "shot.png"), PngBytes);

        McpException refused = Assert.Throws<McpException>(() => InTmp(project, "shot.png", overwrite: false, "take_screenshot"));

        Assert.Equal(TmpOnlyRefusal("take_screenshot"), refused.Message);
    }

    [Fact]
    public void AnExistingFileInTheTmpFolderIsRefusedWithoutOverwriteOnAFrameTool()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string existing = Path.Combine(CheckoutTmp, "a.png");
        Directory.CreateDirectory(CheckoutTmp);
        File.WriteAllBytes(existing, PngBytes);

        McpException refused = Assert.Throws<McpException>(() => InTmp(project, existing, overwrite: false, "wait_for"));

        Assert.Equal($"{existing} exists; pass options.overwrite true to replace it", refused.Message);
        Assert.Equal(existing, InTmp(project, existing, overwrite: true, "wait_for"));
    }

    [Fact]
    public void AJunctionUnderTmpIntoTheProjectIsRefusedOnAFrameTool()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are made with mklink");
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        Directory.CreateDirectory(Path.Combine(project, "docs"));
        Directory.CreateDirectory(CheckoutTmp);
        MakeJunction(Path.Combine(CheckoutTmp, "p"), project);

        McpException refused = Assert.Throws<McpException>(() => InTmp(project, "../../.tmp/p/docs/x.png", overwrite: false, "capture_frames"));

        Assert.Equal(TmpOnlyRefusal("capture_frames"), refused.Message);
    }

    [Fact]
    public void AFrameToolsSaveWritesTheCaptureIntoTheTmpFolder()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string capture = WriteCaptureIn(project);
        OutputTarget target = ScreenshotTarget.ResolveInTmp(project, "../../.tmp/shots/a.png", false, "take_screenshot", NullLogger.Instance);

        ScreenshotTarget.SaveInTmp(capture, target, overwrite: false, "take_screenshot");

        Assert.Equal(PngBytes, File.ReadAllBytes(Path.Combine(CheckoutTmp, "shots", "a.png")));
    }

    [Fact]
    public void ATmpFolderThatIsAJunctionIntoTheProjectIsRefusedOnAFrameTool()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are made with mklink");
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        Directory.CreateDirectory(Path.Combine(project, "docs"));
        MakeJunction(CheckoutTmp, project);

        McpException refused = Assert.Throws<McpException>(() => InTmp(project, "../../.tmp/docs/x.png", overwrite: false, "take_screenshot"));

        Assert.Equal(TmpOnlyRefusal("take_screenshot"), refused.Message);
    }

    [Fact]
    public void AWorktreeInsideTheMainCheckoutsTmpFolderIsHeldToItsOwnTmpFolder()
    {
        string game = Directory.CreateDirectory(Path.Combine(_main.Path, "game")).FullName;
        File.WriteAllText(Path.Combine(game, "project.godot"), "");
        Git.InitAndCommitAll(_main.Path);
        string worktree = Path.Combine(_main.Path, ".tmp", "wt");
        Git.Run(_main.Path, "worktree", "add", "--quiet", worktree);
        string project = Path.Combine(worktree, "game");

        McpException refused = Assert.Throws<McpException>(() => InTmp(project, "docs/x.png", overwrite: false, "take_screenshot"));

        Assert.Equal(TmpOnlyRefusal("take_screenshot"), refused.Message);
        Assert.Equal(Path.Combine(worktree, ".tmp", "x.png"), InTmp(project, "../.tmp/x.png", overwrite: false, "take_screenshot"));
    }

    [Fact]
    public void AFrameToolsSaveHoldsTheTargetToTheTmpFoldersAgainAtTheWrite()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are made with mklink");
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string capture = WriteCaptureIn(project);
        string shots = Directory.CreateDirectory(Path.Combine(CheckoutTmp, "shots")).FullName;
        OutputTarget target = ScreenshotTarget.ResolveInTmp(project, "../../.tmp/shots/a.png", false, "take_screenshot", NullLogger.Instance);
        Directory.Delete(shots);
        MakeJunction(shots, project);

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.SaveInTmp(capture, target, overwrite: false, "take_screenshot"));

        Assert.Equal(TmpOnlyRefusal("take_screenshot"), refused.Message);
        Assert.False(File.Exists(Path.Combine(project, "a.png")));
    }

    [Fact]
    public void AFrameToolsPathInAProjectInNoCheckoutIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => InTmp(Project, ".tmp/a.png", overwrite: false, "wait_for"));

        Assert.Equal(
            $".tmp/a.png is refused: wait_for writes only into a git checkout's .tmp folder, and {Project} is in no git checkout",
            refused.Message
        );
    }

    [Fact]
    public void AScaledShotWithAPathCarriesTheScaleFieldsAndTheCopy()
    {
        JsonObject text = new() { ["path"] = "capture.png" };
        JsonNode reply = JsonNode.Parse("""{"scale": 2, "warning": "the screen let the window grow to 1.5"}""")!;

        RuntimeTools.TakeScreenshotFields(2, files => files["path"] = "copy.png")!(text, reply);

        Assert.Equal("""{"path":"copy.png","scale":2,"warning":"the screen let the window grow to 1.5"}""", text.ToJsonString());
    }

    [Fact]
    public void AWaitForCopyThatFailsKeepsTheCaptureAndWarns()
    {
        string project = ProjectUnderACheckout(dotGitIsFile: true);
        string capture = WriteCaptureIn(project);
        OutputTarget target = ScreenshotTarget.ResolveInTmp(project, "../../.tmp/met.png", false, "wait_for", NullLogger.Instance);
        Directory.CreateDirectory(CheckoutTmp);
        File.WriteAllBytes(target.FullPath, [1, 2]);
        JsonObject files = new() { ["path"] = capture };

        RuntimeTools.KeptCopy(target, overwrite: false, "wait_for")!(files);

        string warning = files["warning"]!.GetValue<string>();
        Assert.StartsWith("the still could not be copied to ../../.tmp/met.png: ", warning, StringComparison.Ordinal);
        Assert.EndsWith($"; it is kept at {capture}", warning, StringComparison.Ordinal);
        Assert.Equal(capture, files["path"]!.GetValue<string>());
        Assert.False(files.ContainsKey("capturePath"), files.ToJsonString());
        Assert.Equal([1, 2], File.ReadAllBytes(target.FullPath));
    }

    internal static string TmpOnlyRefusal(string tool) =>
        tool == "take_screenshot"
            ? "path on take_screenshot writes only into the checkout's or the main checkout's .tmp folder; "
                + "use save_screenshot to write a still elsewhere in the project"
            : $"options.path on {tool} writes only into the checkout's or the main checkout's .tmp folder";

    private static string InTmp(string project, string path, bool overwrite, string tool) =>
        ScreenshotTarget.ResolveInTmp(project, path, overwrite, tool, NullLogger.Instance).FullPath;

    private static string WriteCaptureIn(string project)
    {
        string folder = Path.Combine(project, ".godot", "godot-mcp", "screenshots");
        Directory.CreateDirectory(folder);
        string capture = Path.Combine(folder, "capture.png");
        File.WriteAllBytes(capture, PngBytes);
        return capture;
    }

    /// <summary>
    /// A project at &lt;temp&gt;/src/Game, which git does not track, under a checkout at &lt;temp&gt;: a repository whose
    /// .git is a folder, or a linked worktree, whose .git is a file, of a main checkout at &lt;main&gt;.
    /// </summary>
    private string ProjectUnderACheckout(bool dotGitIsFile)
    {
        if (dotGitIsFile)
        {
            Git.InitAndCommitAll(_main.Path);
            Git.Run(_main.Path, "worktree", "add", "--quiet", _temp.Path);
        }
        else
        {
            Git.Run(_temp.Path, "init", "--quiet", "--initial-branch=main");
        }

        return Directory.CreateDirectory(Path.Combine(_temp.Path, "src", "Game")).FullName;
    }

    private string CheckoutTmp => Path.Combine(_temp.Path, ".tmp");

    /// <summary>The places a refusal names for <see cref="ProjectUnderACheckout"/> with a .git file.</summary>
    private string WorktreePlaces(string project) => UntrackedPlaces(project, _temp.Path, _main.Path);

    /// <summary>
    /// The places a refusal names for a project its checkout does not track, with the main checkout's .tmp folder when there
    /// is one.
    /// </summary>
    internal static string UntrackedPlaces(string project, string checkout, string? main)
    {
        string checkoutTmp =
            $"the checkout's .tmp folder {Path.Combine(checkout, ".tmp")} (the checkout {checkout} does not track "
            + $"{Path.Combine(project, "project.godot")}, so only its .tmp folder is open)";
        return main is null
            ? $"the project {project} and {checkoutTmp}; save inside either"
            : $"the project {project}, {checkoutTmp} and the main checkout's .tmp folder {Path.Combine(main, ".tmp")}; save inside one of them";
    }

    /// <summary>ScreenshotTarget.Resolve's full path.</summary>
    private static string Resolve(string project, string path, bool overwrite) => Destination(project, path, overwrite).FullPath;

    private static OutputTarget Destination(string project, string path, bool overwrite) =>
        ScreenshotTarget.Resolve(project, path, overwrite, NullLogger.Instance);

    // A junction needs no privilege, unlike a directory symbolic link, and .NET has no API that makes one.
    internal static void MakeJunction(string link, string target) => Mklink("/J", link, target);

    /// <summary>Makes <paramref name="link"/> a second name of the file <paramref name="target"/>.</summary>
    internal static void MakeHardLink(string link, string target) => Mklink("/H", link, target);

    private static void Mklink(string kind, string link, string target)
    {
        ProcessStartInfo start = new("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/c", "mklink", kind, link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using Process mklink = Process.Start(start)!;
        mklink.StandardInput.Close();
        string output = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0, $"mklink {kind} {link} {target} failed: {output}");
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
