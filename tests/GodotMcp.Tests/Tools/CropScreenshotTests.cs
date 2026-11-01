using System.Text.Json;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.Tests.Tools;

/// <summary>crop_screenshot: a crop of a PNG already under the project's screenshots folder, with no game and no Godot.</summary>
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
    public void AnImageOutsideTheScreenshotsFolderIsRefused()
    {
        string outside = Path.Combine(_project, "shot.png");
        File.Copy(_shot, outside);

        McpException refused = Refused("shot.png", new CropRect(0, 0, 1, 1));
        McpException refusedAbsolute = Refused(outside, new CropRect(0, 0, 1, 1));

        string expected = $"{outside} is not under the project's screenshots folder {_folder}; crop_screenshot reads only the captures there";
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

        Assert.Equal($"{link} has 2 names (hard links); crop_screenshot reads only the captures in the screenshots folder", refused.Message);
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
        string above = Path.Combine(_project, ".godot", "godot-mcp", "shot.png");
        File.Copy(_shot, above);

        McpException refused = Refused(".godot/godot-mcp/screenshots/../shot.png", new CropRect(0, 0, 1, 1));

        Assert.Equal(
            $"{above} is not under the project's screenshots folder {_folder}; crop_screenshot reads only the captures there",
            refused.Message
        );
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

    private (JsonElement Result, PngImage Image) Crop(string image, CropRect rect, int scale = 1)
    {
        ContentBlock[] blocks = [.. ScreenshotCropTool.CropScreenshot(_project, image, rect, scale)];

        Assert.Equal(2, blocks.Length);
        JsonElement result = JsonDocument.Parse(Assert.IsType<TextContentBlock>(blocks[0]).Text).RootElement.Clone();
        ImageContentBlock png = Assert.IsType<ImageContentBlock>(blocks[1]);
        Assert.Equal("image/png", png.MimeType);
        return (result, PngImage.Decode(png.DecodedData.ToArray(), "block"));
    }

    private McpException Refused(string image, CropRect rect, int scale = 1) =>
        Assert.Throws<McpException>(() => ScreenshotCropTool.CropScreenshot(_project, image, rect, scale).ToList());
}
