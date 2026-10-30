using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>A checked destination: the places it was checked against, the path as the caller gave it, and its full path.</summary>
/// <param name="Places">The places the path was checked against, kept so the write checks it again against the same.</param>
/// <param name="Requested">The path as the caller gave it, which refusals quote.</param>
/// <param name="FullPath">The file the path names.</param>
internal sealed record OutputTarget(OutputPlaces Places, string Requested, string FullPath);

/// <summary>
/// The rule for a file the caller names for a still or a clip: it ends in the output's extension, is project-relative,
/// res:// or fully qualified on a local drive, lies in one of <see cref="OutputPlaces"/> after every link on the way is
/// followed, never inside a .git folder, under a .godot folder outside a .tmp, or on a Windows device name; and the write
/// that replaces the destination's folder entry rather than writing through a link.
/// </summary>
internal static partial class OutputDestination
{
    private const string ResPrefix = "res://";

    /// <summary>The most links one path may pass through, as many as Windows follows: the next one is refused.</summary>
    private const int MaxLinkHops = 63;

    private static readonly string[] DeviceNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "CONIN$",
        "CONOUT$",
        .. Enumerable.Range(0, 10).Select(number => $"COM{number}"),
        "COM¹",
        "COM²",
        "COM³",
        .. Enumerable.Range(0, 10).Select(number => $"LPT{number}"),
        "LPT¹",
        "LPT²",
        "LPT³",
    ];

    /// <summary>The full path <paramref name="path"/> names, checked, and refused when it exists without <paramref name="overwrite"/>.</summary>
    /// <param name="places">Where the output may lie.</param>
    /// <param name="path">Project-relative, res:// or fully qualified.</param>
    /// <param name="extension">The extension the path must end in, such as ".png".</param>
    /// <param name="overwrite">Whether an existing file there may be replaced.</param>
    /// <exception cref="McpException">The path breaks the rule, or names an existing file without <paramref name="overwrite"/>.</exception>
    internal static string Resolve(OutputPlaces places, string path, string extension, bool overwrite)
    {
        string full = Check(places, path, extension);
        return overwrite || !File.Exists(full) ? full : throw new McpException($"{path} exists; pass options.overwrite true to replace it");
    }

    /// <summary>The full path <paramref name="path"/> names, checked against the rule; whether a file is there is not checked.</summary>
    /// <exception cref="McpException">The path breaks the rule, or the project is on a network path.</exception>
    internal static string Check(OutputPlaces places, string path, string extension)
    {
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException($"path must end in {extension}: {path}");
        }

        CheckProjectIsLocal(places.Project);

        // res:// names the project's own tree, so it never reaches the checkout or the main checkout.
        bool isRes = path.StartsWith(ResPrefix, StringComparison.Ordinal);
        string relative = isRes ? path[ResPrefix.Length..] : path;
        CheckDevices(path, relative);
        if (Path.IsPathRooted(relative) && !Path.IsPathFullyQualified(relative))
        {
            throw new McpException(
                $"path {path} is relative to a drive's current folder or the current drive's root; give a full path or one relative to the project"
            );
        }

        string full = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(places.Project, relative));
        CheckLocalDrive(path, full);
        new DestinationCheck(places, path, isRes).Run(full, relative);
        return full;
    }

    /// <summary>
    /// Copies <paramref name="source"/> from where it stands to <paramref name="destination"/>, creating its folders when
    /// missing: the bytes go to a new file of a random name in the destination's folder, which is then moved over the
    /// destination's folder entry, so a link or hard link there is replaced, never written through. A failed write deletes
    /// its temporary file and removes the folders it made, deepest first, while they are empty; a folder it cannot remove is
    /// named in the exception; the source stays where it was.
    /// </summary>
    /// <exception cref="IOException">A step failed, or the destination exists and <paramref name="overwrite"/> is false.</exception>
    /// <exception cref="UnauthorizedAccessException">A step was denied.</exception>
    internal static void CopyReplacing(Stream source, string destination, bool overwrite) =>
        InFolders(destination, () => CopyIntoFolder(source, destination, overwrite));

    private static void CopyIntoFolder(Stream source, string destination, bool overwrite)
    {
        string folder = Path.GetDirectoryName(destination)!;
        string temporary = Path.Combine(folder, $".godot-mcp-{Path.GetRandomFileName()}.tmp");
        bool created = false;
        try
        {
            using (FileStream to = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                source.CopyTo(to);
            }

            File.Move(temporary, destination, overwrite);
        }
        catch (Exception e) when (created && e is IOException or UnauthorizedAccessException)
        {
            DeleteTemporary(temporary, e);
            throw;
        }
    }

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="destination"/>, creating its folders when missing: on one volume a
    /// rename that replaces the destination's folder entry, never writing through a link there; across volumes a
    /// <see cref="CopyReplacing"/> of the source, which is deleted once the copy is in place. A failed write deletes its
    /// temporary file and removes the folders it made, deepest first, while they are empty; a folder it cannot remove is
    /// named in the exception; the source stays where it was.
    /// </summary>
    /// <returns>Null once the source is gone; why the source could not be deleted after its copy reached the destination.</returns>
    /// <exception cref="IOException">A step failed, or the destination exists and <paramref name="overwrite"/> is false.</exception>
    /// <exception cref="UnauthorizedAccessException">A step was denied.</exception>
    internal static string? MoveReplacing(string source, string destination, bool overwrite) => MoveReplacing(source, destination, overwrite, Rename);

    /// <summary>Moves with <paramref name="rename"/>, which returns false when the two paths lie on different volumes.</summary>
    internal static string? MoveReplacing(string source, string destination, bool overwrite, Func<string, string, bool, bool> rename)
    {
        string? leftover = null;
        InFolders(destination, () => leftover = MoveIntoFolder(source, destination, overwrite, rename));
        return leftover;
    }

    private static string? MoveIntoFolder(string source, string destination, bool overwrite, Func<string, string, bool, bool> rename)
    {
        if (rename(source, destination, overwrite))
        {
            return null;
        }

        using (FileStream from = new(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            CopyReplacing(from, destination, overwrite);
        }

        return DeleteSource(source);
    }

    /// <summary>
    /// Creates <paramref name="destination"/>'s folder and the missing ones above it, then runs <paramref name="write"/>;
    /// when the creation or the write fails, removes the folders it made, deepest first, while they are empty, and stops at
    /// the first that cannot be removed, which the exception then names.
    /// </summary>
    private static void InFolders(string destination, Action write)
    {
        string folder = Path.GetDirectoryName(destination)!;
        List<string> made = MissingFolders(folder);
        try
        {
            Directory.CreateDirectory(folder);
            write();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RemoveFolders(made, e);
            throw;
        }
    }

    /// <summary>The folders from <paramref name="folder"/> up that do not exist, deepest first.</summary>
    private static List<string> MissingFolders(string folder)
    {
        List<string> missing = [];
        for (string? current = folder; current is not null && !Directory.Exists(current); current = Path.GetDirectoryName(current))
        {
            missing.Add(current);
        }

        return missing;
    }

    private static void RemoveFolders(List<string> made, Exception failure)
    {
        foreach (string folder in made.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(folder, recursive: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{failure.Message}; the folder {folder} it made could not be removed: {e.Message}", failure);
            }
        }
    }

    /// <summary>Deletes a moved file's source once its copy is in place.</summary>
    /// <returns>Null once it is gone; why it could not be deleted otherwise.</returns>
    internal static string? DeleteSource(string source)
    {
        try
        {
            File.Delete(source);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>A rename that never becomes a copy on Windows; false when the two paths lie on different volumes.</summary>
    private static bool Rename(string source, string destination, bool overwrite)
    {
        if (OperatingSystem.IsWindows())
        {
            return FinalPath.Rename(source, destination, overwrite);
        }

        File.Move(source, destination, overwrite);
        return true;
    }

    private static void DeleteTemporary(string temporary, Exception failure)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"{failure.Message}; the temporary file {temporary} could not be deleted: {e.Message}", failure);
        }
    }

    private static void CheckProjectIsLocal(string project)
    {
        if (IsNetwork(project))
        {
            throw new McpException($"the project {project} is on a network path; named stills and clips are written only on a local drive");
        }
    }

    /// <summary>
    /// Refuses a Windows device name in any of the path's own components, before the full path is taken: older Windows turns
    /// a full path ending in a device name into the device's own path.
    /// </summary>
    private static void CheckDevices(string path, string relative)
    {
        // Windows reserves a device name whatever extension follows it: nul.png opens the null device.
        string? device = relative.Split('/', '\\').Select(part => part.Split('.')[0].TrimEnd(' ')).FirstOrDefault(IsDevice);
        if (device is not null)
        {
            throw new McpException($"path {path} names the Windows device {device.ToUpperInvariant()}; pick another file name");
        }
    }

    /// <summary>
    /// Refuses a name ending in a space or a dot in the path as written, before the full path is taken, which trims some
    /// of them; a . or .. part is a step, not a name.
    /// </summary>
    private static void CheckNames(string path, string relative)
    {
        foreach (string part in relative.Split('/', '\\').Where(part => part is not ("." or "..")))
        {
            CheckOneReading(path, part);
        }
    }

    /// <summary>
    /// Refuses a name ending in a space or a dot on Windows, which trims those from a path's last part only, so a check that
    /// opens the part as the last would read another entry than a write that passes through it; elsewhere such a name is
    /// read one way and passes.
    /// </summary>
    private static void CheckOneReading(string path, string part)
    {
        if (OperatingSystem.IsWindows() && part != part.TrimEnd('.', ' '))
        {
            throw new McpException(
                $"path {path} has a folder or file name ending in a space or a dot ({part}); Windows reads such a name two ways, so it is not written"
            );
        }
    }

    /// <summary>
    /// Refuses where a link leads, on Windows, when one of its names after the root ends in a space or a dot: GetFullPath
    /// trims such a name and GetRelativePath reads it as its trimmed sibling, so the checks would hold another folder than
    /// the one Windows reaches; a . or .. part is a step, not a name. Elsewhere such a name is read one way and passes.
    /// </summary>
    /// <param name="path">The path as the caller gave it.</param>
    /// <param name="scanned">The text whose names are read.</param>
    /// <param name="named">The text the refusal names.</param>
    internal static void CheckLinkedNames(string path, string scanned, string named)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (FirstDottedName(scanned) is { } dotted)
        {
            throw new McpException(
                $"path {path} leads through a link to {named}, whose name {dotted.Part} ends in a space or a dot; "
                    + "Windows reads such a name two ways, so it is not written"
            );
        }
    }

    /// <summary>
    /// The first name of <paramref name="scanned"/> after its root that ends in a space or a dot, with the index just past
    /// it; null for none. A . or .. part is a step, not a name.
    /// </summary>
    private static (string Part, int End)? FirstDottedName(string scanned)
    {
        int start = Path.GetPathRoot(scanned.AsSpan()).Length;
        while (start <= scanned.Length)
        {
            int separator = scanned.AsSpan(start).IndexOfAny('\\', '/');
            int end = separator < 0 ? scanned.Length : start + separator;
            string name = scanned[start..end];
            if (name is not ("" or "." or "..") && name != name.TrimEnd('.', ' '))
            {
                return (name, end);
            }

            start = end + 1;
        }

        return null;
    }

    private static bool IsDevice(string stem) => DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase);

    private static void CheckLocalDrive(string path, string full)
    {
        if (IsRemoteDrive(full))
        {
            throw new McpException($"path {path} is on the network drive {full[..2]}; save on a local drive");
        }
    }

    /// <summary>Whether a path or link target names a network or device path, or a mapped network drive; never opened.</summary>
    private static bool IsNetwork(string target) =>
        target.StartsWith(@"\\", StringComparison.Ordinal)
        || target.StartsWith("//", StringComparison.Ordinal)
        || target.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase)
        || IsRemoteDrive(target);

    private static bool IsRemoteDrive(string path) => OperatingSystem.IsWindows() && FinalPath.IsRemoteDrive(path);

    /// <summary>A path component as Windows finds it: without the trailing dots and spaces it ignores.</summary>
    private static bool IsNamed(string part, string name) => string.Equals(part.TrimEnd('.', ' '), name, StringComparison.OrdinalIgnoreCase);

    private static string[] Parts(string relative) => relative.Split(Path.DirectorySeparatorChar);

    /// <summary>The components from <paramref name="start"/> down to <paramref name="full"/>; none when they are the same.</summary>
    private static string[] PartsFrom(string start, string full)
    {
        string relative = Path.GetRelativePath(start, full);
        return relative == "." ? [] : Parts(relative);
    }

    [GeneratedRegex(@"~\d")]
    private static partial Regex ShortNamePattern();

    /// <summary>
    /// Where a walk stopped: the deepest position that exists, every link on its way followed, and the components below it
    /// that do not exist yet.
    /// </summary>
    private sealed record Walked(string Existing, string[] Missing);

    /// <summary>One path's checks against one set of places.</summary>
    private sealed class DestinationCheck(OutputPlaces places, string path, bool projectOnly)
    {
        private readonly IReadOnlyList<OutputPlace> _given = places.Allowed(projectOnly);
        private readonly IReadOnlyList<OutputPlace> _final = places.AllowedFinal(projectOnly);
        private readonly string? _described = places.Describe(projectOnly);
        private int _hops;

        /// <summary>
        /// Checks the path as written (<paramref name="written"/>, without any res:// prefix), then walks it from its place,
        /// following every link and holding every position the walk reaches to the places, then holds the final path Windows
        /// gives to the places in their final forms.
        /// </summary>
        public void Run(string full, string written)
        {
            OutputPlace place = PlaceOf(full, finalOnly: false) ?? throw new McpException(OutsideMessage());
            string fromWalk = Path.GetRelativePath(place.WalkFrom, full);
            CheckParts(fromWalk);
            if (IsInsideDotGit(place, full))
            {
                throw new McpException($"path {path} is inside a .git folder, which git owns; save outside it");
            }

            if (IsUnderDotGodot(full, finalOnly: false))
            {
                throw new McpException($"path {path} is under .godot/, which the editor owns; pick a tracked folder");
            }

            CheckNames(path, written);
            CheckFinalPath(full, Walk(place.WalkFrom, Parts(fromWalk)));
        }

        private string OutsideMessage() =>
            _described is null
                ? $"path {path} resolves outside the project {places.Project}; save inside the project"
                : $"path {path} resolves outside {_described}";

        /// <summary>
        /// The first allowed place holding <paramref name="full"/>, under either form of it, or under its final form only;
        /// null for none, and for a UNC or device path.
        /// </summary>
        private OutputPlace? PlaceOf(string full, bool finalOnly)
        {
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            return finalOnly ? Holding(_final, full) : Holding(_given, full) ?? Holding(_final, full);
        }

        private static OutputPlace? Holding(IReadOnlyList<OutputPlace> allowed, string full) =>
            allowed.FirstOrDefault(place => !HeadlessTools.IsOutside(Path.GetRelativePath(place.Folder, full)));

        /// <summary>Refuses an NTFS stream or a short (8.3) name on the way from the walk's start.</summary>
        private void CheckParts(string fromWalk)
        {
            // A ':' after the drive names an NTFS alternate stream: README.md:x.png writes into README.md.
            if (fromWalk.Contains(':', StringComparison.Ordinal))
            {
                throw new McpException($"path must not contain ':' after the drive: {path}");
            }

            // GODOT~1 can name .godot, and a short name hides which folder it is.
            if (Parts(fromWalk).Any(part => ShortNamePattern().IsMatch(part)))
            {
                throw new McpException($"path {path} uses a short (8.3) name; use the folder's full name");
            }
        }

        private static bool IsInsideDotGit(OutputPlace place, string full) =>
            Parts(Path.GetRelativePath(place.WalkFrom, full)).Any(part => IsNamed(part, ".git"));

        /// <summary>
        /// Whether a path lies under a .godot folder outside a .tmp, counted against the final forms of the project and the
        /// checkout, and unless <paramref name="finalOnly"/> also against their given forms.
        /// </summary>
        private bool IsUnderDotGodot(string full, bool finalOnly) =>
            IsUnderDotGodot(full, places.FinalProject, places.FinalCheckout)
            || (!finalOnly && IsUnderDotGodot(full, places.Project, places.Checkout));

        /// <summary>
        /// Whether a path lies under a .godot folder outside a .tmp, counted from the checkout, else from the project: the
        /// project's own .godot always counts, wherever the project lies.
        /// </summary>
        private static bool IsUnderDotGodot(string full, string project, string? checkout)
        {
            string inProject = Path.GetRelativePath(project, full);
            if (!HeadlessTools.IsOutside(inProject) && IsNamed(Parts(inProject)[0], ".godot"))
            {
                return true;
            }

            string? inCheckout = checkout is null ? null : Path.GetRelativePath(checkout, full);
            string? counted = inCheckout is not null && !HeadlessTools.IsOutside(inCheckout) ? inCheckout : inProject;
            return !HeadlessTools.IsOutside(counted) && StartsGodotBeforeTmp(Parts(counted));
        }

        private static bool StartsGodotBeforeTmp(string[] parts)
        {
            foreach (string part in parts)
            {
                if (IsNamed(part, ".tmp"))
                {
                    return false;
                }

                if (IsNamed(part, ".godot"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Walks <paramref name="parts"/> down from <paramref name="start"/>, a position with no link left unfollowed:
        /// every entry is read without following it, a link is followed only after its target is held to the places, and
        /// every position reached is held to the places and the .git and .godot rules. Stops at the first missing entry.
        /// </summary>
        private Walked Walk(string start, string[] parts)
        {
            string current = start;
            for (int index = 0; index < parts.Length; index++)
            {
                CheckOneReading(parts[index]);
                string? reached = Step(current, Path.Combine(current, parts[index]));
                if (reached is null)
                {
                    string[] missing = parts[index..];
                    CheckMissing(current, missing);
                    return new Walked(current, missing);
                }

                current = reached;
                CheckReached(current, finalOnly: false);
            }

            return new Walked(current, []);
        }

        /// <summary>
        /// Refuses a name ending in a space or a dot: Windows trims those from a path's last part only, so the walk, which
        /// opens each part as the last, would read another entry than the write passes through. The parts come after
        /// GetFullPath, which has resolved every . and .. part and trimmed a dot ending a name, so a link's target is
        /// checked as stored in <see cref="Follow"/> before it is resolved.
        /// </summary>
        private void CheckOneReading(string part) => OutputDestination.CheckOneReading(path, part);

        /// <summary>Holds each position below the deepest existing one, which nothing can link from yet, to the places.</summary>
        private void CheckMissing(string existing, string[] missing)
        {
            string position = existing;
            foreach (string part in missing)
            {
                position = Path.Combine(position, part);
                CheckReached(position, finalOnly: false);
            }
        }

        /// <summary>Where <paramref name="entry"/> leads, its link followed; null when nothing is there.</summary>
        private string? Step(string current, string entry)
        {
            StoredLink? link = ReadLink(entry);
            if (link is not null)
            {
                return Follow(current, entry, link);
            }

            // Any other reparse point (cloud files, container layers) walks on as a folder; the final path checks guard it.
            return Path.Exists(entry) ? entry : null;
        }

        /// <summary>The link stored at <paramref name="entry"/>, read without following it; null for none.</summary>
        private StoredLink? ReadLink(string entry)
        {
            try
            {
                return OperatingSystem.IsWindows() ? FinalPath.ReadLink(entry) : LinkNamed(new FileInfo(entry).LinkTarget);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new McpException($"path {path} leads through a link at {entry} that cannot be followed: {e.Message}", e);
            }
        }

        /// <summary>A link as .NET names its target, where no reparse data can be read.</summary>
        private static StoredLink? LinkNamed(string? target) =>
            target is null ? null : new StoredLink(LinkKind.SymbolicLink, target, !Path.IsPathFullyQualified(target));

        /// <summary>
        /// Follows the link at <paramref name="entry"/>: its target is held to the places before anything opens it, then
        /// walked from its own place, so a link inside the target is followed too; the walked target must exist.
        /// </summary>
        private string Follow(string current, string entry, StoredLink link)
        {
            if (++_hops > MaxLinkHops)
            {
                throw new McpException(
                    $"path {path} leads through a link at {entry} that cannot be followed: it passes more than {MaxLinkHops} links"
                );
            }

            // GetFullPath trims a dot ending a name, so the target's names are checked as stored, before it is resolved.
            CheckDottedNames(StoredTarget(link), link.Target);

            // A relative symbolic link's target is taken from the link's own folder.
            string next = link.Relative ? Path.GetFullPath(link.Target, current) : AbsoluteTarget(entry, link);
            CheckReached(next, finalOnly: false);
            string start = PlaceOf(next, finalOnly: false)!.WalkFrom;
            Walked walked = Walk(start, PartsFrom(start, next));
            return walked.Missing.Length == 0
                ? walked.Existing
                : throw new McpException($"path {path} leads through a link at {entry} whose target {next} does not exist");
        }

        /// <summary>
        /// An absolute link's target as a path on a drive; a network target, a volume and anything else that is not a path
        /// on a drive (a volume GUID path, GLOBALROOT, a device) are refused before anything opens them.
        /// </summary>
        private string AbsoluteTarget(string entry, StoredLink link)
        {
            string target = StoredTarget(link);
            if (IsNetwork(target))
            {
                throw new McpException(
                    $"path {path} leads through a link at {entry} to the network path {target}; links to network paths are not followed"
                );
            }

            if (link.Kind == LinkKind.MountPoint && target.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
            {
                throw new McpException($"path {path} leads through a link at {entry} to the volume {target}; links to volumes are not followed");
            }

            return IsDrivePath(target)
                ? Path.GetFullPath(target)
                : throw new McpException(
                    $"path {path} leads through a link at {entry} to {link.Target}, which is not a path on a drive; such links are not followed"
                );
        }

        /// <summary>A link's target as stored, without the <c>\??\</c> prefix an absolute target carries.</summary>
        private static string StoredTarget(StoredLink link) =>
            link.Target.StartsWith(@"\??\", StringComparison.Ordinal) ? link.Target[4..] : link.Target;

        /// <summary>A fully qualified path on a drive letter, on Windows; a fully qualified path elsewhere.</summary>
        private static bool IsDrivePath(string target) =>
            OperatingSystem.IsWindows()
                ? target.Length >= 3 && char.IsAsciiLetter(target[0]) && target[1] == ':' && target[2] == '\\'
                : Path.IsPathFullyQualified(target);

        /// <summary>
        /// Opens the deepest position the walk found and holds the final path Windows gives for it, with the missing
        /// components below it, to the places in their final forms: long names, every link Windows follows, followed. The
        /// requested path's own deepest existing part must reach the same final path, so a link the walk misread is caught.
        /// </summary>
        private void CheckFinalPath(string full, Walked walked)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            string final = FinalOf(walked.Existing);
            if (IsNetwork(final))
            {
                throw new McpException(
                    $"path {path} leads through a link at {walked.Existing} to the network path {final}; links to network paths are not followed"
                );
            }

            string reckoned = FinalOf(Ancestor(full, walked.Missing.Length));
            if (!string.Equals(reckoned, final, StringComparison.OrdinalIgnoreCase))
            {
                throw new McpException($"path {path} leads to {reckoned} by Windows' reckoning but to {final} by its links; it is not written");
            }

            CheckReached(Path.Combine([final, .. walked.Missing]), finalOnly: true);
        }

        private static string Ancestor(string full, int levels)
        {
            string ancestor = full;
            for (int level = 0; level < levels; level++)
            {
                ancestor = Path.GetDirectoryName(ancestor)!;
            }

            return ancestor;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private string FinalOf(string position)
        {
            try
            {
                return FinalPath.Of(position);
            }
            catch (IOException e)
            {
                throw new McpException($"path {path} leads through {position}, whose final path Windows cannot give: {e.Message}", e);
            }
        }

        private void CheckReached(string reached, bool finalOnly)
        {
            // GetRelativePath reads X:\W.\x as inside X:\W, so a dotted name is refused before any place is asked to hold it.
            CheckDottedNames(reached, reached);
            OutputPlace place =
                PlaceOf(reached, finalOnly)
                ?? throw new McpException(
                    _described is null
                        ? $"path {path} leads through a link to {reached}, outside the project; save inside the project"
                        : $"path {path} leads through a link to {reached}, outside {_described}"
                );
            if (IsInsideDotGit(place, reached))
            {
                throw new McpException($"path {path} leads through a link to {reached}, inside a .git folder, which git owns; save outside it");
            }

            if (IsUnderDotGodot(reached, finalOnly))
            {
                throw new McpException($"path {path} leads through a link to {reached}, under .godot/, which the editor owns; pick a tracked folder");
            }
        }

        /// <summary>
        /// Refuses <paramref name="scanned"/> when one of its names after the root ends in a space or a dot, as
        /// <see cref="CheckLinkedNames"/> does, but names the place when the name lies within the text of an allowed place's
        /// folder or walk start, given or final, that <paramref name="scanned"/> starts with: no link is to blame then. The
        /// match is textual, since GetRelativePath and GetFullPath trim the name and read the dotted folder as its sibling.
        /// </summary>
        private void CheckDottedNames(string scanned, string named)
        {
            if (!OperatingSystem.IsWindows() || FirstDottedName(scanned) is not { } dotted)
            {
                return;
            }

            foreach (OutputPlace place in _given.Concat(_final))
            {
                foreach ((string text, string label) in new[] { (place.Folder, place.Label), (place.WalkFrom, place.WalkFromLabel) })
                {
                    if (Encloses(text, scanned, dotted.End))
                    {
                        throw new McpException(
                            $"path {path} lies under {label} {text}, whose name {dotted.Part} ends in a space or a dot; "
                                + "Windows reads such a name two ways, so named stills and clips are not written there"
                        );
                    }
                }
            }

            CheckLinkedNames(path, scanned, named);
        }

        /// <summary>
        /// Whether <paramref name="scanned"/> is <paramref name="text"/> or lies under it, compared as text, and the name ending
        /// at <paramref name="nameEnd"/> ends within <paramref name="text"/>.
        /// </summary>
        private static bool Encloses(string text, string scanned, int nameEnd) =>
            nameEnd <= text.Length
            && scanned.StartsWith(text, StringComparison.OrdinalIgnoreCase)
            && (scanned.Length == text.Length || scanned[text.Length] is '\\' or '/');
    }
}
