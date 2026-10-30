using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>save_screenshot's options.</summary>
internal sealed record SaveScreenshotOptions(
    [property: Description("Replace an existing file at path; without it, saving over an existing file is refused.")] bool Overwrite = false,
    [property: Description(RuntimeTools.ResponseModeDescription)] string ResponseMode = "preview",
    [property: Description(RuntimeTools.PreviewMaxWidthDescription)] int PreviewMaxWidth = 480
);

/// <summary>
/// The file save_screenshot writes a capture to: a .png in one of the <see cref="OutputPlaces"/>, held to the rule of
/// <see cref="OutputDestination"/>. The capture itself and its preview stay in .godot/godot-mcp/screenshots/; only the
/// full-size PNG is copied here.
/// </summary>
internal static class ScreenshotTarget
{
    private const string Extension = ".png";

    /// <summary>The folder the bridge saves a session's captures to.</summary>
    internal static string CaptureFolder(string projectDir) => Path.Combine(projectDir, ".godot", "godot-mcp", "screenshots");

    /// <summary>The file <paramref name="path"/> names, checked before any capture is taken.</summary>
    /// <param name="projectDir">The session's project folder.</param>
    /// <param name="path">Project-relative, res:// or fully qualified.</param>
    /// <param name="overwrite">Whether an existing file there may be replaced.</param>
    /// <param name="logger">Where a missing or stopped git is reported.</param>
    /// <exception cref="McpException">The path breaks the rule, or names an existing file without <paramref name="overwrite"/>.</exception>
    internal static OutputTarget Resolve(string projectDir, string path, bool overwrite, ILogger logger)
    {
        var places = OutputPlaces.Find(projectDir, logger);
        return OutputDestination.Resolve(places, path, Extension, overwrite);
    }

    /// <summary>
    /// Checks the target again and copies the capture to it, creating its folders when missing and replacing the folder
    /// entry there, never writing through a link.
    /// </summary>
    /// <exception cref="McpException">
    /// The capture is not under the session's screenshots folder, by its name or by the file its open handle reaches, or it
    /// has more than one name; the target breaks the rule now; the copy failed; or the target appeared since it was checked
    /// and overwrite is false.
    /// </exception>
    internal static void Save(string capturePath, OutputTarget target, bool overwrite)
    {
        string folder = CaptureFolder(target.Places.Project);
        if (HeadlessTools.IsOutside(Path.GetRelativePath(folder, Path.GetFullPath(capturePath))))
        {
            throw new McpException($"the capture {capturePath} is not under the session's screenshots folder {folder}; it is not copied");
        }

        string destination = OutputDestination.Check(target.Places, target.Requested, Extension).FullPath;
        try
        {
            using FileStream capture = new(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (OperatingSystem.IsWindows())
            {
                CheckCaptureIsOurs(capture, capturePath, target.Places);
            }

            OutputDestination.CopyReplacing(capture, destination, overwrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"saving the screenshot to {destination} failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// Refuses an open capture that is not the session's own: the file its handle reaches must lie under the screenshots
    /// folder of the project's final path, taken literally (a linked .godot or screenshots folder leads elsewhere), and have
    /// one name, so a hard link to a file elsewhere is not copied.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void CheckCaptureIsOurs(FileStream capture, string capturePath, OutputPlaces places)
    {
        string final = FinalPath.Of(capture.SafeFileHandle);
        if (HeadlessTools.IsOutside(Path.GetRelativePath(CaptureFolder(places.FinalProject), final)))
        {
            throw new McpException(
                $"the capture {capturePath} resolves to {final}, not under the session's screenshots folder "
                    + $"{CaptureFolder(places.Project)}; it is not copied"
            );
        }

        uint names = FinalPath.LinkCount(capture.SafeFileHandle);
        if (names != 1)
        {
            throw new McpException($"the capture {capturePath} has {names} names (hard links); it is not copied");
        }
    }
}
