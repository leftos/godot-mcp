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
    [SupportedOSPlatform("windows")]
    public void AStillWhoseExistingFolderIsPastMaxPathIsAccepted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "MAX_PATH and the long path prefix are Windows'");
        string project = Directory.CreateDirectory(_temp.Combine("game")).FullName;
        string folder = Path.Combine(project, new string('a', 150), new string('b', 150));
        Directory.CreateDirectory(@"\\?\" + folder);

        Assert.Equal(Path.Combine(folder, "shot.png"), Resolve(project, Path.Combine(folder, "shot.png")));
        Assert.Equal(folder, FinalPath.Of(folder));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AMovePastMaxPathRenames()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "MAX_PATH and the long path prefix are Windows'");
        string source = _temp.Combine("shot.png");
        File.WriteAllBytes(source, PngBytes);
        string folder = _temp.Combine(new string('a', 150), new string('b', 150));
        Directory.CreateDirectory(@"\\?\" + folder);
        string destination = Path.Combine(folder, "shot.png");

        Assert.True(FinalPath.Rename(source, destination, overwrite: false));
        Assert.Equal(PngBytes, File.ReadAllBytes(@"\\?\" + destination));
        Assert.False(File.Exists(source));
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
    [SupportedOSPlatform("windows")]
    public void AProjectInAFolderNamedWithATrailingSpaceIsRefusedNamingTheFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "trailing-space names are made the Windows way");
        // Only a path's last name loses its trailing space, so the project's "par " keeps it.
        string project = _temp.Combine("par ", "game");
        Directory.CreateDirectory(@"\\?\" + project);

        McpException refused = Refused(project, "shot.png");

        Assert.Equal(
            $"path shot.png lies under the project {project}, whose name par  ends in a space or a dot; "
                + "Windows reads such a name two ways, so named stills and clips are not written there",
            refused.Message
        );
        Assert.Empty(Directory.GetFileSystemEntries(@"\\?\" + project));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AProjectReachedThroughALinkToADottedFolderIsRefusedNamingItsFinalPath()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "symbolic links and trailing-dot names are made the Windows way");
        string link = LinkToADottedGame();

        McpException refused = Refused(link, "shot.png");

        Assert.StartsWith("path shot.png lies under the project ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("whose name real. ends in a space or a dot", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void APathIntoTheTrimmedSiblingOfADottedFinalProjectIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "symbolic links and trailing-dot names are made the Windows way");
        string link = LinkToADottedGame();
        // GetRelativePath reads the sibling "real\game" as inside the project's final form "real.\game".
        string sibling = Directory.CreateDirectory(_temp.Combine("real", "game")).FullName;

        Refused(link, Path.Combine(sibling, "shot.png"));

        Assert.Empty(Directory.GetFileSystemEntries(sibling));
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
    public void AMoveWithoutOverwriteRefusesAnExistingFileAndKeepsTheSource()
    {
        string source = _temp.Combine("clip.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        string destination = _temp.Combine("taken.mp4");
        File.WriteAllBytes(destination, [9]);

        Assert.Throws<IOException>(() => OutputDestination.MoveReplacing(source, destination, overwrite: false, allowCopy: true));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        Assert.Equal([9], File.ReadAllBytes(destination));
    }

    [Fact]
    public void AMoveWithOverwriteReplacesAHardLinkedEntryWithoutWritingThroughIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink /H is a Windows command");
        string other = _temp.Combine("other.mp4");
        File.WriteAllBytes(other, [1, 2]);
        string destination = _temp.Combine("clip.mp4");
        ScreenshotPathValidationTests.MakeHardLink(destination, other);
        string source = Path.Combine(Directory.CreateDirectory(_temp.Combine("recordings")).FullName, "clip.mp4");
        File.WriteAllBytes(source, [7, 7, 7]);

        OutputDestination.MoveReplacing(source, destination, overwrite: true, allowCopy: true);

        Assert.Equal([7, 7, 7], File.ReadAllBytes(destination));
        Assert.Equal([1, 2], File.ReadAllBytes(other));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void AMoveDeletesNothingWhenItFails()
    {
        string source = _temp.Combine("clip.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        // A folder stands where the clip would go, which no move replaces, overwrite or not.
        string destination = Directory.CreateDirectory(_temp.Combine("taken.mp4")).FullName;
        File.WriteAllBytes(Path.Combine(destination, "kept.txt"), [5]);

        Exception? failure = Record.Exception(() => OutputDestination.MoveReplacing(source, destination, overwrite: true, allowCopy: true));

        Assert.True(failure is IOException or UnauthorizedAccessException, $"expected an IO or access failure, got {failure}");
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        Assert.Equal([5], File.ReadAllBytes(Path.Combine(destination, "kept.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedMoveRemovesTheFoldersItMade(bool sourceHeldOpen)
    {
        Assert.SkipUnless(!sourceHeldOpen || OperatingSystem.IsWindows(), "only Windows refuses to rename a file held open");
        string source = _temp.Combine("clip.mp4");
        if (sourceHeldOpen)
        {
            File.WriteAllBytes(source, [1, 2, 3]);
        }

        string made = _temp.Combine("new");
        string destination = Path.Combine(made, "deeper", "clip.mp4");
        Exception? failure;
        using (sourceHeldOpen ? new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            failure = Record.Exception(() => OutputDestination.MoveReplacing(source, destination, overwrite: true, allowCopy: true));
        }

        Assert.True(failure is IOException or UnauthorizedAccessException, $"expected an IO or access failure, got {failure}");
        Assert.False(Directory.Exists(made));
        if (sourceHeldOpen)
        {
            Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        }
    }

    [Fact]
    public void AFailedSaveRemovesTheFoldersItMade()
    {
        string made = _temp.Combine("new");
        using FailingRead source = new(() => { });

        Assert.Throws<IOException>(() => OutputDestination.CopyReplacing(source, Path.Combine(made, "deeper", "shot.png"), overwrite: false));

        Assert.False(Directory.Exists(made));
        Assert.Empty(Directory.GetFiles(_temp.Path, ".godot-mcp-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void AFailedSaveNamesAFolderItCouldNotRemove()
    {
        string made = _temp.Combine("new");
        string deeper = Path.Combine(made, "deeper");
        string kept = Path.Combine(deeper, "keep.txt");
        using FailingRead source = new(() => File.WriteAllBytes(kept, [5]));

        IOException failure = Assert.Throws<IOException>(() =>
            OutputDestination.CopyReplacing(source, Path.Combine(deeper, "shot.png"), overwrite: false)
        );

        Assert.Contains($"the folder {deeper} it made could not be removed", failure.Message, StringComparison.Ordinal);
        Assert.Equal([5], File.ReadAllBytes(kept));
        Assert.True(Directory.Exists(made));
    }

    [Fact]
    public void ACrossVolumeMoveCopiesAndDeletesTheSource()
    {
        string source = _temp.Combine("clip.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        string destination = _temp.Combine("moved.mp4");

        string? leftover = OutputDestination.MoveReplacing(source, destination, overwrite: false, allowCopy: true, OnAnotherVolume);

        Assert.Null(leftover);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(destination));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void ACrossVolumeMoveReplacesAHardLinkWithoutWritingThroughIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink /H is a Windows command");
        string other = _temp.Combine("other.mp4");
        File.WriteAllBytes(other, [1, 2]);
        string destination = _temp.Combine("clip.mp4");
        ScreenshotPathValidationTests.MakeHardLink(destination, other);
        string source = Path.Combine(Directory.CreateDirectory(_temp.Combine("recordings")).FullName, "clip.mp4");
        File.WriteAllBytes(source, [7, 7, 7]);

        OutputDestination.MoveReplacing(source, destination, overwrite: true, allowCopy: true, OnAnotherVolume);

        Assert.Equal([7, 7, 7], File.ReadAllBytes(destination));
        Assert.Equal([1, 2], File.ReadAllBytes(other));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void AFailedCrossVolumeMoveKeepsTheSourceAndLeavesNoTemporary()
    {
        string source = _temp.Combine("clip.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        // A folder stands where the clip would go, which no move replaces, overwrite or not.
        string destination = Directory.CreateDirectory(_temp.Combine("taken.mp4")).FullName;

        Exception? failure = Record.Exception(() =>
            OutputDestination.MoveReplacing(source, destination, overwrite: true, allowCopy: true, OnAnotherVolume)
        );

        Assert.True(failure is IOException or UnauthorizedAccessException, $"expected an IO or access failure, got {failure}");
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFiles(_temp.Path, ".godot-mcp-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void AFailedCrossVolumeMoveRemovesTheFoldersItMade()
    {
        string source = _temp.Combine("clip.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        string made = _temp.Combine("new");
        Exception? failure;
        // Held without read sharing, the source cannot be opened for the copy.
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failure = Record.Exception(() =>
                OutputDestination.MoveReplacing(source, Path.Combine(made, "deeper", "clip.mp4"), overwrite: true, allowCopy: true, OnAnotherVolume)
            );
        }

        Assert.True(failure is IOException or UnauthorizedAccessException, $"expected an IO or access failure, got {failure}");
        Assert.False(Directory.Exists(made));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
    }

    [Fact]
    public void AFolderCreationThatFailsPartwayRemovesTheFoldersItMade()
    {
        string source = _temp.Combine("clip.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        string made = _temp.Combine("new");
        // A name longer than any file system allows: new is made, then the folder below it fails.
        string destination = Path.Combine(made, new string('x', 300), "clip.mp4");

        Exception? failure = Record.Exception(() => OutputDestination.MoveReplacing(source, destination, overwrite: true, allowCopy: true));

        Assert.True(failure is IOException or UnauthorizedAccessException, $"expected an IO or access failure, got {failure}");
        Assert.DoesNotContain("could not be removed", failure.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(made));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
    }

    [Fact]
    public void ARenameOnlyMoveOnOneVolumeMovesTheFile()
    {
        string source = _temp.Combine("clip.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        string destination = _temp.Combine("new", "moved.mp4");

        string? leftover = OutputDestination.MoveReplacing(source, destination, overwrite: false, allowCopy: false);

        Assert.Null(leftover);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(destination));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void ARenameOnlyMoveToAnotherVolumeIsRefusedAndKeepsTheSource()
    {
        string source = _temp.Combine("clip.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        string made = _temp.Combine("new");
        string destination = Path.Combine(made, "deeper", "clip.mp4");

        IOException refused = Assert.Throws<IOException>(() =>
            OutputDestination.MoveReplacing(source, destination, overwrite: true, allowCopy: false, OnAnotherVolume)
        );

        Assert.Equal($"{source} is on another volume than {destination}; the server's exit leaves no time to copy it", refused.Message);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        Assert.False(Directory.Exists(made), $"the refused move left the folder {made} it made");
        Assert.Empty(Directory.GetFiles(_temp.Path, ".godot-mcp-*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>A rename that finds the two paths on different volumes, so the move copies.</summary>
    private static bool OnAnotherVolume(string source, string destination, bool overwrite) => false;

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

    /// <summary>
    /// A project at <c>temp\game</c> that is a symbolic link to <c>temp\real.\game</c>, its stored target keeping the dot;
    /// skips the test when the link cannot be made or loses the dot.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private string LinkToADottedGame()
    {
        string real = _temp.Combine("real.", "game");
        Directory.CreateDirectory(@"\\?\" + real);
        string link = _temp.Combine("game");
        MakeSymbolicLink(link, @"\\?\" + real);
        if (!FinalPath.ReadLink(link)!.Target.Contains("real.", StringComparison.Ordinal))
        {
            Assert.Skip($"the link at {link} lost the dot of its target {real}");
        }

        return link;
    }

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

    /// <summary>A source whose every read runs <paramref name="beforeFailing"/> and then fails.</summary>
    private sealed class FailingRead(Action beforeFailing) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            beforeFailing();
            throw new IOException("the source could not be read");
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
