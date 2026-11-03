using System.Runtime.InteropServices;
using System.Text.Json;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// crop_screenshot: a crop of a PNG under the project's .godot/godot-mcp/ or in the .tmp folder of its checkout or main
/// checkout, with no game and no Godot.
/// </summary>
public sealed class CropScreenshotTests : IDisposable
{
    private const int Width = 6;
    private const int Height = 4;
    private const string Relative = ".godot/godot-mcp/screenshots/shot.png";
    private readonly TempDirectory _temp = new();
    private readonly string _project;
    private readonly string _folder;
    private readonly string _shot;

    public CropScreenshotTests()
    {
        _project = Directory.CreateDirectory(_temp.Combine("game")).FullName;
        File.WriteAllText(Path.Combine(_project, "project.godot"), "");
        _folder = Directory.CreateDirectory(ScreenshotTarget.CaptureFolder(_project)).FullName;
        _shot = Path.Combine(_folder, "shot.png");
        File.WriteAllBytes(_shot, Source().Encode());
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ACropReturnsItsPixelsAndWritesTheFirstFreeName()
    {
        (JsonElement first, PngImage image) = Crop(Relative, new CropRect(1, 2, 3, 2));
        (JsonElement second, _) = Crop(_shot, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(_folder, "shot-crop-1.png"), first.GetProperty("path").GetString());
        Assert.Equal((3, 2), (first.GetProperty("width").GetInt32(), first.GetProperty("height").GetInt32()));
        Assert.Equal(Expected(1, 2, 3, 2, 1), image.Pixels);
        Assert.Equal(image.Pixels, PngImage.Decode(File.ReadAllBytes(Path.Combine(_folder, "shot-crop-1.png")), "crop").Pixels);
        Assert.Equal(Path.Combine(_folder, "shot-crop-2.png"), second.GetProperty("path").GetString());
    }

    [Fact]
    public void ScaleRepeatsEachPixelNearestNeighbour()
    {
        (JsonElement result, PngImage image) = Crop(Relative, new CropRect(2, 1, 2, 2), scale: 3);

        Assert.Equal((6, 6), (result.GetProperty("width").GetInt32(), result.GetProperty("height").GetInt32()));
        Assert.Equal((6, 6), (image.Width, image.Height));
        Assert.Equal(Expected(2, 1, 2, 2, 3), image.Pixels);
    }

    [Fact]
    public void ARectPartlyOutsideIsClippedAndTheResultSaysSo()
    {
        (JsonElement result, PngImage image) = Crop(Relative, new CropRect(-2, 3, 4, 5));

        JsonElement rect = result.GetProperty("rect");
        Assert.Equal(
            (0, 3, 2, 1),
            (
                rect.GetProperty("x").GetInt32(),
                rect.GetProperty("y").GetInt32(),
                rect.GetProperty("width").GetInt32(),
                rect.GetProperty("height").GetInt32()
            )
        );
        Assert.Equal(Expected(0, 3, 2, 1, 1), image.Pixels);
    }

    [Fact]
    public void ARectWhollyOutsideIsRefused()
    {
        McpException refused = Refused(Relative, new CropRect(6, 0, 2, 2));

        Assert.Equal($"rect 6,0 2x2 lies wholly outside {_shot}, which is 6x4", refused.Message);
    }

    [Fact]
    public void AnOutputOver4096PixelsOnAnEdgeIsRefused()
    {
        PngImage wide = new(1100, 1, false, new byte[1100 * 3]);
        File.WriteAllBytes(Path.Combine(_folder, "wide.png"), wide.Encode());

        McpException refused = Refused(Path.Combine(_folder, "wide.png"), new CropRect(0, 0, 1100, 1), scale: 4);

        Assert.Equal("the crop would be 4400x4; crop_screenshot writes at most 4096 px on an edge, so use a smaller rect or scale", refused.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void AScaleOutsideOneToEightIsRefused(int scale)
    {
        McpException refused = Refused(Relative, new CropRect(0, 0, 1, 1), scale);

        Assert.Equal($"scale must be 1 to 8; got {scale}", refused.Message);
    }

    [Fact]
    public void AnImageInAnotherProjectFolderIsRefused()
    {
        string outside = Path.Combine(_project, "shot.png");
        File.Copy(_shot, outside);

        McpException refused = Refused("shot.png", new CropRect(0, 0, 1, 1));
        McpException refusedAbsolute = Refused(outside, new CropRect(0, 0, 1, 1));

        string expected = NotUnder(_project, outside);
        Assert.Equal(expected, refused.Message);
        Assert.Equal(expected, refusedAbsolute.Message);
    }

    [Fact]
    public void AMissingImageIsRefused()
    {
        string missing = Path.Combine(_folder, "gone.png");

        McpException refused = Refused(missing, new CropRect(0, 0, 1, 1));

        Assert.Equal($"{missing} does not exist", refused.Message);
    }

    [Fact]
    public void AnImageThatIsASymbolicLinkLeadingOutIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the link check reads Windows' final path of the open file");
        string secret = Path.Combine(Directory.CreateDirectory(_temp.Combine("outside")).FullName, "secret.png");
        File.Copy(_shot, secret);
        string link = Path.Combine(_folder, "link.png");
        try
        {
            File.CreateSymbolicLink(link, secret);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"creating a file symbolic link needs a privilege this run lacks: {e.Message}");
        }

        McpException refused = Refused(link, new CropRect(0, 0, 1, 1));

        Assert.StartsWith($"{link} resolves to ", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_folder, "link-crop-1.png")));
    }

    [Fact]
    public void AnImageWithASecondNameIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink /H is a Windows command");
        string secret = Path.Combine(Directory.CreateDirectory(_temp.Combine("outside")).FullName, "secret.png");
        File.Copy(_shot, secret);
        string link = Path.Combine(_folder, "hard.png");
        ScreenshotPathValidationTests.MakeHardLink(link, secret);

        McpException refused = Refused(link, new CropRect(0, 0, 1, 1));

        Assert.Equal($"{link} has 2 names (hard links); crop_screenshot reads only files with one name in those folders", refused.Message);
    }

    [Theory]
    [InlineData(-1, -1, Width + 2, Height + 2, 0, 0, Width, Height)]
    [InlineData(Width - 1, 0, 1, 1, Width - 1, 0, 1, 1)]
    public void AClippedRectKeepsOnlyThePixelsInsideTheImage(int x, int y, int width, int height, int keptX, int keptY, int keptWidth, int keptHeight)
    {
        (JsonElement result, PngImage image) = Crop(Relative, new CropRect(x, y, width, height));

        JsonElement rect = result.GetProperty("rect");
        Assert.Equal(
            (keptX, keptY, keptWidth, keptHeight),
            (
                rect.GetProperty("x").GetInt32(),
                rect.GetProperty("y").GetInt32(),
                rect.GetProperty("width").GetInt32(),
                rect.GetProperty("height").GetInt32()
            )
        );
        Assert.Equal(Expected(keptX, keptY, keptWidth, keptHeight, 1), image.Pixels);
    }

    [Fact]
    public void ARectEndingAtTheImagesLeftEdgeIsWhollyOutside()
    {
        McpException refused = Refused(Relative, new CropRect(-5, 0, 5, 1));

        Assert.Equal($"rect -5,0 5x1 lies wholly outside {_shot}, which is 6x4", refused.Message);
    }

    [Fact]
    public void AnOutputOfExactly4096PixelsIsWrittenAndATallerOneRefused()
    {
        File.WriteAllBytes(Path.Combine(_folder, "wide.png"), new PngImage(1024, 1, false, new byte[1024 * 3]).Encode());
        File.WriteAllBytes(Path.Combine(_folder, "tall.png"), new PngImage(1, 1100, false, new byte[1100 * 3]).Encode());

        (JsonElement wide, _) = Crop(Path.Combine(_folder, "wide.png"), new CropRect(0, 0, 1024, 1), scale: 4);
        McpException refused = Refused(Path.Combine(_folder, "tall.png"), new CropRect(0, 0, 1, 1100), scale: 4);

        Assert.Equal(4096, wide.GetProperty("width").GetInt32());
        Assert.Equal("the crop would be 4x4400; crop_screenshot writes at most 4096 px on an edge, so use a smaller rect or scale", refused.Message);
    }

    [Fact]
    public void AnRgbaSourceGivesAnRgbaCrop()
    {
        byte[] pixels = [.. Enumerable.Range(0, 2 * 2 * 4).Select(i => (byte)((i * 29) + 3))];
        File.WriteAllBytes(Path.Combine(_folder, "alpha.png"), new PngImage(2, 2, true, pixels).Encode());

        (_, PngImage image) = Crop(Path.Combine(_folder, "alpha.png"), new CropRect(1, 0, 1, 2));

        Assert.True(image.HasAlpha);
        Assert.Equal([.. pixels[4..8], .. pixels[12..16]], image.Pixels);
    }

    [Fact]
    public void ACropOfASourceInANestedFolderIsWrittenInThatFolder()
    {
        string nested = Directory.CreateDirectory(Path.Combine(_folder, "frames", "run1")).FullName;
        File.Copy(_shot, Path.Combine(nested, "f-0.png"));

        (JsonElement result, _) = Crop(".godot/godot-mcp/screenshots/frames/run1/f-0.png", new CropRect(0, 0, 2, 2));

        Assert.Equal(Path.Combine(nested, "f-0-crop-1.png"), result.GetProperty("path").GetString());
        Assert.True(File.Exists(Path.Combine(nested, "f-0-crop-1.png")));
    }

    [Fact]
    public void ARelativeImageThatClimbsOutOfTheFolderIsRefused()
    {
        string above = Path.Combine(_project, ".godot", "shot.png");
        File.Copy(_shot, above);

        McpException refused = Refused(".godot/godot-mcp/../shot.png", new CropRect(0, 0, 1, 1));

        Assert.Equal(NotUnder(_project, above), refused.Message);
    }

    [Fact]
    public void AClimbFromTheCaptureFolderToTheProjectIsRefused()
    {
        string projectGodot = Path.Combine(_project, "project.godot");

        McpException refused = Refused(".godot/godot-mcp/../../project.godot", new CropRect(0, 0, 1, 1));

        Assert.Equal(NotUnder(_project, projectGodot), refused.Message);
    }

    [Fact]
    public void ADotDotSegmentThatLeavesTheFoldersIsRefused()
    {
        string source = Directory.CreateDirectory(Path.Combine(_project, "src")).FullName;
        string image = Path.Combine(source, "x.png");
        File.Copy(_shot, image);
        string climbing = Path.Combine(_project, ".tmp", "..", "src", "x.png");

        McpException relative = Refused(".tmp/../src/x.png", new CropRect(0, 0, 1, 1));
        McpException absolute = Refused(climbing, new CropRect(0, 0, 1, 1));

        string expected = NotUnder(_project, image);
        Assert.Equal(expected, relative.Message);
        Assert.Equal(expected, absolute.Message);
    }

    [Fact]
    public void AnImageThatIsAFolderIsRefused()
    {
        string frames = Directory.CreateDirectory(Path.Combine(_folder, "frames")).FullName;

        McpException refused = Refused(frames, new CropRect(0, 0, 1, 1));

        Assert.Equal($"{frames} is a folder, not a PNG", refused.Message);
    }

    [Fact]
    public void TakenNamesAreSkipped()
    {
        foreach (int n in new[] { 1, 2, 3 })
        {
            File.WriteAllBytes(Path.Combine(_folder, $"shot-crop-{n}.png"), [1]);
        }

        (JsonElement result, _) = Crop(Relative, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(_folder, "shot-crop-4.png"), result.GetProperty("path").GetString());
    }

    [Fact]
    public void ANameTakenBetweenThePickAndTheMoveIsSkippedForTheNext()
    {
        string taken = Path.Combine(_folder, "shot-crop-1.png");
        File.WriteAllBytes(taken, [1, 2]);

        string written = ScreenshotCropTool.Write(_folder, "shot", Source().Encode(), path => path != taken && File.Exists(path));

        Assert.Equal(Path.Combine(_folder, "shot-crop-2.png"), written);
        Assert.Equal([1, 2], File.ReadAllBytes(taken));
    }

    [Fact]
    public void ASourceOverTheLengthCapIsRefusedBeforeItIsRead()
    {
        const long Cap = (16384L * 16384 * 4) + (1 << 20);

        ScreenshotCropTool.CheckLength(Cap, "big.png");
        McpException refused = Assert.Throws<McpException>(() => ScreenshotCropTool.CheckLength(Cap + 1, "big.png"));

        Assert.Equal($"big.png is {Cap + 1} bytes, larger than any PNG Godot saves; crop_screenshot reads at most {Cap} bytes", refused.Message);
    }

    [Fact]
    public void ACropOfALinkIsWrittenBesideTheFileItReaches()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink /J and the final-path check are Windows'");
        string outside = Directory.CreateDirectory(_temp.Combine("outside")).FullName;
        string junction = Path.Combine(_folder, "linked");
        ScreenshotPathValidationTests.MakeJunction(junction, outside);
        try
        {
            File.CreateSymbolicLink(Path.Combine(outside, "link.png"), _shot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"creating a file symbolic link needs a privilege this run lacks: {e.Message}");
        }

        Crop(Path.Combine(junction, "link.png"), new CropRect(0, 0, 1, 1));

        Assert.True(File.Exists(Path.Combine(_folder, "shot-crop-1.png")));
        Assert.Equal(["link.png"], Directory.GetFiles(outside).Select(Path.GetFileName));
    }

    [Fact]
    public void AnImageInTheProjectsScratchFolderIsCropped()
    {
        string scratch = Directory.CreateDirectory(Path.Combine(_project, ".godot", "godot-mcp", "scratch-1", "s")).FullName;
        File.WriteAllBytes(Path.Combine(scratch, "0-a.png"), Source().Encode());

        (JsonElement result, _) = Crop(".godot/godot-mcp/scratch-1/s/0-a.png", new CropRect(0, 0, 2, 1));

        Assert.Equal(Path.Combine(scratch, "0-a-crop-1.png"), result.GetProperty("path").GetString());
    }

    [Fact]
    public void AnImageInTheCheckoutsTmpIsCropped()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string files = Directory.CreateDirectory(Path.Combine(checkout, ".tmp")).FullName;
        File.WriteAllBytes(Path.Combine(files, "absolute.png"), Source().Encode());
        File.WriteAllBytes(Path.Combine(files, "relative.png"), Source().Encode());

        (JsonElement absolute, _) = CropIn(project, Path.Combine(files, "absolute.png"), new CropRect(0, 0, 2, 1));
        (JsonElement relative, _) = CropIn(project, "../.tmp/relative.png", new CropRect(0, 0, 1, 2));

        Assert.Equal(Path.Combine(files, "absolute-crop-1.png"), absolute.GetProperty("path").GetString());
        Assert.Equal(Path.Combine(files, "relative-crop-1.png"), relative.GetProperty("path").GetString());
    }

    [Fact]
    public void AnImageInTheMainCheckoutsTmpIsCropped()
    {
        using TempDirectory main = new();
        using TempDirectory worktree = new();
        Git.InitAndCommitAll(main.Path);
        Git.Run(main.Path, "worktree", "add", "--quiet", worktree.Path);
        string project = Game(worktree.Path, "game");
        string files = Directory.CreateDirectory(Path.Combine(main.Path, ".tmp")).FullName;
        File.WriteAllBytes(Path.Combine(files, "x.png"), Source().Encode());

        (JsonElement result, _) = CropIn(project, Path.Combine(files, "x.png"), new CropRect(0, 0, 2, 2));

        Assert.Equal(Path.Combine(files, "x-crop-1.png"), result.GetProperty("path").GetString());
    }

    [Fact]
    public void AnImageInTheProjectsTrackedTreeIsRefused()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot", "game/art.png");
        string project = Path.Combine(checkout, "game");
        string image = Path.Combine(project, "art.png");

        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(NotUnder(project, image, checkout), refused.Message);
    }

    [Fact]
    public void AnImageInDotGitIsRefused()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string git = Directory.CreateDirectory(Path.Combine(checkout, ".git", "crops")).FullName;
        string image = Path.Combine(git, "x.png");
        File.WriteAllBytes(image, Source().Encode());

        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(NotUnder(project, image, checkout), refused.Message);
    }

    [Fact]
    public void AWorktreeInsideTheMainCheckoutsTmpKeepsItsTrackedFilesOutOfReach()
    {
        using TempDirectory main = new();
        Git.InitAndCommitAll(main.Path);
        string worktree = Path.Combine(Directory.CreateDirectory(Path.Combine(main.Path, ".tmp")).FullName, "wt");
        Git.Run(main.Path, "worktree", "add", "--quiet", worktree);
        string project = Game(worktree, "game");
        string image = Path.Combine(project, "art.png");
        File.WriteAllBytes(image, Source().Encode());

        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(NotUnder(project, image, worktree, main.Path), refused.Message);
    }

    [Fact]
    public void AForceTrackedImageInTmpIsRefused()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string image = Path.Combine(Directory.CreateDirectory(Path.Combine(checkout, ".tmp")).FullName, "x.png");
        File.WriteAllBytes(image, Source().Encode());
        Git.Run(checkout, "add", "--force", ".tmp/x.png");

        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Tracked(image), refused.Message);
    }

    [Fact]
    public void ATrackedImageInTheGodotMcpFolderIsRefused()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string image = Path.Combine(Directory.CreateDirectory(Path.Combine(project, ".godot", "godot-mcp")).FullName, "x.png");
        File.WriteAllBytes(image, Source().Encode());
        Git.Run(checkout, "add", "--force", "game/.godot/godot-mcp/x.png");

        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Tracked(image), refused.Message);
    }

    [Fact]
    public void ATrackedImageOfACheckoutNestedInTmpIsRefused()
    {
        using TempDirectory main = new();
        string project = Game(main.Path, "game");
        File.WriteAllBytes(Path.Combine(project, "art.png"), Source().Encode());
        Git.InitAndCommitAll(main.Path);
        Directory.CreateDirectory(Path.Combine(main.Path, ".tmp"));
        Git.Run(main.Path, "worktree", "add", "--quiet", Path.Combine(main.Path, ".tmp", "wt"));
        string image = Path.Combine(main.Path, ".tmp", "wt", "game", "art.png");

        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Tracked(image), refused.Message);
    }

    [Fact]
    public void ACheckoutTheProjectIsNotUnderIsNoRoot()
    {
        using TempDirectory temp = new();
        using TempDirectory elsewhere = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        Git.Run(checkout, "config", "core.worktree", elsewhere.Path);
        string image = Path.Combine(Directory.CreateDirectory(Path.Combine(elsewhere.Path, ".tmp")).FullName, "x.png");
        File.WriteAllBytes(image, Source().Encode());

        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(NotUnder(project, image), refused.Message);
    }

    [Fact]
    public void ACaseOnlyRenamedTrackedImageIsRefused()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string shots = Directory.CreateDirectory(Path.Combine(checkout, ".tmp", "Shots")).FullName;
        string tracked = Path.Combine(shots, "Art.png");
        File.WriteAllBytes(tracked, Source().Encode());
        Git.Run(checkout, "add", "--force", ".tmp/Shots/Art.png");
        File.Move(tracked, Path.Combine(shots, "moved.png"));
        string renamed = Path.Combine(shots, "art.png");
        File.Move(Path.Combine(shots, "moved.png"), renamed);

        McpException refused = RefusedIn(project, renamed, new CropRect(0, 0, 1, 1));

        Assert.Equal(Tracked(renamed), refused.Message);
    }

    [Fact]
    public void AnImageTrackedByTheOuterCheckoutUnderANestedRepoIsRefused()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string sub = Directory.CreateDirectory(Path.Combine(checkout, ".tmp", "sub")).FullName;
        string image = Path.Combine(sub, "x.png");
        File.WriteAllBytes(image, Source().Encode());
        Git.Run(checkout, "add", "--force", ".tmp/sub/x.png");
        Git.Run(sub, "init", "--quiet", "--initial-branch=main");

        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Tracked(image), refused.Message);
    }

    [Fact]
    public void AnImageGitCannotCheckInsideACheckoutIsRefused()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string sub = Directory.CreateDirectory(Path.Combine(checkout, ".tmp", "sub")).FullName;
        string image = Path.Combine(sub, "x.png");
        File.WriteAllBytes(image, Source().Encode());
        File.WriteAllText(Path.Combine(sub, ".git"), "this is not a gitfile\n");

        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Unchecked(image), refused.Message);
    }

    [Fact]
    public void ATrackedImageWithGlobCharactersInItsNameIsRefused()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string files = Directory.CreateDirectory(Path.Combine(checkout, ".tmp")).FullName;
        string tracked = Path.Combine(files, "x[1].png");
        File.WriteAllBytes(tracked, Source().Encode());
        Git.Run(checkout, "--literal-pathspecs", "add", "--force", ".tmp/x[1].png");
        string plain = Path.Combine(files, "x1.png");
        File.WriteAllBytes(plain, Source().Encode());

        McpException refused = RefusedIn(project, tracked, new CropRect(0, 0, 1, 1));
        (JsonElement read, _) = CropIn(project, plain, new CropRect(0, 0, 1, 1));

        Assert.Equal(Tracked(tracked), refused.Message);
        Assert.Equal(Path.Combine(files, "x1-crop-1.png"), read.GetProperty("path").GetString());
    }

    [Fact]
    public void ACaseDifferingSpellingFollowsTheVolume()
    {
        string upper = Path.Combine(_project.ToUpperInvariant(), ".GODOT", "GODOT-MCP", "SCREENSHOTS", "shot.png");
        if (OperatingSystem.IsWindows())
        {
            (JsonElement result, _) = Crop(upper, new CropRect(0, 0, 1, 1));
            Assert.Equal(Path.Combine(_folder, "shot-crop-1.png"), result.GetProperty("path").GetString());
            return;
        }

        McpException refused = Refused(upper, new CropRect(0, 0, 1, 1));

        Assert.Equal(NotUnder(_project, upper), refused.Message);
    }

    [Fact]
    public void AShortNameSpellingOfTheProjectFolderReads()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 names are Windows'");
        string shortProject = ShortPath(_project);
        Assert.SkipUnless(!HasNoShortName(shortProject, _project), $"the volume holding {_project} makes no short (8.3) names");

        (JsonElement result, _) = CropIn(shortProject, ".godot/godot-mcp/screenshots/shot.png", new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(_folder, "shot-crop-1.png"), result.GetProperty("path").GetString());
    }

    [Fact]
    public void AShortNameSpellingPointingOutOfTheFoldersIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 names are Windows'");
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string files = Directory.CreateDirectory(Path.Combine(checkout, ".tmp")).FullName;
        string outside = Directory.CreateDirectory(temp.Combine("outside")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "x.png"), Source().Encode());
        string junction = Path.Combine(files, "verylonglinkname");
        ScreenshotPathValidationTests.MakeJunction(junction, outside);
        string shortLink = ShortPath(junction);
        Assert.SkipUnless(!HasNoShortName(shortLink, junction), $"the volume holding {junction} makes no short (8.3) names");

        string image = Path.Combine(shortLink, "x.png");
        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(ResolvedOutside(project, Path.GetFullPath(image), Path.Combine(outside, "x.png"), checkout), refused.Message);
    }

    [Fact]
    public void AnExtendedLengthSpellingIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), @"\\?\ paths are Windows'");
        string extended = @"\\?\" + _shot;
        string full = Path.GetFullPath(Path.Combine(_project, extended));

        McpException refused = Refused(extended, new CropRect(0, 0, 1, 1));

        Assert.Equal(NotUnder(_project, full), refused.Message);
    }

    [Fact]
    public void AnExtendedLengthUncSpellingIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), @"\\?\UNC paths are Windows'");
        string extended = @"\\?\UNC\server\share\x.png";
        string full = Path.GetFullPath(Path.Combine(_project, extended));

        McpException refused = Refused(extended, new CropRect(0, 0, 1, 1));

        Assert.Equal(NotUnder(_project, full), refused.Message);
    }

    [Fact]
    public void ATmpJunctionLeadingOutsideIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink /J and the final-path check are Windows'");
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string outside = Directory.CreateDirectory(temp.Combine("outside")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "x.png"), Source().Encode());
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(checkout, ".tmp"), outside);

        string image = Path.Combine(checkout, ".tmp", "x.png");
        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(ResolvedOutside(project, image, Path.Combine(outside, "x.png"), checkout), refused.Message);
    }

    [Fact]
    public void AGodotMcpJunctionLeadingOutsideIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink /J and the final-path check are Windows'");
        using TempDirectory temp = new();
        string project = Game(temp.Path, "game");
        string outside = Directory.CreateDirectory(temp.Combine("outside")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "x.png"), Source().Encode());
        Directory.CreateDirectory(Path.Combine(project, ".godot"));
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(project, ".godot", "godot-mcp"), outside);

        string image = Path.Combine(project, ".godot", "godot-mcp", "x.png");
        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(ResolvedOutside(project, image, Path.Combine(outside, "x.png")), refused.Message);
    }

    /// <summary>The source: pixel (x, y) is RGB (x * 40, y * 60, x + y).</summary>
    private static PngImage Source() =>
        new(Width, Height, false, [.. Enumerable.Range(0, Width * Height).SelectMany(i => Pixel(i % Width, i / Width))]);

    private static byte[] Pixel(int x, int y) => [(byte)(x * 40), (byte)(y * 60), (byte)(x + y)];

    /// <summary>The source's rectangle, each pixel repeated scale times across and down.</summary>
    private static byte[] Expected(int x, int y, int width, int height, int scale) =>
        [
            .. Enumerable
                .Range(0, height * scale)
                .SelectMany(row => Enumerable.Range(0, width * scale).SelectMany(column => Pixel(x + (column / scale), y + (row / scale)))),
        ];

    private (JsonElement Result, PngImage Image) Crop(string image, CropRect rect, int scale = 1) => CropIn(_project, image, rect, scale);

    private McpException Refused(string image, CropRect rect, int scale = 1) => RefusedIn(_project, image, rect, scale);

    private static (JsonElement Result, PngImage Image) CropIn(string project, string image, CropRect rect, int scale = 1) =>
        Decode(ScreenshotCropTool.CropScreenshot(project, image, rect, scale));

    private static McpException RefusedIn(string project, string image, CropRect rect, int scale = 1) =>
        Assert.Throws<McpException>(() => ScreenshotCropTool.CropScreenshot(project, image, rect, scale).ToList());

    private static (JsonElement Result, PngImage Image) Decode(IEnumerable<ContentBlock> blocks)
    {
        ContentBlock[] content = [.. blocks];
        Assert.Equal(2, content.Length);
        JsonElement result = JsonDocument.Parse(Assert.IsType<TextContentBlock>(content[0]).Text).RootElement.Clone();
        ImageContentBlock png = Assert.IsType<ImageContentBlock>(content[1]);
        Assert.Equal("image/png", png.MimeType);
        return (result, PngImage.Decode(png.DecodedData.ToArray(), "block"));
    }

    /// <summary>A game project named <paramref name="name"/> in <paramref name="root"/>, holding a project.godot.</summary>
    private static string Game(string root, string name)
    {
        string project = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(project, "project.godot"), "");
        return project;
    }

    /// <summary>A temp folder made a git repository, with each of <paramref name="files"/> committed at its path under it.</summary>
    private static string Repository(TempDirectory temp, params string[] files)
    {
        foreach (string file in files)
        {
            string full = Path.Combine(temp.Path, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "");
        }

        Git.InitAndCommitAll(temp.Path);
        return temp.Path;
    }

    /// <summary>The folders a refusal names: the project's .godot/godot-mcp, then the checkout's and main checkout's .tmp.</summary>
    private static string Folders(string project, string? checkout = null, string? main = null) =>
        string.Join("; ", new[] { Path.Combine(project, ".godot", "godot-mcp"), Tmp(checkout), Tmp(main) }.OfType<string>());

    private static string? Tmp(string? checkout) => checkout is null ? null : Path.Combine(checkout, ".tmp");

    private static string Tracked(string source) => $"{source} is tracked by git; crop_screenshot reads only untracked captures";

    private static string Unchecked(string source) => $"{source} could not be checked against git; crop_screenshot reads only untracked captures";

    private static string NotUnder(string project, string full, string? checkout = null, string? main = null) =>
        $"{full} is not under a folder crop_screenshot reads ({Folders(project, checkout, main)}); it reads only the captures in "
        + "the project's .godot/godot-mcp/ and the .tmp folder of the project's checkout or main checkout";

    private static string ResolvedOutside(string project, string source, string final, string? checkout = null, string? main = null) =>
        $"{source} resolves to {final}, not under a folder crop_screenshot reads ({Folders(project, checkout, main)}); it reads only "
        + "the captures in the project's .godot/godot-mcp/ and the .tmp folder of the project's checkout or main checkout";

    /// <summary>Whether <paramref name="shortPath"/> is still the long form: the volume made no 8.3 name along it.</summary>
    private static bool HasNoShortName(string shortPath, string path) => shortPath.Equals(path, StringComparison.OrdinalIgnoreCase);

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
}
