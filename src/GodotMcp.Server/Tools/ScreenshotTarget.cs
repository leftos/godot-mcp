using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>save_screenshot's options.</summary>
internal sealed record SaveScreenshotOptions(
    [property: Description("Replace an existing file at path; without it, saving over an existing file is refused.")] bool Overwrite = false,
    [property: Description(RuntimeTools.ResponseModeDescription)] string ResponseMode = "preview",
    [property: Description(RuntimeTools.PreviewMaxWidthDescription)] int PreviewMaxWidth = 480
);

/// <summary>take_screenshot's options.</summary>
internal sealed record TakeScreenshotOptions(
    [property: Description("Replace an existing file at path; without it, saving over an existing file is refused.")] bool Overwrite = false
);

/// <summary>
/// The file save_screenshot writes a capture to: a .png in one of the <see cref="OutputPlaces"/>, held to the rule of
/// <see cref="OutputDestination"/>; and the file take_screenshot, wait_for and capture_frames write one to, held besides to
/// the checkout's and the main checkout's .tmp folders. The capture itself and its preview stay in
/// .godot/godot-mcp/screenshots/; only the full-size PNG is copied here.
/// </summary>
internal static class ScreenshotTarget
{
    internal const string Extension = ".png";

    /// <summary>The folder the bridge saves a session's captures to.</summary>
    internal static string CaptureFolder(string projectDir) => Path.Combine(projectDir, ".godot", "godot-mcp", "screenshots");

    /// <summary>The file <paramref name="path"/> names, checked before any capture is taken.</summary>
    /// <param name="projectDir">The session's project folder.</param>
    /// <param name="path">Project-relative, res:// or fully qualified.</param>
    /// <param name="overwrite">Whether an existing file there may be replaced.</param>
    /// <param name="logger">Where a missing or stopped git is reported.</param>
    /// <exception cref="McpException">The path breaks the rule, or names an existing file without <paramref name="overwrite"/>.</exception>
    internal static OutputTarget Resolve(string projectDir, string path, bool overwrite, ILogger logger) =>
        Resolve(OutputPlaces.Find(projectDir, logger), path, overwrite);

    /// <summary>The file <paramref name="path"/> names in <paramref name="places"/>, checked.</summary>
    /// <exception cref="McpException">The path breaks the rule, or names an existing file without <paramref name="overwrite"/>.</exception>
    internal static OutputTarget Resolve(OutputPlaces places, string path, bool overwrite) =>
        OutputDestination.Resolve(places, path, Extension, overwrite);

    /// <summary>
    /// The file <paramref name="path"/> names for <paramref name="tool"/>, which writes only into a .tmp place: checked as
    /// <see cref="Resolve(string, string, bool, ILogger)"/> checks it, then held to the checkout's and the main checkout's
    /// .tmp folders, then refused when it exists without <paramref name="overwrite"/>.
    /// </summary>
    /// <exception cref="McpException">
    /// The path breaks the rule, lies outside both .tmp folders, or names an existing file without <paramref name="overwrite"/>.
    /// </exception>
    internal static OutputTarget ResolveInTmp(string projectDir, string path, bool overwrite, string tool, ILogger logger) =>
        ResolveInTmp(OutputPlaces.Find(projectDir, logger), path, overwrite, tool);

    /// <summary>The file <paramref name="path"/> names in <paramref name="places"/> for <paramref name="tool"/>, held to the .tmp folders.</summary>
    /// <exception cref="McpException">
    /// The path breaks the rule, lies outside both .tmp folders, or names an existing file without <paramref name="overwrite"/>.
    /// </exception>
    internal static OutputTarget ResolveInTmp(OutputPlaces places, string path, bool overwrite, string tool)
    {
        OutputTarget target = Resolve(places, path, overwrite: true);
        CheckInTmp(target, tool);
        return overwrite || !File.Exists(target.FullPath)
            ? target
            : throw new McpException($"{path} exists; pass options.overwrite true to replace it");
    }

    /// <summary>Holds the target to the .tmp folders again, then <see cref="Save"/>s the capture to it.</summary>
    /// <exception cref="McpException">The target lies outside both .tmp folders now, or <see cref="Save"/> refuses or fails.</exception>
    internal static void SaveInTmp(string capturePath, OutputTarget target, bool overwrite, string tool)
    {
        CheckInTmp(target, tool);
        Save(capturePath, target, overwrite);
    }

