using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>A rectangle of a saved PNG, in the image's own pixels from its top-left corner.</summary>
internal sealed record CropRect(
    [property: Description("The left edge, in the PNG's own pixels from its left.")] int X,
    [property: Description("The top edge, in the PNG's own pixels from its top.")] int Y,
    [property: Description("The width, in the PNG's own pixels; at least 1.")] int Width,
    [property: Description("The height, in the PNG's own pixels; at least 1.")] int Height
);

/// <summary>
/// crop_screenshot: cuts a rectangle out of a PNG already saved under a project's screenshots folder, scaled up
/// nearest-neighbour, with no session and no Godot; the crop is written beside its source.
/// </summary>
[McpServerToolType]
internal sealed class ScreenshotCropTool
{
    internal const string ToolName = "crop_screenshot";
    private const int MaxScale = 8;
    private const int MaxEdge = 4096;
    private const int MaxWriteTries = 100;

    /// <summary>The longest source read: a 16384 px square of RGBA, unfiltered, and 1 MiB besides.</summary>
    internal const long MaxSourceBytes = (16384L * 16384 * 4) + (1 << 20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = ToolName, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Crops a PNG already captured under the project's .godot/godot-mcp/screenshots/ (a take_screenshot capture, or a frame "
            + "capture_frames or wait_for saved there), with no session and no re-capture, and scales the crop up nearest-neighbour "
            + "to read small detail. rect is in the PNG's own pixels from its top-left, not viewport coordinates. A rect partly "
            + "outside the image is clipped to it; one wholly outside is refused, and so is a crop over 4096 px on an edge after "
            + "scaling. Writes <stem>-crop-<n>.png beside the source, n the first free number from 1, and returns the cropped image "
            + "and {path, width, height, rect}: rect is the source rectangle used after clipping."
    )]
    public static IEnumerable<ContentBlock> CropScreenshot(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description(
            "The PNG to crop: absolute or relative to the project folder, anywhere under the project's "
                + ".godot/godot-mcp/screenshots/; an 8-bit RGB or RGBA PNG as Godot saves it."
        )]
            string image,
        [Description("{x, y, width, height}: the rectangle to keep, in the PNG's own pixels.")] CropRect rect,
        [Description("How many times to repeat each pixel across and down, 1 to 8.")] int scale = 1
    )
    {
        CheckArguments(rect, scale);
        string projectDir = ProjectDir(projectPath);
        string source = Source(projectDir, image);
        (byte[] bytes, string final) = ReadChecked(projectDir, source);
        var original = PngImage.Decode(bytes, source);
        CropRect used = Clip(rect, original, source);
        CheckEdge(used, scale);
        PngImage cropped = original.Crop(used.X, used.Y, used.Width, used.Height, scale);
        byte[] png = cropped.Encode();
        string path = Write(Path.GetDirectoryName(final)!, Path.GetFileNameWithoutExtension(final), png, File.Exists);
        string text = JsonSerializer.Serialize(
            new
            {
                path,
                width = cropped.Width,
                height = cropped.Height,
                rect = used,
            },
            Json
        );
        return [new TextContentBlock { Text = text }, ImageContentBlock.FromBytes(png, "image/png")];
    }

    private static void CheckArguments(CropRect? rect, int scale)
    {
        if (scale is < 1 or > MaxScale)
        {
            throw new McpException($"scale must be 1 to {MaxScale}; got {scale}");
        }

        if (rect is null)
        {
            throw new McpException("rect is required: {x, y, width, height} in the PNG's own pixels");
        }

        if (rect.Width < 1 || rect.Height < 1)
        {
            throw new McpException($"rect width and height must be at least 1; got {rect.Width}x{rect.Height}");
        }
    }

    private static string ProjectDir(string projectPath)
    {
        try
        {
            return SessionRegistry.NormaliseProjectDir(projectPath);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
    }

    /// <summary>The image's full path, which must lie under the project's screenshots folder and exist.</summary>
    private static string Source(string projectDir, string image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            throw new McpException("image is empty; pass a PNG under the project's .godot/godot-mcp/screenshots/");
        }

        string full = Path.GetFullPath(Path.Combine(projectDir, image));
        string folder = ScreenshotTarget.CaptureFolder(projectDir);
        if (HeadlessTools.IsOutside(Path.GetRelativePath(folder, full)))
        {
            throw new McpException($"{full} is not under the project's screenshots folder {folder}; {ToolName} reads only the captures there");
        }

        if (Directory.Exists(full))
        {
            throw new McpException($"{full} is a folder, not a PNG");
        }

        return File.Exists(full) ? full : throw new McpException($"{full} does not exist");
    }

    /// <summary>
    /// The source's bytes and the path its file finally lies at, read through a handle that, on Windows, must reach a
    /// one-name file under the project's screenshots folder; elsewhere the path is the source as given.
    /// </summary>
    private static (byte[] Bytes, string Final) ReadChecked(string projectDir, string source)
    {
        try
        {
            using FileStream stream = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            string final = OperatingSystem.IsWindows() ? CheckIsCapture(stream, projectDir, source) : source;
            CheckLength(stream.Length, source);
            using MemoryStream bytes = new();
            stream.CopyTo(bytes);
            return (bytes.ToArray(), final);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"reading {source} failed: {e.Message}", e);
        }
    }

    /// <summary>Refuses a source longer than any PNG Godot saves: a 16384 px square of RGBA, unfiltered, and 1 MiB besides.</summary>
    /// <exception cref="McpException">The length is over <see cref="MaxSourceBytes"/>.</exception>
    internal static void CheckLength(long length, string source)
    {
        if (length > MaxSourceBytes)
        {
            throw new McpException($"{source} is {length} bytes, larger than any PNG Godot saves; {ToolName} reads at most {MaxSourceBytes} bytes");
        }
    }

    /// <summary>
    /// The source's final path, refusing a source that is no capture by <see cref="ScreenshotTarget.IdentifyCapture"/>: a
    /// link leading out of the screenshots folder, or a file with more than one name.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string CheckIsCapture(FileStream stream, string projectDir, string source)
    {
        string finalProject = Path.TrimEndingDirectorySeparator(FinalPath.Of(projectDir));
        CaptureIdentity identity = ScreenshotTarget.IdentifyCapture(stream.SafeFileHandle, finalProject);
        if (!identity.UnderFolder)
        {
            throw new McpException(
                $"{source} resolves to {identity.Final}, not under the project's screenshots folder "
                    + $"{ScreenshotTarget.CaptureFolder(projectDir)}; {ToolName} reads only the captures there"
            );
        }

        return identity.Names == 1
            ? identity.Final
            : throw new McpException(
                $"{source} has {identity.Names} names (hard links); {ToolName} reads only the captures in the screenshots folder"
            );
    }

    /// <summary>The part of <paramref name="rect"/> inside the image; refused when none is.</summary>
    private static CropRect Clip(CropRect rect, PngImage image, string source)
    {
        long left = Math.Max(rect.X, 0L);
        long top = Math.Max(rect.Y, 0L);
        long right = Math.Min((long)rect.X + rect.Width, image.Width);
        long bottom = Math.Min((long)rect.Y + rect.Height, image.Height);
        if (right <= left || bottom <= top)
        {
            throw new McpException(
                $"rect {rect.X},{rect.Y} {rect.Width}x{rect.Height} lies wholly outside {source}, which is {image.Width}x{image.Height}"
            );
        }

        return new CropRect((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }

    private static void CheckEdge(CropRect used, int scale)
    {
        long width = (long)used.Width * scale;
        long height = (long)used.Height * scale;
        if (width > MaxEdge || height > MaxEdge)
        {
            throw new McpException(
                $"the crop would be {width}x{height}; {ToolName} writes at most {MaxEdge} px on an edge, so use a smaller rect or scale"
            );
        }
    }

    /// <summary>
    /// Writes the crop into <paramref name="folder"/> as the first &lt;stem&gt;-crop-&lt;n&gt;.png that
    /// <paramref name="exists"/> says is free, never replacing a file: a name another writer takes before the move moves on
    /// to the next free one, at most <see cref="MaxWriteTries"/> times.
    /// </summary>
    /// <returns>The file written.</returns>
    /// <exception cref="McpException">The write failed, or every try found its name taken.</exception>
    internal static string Write(string folder, string stem, byte[] png, Func<string, bool> exists)
    {
        int n = 1;
        for (int attempt = 1; ; attempt++)
        {
            while (exists(CropName(folder, stem, n)))
            {
                n++;
            }

            string destination = CropName(folder, stem, n);
            try
            {
                using MemoryStream bytes = new(png);
                OutputDestination.CopyReplacing(bytes, destination, overwrite: false);
                return destination;
            }
            catch (IOException) when (attempt < MaxWriteTries && File.Exists(destination))
            {
                n++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new McpException($"writing the crop to {destination} failed: {e.Message}", e);
            }
        }
    }

    private static string CropName(string folder, string stem, int n) => Path.Combine(folder, $"{stem}-crop-{n}{ScreenshotTarget.Extension}");
}
