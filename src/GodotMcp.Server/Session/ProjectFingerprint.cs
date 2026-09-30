namespace GodotMcp.Server.Session;

/// <summary>
/// What a project folder's files look like on disk: each file's path relative to the folder, its size and its last write
/// time. <c>.git</c>, every folder holding a <c>.gdignore</c> (its whole subtree), the root's <c>override.cfg</c> when it
/// is the server's own, marked one (a live session's, which a warm host read at start and freed the bridge of; a user's own
/// counts) and the root's <c>.godot/</c> are left out, except <c>.godot/imported/</c>, <c>.godot/uid_cache.bin</c> and
/// <c>.godot/global_script_class_cache.cfg</c>, which a Godot process reads at start. A folder that is a junction or a
/// symbolic link is listed as itself, with its own write time, and never followed, so one pointing at an ancestor ends.
/// Two fingerprints are equal when they list the same files with the same sizes and times; a folder that is gone has one of
/// its own that equals no fingerprint of a folder that was there.
/// </summary>
internal sealed class ProjectFingerprint : IEquatable<ProjectFingerprint>
{
    private const string GodotFolder = ".godot";
    private static readonly string[] GodotFiles = ["uid_cache.bin", "global_script_class_cache.cfg"];
    private static readonly EnumerationOptions Listing = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.None,
    };

    private readonly Entry[] _entries;
    private readonly bool _missing;

    private ProjectFingerprint(Entry[] entries, bool missing)
    {
        _entries = entries;
        _missing = missing;
    }

    /// <summary>How many files it lists.</summary>
    public int Count => _entries.Length;

    /// <summary>Reads the folder's fingerprint now.</summary>
    public static ProjectFingerprint Take(string projectDir)
    {
        DirectoryInfo root = new(ProjectPaths.Normalise(projectDir));
        if (!root.Exists)
        {
            return new ProjectFingerprint([], missing: true);
        }

        List<Entry> entries = [];
        Walk(root, string.Empty, entries, isRoot: true);
        TakeGodotFolder(root, entries);
        entries.Sort((first, second) => string.CompareOrdinal(first.Path, second.Path));
        return new ProjectFingerprint([.. entries], missing: false);
    }

    public bool Equals(ProjectFingerprint? other) =>
        other is not null && _missing == other._missing && _entries.AsSpan().SequenceEqual(other._entries);

    public override bool Equals(object? obj) => Equals(obj as ProjectFingerprint);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(_missing);
        foreach (Entry entry in _entries)
        {
            hash.Add(entry);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Adds the folder's files, and its subfolders' unless left out, under <paramref name="prefix"/>; a linked subfolder is
    /// added as itself.
    /// </summary>
    private static void Walk(DirectoryInfo folder, string prefix, List<Entry> entries, bool isRoot)
    {
        foreach (FileSystemInfo item in List(folder))
        {
            string path = prefix + item.Name;
            if (item is FileInfo file)
            {
                if (Counts(file, isRoot))
                {
                    entries.Add(new Entry(path, file.Length, file.LastWriteTimeUtc.Ticks));
                }
            }
            else if (item is DirectoryInfo child && !IsLeftOut(child, isRoot))
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    entries.Add(new Entry(path + "/", 0, child.LastWriteTimeUtc.Ticks));
                }
                else
                {
                    Walk(child, path + "/", entries, isRoot: false);
                }
            }
        }
    }

    /// <summary>Whether a file counts: every one but the root's override.cfg when it is the server's own.</summary>
    private static bool Counts(FileInfo file, bool inRoot) => !(inRoot && file.Name == "override.cfg" && IsServersOverride(file));

    /// <summary>
    /// Whether the root's override.cfg is the server's own: its first line is <see cref="OverrideFile.Marker"/>. One gone since
    /// the listing is not there to count; one that cannot be read counts, so a change to it still shows.
    /// </summary>
    private static bool IsServersOverride(FileInfo file)
    {
        try
        {
            return OverrideFile.IsOurs(file.FullName);
        }
        catch (FileNotFoundException)
        {
            // Deleted between the listing and the read: the server's own going away, or anyone's, leaves nothing to list.
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Held by its writer or locked away: counted as the user's, which at worst costs one cold start.
            return false;
        }
    }

    /// <summary>Whether a subfolder's files are left out: <c>.git</c>, the root's <c>.godot</c>, and a folder holding <c>.gdignore</c>.</summary>
    private static bool IsLeftOut(DirectoryInfo folder, bool inRoot) =>
        folder.Name == ".git" || (inRoot && folder.Name == GodotFolder) || File.Exists(Path.Combine(folder.FullName, ".gdignore"));

    /// <summary>Adds the parts of the root's <c>.godot/</c> a Godot process reads at start.</summary>
    private static void TakeGodotFolder(DirectoryInfo root, List<Entry> entries)
    {
        string godot = Path.Combine(root.FullName, GodotFolder);
        foreach (string name in GodotFiles)
        {
            FileInfo file = new(Path.Combine(godot, name));
            if (file.Exists)
            {
                entries.Add(new Entry($"{GodotFolder}/{name}", file.Length, file.LastWriteTimeUtc.Ticks));
            }
        }

        DirectoryInfo imported = new(Path.Combine(godot, "imported"));
        if (imported.Exists)
        {
            Walk(imported, $"{GodotFolder}/imported/", entries, isRoot: false);
        }
    }

    /// <summary>The folder's files and subfolders; none when it went away during the walk.</summary>
    private static IEnumerable<FileSystemInfo> List(DirectoryInfo folder)
    {
        try
        {
            return [.. folder.EnumerateFileSystemInfos("*", Listing)];
        }
        catch (DirectoryNotFoundException)
        {
            // Deleted between its parent's listing and its own; its files are gone, which the fingerprint then says.
            return [];
        }
    }

    /// <summary>One file: its path relative to the project folder, '/'-separated, its size and its last write time in ticks.</summary>
    private readonly record struct Entry(string Path, long Size, long WriteTicks);
}