    /// <summary>
    /// Refuses a target outside the checkout's and the main checkout's .tmp folders, as written and, on Windows, by the final
    /// path of its deepest existing folder, held to the folders' written forms and their checkouts' final forms, never to
    /// where a .tmp folder leads: a .tmp folder, or a link under one, that leads elsewhere is refused too.
    /// </summary>
    private static void CheckInTmp(OutputTarget target, string tool)
    {
        OutputPlaces places = target.Places;
        string?[] checkouts = [places.Checkout, places.MainCheckout, places.FinalCheckout, places.FinalMainCheckout];
        string[] folders = [.. checkouts.OfType<string>().Select(checkout => Path.Combine(checkout, ".tmp"))];
        if (folders.Length == 0)
        {
            throw new McpException(
                $"{target.Requested} is refused: {tool} writes only into a git checkout's .tmp folder, and {places.Project} is in no git checkout"
            );
        }

        bool inTmp = IsInTmp(places, folders, target.FullPath);
        if (inTmp && OperatingSystem.IsWindows())
        {
            inTmp = IsInTmp(places, folders, FinalOfMissing(target.FullPath));
        }

        if (!inTmp)
        {
            throw new McpException(TmpOnlyRefusal(tool));
        }
    }

    /// <summary>
    /// Whether <paramref name="full"/> lies in one of the .tmp <paramref name="folders"/>; a path under the project's own
    /// checkout counts only in that checkout's .tmp folder, so a checkout inside the main checkout's .tmp folder keeps its
    /// tracked files out of reach.
    /// </summary>
    private static bool IsInTmp(OutputPlaces places, string[] folders, string full)
    {
        string[] own = [.. new[] { places.Checkout, places.FinalCheckout }.OfType<string>()];
        return IsUnderAny(IsUnderAny(own, full) ? [.. own.Select(checkout => Path.Combine(checkout, ".tmp"))] : folders, full);
    }

    /// <summary>
    /// The refusal of a path outside the .tmp folders: take_screenshot takes path as an argument and points to
    /// save_screenshot; wait_for and capture_frames take it as options.path.
    /// </summary>
    private static string TmpOnlyRefusal(string tool)
    {
        const string Rule = "writes only into the checkout's or the main checkout's .tmp folder";
        return tool == RuntimeTools.TakeScreenshotToolName
            ? $"path on {tool} {Rule}; use save_screenshot to write a still elsewhere in the project"
            : $"options.path on {tool} {Rule}";
    }

    /// <summary>Why a copy failed: the underlying error when the refusal wraps one, else the refusal itself.</summary>
    internal static string ReasonOf(McpException failure) => failure.InnerException?.Message ?? failure.Message;

    private static bool IsUnderAny(IEnumerable<string> folders, string full) =>
        folders.Any(folder => !HeadlessTools.IsOutside(Path.GetRelativePath(folder, full)));

    /// <summary>The final path of <paramref name="full"/>'s deepest existing folder, with the missing components below it.</summary>
    [SupportedOSPlatform("windows")]
    private static string FinalOfMissing(string full)
    {
        string existing = Path.GetDirectoryName(full)!;
        List<string> missing = [Path.GetFileName(full)];
        while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
        {
            missing.Insert(0, Path.GetFileName(existing));
            existing = parent;
        }

        return Path.Combine([FinalOf(existing), .. missing]);
    }

    [SupportedOSPlatform("windows")]
    private static string FinalOf(string folder)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(FinalPath.Of(folder));
        }
        catch (IOException e)
        {
            throw new McpException($"the folder {folder} has no final path Windows can give: {e.Message}; the .tmp rule cannot be checked", e);
        }
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

/// <summary>
/// capture_frames' destination: a .png stem in a .tmp place, whose frame k of the n distinct files lands at
/// &lt;stem&gt;-&lt;k&gt;.png, k zero-padded to the width of n.
/// </summary>
internal sealed class FrameTargets
{
    private const string Tool = "capture_frames";
    private readonly OutputPlaces _places;
    private readonly string _path;
    private readonly bool _overwrite;

