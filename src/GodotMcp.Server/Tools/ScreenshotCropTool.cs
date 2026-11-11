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
/// crop_screenshot: cuts a rectangle out of any PNG on a local drive, scaled up nearest-neighbour, with no session and no
/// Godot, on Windows only. The crop is written beside an untracked source under the project's .godot/godot-mcp/ or in the
/// .tmp folder of its checkout or main checkout, else into the checkout's .tmp folder (the project's
/// .godot/godot-mcp/screenshots/ with no checkout); the source is never changed.
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
        "Crops a PNG on a local drive (a take_screenshot capture, a frame capture_frames or wait_for saved, a run_scratches "
            + "step shot, a committed screenshot or any other PNG, tracked by git or not, inside the checkout or outside it; "
            + "never a network path), with no session and no re-capture, and scales the crop up nearest-neighbour to read small "
            + "detail. rect is in the PNG's own pixels from its top-left, not viewport coordinates. A rect partly outside the "
            + "image is clipped to it; one wholly outside is refused, and so is a crop over 4096 px on an edge after scaling. "
            + "Writes <stem>-crop-<n>.png, n the first free number from 1: beside the source when it is an untracked file under "
            + "the project's .godot/godot-mcp/ or in the .tmp folder of the project's git checkout or main checkout, else into "
            + "the checkout's .tmp folder (the project's .godot/godot-mcp/screenshots/ when the project is in no checkout); the "
            + "source is never changed. Returns the cropped image and {path, width, height, rect}: rect is the source rectangle "
            + "used after clipping. Windows only."
    )]
    public static IEnumerable<ContentBlock> CropScreenshot(
        [Description("The folder that holds the project's project.godot, on a local drive.")] string projectPath,
        [Description(
            "The PNG to crop: absolute or relative to the project folder, on a local drive, never a network path; an 8-bit "
                + "RGB or RGBA PNG as Godot saves it."
        )]
            string image,
        [Description("{x, y, width, height}: the rectangle to keep, in the PNG's own pixels.")] CropRect rect,
        [Description("How many times to repeat each pixel across and down, 1 to 8.")] int scale = 1
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new McpException($"{ToolName} runs only on Windows, where its link and network checks are made");
        }

        CheckArguments(rect, scale);
        OutputPlaces places = Places(projectPath, FinalPath.DriveType);
        string source = Source(places, image);
        (byte[] bytes, string final) = ReadChecked(image, source);
        var original = PngImage.Decode(bytes, source);
        CropRect used = Clip(rect, original, source);
        CheckEdge(used, scale);
        PngImage cropped = original.Crop(used.X, used.Y, used.Width, used.Height, scale);
        byte[] png = cropped.Encode();
        CropOrigin origin = CropSource.Place(places, final, Log.Ambient);
        // The crop is named after the name the caller passed, never the one a link led to.
        string path = Write(origin.Output, Path.GetFileNameWithoutExtension(source), png, Path.Exists);
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

    /// <summary>
    /// The project's folder and the places its outputs may lie in; the folder must be on a local drive, checked before git or
    /// any file call touches it.
    /// </summary>
    /// <exception cref="McpException">
    /// The folder is not on a local drive, holds no project.godot, or a folder has no final path Windows can give.
    /// </exception>
    [SupportedOSPlatform("windows")]
    internal static OutputPlaces Places(string projectPath, Func<string, uint> driveType)
    {
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            string full = CropSource.LocalPath(CropSource.ProjectLabel, projectPath, null, driveType);
            if (!CropSource.Walk(CropSource.ProjectLabel, projectPath, full, driveType).IsFolder)
            {
                throw new McpException($"projectPath {projectPath} is a file; pass the folder that holds the project's project.godot");
            }
        }

        string projectDir;
        try
        {
            projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }

        var places = OutputPlaces.Find(projectDir, Log.Ambient);
        CropSource.CheckPlaces(projectPath, places, driveType);
        return places;
    }

    /// <summary>The image's full path, which must be a path on a local drive.</summary>
    [SupportedOSPlatform("windows")]
    private static string Source(OutputPlaces places, string image) =>
        string.IsNullOrWhiteSpace(image)
            ? throw new McpException("image is empty; pass the path of a PNG on a local drive, absolute or relative to the project folder")
            : CropSource.LocalPath("image", image, places.Project, FinalPath.DriveType);

    /// <summary>
    /// The source's bytes and the path its file finally lies at: the path is walked from its drive's root with every link
    /// checked before it is followed, the walked path is opened read-only, and the one handle must reach the walked path.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (byte[] Bytes, string Final) ReadChecked(string image, string source)
    {
        Walked walked = CropSource.Walk("image", image, source, FinalPath.DriveType);
        if (walked.IsFolder)
        {
            throw new McpException($"{source} is a folder, not a PNG");
        }

        try
        {
            using FileStream stream = new(walked.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string final = CropSource.CheckHandle(stream.SafeFileHandle, source, walked.Path);
            return (CropSource.ReadPng(stream, source), final);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The runtime's message names the path it opened, which a link may have led to: only the error's own text is given.
            throw new McpException($"reading {source} failed: {CropSource.Reason(e)}", e);
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
    /// Writes the crop into <paramref name="target"/>'s folder as the first &lt;stem&gt;-crop-&lt;n&gt;.png that
    /// <paramref name="exists"/> says is free, never replacing an entry: the folder is walked from its root with no link
    /// followed, the bytes go to a new temporary file that must lie in the walked folder, and that file is renamed to the
    /// name, never over an existing entry; a name another writer takes before the rename moves on to the next free one, at
    /// most <see cref="MaxWriteTries"/> times. A failed write deletes its temporary file.
    /// </summary>
    /// <returns>The file written.</returns>
    /// <exception cref="McpException">
    /// The folder passes a link, is gone and may not be made, changed during the write, the write failed, or every try found
    /// its name taken.
    /// </exception>
    [SupportedOSPlatform("windows")]
    internal static string Write(CropFolder target, string stem, byte[] png, Func<string, bool> exists)
    {
        PrepareFolder(target);
        string temporary = WriteTemporary(target.Folder, png);
        try
        {
            return MoveIntoPlace(temporary, target.Folder, stem, exists);
        }
        catch (McpException e)
        {
            throw WithoutTemporary(temporary, e);
        }
    }

    /// <summary>
    /// Walks from <paramref name="target"/>'s root down to its folder reading each entry as it stands: an entry that is a
    /// link, or no folder, is refused; a missing one is made only when the folder may be made, below the entries that passed.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void PrepareFolder(CropFolder target)
    {
        string current = target.Root;
        foreach (string part in Path.GetRelativePath(target.Root, target.Folder).Split('\\').Where(part => part is not ("" or ".")))
        {
            current = Path.Combine(current, part);
            LinkEntry entry = ReadWriteEntry(target, current);
            if (entry.Link is not null || FinalPath.IsNameSurrogate(entry.Tag))
            {
                throw new McpException(
                    $"the crop's folder {target.Folder} passes the link at {current}; {ToolName} writes only into folders it reaches without a link"
                );
            }

            if (!entry.Exists)
            {
                MakeFolder(target, current);
            }
            else if (!entry.IsFolder)
            {
                throw new McpException($"the crop's folder {target.Folder} passes {current}, which is no folder");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static LinkEntry ReadWriteEntry(CropFolder target, string entry)
    {
        try
        {
            return FinalPath.ReadEntry(entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"writing the crop into {target.Folder} failed: {e.Message}", e);
        }
    }

    /// <summary>Makes the missing folder <paramref name="folder"/>, refused when the crop's folder must already exist.</summary>
    private static void MakeFolder(CropFolder target, string folder)
    {
        if (!target.MayCreate)
        {
            throw new McpException($"the crop's folder {target.Folder} no longer exists; {ToolName} does not make it again");
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"writing the crop into {target.Folder} failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// A new temporary file in <paramref name="folder"/> holding <paramref name="png"/>, whose final path must lie in the
    /// folder's long form, so a folder swapped for a link after the walk holds nothing.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string WriteTemporary(string folder, byte[] png)
    {
        string temporary = Path.Combine(folder, $".godot-mcp-{Path.GetRandomFileName()}.tmp");
        FileStream stream;
        try
        {
            stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"writing the crop into {folder} failed: {e.Message}", e);
        }

        try
        {
            using (stream)
            {
                string reached = Path.GetDirectoryName(FinalPath.Of(stream.SafeFileHandle))!;
                if (!string.Equals(reached, FinalPath.LongName(folder), StringComparison.OrdinalIgnoreCase))
                {
                    throw new McpException($"the crop's folder {folder} changed while the crop was written; nothing was written");
                }

                stream.Write(png);
            }

            return temporary;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw WithoutTemporary(temporary, new McpException($"writing the crop into {folder} failed: {e.Message}", e));
        }
        catch (McpException e)
        {
            throw WithoutTemporary(temporary, e);
        }
    }

    /// <summary>Renames the temporary file to the first free crop name, never over an existing entry.</summary>
    [SupportedOSPlatform("windows")]
    private static string MoveIntoPlace(string temporary, string folder, string stem, Func<string, bool> exists)
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
                return FinalPath.Rename(temporary, destination, overwrite: false)
                    ? destination
                    : throw new IOException("the temporary file and the crop lie on different volumes");
            }
            catch (IOException) when (attempt < MaxWriteTries && Path.Exists(destination))
            {
                n++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new McpException($"writing the crop to {destination} failed: {e.Message}", e);
            }
        }
    }

    /// <summary>
    /// Deletes the temporary file a failed write leaves and gives back <paramref name="failure"/>, or, when the file cannot be
    /// deleted, a refusal that names it too.
    /// </summary>
    private static McpException WithoutTemporary(string temporary, McpException failure)
    {
        try
        {
            File.Delete(temporary);
            return failure;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new McpException($"{failure.Message}; its temporary file {temporary} could not be deleted: {e.Message}", failure);
        }
    }

    private static string CropName(string folder, string stem, int n) => Path.Combine(folder, $"{stem}-crop-{n}{ScreenshotTarget.Extension}");
}
