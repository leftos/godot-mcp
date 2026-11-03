using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
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

    /// <summary>
    /// The folders crop_screenshot reads a PNG from, as written and as Windows finally finds them: the project's
    /// .godot/godot-mcp folder, and the .tmp folders of the project's checkout and of the main checkout when there is one.
    /// A project outside git has only the first.
    /// </summary>
    internal static IReadOnlyList<string> CropSourceFolders(OutputPlaces places)
    {
        List<string> folders = [];
        foreach (string folder in ProjectFolders(places).Concat(CheckoutTmpFolders(places)).Concat(MainTmpFolders(places)))
        {
            if (!folders.Exists(existing => SamePath(existing, folder)))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }

    /// <summary>
    /// Whether <paramref name="full"/> lies in one of the folders <see cref="CropSourceFolders"/> names; a path under the
    /// project's own checkout counts only in that checkout's .tmp folder, so a checkout inside the main checkout's .tmp
    /// folder keeps its tracked files out of reach.
    /// </summary>
    internal static bool IsUnderCropFolders(OutputPlaces places, string full)
    {
        string?[] own = [places.Checkout, places.FinalCheckout];
        return IsUnderAny(ProjectFolders(places), full)
            || IsUnderAny(CheckoutTmpFolders(places), full)
            || (!IsUnderAny(own.OfType<string>(), full) && IsUnderAny(MainTmpFolders(places), full));
    }

    /// <summary>Whether <paramref name="full"/> lies under the project's .godot/godot-mcp folder.</summary>
    internal static bool IsUnderProjectCaptureFolders(OutputPlaces places, string full) => IsUnderAny(ProjectFolders(places), full);

    /// <summary>
    /// Whether git tracks the file at <paramref name="file"/>, asked of every repository that could hold it: the innermost
    /// one its folder is in, and the project's checkout and main checkout when the file lies under them. Each is asked from
    /// its top with a case-insensitive, literal pathspec, so a file renamed only by case is still seen. True when one lists
    /// it; null when none does and one could not be asked.
    /// </summary>
    internal static bool? IsTracked(OutputPlaces places, string file, ILogger logger)
    {
        bool? tracked = InnermostTracked(file, logger);
        foreach (string top in CheckoutTops(places, file))
        {
            tracked = Either(tracked, AskedTracked(top, file, logger));
        }

        return tracked;
    }

    /// <summary>Whether the innermost repository the file's folder is in lists it; null when git cannot name that repository.</summary>
    private static bool? InnermostTracked(string file, ILogger logger)
    {
        string? top = GitRunner.Run(Path.GetDirectoryName(file)!, logger, "rev-parse", "--show-toplevel");
        return string.IsNullOrWhiteSpace(top) ? null : AskedTracked(top.Trim(), file, logger);
    }

    /// <summary>Whether the repository at <paramref name="top"/> lists <paramref name="file"/>; null when git cannot be asked.</summary>
    private static bool? AskedTracked(string top, string file, ILogger logger)
    {
        string pathspec = ":(literal,icase)" + Path.GetRelativePath(top, file).Replace('\\', '/');
        GitResult result = GitRunner.RunForResult(top, logger, GitRunner.Ceiling, ["ls-files", "-z", "--", pathspec]);
        return !result.OutputRead || result.ExitCode != 0 ? null : result.Output.Length > 0;
    }

    /// <summary>The checkouts the file lies under, as Windows finally finds them: the project's and the main checkout's.</summary>
    private static List<string> CheckoutTops(OutputPlaces places, string file)
    {
        List<string> tops = [];
        foreach (string? checkout in new[] { places.FinalCheckout, places.FinalMainCheckout })
        {
            bool held = checkout is not null && !HeadlessTools.IsOutside(Path.GetRelativePath(checkout, file));
            if (held && !tops.Exists(top => SamePath(top, checkout!)))
            {
                tops.Add(checkout!);
            }
        }

        return tops;
    }

    /// <summary>True when either answer says tracked; null when neither does and one is unknown; false only when both say no.</summary>
    private static bool? Either(bool? first, bool? second) =>
        first == true || second == true ? true
        : first is null || second is null ? null
        : false;

    private static IEnumerable<string> ProjectFolders(OutputPlaces places) => FoldersOf(places.Project, places.FinalProject, ".godot", "godot-mcp");

    private static IEnumerable<string> CheckoutTmpFolders(OutputPlaces places) =>
        CheckoutHoldsProject(places) ? FoldersOf(places.Checkout, places.FinalCheckout, ".tmp") : [];

    private static IEnumerable<string> MainTmpFolders(OutputPlaces places) =>
        CheckoutHoldsProject(places) ? FoldersOf(places.MainCheckout, places.FinalMainCheckout, ".tmp") : [];

    /// <summary>
    /// Whether the checkout git reports really holds the project: a git with core.worktree set elsewhere names a folder the
    /// project does not lie under, and neither that folder nor its main checkout is a root then.
    /// </summary>
    private static bool CheckoutHoldsProject(OutputPlaces places) =>
        places.FinalCheckout is { } checkout && !HeadlessTools.IsOutside(Path.GetRelativePath(checkout, places.FinalProject));

    /// <summary>The folder <paramref name="parts"/> names below each root, as written and as Windows finally finds it.</summary>
    private static IEnumerable<string> FoldersOf(string? written, string? final, params string[] parts) =>
        new[] { written, final }.OfType<string>().Select(root => Path.Combine([root, .. parts]));

    private static bool SamePath(string first, string second) =>
        (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Equals(first, second);

    /// <summary>The file <paramref name="path"/> names, checked before any capture is taken.</summary>
    /// <param name="projectDir">The session's project folder.</param>
    /// <param name="path">
    /// Relative, res:// or fully qualified; a relative path whose first folder is .tmp resolves against the project's git
    /// checkout, any other against the project folder.
    /// </param>
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
        CheckInCaptureFolder(capturePath, target.Places.Project);
        string destination = OutputDestination.Check(target.Places, target.Requested, Extension).FullPath;
        CopyCapture(capturePath, target.Places, destination, overwrite);
    }

    /// <summary>
    /// Copies the capture, checked as <see cref="Save"/> checks it, to <paramref name="destination"/>, a file the server names
    /// under the project's .godot/godot-mcp folder (a scratch run's shot), creating its folders and replacing a file there.
    /// <paramref name="places"/> are the session project's, resolved once by the caller.
    /// </summary>
    /// <exception cref="McpException">
    /// The capture is not under the session's screenshots folder, by its name or by the file its open handle reaches, or it
    /// has more than one name; or the copy failed.
    /// </exception>
    internal static void SaveToRunFolder(string capturePath, string destination, OutputPlaces places)
    {
        CheckInCaptureFolder(capturePath, places.Project);
        CopyCapture(capturePath, places, destination, overwrite: true);
    }

    /// <exception cref="McpException">The capture is not under the project's screenshots folder by its name.</exception>
    private static void CheckInCaptureFolder(string capturePath, string projectDir)
    {
        string folder = CaptureFolder(projectDir);
        if (HeadlessTools.IsOutside(Path.GetRelativePath(folder, Path.GetFullPath(capturePath))))
        {
            throw new McpException($"the capture {capturePath} is not under the session's screenshots folder {folder}; it is not copied");
        }
    }

    /// <summary>Copies the capture to <paramref name="destination"/>, once its open handle is seen to be the session's own.</summary>
    /// <exception cref="McpException">The capture is not the session's own, or the copy failed.</exception>
    private static void CopyCapture(string capturePath, OutputPlaces places, string destination, bool overwrite)
    {
        try
        {
            using FileStream capture = new(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (OperatingSystem.IsWindows())
            {
                CheckCaptureIsOurs(capture, capturePath, places);
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
        CaptureIdentity identity = IdentifyCapture(capture.SafeFileHandle, places.FinalProject);
        if (!identity.UnderFolder)
        {
            throw new McpException(
                $"the capture {capturePath} resolves to {identity.Final}, not under the session's screenshots folder "
                    + $"{CaptureFolder(places.Project)}; it is not copied"
            );
        }

        if (identity.Names != 1)
        {
            throw new McpException($"the capture {capturePath} has {identity.Names} names (hard links); it is not copied");
        }
    }

    /// <summary>
    /// Where an open file finally lies, whether that is under the screenshots folder of <paramref name="finalProject"/>
    /// taken literally (a linked .godot or screenshots folder leads elsewhere), and how many names the file has; a capture is
    /// a file under the folder with one name, so neither a link leading out nor a hard link to a file elsewhere passes.
    /// </summary>
    /// <param name="handle">The open file.</param>
    /// <param name="finalProject">The project folder's final path.</param>
    /// <exception cref="IOException">Windows cannot give the file's final path or its link count.</exception>
    [SupportedOSPlatform("windows")]
    internal static CaptureIdentity IdentifyCapture(SafeFileHandle handle, string finalProject)
    {
        string final = FinalPath.Of(handle);
        bool underFolder = !HeadlessTools.IsOutside(Path.GetRelativePath(CaptureFolder(finalProject), final));
        return new CaptureIdentity(final, underFolder, FinalPath.LinkCount(handle));
    }

    /// <summary>
    /// Where crop_screenshot's open source finally lies, whether that is in one of the folders
    /// <see cref="CropSourceFolders"/> names, and how many names the file has; a link leading out of those folders, a
    /// .tmp folder that is itself a link elsewhere, and a hard link to a file elsewhere all fail.
    /// </summary>
    /// <param name="handle">The open source.</param>
    /// <param name="places">The project's places, whose final forms the folder set includes.</param>
    /// <exception cref="IOException">Windows cannot give the file's final path or its link count.</exception>
    [SupportedOSPlatform("windows")]
    internal static CaptureIdentity IdentifyCropSource(SafeFileHandle handle, OutputPlaces places)
    {
        string final = FinalPath.Of(handle);
        return new CaptureIdentity(final, IsUnderCropFolders(places, final), FinalPath.LinkCount(handle));
    }
}

/// <summary>What <see cref="ScreenshotTarget.IdentifyCapture"/> or <see cref="ScreenshotTarget.IdentifyCropSource"/> found of an open file.</summary>
/// <param name="Final">The file's final path, every link followed.</param>
/// <param name="UnderFolder">Whether the final path lies under the folder or folders the caller allows.</param>
/// <param name="Names">How many names (hard links) the file has.</param>
internal sealed record CaptureIdentity(string Final, bool UnderFolder, uint Names);

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