    private FrameTargets(OutputPlaces places, string path, bool overwrite)
    {
        _places = places;
        _path = path;
        _overwrite = overwrite;
    }

    /// <summary>
    /// The stem <paramref name="path"/>, checked with every name a capture of <paramref name="points"/> points could write,
    /// whichever count of distinct files it takes, before anything is captured.
    /// </summary>
    /// <exception cref="McpException">
    /// The path or one of its names breaks the rule or lies outside both .tmp folders, or a name is an existing file without
    /// <paramref name="overwrite"/>.
    /// </exception>
    internal static FrameTargets Resolve(OutputPlaces places, string path, int points, bool overwrite)
    {
        ScreenshotTarget.ResolveInTmp(places, path, overwrite: true, Tool);
        foreach (string name in PossibleNames(path, points))
        {
            ScreenshotTarget.ResolveInTmp(places, name, overwrite, Tool);
        }

        return new FrameTargets(places, path, overwrite);
    }

    /// <summary>
    /// The name of file <paramref name="index"/> of <paramref name="count"/>: the stem, a dash and the index padded to the
    /// count's width.
    /// </summary>
    internal static string Name(string path, int index, int count) => NameAt(path, index, count.ToString(CultureInfo.InvariantCulture).Length);

    private static string NameAt(string path, int index, int width)
    {
        string padded = index.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
        return $"{path[..^ScreenshotTarget.Extension.Length]}-{padded}{ScreenshotTarget.Extension}";
    }

    /// <summary>
    /// Every name a capture of <paramref name="points"/> points could write: for each width, the indexes of the largest
    /// count of that width.
    /// </summary>
    private static IEnumerable<string> PossibleNames(string path, int points)
    {
        int count = 0;
        for (int width = 1, widest = 9; count < points; width++, widest = (widest * 10) + 9)
        {
            count = Math.Min(points, widest);
            for (int index = 0; index < count; index++)
            {
                yield return NameAt(path, index, width);
            }
        }
    }

    /// <summary>
    /// Copies the capture's files to their names in first-taken order and points <paramref name="result"/> at them: folder
    /// and files name the copies, captureFolder the capture's own folder. A capture that took no frame writes nothing.
    /// </summary>
    /// <remarks>
    /// A copy that fails or is refused leaves folder and files naming the capture's own files, removes the copies this call
    /// made and adds a warning naming the capture's folder, which still holds every frame.
    /// </remarks>
    internal void Deliver(JsonObject result)
    {
        if (result["folder"]?.GetValue<string>() is not { } captureFolder)
        {
            return;
        }

        string[] captures = [.. result["files"]!.AsArray().Select(file => Path.Combine(captureFolder, file!.GetValue<string>()))];
        List<string> written = [];
        for (int index = 0; index < captures.Length; index++)
        {
            string name = Name(_path, index, captures.Length);
            try
            {
                OutputTarget target = ScreenshotTarget.ResolveInTmp(_places, name, _overwrite, Tool);
                ScreenshotTarget.SaveInTmp(captures[index], target, _overwrite, Tool);
                written.Add(target.FullPath);
            }
            catch (McpException e)
            {
                result["warning"] = Failed(name, e, captureFolder, written);
                return;
            }
        }

        result["folder"] = Path.GetDirectoryName(written[0]);
        result["files"] = new JsonArray([.. written.Select(copy => JsonValue.Create(Path.GetFileName(copy)))]);
        result["captureFolder"] = captureFolder;
    }

    /// <summary>The warning for a copy to <paramref name="name"/> that failed, once the copies already made are removed.</summary>
    private static string Failed(string name, McpException failure, string captureFolder, List<string> written)
    {
        string removals = string.Concat(written.Select(RemoveCopy));
        return $"the frames could not be copied to {name}: {ScreenshotTarget.ReasonOf(failure)}; they are kept in {captureFolder}{removals}";
    }

    /// <summary>Deletes a copy this call made; empty once it is gone, else why it stays.</summary>
    private static string RemoveCopy(string copy)
    {
        try
        {
            File.Delete(copy);
            return "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"; the copy {copy} could not be removed: {e.Message}";
        }
    }
}
