using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>Where a walk from a drive's root ended, every link on the way followed, and whether a folder is there.</summary>
/// <param name="Path">The walked path, no link left in it; short (8.3) names stay as they were written.</param>
/// <param name="IsFolder">Whether the entry there is a folder.</param>
internal sealed record Walked(string Path, bool IsFolder);

/// <summary>The folder a crop is written into, and the root, in its final form, the walk down to it starts from.</summary>
/// <param name="Root">A folder as Windows finally finds it: the project's, the checkout's or the main checkout's.</param>
/// <param name="Folder">The folder the crop goes into, at or below <paramref name="Root"/>.</param>
/// <param name="MayCreate">Whether a missing folder may be made; a folder beside the source must still exist.</param>
internal sealed record CropFolder(string Root, string Folder, bool MayCreate);

/// <summary>A source crop_screenshot read, and where its crop goes.</summary>
/// <param name="Final">The source's identity: the final path of the handle it was read through.</param>
/// <param name="Output">The folder its crop is written into: beside it, or the redirect folder.</param>
internal sealed record CropOrigin(string Final, CropFolder Output);

/// <summary>
/// crop_screenshot's source, read so that no name passes a network redirector: the path's form is checked with no I/O, its
/// drive must be local, every link on the way is read without being followed and followed only to a local drive, the walked
/// path is opened, and the open handle must have reached the walked path.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CropSource
{
    /// <summary><c>DRIVE_UNKNOWN</c>.</summary>
    internal const uint DriveUnknown = 0;

    /// <summary><c>DRIVE_NO_ROOT_DIR</c>: no drive has that letter.</summary>
    internal const uint DriveNoRootDir = 1;

    /// <summary><c>DRIVE_REMOTE</c>: a mapped network drive.</summary>
    internal const uint DriveRemote = 4;

    /// <summary>The name a refusal gives the project folder argument.</summary>
    internal const string ProjectLabel = "projectPath";

    /// <summary>The most links one path may pass through, as many as Windows follows: the next one is refused.</summary>
    private const int MaxLinkHops = 63;

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// <paramref name="given"/> as a full path on a local drive, taken from <paramref name="baseFolder"/> (the current
    /// folder when null): <see cref="CheckForm"/>, then <see cref="CheckDrive"/>.
    /// </summary>
    /// <exception cref="McpException">The path is no path on a drive, or its drive is a network drive or none.</exception>
    internal static string LocalPath(string label, string given, string? baseFolder, Func<string, uint> driveType)
    {
        string full = CheckForm(label, given, baseFolder);
        CheckDrive(label, given, full, driveType);
        return full;
    }

    /// <summary>
    /// The full path of <paramref name="given"/>, refused with no I/O unless it is a path on a drive letter: not drive- or
    /// root-relative, no device name, <c>X:\</c> once full (which every UNC, <c>\\?\</c>, <c>\??\</c> and <c>\\.\</c> spelling
    /// fails), no <c>:</c> after the drive, and no name ending in a space or a dot.
    /// </summary>
    /// <param name="label">How a refusal names the argument, as "image".</param>
    /// <param name="given">The path as the caller gave it.</param>
    /// <param name="baseFolder">The folder a relative path is taken from; the current folder when null.</param>
    /// <exception cref="McpException">The path breaks one of the rules.</exception>
    internal static string CheckForm(string label, string given, string? baseFolder)
    {
        CheckRooted(label, given, baseFolder);
        CheckDevice(label, given);
        string full = baseFolder is null ? Path.GetFullPath(given) : Path.GetFullPath(Path.Combine(baseFolder, given));
        if (!OutputDestination.IsDrivePath(full))
        {
            throw new McpException(
                $"{label} {given} is not a path on a local drive; {ScreenshotCropTool.ToolName} takes only a full path that begins "
                    + @"with a drive letter, never a network, device or \\?\ path"
            );
        }

        if (full.IndexOf(':', 2) >= 0)
        {
            throw new McpException($"{label} {given} must not contain ':' after the drive (an NTFS alternate stream)");
        }

        return OutputDestination.FirstDottedName(full) is { } dotted
            ? throw new McpException(
                $"{label} {given} has a folder or file name ending in a space or a dot ({dotted.Part}); Windows reads such a name two ways, "
                    + "so it is not read"
            )
            : full;
    }

    /// <summary>Refuses a drive-relative (<c>X:a.png</c>) or root-relative (<c>\a.png</c>) path.</summary>
    private static void CheckRooted(string label, string given, string? baseFolder)
    {
        if (Path.IsPathRooted(given) && !Path.IsPathFullyQualified(given))
        {
            string relative = baseFolder is null ? "" : " or one relative to the project folder";
            throw new McpException(
                $"{label} {given} is relative to a drive's current folder or the current drive's root; give a full path{relative}"
            );
        }
    }

    /// <summary>Refuses a path that names a Windows device such as NUL; a project folder's refusal names projectPath.</summary>
    private static void CheckDevice(string label, string given)
    {
        try
        {
            OutputDestination.CheckDevices(given, given);
        }
        catch (McpException e) when (label == ProjectLabel)
        {
            throw new McpException($"{ProjectLabel} {given} names a Windows device; pass the folder that holds the project's project.godot", e);
        }
    }

    /// <summary>
    /// Refuses a project whose folder Windows finally finds anywhere but a local drive, the walk of the folder
    /// notwithstanding; the refusal names only the path the caller gave. A checkout that survived
    /// <see cref="OutputPlaces.Find"/> holds the project's final folder, so it lies on the same drive and needs no check.
    /// </summary>
    /// <exception cref="McpException">The project's final path is not on a local drive.</exception>
    internal static void CheckPlaces(string given, OutputPlaces places, Func<string, uint> driveType)
    {
        if (!IsLocal(places.FinalProject, driveType))
        {
            throw new McpException($"{ProjectLabel} {given} does not finally lie on a local drive; {ScreenshotCropTool.ToolName} works only there");
        }
    }

    private static bool IsLocal(string final, Func<string, uint> driveType) =>
        OutputDestination.IsDrivePath(final) && driveType(final[..3]) is not (DriveRemote or DriveUnknown or DriveNoRootDir);

    /// <summary>
    /// What Windows says of the failure <paramref name="e"/> stands for, from its error code alone: the runtime's own message
    /// names the path it opened, which a link may have led to.
    /// </summary>
    internal static string Reason(Exception e)
    {
        uint result = unchecked((uint)e.HResult);
        return (result & 0xFFFF0000) == 0x80070000 ? new Win32Exception((int)(result & 0xFFFF)).Message : "Windows could not read it";
    }

    /// <summary>Refuses a path whose drive is a network drive, or no drive at all, as <paramref name="driveType"/> gives it.</summary>
    /// <param name="label">How a refusal names the argument, as "image".</param>
    /// <param name="given">The path as the caller gave it.</param>
    /// <param name="full">Its full path, starting <c>X:\</c>.</param>
    /// <param name="driveType">What <c>GetDriveTypeW</c> says of a drive root.</param>
    /// <exception cref="McpException">The drive is a network drive or none.</exception>
    internal static void CheckDrive(string label, string given, string full, Func<string, uint> driveType)
    {
        uint type = driveType(full[..3]);
        if (type == DriveRemote)
        {
            throw new McpException(
                $"{label} {given} is on the network drive {full[..2]}; {ScreenshotCropTool.ToolName} reads only from a local drive"
            );
        }

        if (type is DriveUnknown or DriveNoRootDir)
        {
            throw new McpException($"{label} {given} is on the drive {full[..2]}, which does not exist");
        }
    }

    /// <summary>
    /// Walks <paramref name="full"/> down from its drive's root without following a link: each entry is read as it stands,
    /// and a symbolic link or junction is followed only once its stored target is a path on a local drive, at most
    /// <see cref="MaxLinkHops"/> times.
    /// </summary>
    /// <param name="label">How a refusal names the argument, as "image".</param>
    /// <param name="given">The path as the caller gave it, which a refusal names.</param>
    /// <param name="full">Its full path, as <see cref="LocalPath"/> gave it.</param>
    /// <param name="driveType">What <c>GetDriveTypeW</c> says of a drive root.</param>
    /// <exception cref="McpException">An entry is missing or cannot be read, or a link leads somewhere it is not followed.</exception>
    internal static Walked Walk(string label, string given, string full, Func<string, uint> driveType) =>
        new ReadWalk($"{label} {given}", full, driveType).Run();

    /// <summary>
    /// The final path of the open source, which must be a path on a drive and the walked path's long form, ignoring case: a
    /// link swapped in after the walk reaches another file.
    /// </summary>
    /// <exception cref="McpException">The handle reached another path than the walk.</exception>
    /// <exception cref="IOException">The final path or the walked path's long form cannot be read.</exception>
    internal static string CheckHandle(SafeFileHandle handle, string full, string walked)
    {
        string final = FinalPath.Of(handle);
        bool same = OutputDestination.IsDrivePath(final) && string.Equals(final, FinalPath.LongName(walked), StringComparison.OrdinalIgnoreCase);
        return same ? final : throw new McpException($"{full} changed while it was read; {ScreenshotCropTool.ToolName} read nothing from it");
    }

    /// <summary>The PNG's bytes: its signature first, then its length, then the rest.</summary>
    /// <exception cref="McpException">The file does not start with the PNG signature, or is longer than any PNG Godot saves.</exception>
    /// <exception cref="IOException">The read failed.</exception>
    internal static byte[] ReadPng(Stream stream, string full)
    {
        byte[] head = new byte[PngSignature.Length];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (read < head.Length || !head.AsSpan().SequenceEqual(PngSignature))
        {
            throw new McpException(
                $"{ScreenshotCropTool.ToolName} reads only 8-bit RGB or RGBA PNGs that are not interlaced, as Godot saves them; "
                    + $"{full} is not a PNG (its first bytes are not the PNG signature)"
            );
        }

        ScreenshotCropTool.CheckLength(stream.Length, full);
        using MemoryStream bytes = new();
        bytes.Write(head);
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    /// <summary>
    /// Where the crop of the source at <paramref name="final"/> goes: beside it when it lies under the project's
    /// .godot/godot-mcp folder or the .tmp folder of the checkout or the main checkout (that one only when the source is
    /// outside the checkout), passes no .git folder and no folder whose name ends in a space or a dot, and git says it does
    /// not track it; when git cannot answer, beside it only for a project in no checkout. Otherwise into the checkout's .tmp
    /// folder, or the project's .godot/godot-mcp/screenshots folder when the project is in no checkout or the checkout's .tmp
    /// is itself a link. git runs only for a source that could take its crop beside it, and only in the checkout and the main
    /// checkout: a folder below the crop folder's root that holds a .git entry of its own sends the crop to the redirect
    /// folder without asking any git.
    /// </summary>
    /// <param name="places">The project's places, a checkout that does not hold the project already dropped.</param>
    /// <param name="final">The source's final path, as its open handle gave it.</param>
    /// <param name="logger">Where a missing or stopped git is reported.</param>
    internal static CropOrigin Place(OutputPlaces places, string final, ILogger logger)
    {
        string folder = Path.GetDirectoryName(final)!;
        string? root = BesideRoot(places, folder);
        if (root is not null && IsPlainBelow(root, folder) && !(ScreenshotTarget.IsTracked(places, final, logger) ?? places.Checkout is not null))
        {
            return new CropOrigin(final, new CropFolder(root, folder, MayCreate: false));
        }

        return new CropOrigin(final, Redirect(places));
    }

    /// <summary>
    /// The checkout's .tmp folder; the project's .godot/godot-mcp/screenshots folder when the project is in no checkout or the
    /// checkout's .tmp is a link, which no crop is written through.
    /// </summary>
    private static CropFolder Redirect(OutputPlaces places) =>
        places.FinalCheckout is { } checkout && !IsLink(Path.Combine(checkout, ".tmp"))
            ? new CropFolder(checkout, Path.Combine(checkout, ".tmp"), MayCreate: true)
            : new CropFolder(places.FinalProject, ScreenshotTarget.CaptureFolder(places.FinalProject), MayCreate: true);

    /// <summary>
    /// Whether the entry at <paramref name="entry"/> is a link, read as it stands; an entry that cannot be read is taken as
    /// none, and the write's own walk reads it again and refuses it.
    /// </summary>
    private static bool IsLink(string entry)
    {
        try
        {
            LinkEntry found = FinalPath.ReadEntry(entry);
            return found.Link is not null || FinalPath.IsNameSurrogate(found.Tag);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The root whose crop folder holds the source's folder <paramref name="folder"/>: the project for its .godot/godot-mcp
    /// folder, the checkout for its .tmp folder, and the main checkout for its .tmp folder when the source is outside the
    /// project's own checkout; null for none. A file that is itself named like a crop folder lies beside it, not in it.
    /// </summary>
    private static string? BesideRoot(OutputPlaces places, string folder)
    {
        if (IsUnder(Path.Combine(places.FinalProject, ".godot", "godot-mcp"), folder))
        {
            return places.FinalProject;
        }

        if (places.FinalCheckout is { } checkout && IsUnder(Path.Combine(checkout, ".tmp"), folder))
        {
            return checkout;
        }

        bool inOwnCheckout = places.FinalCheckout is { } own && IsUnder(own, folder);
        return !inOwnCheckout && places.FinalMainCheckout is { } main && IsUnder(Path.Combine(main, ".tmp"), folder) ? main : null;
    }

    /// <summary>
    /// Whether no folder from <paramref name="root"/> down to <paramref name="folder"/> is a .git folder, has a name ending in
    /// a space or a dot, or, below the root, holds a .git entry (a nested repository, or a git file naming one elsewhere,
    /// which git would follow); each folder is read as it stands, with no git run.
    /// </summary>
    private static bool IsPlainBelow(string root, string folder)
    {
        if (OutputDestination.FirstDottedName(folder) is not null)
        {
            return false;
        }

        string current = root;
        foreach (string part in Path.GetRelativePath(root, folder).Split('\\').Where(part => part is not ("" or ".")))
        {
            current = Path.Combine(current, part);
            if (string.Equals(part.TrimEnd('.', ' '), ".git", StringComparison.OrdinalIgnoreCase) || HoldsGitEntry(current))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="folder"/> holds a .git file or folder; true when that cannot be read.</summary>
    private static bool HoldsGitEntry(string folder)
    {
        try
        {
            return FinalPath.ReadEntry(Path.Combine(folder, ".git")).Exists;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsUnder(string folder, string full) => !HeadlessTools.IsOutside(Path.GetRelativePath(folder, full));

    /// <summary>The names of <paramref name="path"/> after its drive's root.</summary>
    private static List<string> PartsAfterRoot(string path) => [.. path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>One walk of a source path, counting the links it follows and naming the first in a refusal.</summary>
    /// <param name="named">The argument and the path as the caller gave it, as "image C:\a.png", which a refusal names.</param>
    private sealed class ReadWalk(string named, string full, Func<string, uint> driveType)
    {
        private const string NetworkPath = "a network path; crop_screenshot does not follow links to network paths";

        private int _hops;

        /// <summary>The first link on the caller's own path: a refusal names it, never a place a link led to.</summary>
        private string? _firstLink;

        internal Walked Run()
        {
            string current = full[..3];
            List<string> parts = PartsAfterRoot(full);
            bool folder = true;
            int index = 0;
            while (index < parts.Count)
            {
                string entry = Path.Combine(current, parts[index]);
                LinkEntry found = Read(entry);
                if (found.Link is { } link)
                {
                    string next = Follow(current, entry, link);
                    parts = [.. PartsAfterRoot(next), .. parts.Skip(index + 1)];
                    current = next[..3];
                    folder = true;
                    index = 0;
                    continue;
                }

                CheckPlain(found);
                current = entry;
                folder = found.IsFolder;
                index++;
            }

            return new Walked(current, folder);
        }

        private LinkEntry Read(string entry)
        {
            try
            {
                return FinalPath.ReadEntry(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new McpException($"reading {full} failed: {e.Message}", e);
            }
        }

        /// <summary>Refuses a missing entry, and a link of a kind not followed (any name surrogate but a symbolic link or junction).</summary>
        private void CheckPlain(LinkEntry found)
        {
            if (!found.Exists)
            {
                throw new McpException($"{full} does not exist");
            }

            if (FinalPath.IsNameSurrogate(found.Tag))
            {
                throw new McpException(
                    $"{named} leads through a link of a kind {ScreenshotCropTool.ToolName} does not follow; it follows only "
                        + "symbolic links and junctions"
                );
            }
        }

        /// <summary>Where the link at <paramref name="entry"/> leads, checked before anything opens it.</summary>
        private string Follow(string current, string entry, StoredLink link)
        {
            if (++_hops > MaxLinkHops)
            {
                throw new McpException(
                    $"{named} leads through more than {MaxLinkHops} links; {ScreenshotCropTool.ToolName} follows at most {MaxLinkHops}"
                );
            }

            _firstLink ??= entry;
            string stored = OutputDestination.StoredTarget(link);
            CheckStored(link, stored);

            // A relative symbolic link's target is taken from the link's own folder.
            string next = link.Relative ? Path.GetFullPath(link.Target, current) : stored;
            if (!OutputDestination.IsDrivePath(next) || next.IndexOf(':', 2) >= 0)
            {
                throw LeadsTo("a target that is not a path on a drive; such links are not followed");
            }

            next = Path.GetFullPath(next);
            uint type = driveType(next[..3]);
            if (type == DriveRemote)
            {
                throw LeadsTo(NetworkPath);
            }

            return type is DriveUnknown or DriveNoRootDir ? throw new McpException($"{full} does not exist") : next;
        }

        /// <summary>Refuses a stored target that names a network path or a volume, or a name ending in a space or a dot.</summary>
        private void CheckStored(StoredLink link, string stored)
        {
            if (OutputDestination.IsNetwork(stored))
            {
                throw LeadsTo(NetworkPath);
            }

            if (link.Kind == LinkKind.MountPoint && stored.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
            {
                throw LeadsTo("a volume; crop_screenshot does not follow links to volumes");
            }

            if (OutputDestination.FirstDottedName(stored) is not null)
            {
                throw LeadsTo("a name ending in a space or a dot; Windows reads such a name two ways, so it is not followed");
            }
        }

        private McpException LeadsTo(string what) => new($"{named} leads through the link at {_firstLink} to {what}");
    }
}
