using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>save_screenshot's options.</summary>
internal sealed record SaveScreenshotOptions(
    [property: Description("Replace an existing file at path; without it, saving over an existing file is refused.")] bool Overwrite = false,
    [property: Description(RuntimeTools.ResponseModeDescription)] string ResponseMode = "preview",
    [property: Description(RuntimeTools.PreviewMaxWidthDescription)] int PreviewMaxWidth = 480
);

/// <summary>
/// The file save_screenshot writes a capture to: a .png inside the project, or inside the git checkout's .tmp folder above it,
/// outside the editor's .godot/ folder. The capture itself and its preview stay in .godot/godot-mcp/screenshots/; only the
/// full-size PNG is copied here.
/// </summary>
internal static partial class ScreenshotTarget
{
    private const string ResPrefix = "res://";

    /// <summary>The full path <paramref name="path"/> names, checked before any capture is taken.</summary>
    /// <param name="projectDir">The session's project folder.</param>
    /// <param name="path">Project-relative, res:// or absolute.</param>
    /// <param name="overwrite">Whether an existing file there may be replaced.</param>
    /// <exception cref="McpException">
    /// The path does not end in .png; resolves outside the project and the git checkout's .tmp folder above it, or under
    /// .godot/, itself or through a link; holds a ':' after the drive or a short (8.3) name; or names an existing file
    /// without <paramref name="overwrite"/>.
    /// </exception>
    internal static string Resolve(string projectDir, string path, bool overwrite)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException($"path must end in .png: {path}");
        }

        // res:// names the project's own tree, so it never reaches the checkout's .tmp folder.
        bool isRes = path.StartsWith(ResPrefix, StringComparison.Ordinal);
        string relative = isRes ? path[ResPrefix.Length..] : path;
        string full = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(projectDir, relative));
        string inProject = Path.GetRelativePath(projectDir, full);
        if (HeadlessTools.IsOutside(inProject))
        {
            CheckOutside(projectDir, path, full, allowTmp: !isRes);
        }
        else
        {
            CheckParts(path, inProject, underDotGodot: true);
            CheckLinks(projectDir, path, inProject);
        }

        return overwrite || !File.Exists(full) ? full : throw new McpException($"{path} exists; pass options.overwrite true to replace it");
    }

    /// <summary>
    /// Checks a path outside the project: it may lie only inside the git checkout's .tmp folder above the project, and
    /// every other outside path is refused with both places a save may go.
    /// </summary>
    private static void CheckOutside(string projectDir, string path, string full, bool allowTmp)
    {
        string? checkout =
            (allowTmp ? FindCheckout(projectDir) : null)
            ?? throw new McpException($"path {path} resolves outside the project {projectDir}; save inside the project");
        string tmpDir = Path.Combine(checkout, ".tmp");
        string inTmp = Path.GetRelativePath(tmpDir, full);
        if (HeadlessTools.IsOutside(inTmp))
        {
            throw new McpException(
                $"path {path} resolves outside the project {projectDir} and the checkout's .tmp folder {tmpDir}; save inside either"
            );
        }

        CheckParts(path, inTmp, underDotGodot: false);
        CheckTmpLinks(checkout, tmpDir, projectDir, path, inTmp);
    }

    /// <summary>
    /// The nearest folder at or above <paramref name="projectDir"/> holding a .git entry — a folder, or a linked
    /// worktree's file — or null when no ancestor has one.
    /// </summary>
    private static string? FindCheckout(string projectDir)
    {
        for (DirectoryInfo? dir = new(Path.GetFullPath(projectDir)); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>Copies the capture to the target, creating its folders when missing.</summary>
    /// <exception cref="McpException">The copy failed, or the target appeared since it was checked and overwrite is false.</exception>
    internal static void Save(string capturePath, string destination, bool overwrite)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(capturePath, destination, overwrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"saving the screenshot to {destination} failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// Refuses a path that names an NTFS stream or a short (8.3) name, and, when <paramref name="underDotGodot"/>, one
    /// under .godot/. <paramref name="relative"/> is the path relative to the folder it is saved under, the project or
    /// the checkout's .tmp folder.
    /// </summary>
    private static void CheckParts(string path, string relative, bool underDotGodot)
    {
        // A ':' after the drive names an NTFS alternate stream: README.md:x.png writes into README.md.
        if (relative.Contains(':', StringComparison.Ordinal))
        {
            throw new McpException($"path must not contain ':' after the drive: {path}");
        }

        // GODOT~1 can name .godot, and a short name hides which folder it is.
        if (relative.Split(Path.DirectorySeparatorChar).Any(part => ShortNamePattern().IsMatch(part)))
        {
            throw new McpException($"path {path} uses a short (8.3) name; use the folder's full name");
        }

        if (underDotGodot && IsUnderDotGodot(relative))
        {
            throw new McpException($"path {path} is under .godot/, which the editor owns; pick a tracked folder");
        }
    }

    /// <summary>
    /// Follows every link on the way from the project folder to the target, the target included when it exists, and refuses
    /// a link whose final target lies outside the project or under .godot/.
    /// </summary>
    private static void CheckLinks(string projectDir, string path, string inProject)
    {
        string current = projectDir;
        foreach (string part in inProject.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            string? target = FinalLinkTarget(path, current);
            if (target is null)
            {
                continue;
            }

            string targetInProject = Path.GetRelativePath(projectDir, target);
            if (HeadlessTools.IsOutside(targetInProject))
            {
                throw new McpException($"path {path} leads through a link to {target}, outside the project; save inside the project");
            }

            if (IsUnderDotGodot(targetInProject))
            {
                throw new McpException($"path {path} leads through a link to {target}, under .godot/, which the editor owns; pick a tracked folder");
            }

            current = target;
        }
    }

    /// <summary>
    /// Follows every link on the way from the checkout down to the target, the target included when it exists: the .tmp
    /// entry itself and every entry below it must finally lead inside that .tmp folder or the project, and a position
    /// that lies inside the project is held to the project's .godot rule.
    /// </summary>
    private static void CheckTmpLinks(string checkout, string tmpDir, string projectDir, string path, string inTmp)
    {
        string current = checkout;
        string[] parts = [Path.GetFileName(tmpDir), .. inTmp.Split(Path.DirectorySeparatorChar)];
        foreach (string part in parts)
        {
            current = CheckTmpStep(tmpDir, projectDir, path, current, part);
        }
    }

    /// <summary>
    /// Follows the link, if any, at the entry <paramref name="part"/> names under <paramref name="current"/> and returns
    /// the position that names: a link's final target, held to the .tmp and .godot rules, or the entry itself.
    /// </summary>
    private static string CheckTmpStep(string tmpDir, string projectDir, string path, string current, string part)
    {
        string entry = Path.Combine(current, part);
        string reached = FinalLinkTarget(path, entry) ?? entry;
        string inTmp = Path.GetRelativePath(tmpDir, reached);
        string inProject = Path.GetRelativePath(projectDir, reached);
        if (HeadlessTools.IsOutside(inTmp) && HeadlessTools.IsOutside(inProject))
        {
            string places = $"the project {projectDir} and the checkout's .tmp folder {tmpDir}";
            throw new McpException($"path {path} leads through a link to {reached}, outside {places}; save inside either");
        }

        if (!HeadlessTools.IsOutside(inProject) && IsUnderDotGodot(inProject))
        {
            throw new McpException($"path {path} leads through a link to {reached}, under .godot/, which the editor owns; pick a tracked folder");
        }

        return reached;
    }

    /// <summary>The full path a link at <paramref name="current"/> finally leads to; null when nothing or no link is there.</summary>
    private static string? FinalLinkTarget(string path, string current)
    {
        try
        {
            FileInfo entry = new(current);
            return entry.LinkTarget is null ? null : entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"path {path} leads through a link at {current} that cannot be followed: {e.Message}", e);
        }
    }

    /// <summary>
    /// Whether a project-relative path starts in .godot: Windows finds .GODOT and .godot. as .godot, and Godot's own folder is
    /// never a tracked one on any system.
    /// </summary>
    private static bool IsUnderDotGodot(string inProject) =>
        string.Equals(inProject.Split(Path.DirectorySeparatorChar)[0].TrimEnd('.', ' '), ".godot", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"~\d")]
    private static partial Regex ShortNamePattern();
}
