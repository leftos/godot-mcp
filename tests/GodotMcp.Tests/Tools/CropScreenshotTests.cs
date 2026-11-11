using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// crop_screenshot: a crop of any PNG on a local drive, written beside an untracked source under the project's
/// .godot/godot-mcp/ or in the .tmp folder of its checkout or main checkout, else into the checkout's .tmp, with no game and
/// no Godot; the tool runs only on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CropScreenshotTests : IDisposable
{
    private const int Width = 6;
    private const int Height = 4;
    private const string Relative = ".godot/godot-mcp/screenshots/shot.png";

    /// <summary><c>ERROR_SHARING_VIOLATION</c>.</summary>
    private const int SharingViolation = 32;
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
    public void AnImageElsewhereInTheProjectIsCroppedIntoTheScreenshotsFolder()
    {
        string outside = Path.Combine(_project, "shot.png");
        File.Copy(_shot, outside);

        (JsonElement relative, _) = Crop("shot.png", new CropRect(0, 0, 1, 1));
        (JsonElement absolute, _) = Crop(outside, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(_folder, "shot-crop-1.png"), relative.GetProperty("path").GetString());
        Assert.Equal(Path.Combine(_folder, "shot-crop-2.png"), absolute.GetProperty("path").GetString());
        Assert.Equal(["project.godot", "shot.png"], Directory.GetFiles(_project).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AMissingImageIsRefused()
    {
        string missing = Path.Combine(_folder, "gone.png");

        McpException refused = Refused(missing, new CropRect(0, 0, 1, 1));

        Assert.Equal($"{missing} does not exist", refused.Message);
    }

    [Fact]
    public void AnImageThatIsASymbolicLinkLeadingOutIsCroppedIntoTheScreenshotsFolder()
    {
        string outside = Directory.CreateDirectory(_temp.Combine("outside")).FullName;
        string secret = Path.Combine(outside, "secret.png");
        File.Copy(_shot, secret);
        string link = Path.Combine(_folder, "link.png");
        SymbolicLinkOrSkip(link, secret, folder: false);

        (JsonElement result, _) = Crop(link, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(_folder, "link-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["secret.png"], Directory.GetFiles(outside).Select(Path.GetFileName));
    }

    [Fact]
    public void AFileHeldOpenBehindALinkIsRefusedWithoutNamingWhereTheLinkLed()
    {
        string outside = Directory.CreateDirectory(_temp.Combine("outside")).FullName;
        string secret = Path.Combine(outside, "secret.png");
        File.Copy(_shot, secret);
        string junction = Path.Combine(_folder, "linked");
        ScreenshotPathValidationTests.MakeJunction(junction, outside);
        string image = Path.Combine(junction, "secret.png");

        McpException refused;
        using (new FileStream(secret, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            refused = Refused(image, new CropRect(0, 0, 1, 1));
        }

        Assert.Equal($"reading {image} failed: {new Win32Exception(SharingViolation).Message}", refused.Message);
        Assert.DoesNotContain(outside, refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnImageWithASecondNameIsCroppedAndItsOtherFolderGainsNothing()
    {
        using TempDirectory temp = new();
        using TempDirectory other = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string secret = Path.Combine(other.Path, "secret.png");
        File.Copy(_shot, secret);
        string link = Path.Combine(Directory.CreateDirectory(Path.Combine(checkout, ".tmp")).FullName, "hard.png");
        ScreenshotPathValidationTests.MakeHardLink(link, secret);

        (JsonElement result, _) = CropIn(project, link, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(checkout, ".tmp"), Path.GetDirectoryName(result.GetProperty("path").GetString()));
        Assert.Equal(["secret.png"], Directory.GetFiles(other.Path).Select(Path.GetFileName));
        Assert.All(UntrackedOrChanged(checkout), line => Assert.StartsWith("?? .tmp/", line, StringComparison.Ordinal));
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
    public void ARelativeImageThatClimbsOutOfTheFolderIsCroppedIntoTheScreenshotsFolder()
    {
        string above = Path.Combine(_project, ".godot", "shot.png");
        File.Copy(_shot, above);

        (JsonElement result, _) = Crop(".godot/godot-mcp/../shot.png", new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(_folder, "shot-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["shot.png"], Directory.GetFiles(Path.Combine(_project, ".godot")).Select(Path.GetFileName));
    }

    [Fact]
    public void AClimbFromTheCaptureFolderToANonPngIsRefusedAsNoPng()
    {
        string projectGodot = Path.Combine(_project, "project.godot");

        McpException refused = Refused(".godot/godot-mcp/../../project.godot", new CropRect(0, 0, 1, 1));

        Assert.Equal(NotPng(projectGodot), refused.Message);
    }

    [Fact]
    public void ADotDotSegmentIsCroppedByTheFileItReaches()
    {
        string source = Directory.CreateDirectory(Path.Combine(_project, "src")).FullName;
        string image = Path.Combine(source, "x.png");
        File.Copy(_shot, image);
        string climbing = Path.Combine(_project, ".tmp", "..", "src", "x.png");

        (JsonElement relative, _) = Crop(".tmp/../src/x.png", new CropRect(0, 0, 1, 1));
        (JsonElement absolute, _) = Crop(climbing, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(_folder, "x-crop-1.png"), relative.GetProperty("path").GetString());
        Assert.Equal(Path.Combine(_folder, "x-crop-2.png"), absolute.GetProperty("path").GetString());
        Assert.Equal(["x.png"], Directory.GetFiles(source).Select(Path.GetFileName));
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

        string written = ScreenshotCropTool.Write(
            new CropFolder(_project, _folder, MayCreate: false),
            "shot",
            Source().Encode(),
            path => path != taken && File.Exists(path)
        );

        Assert.Equal(Path.Combine(_folder, "shot-crop-2.png"), written);
        Assert.Equal([1, 2], File.ReadAllBytes(taken));
    }

    [Fact]
    public void ABesideFolderThatIsAJunctionAtWriteTimeIsRefused()
    {
        string outside = Directory.CreateDirectory(_temp.Combine("outside")).FullName;
        string junction = Path.Combine(_folder, "linked");
        ScreenshotPathValidationTests.MakeJunction(junction, outside);

        McpException refused = Assert.Throws<McpException>(() =>
            ScreenshotCropTool.Write(new CropFolder(_project, junction, MayCreate: false), "shot", Source().Encode(), Path.Exists)
        );

        Assert.Equal(
            $"the crop's folder {junction} passes the link at {junction}; crop_screenshot writes only into folders it reaches without a link",
            refused.Message
        );
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Equal(["shot.png"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void AFolderNamedLikeTheNextCropIsSkipped()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "shot-crop-1.png"));

        (JsonElement result, _) = Crop(Relative, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(_folder, "shot-crop-2.png"), result.GetProperty("path").GetString());
        Assert.True(File.Exists(Path.Combine(_folder, "shot-crop-2.png")));
    }

    [Fact]
    public void ASaveScreenshotPathInAForgedWorktreeIsRefused()
    {
        using TempDirectory temp = new();
        using TempDirectory elsewhere = new();
        string project = ForgedWorktree(temp, elsewhere);
        string shot = Path.Combine(elsewhere.Path, ".tmp", "x.png");

        McpException refused = Assert.Throws<McpException>(() => ScreenshotTarget.Resolve(project, shot, false, NullLogger.Instance));

        Assert.Equal($"path {shot} resolves outside the project {project}; save inside the project", refused.Message);
    }

    [Fact]
    public void ACaptureFramesPathInAForgedWorktreeIsRefused()
    {
        using TempDirectory temp = new();
        using TempDirectory elsewhere = new();
        string project = ForgedWorktree(temp, elsewhere);

        McpException refused = Assert.Throws<McpException>(() =>
            FrameTargets.Resolve(OutputPlaces.Find(project, NullLogger.Instance), ".tmp/anim.png", 3, overwrite: false)
        );

        Assert.Equal(
            $".tmp/anim.png is refused: capture_frames writes only into a git checkout's .tmp folder, and {project} is in no git checkout",
            refused.Message
        );
    }

    [Fact]
    public void ARecordingDestinationInAForgedWorktreeIsRefused()
    {
        using TempDirectory temp = new();
        using TempDirectory elsewhere = new();
        string project = ForgedWorktree(temp, elsewhere);
        string clip = Path.Combine(elsewhere.Path, ".tmp", "a.mp4");

        McpException refused = Assert.Throws<McpException>(() =>
            OutputDestination.Resolve(OutputPlaces.Find(project, NullLogger.Instance), clip, ".mp4", false)
        );

        Assert.Equal($"path {clip} resolves outside the project {project}; save inside the project", refused.Message);
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

        Assert.True(File.Exists(Path.Combine(_folder, "link-crop-1.png")));
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
    public void ATrackedScreenshotIsCroppedIntoTheCheckoutsTmp()
    {
        using TempDirectory temp = new();
        string project = Game(temp.Path, "game");
        string image = Path.Combine(project, "art.png");
        File.WriteAllBytes(image, Source().Encode());
        Git.InitAndCommitAll(temp.Path);

        (JsonElement result, _) = CropIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(temp.Path, ".tmp", "art-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["?? .tmp/art-crop-1.png"], UntrackedOrChanged(temp.Path));
    }

    [Fact]
    public void APngOutsideTheCheckoutIsCroppedIntoTheCheckoutsTmp()
    {
        using TempDirectory temp = new();
        using TempDirectory scratchpad = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string image = Path.Combine(scratchpad.Path, "x.png");
        File.WriteAllBytes(image, Source().Encode());

        (JsonElement result, PngImage cropped) = CropIn(project, image, new CropRect(1, 2, 3, 2));

        Assert.Equal(Path.Combine(checkout, ".tmp", "x-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(Expected(1, 2, 3, 2, 1), cropped.Pixels);
        Assert.Equal(["x.png"], Directory.GetFiles(scratchpad.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void AnImageInDotGitIsCroppedIntoTheCheckoutsTmp()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string git = Directory.CreateDirectory(Path.Combine(checkout, ".git", "crops")).FullName;
        string image = Path.Combine(git, "x.png");
        File.WriteAllBytes(image, Source().Encode());

        (JsonElement result, _) = CropIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(checkout, ".tmp", "x-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["x.png"], Directory.GetFiles(git).Select(Path.GetFileName));
    }

    [Fact]
    public void AnImageInAWorktreeInsideTheMainCheckoutsTmpIsCroppedIntoItsOwnTmp()
    {
        using TempDirectory main = new();
        Git.InitAndCommitAll(main.Path);
        string worktree = Path.Combine(Directory.CreateDirectory(Path.Combine(main.Path, ".tmp")).FullName, "wt");
        Git.Run(main.Path, "worktree", "add", "--quiet", worktree);
        string project = Game(worktree, "game");
        string image = Path.Combine(project, "art.png");
        File.WriteAllBytes(image, Source().Encode());

        (JsonElement result, _) = CropIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(worktree, ".tmp", "art-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["art.png", "project.godot"], Directory.GetFiles(project).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AForceTrackedImageInTmpIsCroppedIntoTheCheckoutsTmp()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string image = Path.Combine(Directory.CreateDirectory(Path.Combine(checkout, ".tmp", "sub")).FullName, "x.png");
        File.WriteAllBytes(image, Source().Encode());
        Git.Run(checkout, "add", "--force", ".tmp/sub/x.png");

        (JsonElement result, _) = CropIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(checkout, ".tmp", "x-crop-1.png"), result.GetProperty("path").GetString());
    }

    [Fact]
    public void ATrackedImageInTheGodotMcpFolderIsCroppedIntoTheCheckoutsTmp()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string folder = Directory.CreateDirectory(Path.Combine(project, ".godot", "godot-mcp")).FullName;
        string image = Path.Combine(folder, "x.png");
        File.WriteAllBytes(image, Source().Encode());
        Git.Run(checkout, "add", "--force", "game/.godot/godot-mcp/x.png");

        (JsonElement result, _) = CropIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(checkout, ".tmp", "x-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["x.png"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void ATrackedImageOfACheckoutNestedInTmpIsCroppedIntoTheCheckoutsTmp()
    {
        using TempDirectory main = new();
        string project = Game(main.Path, "game");
        File.WriteAllBytes(Path.Combine(project, "art.png"), Source().Encode());
        Git.InitAndCommitAll(main.Path);
        Directory.CreateDirectory(Path.Combine(main.Path, ".tmp"));
        Git.Run(main.Path, "worktree", "add", "--quiet", Path.Combine(main.Path, ".tmp", "wt"));
        string image = Path.Combine(main.Path, ".tmp", "wt", "game", "art.png");

        (JsonElement result, _) = CropIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(main.Path, ".tmp", "art-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(
            ["art.png", "project.godot"],
            Directory.GetFiles(Path.GetDirectoryName(image)!).Select(Path.GetFileName).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void AnImageUnderACheckoutTheProjectIsNotUnderIsCroppedIntoTheScreenshotsFolder()
    {
        using TempDirectory temp = new();
        using TempDirectory elsewhere = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        Git.Run(checkout, "config", "core.worktree", elsewhere.Path);
        string tmp = Directory.CreateDirectory(Path.Combine(elsewhere.Path, ".tmp")).FullName;
        string image = Path.Combine(tmp, "x.png");
        File.WriteAllBytes(image, Source().Encode());

        (JsonElement result, _) = CropIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(ScreenshotTarget.CaptureFolder(project), "x-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["x.png"], Directory.GetFiles(tmp).Select(Path.GetFileName));
    }

    [Fact]
    public void ACaseOnlyRenamedTrackedImageIsCroppedIntoTheCheckoutsTmp()
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

        (JsonElement result, _) = CropIn(project, renamed, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(checkout, ".tmp", "art-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["art.png"], Directory.GetFiles(shots).Select(Path.GetFileName));
    }

    [Fact]
    public void AnImageTrackedByTheOuterCheckoutUnderANestedRepoIsCroppedIntoTheCheckoutsTmp()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string sub = Directory.CreateDirectory(Path.Combine(checkout, ".tmp", "sub")).FullName;
        string image = Path.Combine(sub, "x.png");
        File.WriteAllBytes(image, Source().Encode());
        Git.Run(checkout, "add", "--force", ".tmp/sub/x.png");
        Git.Run(sub, "init", "--quiet", "--initial-branch=main");

        (JsonElement result, _) = CropIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(checkout, ".tmp", "x-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["x.png"], Directory.GetFiles(sub).Select(Path.GetFileName));
    }

    [Fact]
    public void AnImageBelowAGitFileInsideACheckoutIsCroppedIntoTheCheckoutsTmpWithoutAskingThatGit()
    {
        using TempDirectory temp = new();
        using TempDirectory other = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string elsewhere = Repository(other, "a.txt");
        string sub = Directory.CreateDirectory(Path.Combine(checkout, ".tmp", "sub")).FullName;
        string image = Path.Combine(sub, "x.png");
        File.WriteAllBytes(image, Source().Encode());

        // A git file names the repository git would use in sub; that repository, a local one, would answer that x.png is
        // untracked and let the crop go beside it, so a crop into the checkout's .tmp shows it was never asked. A git file
        // naming a network path would make that git reach the network.
        File.WriteAllText(Path.Combine(sub, ".git"), $"gitdir: {Path.Combine(elsewhere, ".git").Replace('\\', '/')}\n");

        (JsonElement result, _) = CropIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(checkout, ".tmp", "x-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal([".git", "x.png"], Directory.GetFiles(sub).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnUntrackedImageInANestedRepoInsideTheCheckoutsTmpIsCroppedIntoTheCheckoutsTmp()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string sub = Directory.CreateDirectory(Path.Combine(checkout, ".tmp", "sub")).FullName;
        string image = Path.Combine(sub, "x.png");
        File.WriteAllBytes(image, Source().Encode());
        Git.Run(sub, "init", "--quiet", "--initial-branch=main");

        (JsonElement result, _) = CropIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(checkout, ".tmp", "x-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["x.png"], Directory.GetFiles(sub).Select(Path.GetFileName));
    }

    [Fact]
    public void ATrackedImageWithGlobCharactersInItsNameIsCroppedIntoTheCheckoutsTmp()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string files = Directory.CreateDirectory(Path.Combine(checkout, ".tmp", "sub")).FullName;
        string tracked = Path.Combine(files, "x[1].png");
        File.WriteAllBytes(tracked, Source().Encode());
        Git.Run(checkout, "--literal-pathspecs", "add", "--force", ".tmp/sub/x[1].png");
        string plain = Path.Combine(files, "x1.png");
        File.WriteAllBytes(plain, Source().Encode());

        (JsonElement redirected, _) = CropIn(project, tracked, new CropRect(0, 0, 1, 1));
        (JsonElement beside, _) = CropIn(project, plain, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(checkout, ".tmp", "x[1]-crop-1.png"), redirected.GetProperty("path").GetString());
        Assert.Equal(Path.Combine(files, "x1-crop-1.png"), beside.GetProperty("path").GetString());
    }

    [Fact]
    public void ACaseDifferingSpellingFollowsTheVolume()
    {
        string upper = Path.Combine(_project.ToUpperInvariant(), ".GODOT", "GODOT-MCP", "SCREENSHOTS", "shot.png");

        (JsonElement result, _) = Crop(upper, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(_folder, "shot-crop-1.png"), result.GetProperty("path").GetString());
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
    public void AShortNameSpellingPointingOutOfTheFoldersIsReadAndCroppedIntoTheCheckoutsTmp()
    {
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
        (JsonElement result, _) = CropIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(files, "x-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["x.png"], Directory.GetFiles(outside).Select(Path.GetFileName));
    }

    [Fact]
    public void AnExtendedLengthSpellingIsRefused()
    {
        string extended = @"\\?\" + _shot;

        McpException refused = Refused(extended, new CropRect(0, 0, 1, 1));

        Assert.Equal(NotLocal("image", extended), refused.Message);
    }

    [Fact]
    public void AnExtendedLengthUncSpellingIsRefused()
    {
        string extended = @"\\?\UNC\server\share\x.png";

        McpException refused = Refused(extended, new CropRect(0, 0, 1, 1));

        Assert.Equal(NotLocal("image", extended), refused.Message);
    }

    [Theory]
    [InlineData(@"\\192.0.2.1\s\x.png")]
    [InlineData("//192.0.2.1/s/x.png")]
    [InlineData(@"\\?\UNC\192.0.2.1\s\x.png")]
    [InlineData(@"\\.\UNC\192.0.2.1\s\x.png")]
    [InlineData(@"\??\UNC\192.0.2.1\s\x.png")]
    [InlineData(@"\\?\{drive}\x.png")]
    [InlineData(@"\??\{drive}\x.png")]
    [InlineData(@"\\.\{drive}\x.png")]
    [InlineData(@"\\?\GLOBALROOT\Device\Mup\192.0.2.1\s\x.png")]
    [InlineData(@"\\localhost\c$\x.png")]
    [InlineData(@"\\192.0.2.1@SSL\DavWWWRoot\x.png")]
    [InlineData(@"\\.\pipe\x.png")]
    public void ANonDrivePathIsRefusedBeforeAnyIo(string spelling)
    {
        string image = spelling.Replace("{drive}", Drive(), StringComparison.Ordinal);

        McpException refused = Refused(image, new CropRect(0, 0, 1, 1));

        Assert.Equal(NotLocal("image", image), refused.Message);
    }

    [Theory]
    [InlineData("{drive}a.png")]
    [InlineData(@"\a.png")]
    public void ADriveRelativeOrRootRelativePathIsRefused(string spelling)
    {
        string image = spelling.Replace("{drive}", Drive(), StringComparison.Ordinal);

        McpException refused = Refused(image, new CropRect(0, 0, 1, 1));

        Assert.Equal(
            $"image {image} is relative to a drive's current folder or the current drive's root; give a full path or one relative "
                + "to the project folder",
            refused.Message
        );
    }

    [Fact]
    public void AnAlternateStreamIsRefused()
    {
        McpException refused = Refused("shot.png:x.png", new CropRect(0, 0, 1, 1));

        Assert.Equal("image shot.png:x.png must not contain ':' after the drive (an NTFS alternate stream)", refused.Message);
    }

    [Theory]
    [InlineData("CON.png", "CON")]
    [InlineData("NUL.png", "NUL")]
    [InlineData("COM1.png", "COM1")]
    public void ADeviceNameIsRefused(string image, string device)
    {
        McpException refused = Refused(image, new CropRect(0, 0, 1, 1));

        Assert.Equal($"path {image} names the Windows device {device}; pick another file name", refused.Message);
    }

    [Theory]
    [InlineData(CropSource.DriveRemote, @"image Z:\x.png is on the network drive Z:; crop_screenshot reads only from a local drive")]
    [InlineData(CropSource.DriveNoRootDir, @"image Z:\x.png is on the drive Z:, which does not exist")]
    public void AMappedNetworkDriveOrAMissingDriveIsRefusedBeforeAnyIo(uint type, string message)
    {
        List<string> asked = [];

        McpException refused = Assert.Throws<McpException>(() => CropSource.LocalPath("image", @"Z:\x.png", null, Faked("Z:", type, asked)));

        Assert.Equal(message, refused.Message);
        Assert.Equal([@"Z:\"], asked);
    }

    [Theory]
    [InlineData(CropSource.DriveRemote)]
    [InlineData(CropSource.DriveNoRootDir)]
    public void ALinkToANetworkDriveOrAMissingDriveIsNotFollowed(uint type)
    {
        // The link leads to a real file on the test's own drive, which the faked answer calls a network drive or none: only the
        // check on the link's target in the walk can refuse it, since the walk asks no drive type for its own start.
        string link = Path.Combine(_folder, "q.png");
        SymbolicLinkOrSkip(link, _shot, folder: false);
        List<string> asked = [];
        try
        {
            McpException refused = Assert.Throws<McpException>(() => CropSource.Walk("image", link, link, Faked(Drive(), type, asked)));

            Assert.Equal(type == CropSource.DriveRemote ? LeadsToNetwork(link, link) : $"{link} does not exist", refused.Message);
            Assert.Contains(Drive() + @"\", asked);
        }
        finally
        {
            // The link is removed so nothing after the test can open through it.
            File.Delete(link);
        }
    }

    [Fact]
    public void AHandleThatReachedAnotherFileThanTheWalkIsRefused()
    {
        string other = Path.Combine(_folder, "other.png");
        File.Copy(_shot, other);
        using FileStream stream = new(_shot, FileMode.Open, FileAccess.Read, FileShare.Read);

        McpException refused = Assert.Throws<McpException>(() => CropSource.CheckHandle(stream.SafeFileHandle, other, other));

        Assert.Equal($"{other} changed while it was read; crop_screenshot read nothing from it", refused.Message);
    }

    [Fact]
    public void AWriteThatFindsEveryNameTakenIsRefusedAndLeavesNoTemporaryFile()
    {
        for (int n = 1; n <= 100; n++)
        {
            File.WriteAllBytes(Path.Combine(_folder, $"shot-crop-{n}.png"), [1]);
        }

        McpException refused = Assert.Throws<McpException>(() =>
            ScreenshotCropTool.Write(new CropFolder(_project, _folder, MayCreate: false), "shot", Source().Encode(), _ => false)
        );

        Assert.StartsWith($"writing the crop to {Path.Combine(_folder, "shot-crop-100.png")} failed: ", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder, ".godot-mcp-*.tmp"));
    }

    [Fact]
    public void AProjectPathThatIsALinkToANetworkDriveIsRefusedBeforeAnyProbe()
    {
        string drive = FreeDrive();
        string link = _temp.Combine("gamelink");
        SymbolicLinkOrSkip(link, drive + @"\game", folder: true);
        List<string> asked = [];
        try
        {
            McpException refused = Assert.Throws<McpException>(() => ScreenshotCropTool.Places(link, Faked(drive, CropSource.DriveRemote, asked)));

            Assert.Equal(
                $"projectPath {link} leads through the link at {link} to a network path; crop_screenshot does not follow links to network paths",
                refused.Message
            );
            Assert.Contains(drive + @"\", asked);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void AProjectPathThatIsAJunctionToALocalFolderIsCropped()
    {
        string link = _temp.Combine("gamelink");
        ScreenshotPathValidationTests.MakeJunction(link, _project);

        (JsonElement result, _) = CropIn(link, Relative, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(_folder, "shot-crop-1.png"), result.GetProperty("path").GetString());
    }

    [Fact]
    public void APngSavedAsAFileNamedTmpIsNeverCroppedBesideIt()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string tmp = Path.Combine(checkout, ".tmp");
        File.WriteAllBytes(tmp, Source().Encode());

        McpException refused = RefusedIn(project, tmp, new CropRect(0, 0, 1, 1));

        Assert.Equal($"the crop's folder {tmp} passes {tmp}, which is no folder", refused.Message);
        Assert.False(File.Exists(Path.Combine(checkout, "-crop-1.png")));
    }

    [Fact]
    public void ASymbolicLinkToANetworkPathIsRefusedBeforeItIsOpened()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string link = Path.Combine(Directory.CreateDirectory(Path.Combine(checkout, ".tmp")).FullName, "x.png");
        SymbolicLinkOrSkip(link, @"\\192.0.2.1\s\x.png", folder: false);
        try
        {
            McpException refused = RefusedIn(project, link, new CropRect(0, 0, 1, 1));

            Assert.Equal(LeadsToNetwork(link, link), refused.Message);
            Assert.DoesNotContain("192.0.2.1", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            // The link is removed so nothing after the test can open through it.
            File.Delete(link);
        }
    }

    [Fact]
    public void AFolderLinkToANetworkPathIsRefusedBeforeAnyProbe()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string link = Path.Combine(Directory.CreateDirectory(Path.Combine(checkout, ".tmp")).FullName, "dirlink");
        SymbolicLinkOrSkip(link, @"\\192.0.2.1\s", folder: true);
        string image = Path.Combine(link, "x.png");
        try
        {
            McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

            Assert.Equal(LeadsToNetwork(image, link), refused.Message);
            Assert.DoesNotContain("192.0.2.1", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            // The temp folder's cleanup lists every file below it to clear its attributes, and that listing would enter the
            // link to the network path (the recursive delete after it would not), so the link goes first.
            Directory.Delete(link);
        }
    }

    [Fact]
    public void ALinkChainLongerThan63IsRefused()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string files = Directory.CreateDirectory(Path.Combine(checkout, ".tmp")).FullName;
        string first = Path.Combine(files, "a.png");
        string second = Path.Combine(files, "b.png");
        SymbolicLinkOrSkip(first, second, folder: false);
        SymbolicLinkOrSkip(second, first, folder: false);

        McpException refused = RefusedIn(project, first, new CropRect(0, 0, 1, 1));

        Assert.Equal($"image {first} leads through more than 63 links; crop_screenshot follows at most 63", refused.Message);
    }

    [Fact]
    public void ANonPngIsRefusedBeforeItIsReadWhole()
    {
        string big = Path.Combine(_folder, "big.png");
        using (FileStream stream = new(big, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(ScreenshotCropTool.MaxSourceBytes + 1);
        }

        McpException refused = Refused(big, new CropRect(0, 0, 1, 1));

        Assert.Equal(
            "crop_screenshot reads only 8-bit RGB or RGBA PNGs that are not interlaced, as Godot saves them; "
                + $"{big} is not a PNG (its first bytes are not the PNG signature)",
            refused.Message
        );
    }

    [Fact]
    public void AProjectPathOnANetworkPathIsRefusedBeforeAnyIo()
    {
        const string Project = @"\\192.0.2.1\s\proj";

        McpException refused = RefusedIn(Project, "x.png", new CropRect(0, 0, 1, 1));

        Assert.Equal(NotLocal("projectPath", Project), refused.Message);
    }

    [Fact]
    public void ATmpJunctionLeadingOutsideIsReadAndItsCropGoesToTheScreenshotsFolder()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string outside = Directory.CreateDirectory(temp.Combine("outside")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "x.png"), Source().Encode());
        string tmp = Path.Combine(checkout, ".tmp");
        ScreenshotPathValidationTests.MakeJunction(tmp, outside);

        (JsonElement result, _) = CropIn(project, Path.Combine(tmp, "x.png"), new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(ScreenshotTarget.CaptureFolder(project), "x-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["x.png"], Directory.GetFiles(outside).Select(Path.GetFileName));
    }

    [Fact]
    public void AGodotMcpJunctionLeadingOutsideIsReadAndItsCropGoesToTheCheckoutsTmp()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string outside = Directory.CreateDirectory(temp.Combine("outside")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "x.png"), Source().Encode());
        Directory.CreateDirectory(Path.Combine(project, ".godot"));
        string godotMcp = Path.Combine(project, ".godot", "godot-mcp");
        ScreenshotPathValidationTests.MakeJunction(godotMcp, outside);

        (JsonElement result, _) = CropIn(project, Path.Combine(godotMcp, "x.png"), new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(checkout, ".tmp", "x-crop-1.png"), result.GetProperty("path").GetString());
        Assert.Equal(["x.png"], Directory.GetFiles(outside).Select(Path.GetFileName));
    }

    [Fact]
    public void AScreenshotsFolderThatPassesALinkIsRefusedWhenTheTmpIsALinkToo()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string outsideTmp = Directory.CreateDirectory(temp.Combine("outside-tmp")).FullName;
        string outsideMcp = Directory.CreateDirectory(temp.Combine("outside-mcp")).FullName;
        ScreenshotPathValidationTests.MakeJunction(Path.Combine(checkout, ".tmp"), outsideTmp);
        Directory.CreateDirectory(Path.Combine(project, ".godot"));
        string godotMcp = Path.Combine(project, ".godot", "godot-mcp");
        ScreenshotPathValidationTests.MakeJunction(godotMcp, outsideMcp);
        string image = Path.Combine(project, "art.png");
        File.WriteAllBytes(image, Source().Encode());

        McpException refused = RefusedIn(project, image, new CropRect(0, 0, 1, 1));

        Assert.Equal(PassesLink(ScreenshotTarget.CaptureFolder(project), godotMcp), refused.Message);
        Assert.Empty(Directory.GetFileSystemEntries(outsideTmp));
        Assert.Empty(Directory.GetFileSystemEntries(outsideMcp));
    }

    [Fact]
    public void AnImageInAFolderEndingInADotNeverWritesIntoItsUndottedSibling()
    {
        using TempDirectory temp = new();
        string checkout = Repository(temp, "game/project.godot");
        string project = Path.Combine(checkout, "game");
        string files = Directory.CreateDirectory(Path.Combine(checkout, ".tmp")).FullName;
        string sibling = Directory.CreateDirectory(Path.Combine(files, "a")).FullName;
        string dotted = @"\\?\" + Path.Combine(files, "a.");
        Directory.CreateDirectory(dotted);
        try
        {
            File.WriteAllBytes(Path.Combine(dotted, "x.png"), Source().Encode());
            var places = OutputPlaces.Find(project, NullLogger.Instance);
            string final = Path.Combine(places.FinalCheckout!, ".tmp", "a.", "x.png");

            // No spelling the read accepts reaches a folder whose name ends in a dot, so the choice is asked of the final
            // path such a folder would give, and the crop it chooses is written.
            CropOrigin origin = CropSource.Place(places, final, NullLogger.Instance);
            string written = ScreenshotCropTool.Write(origin.Output, "x", Source().Encode(), Path.Exists);

            Assert.Equal(new CropFolder(places.FinalCheckout!, Path.Combine(places.FinalCheckout!, ".tmp"), MayCreate: true), origin.Output);
            Assert.Equal(Path.Combine(files, "x-crop-1.png"), written);
            Assert.Empty(Directory.GetFileSystemEntries(sibling));
            Assert.Equal(["x.png"], Directory.GetFiles(dotted).Select(Path.GetFileName));
        }
        finally
        {
            // A name ending in a dot is reached only in the \\?\ form, which the temp folder's cleanup does not use.
            File.Delete(Path.Combine(dotted, "x.png"));
            Directory.Delete(dotted);
        }
    }

    [Fact]
    public void AStemWithTildeDigitIsCropped()
    {
        string tilde = Path.Combine(_folder, "shot~1.png");
        File.Copy(_shot, tilde);

        (JsonElement result, _) = Crop(tilde, new CropRect(0, 0, 1, 1));

        Assert.Equal(Path.Combine(_folder, "shot~1-crop-1.png"), result.GetProperty("path").GetString());
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

    /// <summary>
    /// A checkout in <paramref name="temp"/> tracking game/project.godot whose git names <paramref name="elsewhere"/> as its
    /// working tree (core.worktree), so git reports a checkout the project does not lie under; returns the project.
    /// </summary>
    private static string ForgedWorktree(TempDirectory temp, TempDirectory elsewhere)
    {
        string checkout = Repository(temp, "game/project.godot");
        Git.Run(checkout, "config", "core.worktree", elsewhere.Path);
        Directory.CreateDirectory(Path.Combine(elsewhere.Path, ".tmp"));
        return Path.Combine(checkout, "game");
    }

    /// <summary>What <c>git status --porcelain</c> lists in <paramref name="checkout"/>, every untracked file named.</summary>
    private static string[] UntrackedOrChanged(string checkout) =>
        Git.Run(checkout, "status", "--porcelain", "--untracked-files=all")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NotPng(string source) =>
        "crop_screenshot reads only 8-bit RGB or RGBA PNGs that are not interlaced, as Godot saves them; "
        + $"{source} is not a PNG (its first bytes are not the PNG signature)";

    private static string PassesLink(string folder, string link) =>
        $"the crop's folder {folder} passes the link at {link}; crop_screenshot writes only into folders it reaches without a link";

    private static string NotLocal(string label, string given) =>
        $@"{label} {given} is not a path on a local drive; crop_screenshot takes only a full path that begins with a drive letter, never a network, "
        + @"device or \\?\ path";

    private static string LeadsToNetwork(string image, string link) =>
        $"image {image} leads through the link at {link} to a network path; crop_screenshot does not follow links to network paths";

    /// <summary>The drive the test's temp folder lies on, as <c>X:</c>.</summary>
    private string Drive() => _project[..2];

    /// <summary>
    /// A drive letter, as <c>Q:</c>, that names no drive on this machine, so a check removed by mistake finds nothing there
    /// rather than reaching a real network drive.
    /// </summary>
    private static string FreeDrive()
    {
        string? free = "QRSTUVWXYJKLMNOP"
            .Select(letter => $"{letter}:")
            .FirstOrDefault(drive => FinalPath.DriveType(drive + @"\") == CropSource.DriveNoRootDir);
        Assert.SkipWhen(free is null, "every drive letter this test could fake is in use");
        return free!;
    }

    /// <summary>A drive-type answer that gives <paramref name="type"/> for <paramref name="drive"/> and the real one for any other.</summary>
    private static Func<string, uint> Faked(string drive, uint type, List<string> asked) =>
        root =>
        {
            asked.Add(root);
            return string.Equals(root, drive + @"\", StringComparison.OrdinalIgnoreCase) ? type : FinalPath.DriveType(root);
        };

    /// <summary>Makes a symbolic link, or skips the test where this run lacks the privilege to.</summary>
    private static void SymbolicLinkOrSkip(string link, string target, bool folder)
    {
        try
        {
            if (folder)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"creating a symbolic link needs a privilege this run lacks: {e.Message}");
        }
    }

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
