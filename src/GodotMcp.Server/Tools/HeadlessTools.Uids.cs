using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The headless uid stamp (headless/scene_ops.gd, SceneFiles.stamp_uids): stamp_uids, which gives a hand-written .tscn or
/// .tres with no uid in its header the uid the editor would give it, changing the header line alone.
/// </summary>
internal sealed partial class HeadlessTools
{
    /// <summary>The extensions stamp_uids takes: the text formats whose header holds their uid.</summary>
    internal static readonly string[] StampExtensions = [".tscn", ".tres"];

    private const string StampRefusal = "is not a scene or resource: stamp_uids takes .tscn and .tres files and folders.";

    /// <summary>How a path that names a file is checked: it must be a .tscn or a .tres.</summary>
    private static readonly PathRule StampRule = new("paths", StampExtensions, StampRefusal);

    /// <summary>
    /// How a path on stamp_uids' list is checked before it is known to be a file's: the source of its res:// or relative path, its
    /// place inside the project, and its case. No extension is required, since the path may name a folder.
    /// </summary>
    private static readonly PathRule StampFolderRule = new("paths", [], StampRefusal);

    [McpServerTool(Name = "stamp_uids", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Gives every .tscn and .tres that has no uid in its header the uid the editor would give it, in a headless Godot, without "
            + "running the game: only the first line of the file changes (a hand-written header's other attributes, such as "
            + "load_steps, are dropped), the rest of the text is kept byte for byte, and the uid is also added to "
            + ".godot/uid_cache.bin when the project has one. paths lists .tscn and .tres files and folders (res:// or relative "
            + "to the project folder); left out, the whole project is walked, skipping the folders Godot's scan skips (a name "
            + "starting with \".\", a .gdignore, a nested project.godot). Returns {checked, stamped: [{path, uid}], alreadyHad, "
            + "failed?: [{path, error}], errors?}: stamped lists the files it changed; commit them."
    )]
    public async Task<string> StampUidsAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description(
            "The .tscn and .tres files and folders to stamp: res:// paths or paths relative to the project folder; left out, the " + "whole project."
        )]
            string[]? paths = null,
        CancellationToken cancellationToken = default
    )
    {
        HeadlessResult run = await RunAsync(() =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            JsonObject parameters = StampUidsParameters(projectDir, paths);
            HeadlessRequest request = new(projectDir, "stamp_uids", parameters, Prepare: false, RunCeiling);
            return HeadlessRunner.RunAsync(sessions, request, cancellationToken);
        });
        return WithErrors(run);
    }

    /// <summary>stamp_uids' request parameters: <c>{files}</c>, the files to stamp, each a res:// path.</summary>
    /// <exception cref="McpException">As <see cref="CheckStampPaths"/>.</exception>
    internal static JsonObject StampUidsParameters(string projectDir, IReadOnlyList<string>? paths) =>
        new() { ["files"] = new JsonArray([.. CheckStampPaths(projectDir, paths).Select(path => (JsonNode)path)]) };

    /// <summary>
    /// The .tscn and .tres files stamp_uids stamps, as res:// paths: the whole project when paths is null, else every file the
    /// paths name, a folder's walked recursively. A folder Godot's scan skips is left out of a walk, and a path repeated is taken
    /// once.
    /// </summary>
    /// <exception cref="McpException">
    /// paths is an empty list, a path is empty, outside the project or missing, a file is not a .tscn or .tres, or more than
    /// <see cref="MaxSweep"/> files are found.
    /// </exception>
    internal static IReadOnlyList<string> CheckStampPaths(string projectDir, IReadOnlyList<string>? paths)
    {
        if (paths is { Count: 0 })
        {
            throw new McpException("paths takes at least one file or folder; leave it out to stamp the whole project.");
        }

        List<string> files =
        [
            .. (paths is null ? StampFilesUnder(projectDir, projectDir) : paths.SelectMany(path => StampFilesAt(projectDir, path))).Distinct(
                StringComparer.OrdinalIgnoreCase
            ),
        ];
        return files.Count <= MaxSweep
            ? files
            : throw new McpException($"stamp_uids found {files.Count} .tscn and .tres files; it stamps at most {MaxSweep} at once: pass paths.");
    }

    /// <summary>The files a path on stamp_uids' list names: the .tscn and .tres files of the folder it names, else the file itself.</summary>
    /// <exception cref="McpException">
    /// The path is empty, outside the project, missing, not a .tscn or .tres, or a folder Godot's scan skips.
    /// </exception>
    private static IEnumerable<string> StampFilesAt(string projectDir, string path)
    {
        string full = ResolvePath(projectDir, path, StampFolderRule);
        if (!Directory.Exists(full))
        {
            return File.Exists(full) ? [ToResPath(projectDir, path, StampRule)] : throw new McpException($"paths '{path}' does not exist: {full}.");
        }

        // The editor gives a file in such a folder no uid either, so the file the caller named would never gain one.
        return UnscannedReason(projectDir, full) is { } reason
            ? throw new McpException(
                $"paths '{path}' is a folder Godot's scan skips ({reason}), so its files get no uid from the editor either; name scanned "
                    + "files or folders."
            )
            : StampFilesUnder(projectDir, full);
    }

    /// <summary>
    /// Every .tscn and .tres under folder, however deep, as res:// paths in ordinal order, skipping a folder Godot's scan skips
    /// (<see cref="IsUnscanned"/>).
    /// </summary>
    private static IEnumerable<string> StampFilesUnder(string projectDir, string folder)
    {
        IEnumerable<string> files = Directory
            .EnumerateFiles(folder)
            .Where(file => StampExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(file => ResOf(projectDir, file));
        IEnumerable<string> subfolders = Directory
            .EnumerateDirectories(folder)
            .Where(sub => UnscannedReason(projectDir, sub) is null)
            .Order(StringComparer.Ordinal)
            .SelectMany(sub => StampFilesUnder(projectDir, sub));
        return [.. files, .. subfolders];
    }

    /// <summary>
    /// The reason Godot's scan skips folder, by what <see cref="PrepScan.UnscannedFolder"/> names for a file directly inside it (4.7.2
    /// <c>editor/file_system/editor_file_system.cpp</c> L1179-1185, L3494-3505): a name starting with ".", a <c>.gdignore</c>, or a
    /// nested <c>project.godot</c>, on it or on a folder above it. Null when the scan reaches it.
    /// </summary>
    private static string? UnscannedReason(string projectDir, string folder)
    {
        string? skipped = PrepScan.UnscannedFolder(projectDir, Path.Combine(folder, "probe"));
        if (skipped is null)
        {
            return null;
        }

        if (Path.GetFileName(skipped).StartsWith('.'))
        {
            return "a name starting with \".\"";
        }

        return File.Exists(Path.Combine(skipped, ".gdignore")) ? "a .gdignore" : "a nested project.godot";
    }
}
