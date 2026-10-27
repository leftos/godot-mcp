using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// Where a named still may be written, from a real main checkout and its linked worktree, with junctions on the way, and
/// the write that replaces the destination's folder entry; no Godot runs here.
/// </summary>
public sealed class OutputDestinationTests : IDisposable
{
    private static readonly byte[] PngBytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
    private readonly TempDirectory _temp = new();

    private string Main => _temp.Combine("main");

    private string Worktree => _temp.Combine("wt");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheWholeWorktreeIsOpenWhenItTracksTheProject()
    {
        string project = GameInWorktree();
        string shot = Path.Combine(Worktree, "docs", "screenshots", "showcase", "x.png");

        Assert.Equal(shot, Resolve(project, shot));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheMainCheckoutsTmpFolderIsOpenFromAWorktree(bool absolute)
    {
        string project = GameInWorktree();
        string shot = Path.Combine(Main, ".tmp", "owner", "item", "x.png");

        Assert.Equal(shot, Resolve(project, absolute ? shot : "../../main/.tmp/owner/item/x.png"));
    }

    [Fact]
    public void TheMainCheckoutOutsideItsTmpFolderIsRefused()
    {
        string project = GameInWorktree();
        string shot = Path.Combine(Main, "docs", "x.png");

        Assert.Equal($"path {shot} resolves outside {WorktreePlaces(project)}", Refused(project, shot).Message);
    }

    [Theory]
    [InlineData(".git/x.png")]
    [InlineData(".GIT/x.png")]
    [InlineData(".git./x.png")]
    public void APathInsideDotGitIsRefused(string path)
    {
        Directory.CreateDirectory(Main);
        File.WriteAllText(Path.Combine(Main, "project.godot"), "config_version=5\n");
        Git.InitAndCommitAll(Main);

        Assert.Equal($"path {path} is inside a .git folder, which git owns; save outside it", Refused(Main, path).Message);
    }

    [Fact]
    public void AnotherProjectsDotGodotInTheWorktreeIsRefused()
    {
        string project = GameInWorktree();
        string shot = Path.Combine(Worktree, "other", ".godot", "x.png");

        Assert.Equal($"path {shot} is under .godot/, which the editor owns; pick a tracked folder", Refused(project, shot).Message);
    }

    [Fact]
    public void ADotGodotFolderInTheWorktreesTmpIsAccepted()
    {
        string project = GameInWorktree();
        string shot = Path.Combine(Worktree, ".tmp", ".godot", "x.png");

        Assert.Equal(shot, Resolve(project, shot));
    }

    [Theory]
    [InlineData("X:shot.png")]
    [InlineData(@"\shot.png")]
    public void ADriveRelativeOrRootRelativePathIsRefused(string path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows has drive-relative and root-relative paths");

        Assert.Equal(
            $"path {path} is relative to a drive's current folder or the current drive's root; give a full path or one relative to the project",
            Refused(_temp.Path, path).Message
        );
    }

    [Theory]
    [InlineData("NUL.png", "NUL")]
    [InlineData("com1.png", "COM1")]
    [InlineData("COM¹.png", "COM¹")]
    [InlineData("docs/lpt3.x.png", "LPT3")]
    public void AWindowsDeviceNameIsRefused(string path, string device)
    {
        // The absolute form is the one older Windows turns into \\.\NUL when the full path is taken.
        string absolute = Path.Combine(_temp.Path, path);

        Assert.Equal($"path {path} names the Windows device {device}; pick another file name", Refused(_temp.Path, path).Message);
        Assert.Equal($"path {absolute} names the Windows device {device}; pick another file name", Refused(_temp.Path, absolute).Message);
    }

    [Theory]
    [InlineData("CONIN$.png", "CONIN$")]
    [InlineData("conout$.png", "CONOUT$")]
    public void AConsoleDeviceNameIsRefused(string path, string device) =>
        Assert.Equal($"path {path} names the Windows device {device}; pick another file name", Refused(_temp.Path, path).Message);

    [Fact]
    public void AProjectGivenWithTheLongPathPrefixIsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), @"\\?\ is a Windows path prefix");
        string project = Directory.CreateDirectory(_temp.Combine("game")).FullName;

        Assert.Equal(Path.Combine(project, "shot.png"), Resolve(@"\\?\" + project, "shot.png"));
    }

    [Fact]
    public void AJunctionInsideAnotherJunctionsTargetLeadingOutsideIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = GameInWorktree();
        string outside = OutsideFolder();
        Directory.CreateDirectory(Path.Combine(outside, "y"));
        Directory.CreateDirectory(Path.Combine(Worktree, "assets"));
        Directory.CreateDirectory(Path.Combine(Worktree, "docs"));
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(Worktree, "assets", "shared"), outside);
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(Worktree, "docs", "a"), Path.Combine(Worktree, "assets", "shared", "y"));
        string shot = Path.Combine(Worktree, "docs", "a", "x.png");

        Assert.Equal($"path {shot} leads through a link to {outside}, outside {WorktreePlaces(project)}", Refused(project, shot).Message);
        Assert.Empty(Directory.GetFiles(outside, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ASymbolicLinkToAVolumeGuidPathBesideADecoyFolderIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "volume GUID paths are a Windows path");
        string project = GameInWorktree();
        string docs = Directory.CreateDirectory(Path.Combine(Worktree, "docs")).FullName;
        string volume = VolumeOf(_temp.Path);
        // LinkTarget gives the volume back as the relative-looking Volume{guid}\, which this folder would answer to.
        Directory.CreateDirectory(Path.Combine(docs, volume[@"\\?\".Length..].TrimEnd('\\')));
        string link = Path.Combine(docs, "v");
        MakeSymbolicLink(link, volume);
        string shot = Path.Combine(link, "x.png");
        McpException refused;
        try
        {
            refused = Refused(project, shot);
        }
        finally
        {
            // The temp folder's cleanup lists files through folder links, so a link to the system volume must go first.
            Directory.Delete(link);
        }

        Assert.StartsWith($"path {shot} leads through a link at {link} to ", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(", which is not a path on a drive; such links are not followed", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeSymbolicLinkIntoTheWorktreesTmpIsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the link is made the Windows way");
        string project = GameInWorktree();
        Directory.CreateDirectory(Path.Combine(Worktree, ".tmp"));
        string docs = Directory.CreateDirectory(Path.Combine(Worktree, "docs")).FullName;
        MakeSymbolicLink(Path.Combine(docs, "r"), @"..\.tmp");
        string shot = Path.Combine(docs, "r", "x.png");

        Assert.Equal(shot, Resolve(project, shot));
    }

    [Fact]
    public void ARelativeSymbolicLinkLeadingOutsideIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the link is made the Windows way");
        string project = GameInWorktree();
        string outside = OutsideFolder();
        string docs = Directory.CreateDirectory(Path.Combine(Worktree, "docs")).FullName;
        MakeSymbolicLink(Path.Combine(docs, "r"), @"..\..\outside");
        string shot = Path.Combine(docs, "r", "x.png");

        Assert.Equal($"path {shot} leads through a link to {outside}, outside {WorktreePlaces(project)}", Refused(project, shot).Message);
    }

    [Fact]
    public void AJunctionNamedWithATrailingSpaceIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions and trailing-space names are Windows features");
        string project = Directory.CreateDirectory(_temp.Combine("game")).FullName;
        string outside = OutsideFolder();
        // Only the \\?\ form keeps a trailing space, so the junction is made under a plain name and renamed to "docs ".
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(project, "j"), outside);
        Directory.Move(@"\\?\" + Path.Combine(project, "j"), @"\\?\" + Path.Combine(project, "docs "));

        McpException refused = Refused(project, "docs /x.png");

        Assert.Equal(
            "path docs /x.png has a folder or file name ending in a space or a dot (docs ); Windows reads such a name two ways, so it is not written",
            refused.Message
        );
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public void AFolderNameEndingInADotIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows reads a name ending in a space or a dot two ways");

        Assert.Equal(
            "path docs./x.png has a folder or file name ending in a space or a dot (docs.); Windows reads such a name two ways, so it is not written",
            Refused(_temp.Path, "docs./x.png").Message
        );
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ARelativeLinkToAFolderNamedWithATrailingDotIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "symbolic links and trailing-dot names are made the Windows way");
        string project = Directory.CreateDirectory(_temp.Combine("game")).FullName;
        // GetFullPath trims the target's "foo." to the decoy "foo", while Windows follows the link into "foo." itself.
        string dotted = Directory.CreateDirectory(@"\\?\" + Path.Combine(project, "foo.")).FullName;
        string decoy = Directory.CreateDirectory(Path.Combine(project, "foo")).FullName;
        string link = Path.Combine(Directory.CreateDirectory(Path.Combine(project, "docs")).FullName, "r");
        MakeSymbolicLink(link, @"..\foo.");
        Assert.Equal(@"..\foo.", FinalPath.ReadLink(link)!.Target);

        McpException refused = Refused(project, @"docs\r\x.png");

        Assert.Equal(LinkedNameRefusal(@"docs\r\x.png", @"..\foo.", "foo."), refused.Message);
        Assert.Empty(Directory.GetFileSystemEntries(decoy));
        Assert.Empty(Directory.GetFileSystemEntries(dotted));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AnAbsoluteLinkWhoseTargetHasANameEndingInASpaceIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "symbolic links and trailing-space names are made the Windows way");
        string project = Directory.CreateDirectory(_temp.Combine("game")).FullName;
        string target = Path.Combine(project, "sub ", "x");
        Directory.CreateDirectory(@"\\?\" + target);
        string link = Path.Combine(Directory.CreateDirectory(Path.Combine(project, "docs")).FullName, "a");
        MakeSymbolicLink(link, target);
        string stored = FinalPath.ReadLink(link)!.Target;
        Assert.EndsWith(@"sub \x", stored, StringComparison.Ordinal);

        McpException refused = Refused(project, @"docs\a\x.png");

        Assert.Equal(LinkedNameRefusal(@"docs\a\x.png", stored, "sub "), refused.Message);
    }

    [Fact]
    public void AReachedPathWithADottedNameIsRefusedBeforeContainment()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows reads a name ending in a space or a dot two ways");
        // GetRelativePath reads X:\W.\x as inside X:\W, so the dotted name is refused before any place is asked to hold it.
        McpException refused = Assert.Throws<McpException>(() => OutputDestination.CheckLinkedNames("x.png", @"X:\W.\x", @"X:\W.\x"));

        Assert.Equal(LinkedNameRefusal("x.png", @"X:\W.\x", "W."), refused.Message);
        OutputDestination.CheckLinkedNames("x.png", @"X:\W\x", @"X:\W\x");
    }

    [Fact]
    public void AJunctionToAMissingOutsideFolderIsRefusedAsOutsideBeforeItIsOpened()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = Directory.CreateDirectory(_temp.Combine("game")).FullName;
        string outside = OutsideFolder();
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(project, "out"), outside);
        Directory.Delete(outside);

        Assert.Equal(
            $"path out/x.png leads through a link to {outside}, outside the project; save inside the project",
            Refused(project, "out/x.png").Message
        );
    }

    [Fact]
    public void AShortNameInAJunctionTargetIntoDotGitIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions and short names are Windows features");
        Directory.CreateDirectory(Main);
        File.WriteAllText(Path.Combine(Main, "project.godot"), "config_version=5\n");
        Git.InitAndCommitAll(Main);
        string dotGit = Path.Combine(Main, ".git");
        string shortName = Path.GetFileName(ShortPath(dotGit));
        Assert.SkipUnless(shortName != ".git", $"the volume holding {dotGit} makes no short (8.3) names");
        Directory.CreateDirectory(Path.Combine(Main, "docs"));
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(Main, "docs", "g"), Path.Combine(Main, shortName));

        McpException refused = Refused(Main, "docs/g/x.png");

        Assert.StartsWith("path docs/g/x.png leads through a link to ", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(", inside a .git folder, which git owns; save outside it", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dotGit, "x.png")));
    }

    [Fact]
    public void ACaptureThatIsAHardLinkIsNotCopied()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink /H is a Windows command");
        string project = Directory.CreateDirectory(_temp.Combine("game")).FullName;
        OutputTarget target = Destination(project, "shot.png", overwrite: false);
        string secret = Path.Combine(OutsideFolder(), "secret.png");
        File.WriteAllBytes(secret, PngBytes);
        string folder = Directory.CreateDirectory(Path.Combine(project, ".godot", "godot-mcp", "screenshots")).FullName;
        string capture = Path.Combine(folder, "capture.png");
        ScreenshotPathValidationTests.MakeHardLink(capture, secret);

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Save(capture, target, overwrite: false));

        Assert.Equal($"the capture {capture} has 2 names (hard links); it is not copied", refused.Message);
        Assert.False(File.Exists(Path.Combine(project, "shot.png")));
    }

    [Fact]
    public void ACaptureInAJunctionedScreenshotsFolderIsNotCopied()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = Directory.CreateDirectory(_temp.Combine("game")).FullName;
        OutputTarget target = Destination(project, "shot.png", overwrite: false);
        string outside = OutsideFolder();
        File.WriteAllBytes(Path.Combine(outside, "capture.png"), PngBytes);
        string folder = Path.Combine(project, ".godot", "godot-mcp", "screenshots");
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        ScreenshotPathValidationTests.MakeJunction(folder, outside);
        string capture = Path.Combine(folder, "capture.png");

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Save(capture, target, overwrite: false));

        Assert.StartsWith($"the capture {capture} resolves to ", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            $"capture.png, not under the session's screenshots folder {folder}; it is not copied",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.False(File.Exists(Path.Combine(project, "shot.png")));
    }

    [Fact]
    public void AJunctionInTheWorktreeLeadingOutsideIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = GameInWorktree();
        string outside = OutsideFolder();
        Directory.CreateDirectory(Path.Combine(Worktree, "docs"));
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(Worktree, "docs", "out"), outside);
        string shot = Path.Combine(Worktree, "docs", "out", "x.png");

        Assert.Equal($"path {shot} leads through a link to {outside}, outside {WorktreePlaces(project)}", Refused(project, shot).Message);
    }

    [Fact]
    public void AJunctionInTheWorktreeToTheMainCheckoutsDocsIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = GameInWorktree();
        string mainDocs = Directory.CreateDirectory(Path.Combine(Main, "docs")).FullName;
        Directory.CreateDirectory(Path.Combine(Worktree, "docs"));
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(Worktree, "docs", "m"), mainDocs);
        string shot = Path.Combine(Worktree, "docs", "m", "x.png");

        Assert.Equal($"path {shot} leads through a link to {mainDocs}, outside {WorktreePlaces(project)}", Refused(project, shot).Message);
    }

    [Fact]
    public void AJunctionInTheWorktreeToTheMainCheckoutsTmpIsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = GameInWorktree();
        string owner = Directory.CreateDirectory(Path.Combine(Main, ".tmp", "owner")).FullName;
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(Worktree, "keep"), owner);
        string shot = Path.Combine(Worktree, "keep", "x.png");

        Assert.Equal(shot, Resolve(project, shot));
    }

    [Fact]
    public void AMainCheckoutTmpFolderThatIsAJunctionLeadingOutsideIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = GameInWorktree();
        string outside = OutsideFolder();
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(Main, ".tmp"), outside);
        string shot = Path.Combine(Main, ".tmp", "x.png");

        Assert.Equal($"path {shot} leads through a link to {outside}, outside {WorktreePlaces(project)}", Refused(project, shot).Message);
    }

    [Fact]
    public void AJunctionIntoTheMainCheckoutsDotGitIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = GameInWorktree();
        string dotGit = Path.Combine(Main, ".git");
        Directory.CreateDirectory(Path.Combine(Worktree, "docs"));
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(Worktree, "docs", "g"), dotGit);
        string shot = Path.Combine(Worktree, "docs", "g", "x.png");

        Assert.Equal($"path {shot} leads through a link to {dotGit}, outside {WorktreePlaces(project)}", Refused(project, shot).Message);
    }

    [Fact]
    public void ALinkToAMissingTargetBehindAnotherJunctionIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = _temp.Path;
        string real = Directory.CreateDirectory(Path.Combine(project, "real")).FullName;
        string gone = Directory.CreateDirectory(Path.Combine(project, "gone")).FullName;
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(real, "c"), gone);
        Directory.Delete(gone);
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(project, "a"), real);

        Assert.Equal(
            $"path a/c/x.png leads through a link at {Path.Combine(real, "c")} whose target {gone} does not exist",
            Refused(project, "a/c/x.png").Message
        );
    }

    [Fact]
    public void SavingOverAHardLinkLeavesItsOtherNameAlone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink /H is a Windows command");
        string project = _temp.Path;
        string other = Path.Combine(project, "other.png");
        File.WriteAllBytes(other, [1, 2]);
        string destination = Path.Combine(project, "shot.png");
        ScreenshotPathValidationTests.MakeHardLink(destination, other);
        OutputTarget target = Destination(project, "shot.png", overwrite: true);

        ScreenshotTarget.Save(WriteCapture(project), target, overwrite: true);

        Assert.Equal(PngBytes, File.ReadAllBytes(destination));
        Assert.Equal([1, 2], File.ReadAllBytes(other));
    }

    [Fact]
    public void SavingOverASymbolicLinkPlantedSinceTheCheckLeavesItsTargetAlone()
    {
        string project = _temp.Path;
        OutputTarget target = Destination(project, "shot.png", overwrite: true);
        string kept = Path.Combine(project, "kept.png");
        File.WriteAllBytes(kept, [1, 2]);
        string destination = Path.Combine(project, "shot.png");
        try
        {
            File.CreateSymbolicLink(destination, kept);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"creating a file symbolic link needs a privilege this run lacks: {e.Message}");
        }

        ScreenshotTarget.Save(WriteCapture(project), target, overwrite: true);

        Assert.Equal([1, 2], File.ReadAllBytes(kept));
        Assert.Null(new FileInfo(destination).LinkTarget);
        Assert.Equal(PngBytes, File.ReadAllBytes(destination));
    }

    [Fact]
    public void SavingRefusesAFolderSwappedForAJunctionSinceTheCheck()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows link");
        string project = _temp.Combine("game");
        string sub = Directory.CreateDirectory(Path.Combine(project, "docs", "sub")).FullName;
        string capture = WriteCapture(project);
        OutputTarget target = Destination(project, "docs/sub/x.png", overwrite: false);
        string outside = OutsideFolder();
        Directory.Delete(sub);
        ScreenshotPathValidationTests.MakeJunction(sub, outside);

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Save(capture, target, overwrite: false));

        Assert.Equal($"path docs/sub/x.png leads through a link to {outside}, outside the project; save inside the project", refused.Message);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public void AForgedWorktreeTheMainCheckoutDoesNotListGetsNoMainTmpFolder()
    {
        GameInWorktree();
        string forged = Directory.CreateDirectory(_temp.Combine("forged")).FullName;
        File.WriteAllText(Path.Combine(forged, ".git"), $"gitdir: {Path.Combine(Main, ".git", "worktrees", "wt").Replace('\\', '/')}\n");
        string shot = Path.Combine(Main, ".tmp", "x.png");

        McpException refused = Refused(forged, shot);

        Assert.StartsWith($"path {shot} resolves outside the project {forged}", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("main checkout", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACheckoutThatDoesNotTrackTheProjectOpensOnlyItsTmpFolder()
    {
        Directory.CreateDirectory(Main);
        Git.InitAndCommitAll(Main);
        string project = Directory.CreateDirectory(Path.Combine(Main, "game")).FullName;
        File.WriteAllText(Path.Combine(project, "project.godot"), "config_version=5\n");
        string shot = Path.Combine(Main, "docs", "x.png");
        string tmp = Path.Combine(Main, ".tmp", "x.png");

        Assert.Equal(
            $"path {shot} resolves outside {ScreenshotPathValidationTests.UntrackedPlaces(project, Main, main: null)}",
            Refused(project, shot).Message
        );
        Assert.Equal(tmp, Resolve(project, tmp));
    }

    [Fact]
    public void SavingACaptureFromOutsideTheScreenshotsFolderIsRefused()
    {
        string project = _temp.Path;
        OutputTarget target = Destination(project, "shot.png", overwrite: false);
        string capture = Path.Combine(project, "elsewhere.png");
        File.WriteAllBytes(capture, PngBytes);

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Save(capture, target, overwrite: false));

        string folder = Path.Combine(project, ".godot", "godot-mcp", "screenshots");
        Assert.Equal($"the capture {capture} is not under the session's screenshots folder {folder}; it is not copied", refused.Message);
        Assert.False(File.Exists(Path.Combine(project, "shot.png")));
    }

    /// <summary>
    /// A main checkout at &lt;temp&gt;/main tracking game/project.godot, and its linked worktree at &lt;temp&gt;/wt; the
    /// worktree's game.
    /// </summary>
    private string GameInWorktree()
    {
        Directory.CreateDirectory(Path.Combine(Main, "game"));
        File.WriteAllText(Path.Combine(Main, "game", "project.godot"), "config_version=5\n");
        Git.InitAndCommitAll(Main);
        Git.Run(Main, "worktree", "add", "--quiet", Worktree);
        return Path.Combine(Worktree, "game");
    }

    private string WorktreePlaces(string project) =>
        $"the project {project}, its checkout {Worktree} and the main checkout's .tmp folder {Path.Combine(Main, ".tmp")}; save inside one of them";

    private string OutsideFolder() => Directory.CreateDirectory(_temp.Combine("outside")).FullName;

    /// <summary>The refusal of a link that leads to <paramref name="named"/>, whose name <paramref name="part"/> ends in a space or a dot.</summary>
    private static string LinkedNameRefusal(string path, string named, string part) =>
        $"path {path} leads through a link to {named}, whose name {part} ends in a space or a dot; "
        + "Windows reads such a name two ways, so it is not written";

    private static string WriteCapture(string project)
    {
        string folder = Directory.CreateDirectory(Path.Combine(project, ".godot", "godot-mcp", "screenshots")).FullName;
        string capture = Path.Combine(folder, "capture.png");
        File.WriteAllBytes(capture, PngBytes);
        return capture;
    }

    private static string Resolve(string project, string path) => Destination(project, path, overwrite: false).FullPath;

    private static OutputTarget Destination(string project, string path, bool overwrite) =>
        ScreenshotTarget.Resolve(project, path, overwrite, NullLogger.Instance);

    private static McpException Refused(string project, string path) =>
        Assert.Throws<McpException>(() => Destination(project, path, overwrite: false));

    /// <summary>The short (8.3) form of <paramref name="path"/>, as Windows gives it.</summary>
    private static string ShortPath(string path)
    {
        char[] buffer = new char[1024];
        uint length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        Assert.True(length > 0 && length < buffer.Length, $"GetShortPathNameW({path}) failed with Win32 error {Marshal.GetLastPInvokeError()}");
        return new string(buffer, 0, (int)length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, uint length);

    /// <summary>The volume GUID path (\\?\Volume{guid}\) of the volume holding <paramref name="path"/>.</summary>
    private static string VolumeOf(string path)
    {
        char[] buffer = new char[64];
        Assert.True(
            GetVolumeNameForVolumeMountPointW(Path.GetPathRoot(path)!, buffer, (uint)buffer.Length),
            $"GetVolumeNameForVolumeMountPointW({Path.GetPathRoot(path)}) failed with Win32 error {Marshal.GetLastPInvokeError()}"
        );
        return new string(buffer).TrimEnd('\0');
    }

    /// <summary>A folder symbolic link at <paramref name="link"/>; skips the test when this run lacks the privilege.</summary>
    private static void MakeSymbolicLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"creating a symbolic link needs a privilege this run lacks: {e.Message}");
        }
    }
}
