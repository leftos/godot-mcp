using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>How a project's C# project file was found: named by project.godot or the only one, none, or several to choose from.</summary>
internal enum CsprojKind
{
    Found,
    None,
    Several,
}

/// <summary>The C# project a Godot project builds, and the assembly name Godot loads; <see cref="Note"/> says why there is none.</summary>
internal sealed record CsprojLookup(CsprojKind Kind, string? ProjectFile, string? AssemblyName, string? Note);

/// <summary>
/// The files the prep looks at, as full paths: the C# build's inputs from the whole git top level (or the project folder
/// outside git), and the project folder's <c>.import</c> sidecars, <c>.uid</c> files, GDScript files and text scenes and
/// resources (<c>.tscn</c>, <c>.tres</c>).
/// </summary>
internal sealed record ProjectFiles(
    IReadOnlyList<string> BuildInputs,
    IReadOnlyList<string> ImportFiles,
    IReadOnlyList<string> UidFiles,
    IReadOnlyList<string> Scripts,
    IReadOnlyList<string> TextResources
);

/// <summary>What run_project's prep finds in a project before deciding to build or import.</summary>
internal static partial class PrepScan
{
    private static readonly HashSet<string> InputExtensions = new(
        [".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".resx"],
        StringComparer.OrdinalIgnoreCase
    );

    // The files a load reads without an import: Godot's own resources, and the textures modules/dds and modules/ktx register
    // a loader for (4.7.2 register_types.cpp of each).
    private static readonly HashSet<string> LoadsWithoutImportExtensions = new([".tres", ".res", ".dds", ".ktx"], StringComparer.OrdinalIgnoreCase);

    private const string UidPrefix = "uid://";

    /// <summary>What every import sidecar of a source is named: the source's own path plus this.</summary>
    private const string ImportSuffix = ".import";

    // ResourceUID's char_count ('z' - 'a', 25) and base (char_count + ('9' - '0'), 34): 4.7.2 core/io/resource_uid.cpp L44-45.
    private const ulong UidCharCount = 'z' - 'a';
    private const ulong UidBase = UidCharCount + ('9' - '0');

    private static readonly HashSet<string> InputNames = new(["global.json", "nuget.config", "packages.lock.json"], StringComparer.OrdinalIgnoreCase);

    // The folders a walk outside git skips: VCS data, Godot's cache and build outputs.
    private static readonly HashSet<string> SkippedFolders = new(
        [".git", ".godot", "bin", "obj", ".vs", "node_modules"],
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// The csproj project.godot's <c>[dotnet] project/assembly_name</c> names (as Godot's GodotSharpDirs finds it), else the
    /// only <c>*.csproj</c> beside project.godot.
    /// </summary>
    public static CsprojLookup FindCsproj(string projectDir)
    {
        if (ReadAssemblyName(Path.Combine(projectDir, "project.godot")) is { } named)
        {
            string path = Path.Combine(projectDir, named + ".csproj");
            return File.Exists(path)
                ? new CsprojLookup(CsprojKind.Found, path, named, null)
                : new CsprojLookup(
                    CsprojKind.None,
                    null,
                    null,
                    $"project.godot's [dotnet] project/assembly_name is \"{named}\", but {path} does not exist, so nothing was built."
                );
        }

        string[] found = [.. Directory.EnumerateFiles(projectDir, "*.csproj").Where(IsCsproj).Order(StringComparer.OrdinalIgnoreCase)];
        return found.Length switch
        {
            0 => new CsprojLookup(CsprojKind.None, null, null, null),
            1 => new CsprojLookup(CsprojKind.Found, found[0], Path.GetFileNameWithoutExtension(found[0]), null),
            _ => new CsprojLookup(
                CsprojKind.Several,
                null,
                null,
                $"The C# build was skipped: {string.Join(", ", found.Select(Path.GetFileName))} sit beside project.godot, and its "
                    + "[dotnet] project/assembly_name, which picks one, is not set."
            ),
        };
    }

    /// <summary>
    /// Where Godot loads the prep's build from (GodotSharpDirs' <c>.godot/mono/temp/bin/Debug</c>, <see cref="ProjectPrep.Configuration"/>).
    /// </summary>
    public static string AssemblyPath(string projectDir, string assemblyName) =>
        Path.Combine(projectDir, ".godot", "mono", "temp", "bin", ProjectPrep.Configuration, assemblyName + ".dll");

    /// <summary>The file the prep touches after each green build.</summary>
    public static string StampPath(string projectDir) => Path.Combine(ProjectPrep.LogFolder(projectDir), "build.stamp");

    /// <summary>
    /// The project's files: in git, the versioned and untracked-but-not-ignored files of the whole top level (a csproj
    /// builds sibling projects); outside git, the project folder walked without its VCS, cache and build folders.
    /// </summary>
    public static ProjectFiles Scan(string projectDir, ILogger logger)
    {
        (List<string> TopFiles, List<string> ProjectOnly)? listed = ListWithGit(projectDir, logger);
        List<string> topFiles = listed?.TopFiles ?? WalkFolder(projectDir);
        List<string> projectFiles = listed?.ProjectOnly ?? topFiles;
        return new ProjectFiles(
            [.. topFiles.Where(IsBuildInput)],
            [.. projectFiles.Where(file => HasExtension(file, ImportSuffix))],
            [.. projectFiles.Where(file => HasExtension(file, ".uid"))],
            [.. projectFiles.Where(file => HasExtension(file, ".gd"))],
            [.. projectFiles.Where(file => HasExtension(file, ".tscn") || HasExtension(file, ".tres"))]
        );
    }

    /// <summary>Where the editor's scan writes the <c>class_name</c> globals a run reads (<c>project_settings.cpp</c> L1469).</summary>
    public static string ClassCachePath(string projectDir) => Path.Combine(projectDir, ".godot", "global_script_class_cache.cfg");

    /// <summary>
    /// The project's <c>.import</c> sidecars as they are now: what <see cref="Scan"/> lists, for a prep that ran an import and
    /// must record the sidecars that import itself created.
    /// </summary>
    public static IReadOnlyList<string> ImportFiles(string projectDir, ILogger logger)
    {
        (List<string> TopFiles, List<string> ProjectOnly)? listed = ListWithGit(projectDir, logger);
        return [.. (listed?.ProjectOnly ?? WalkFolder(projectDir)).Where(file => HasExtension(file, ImportSuffix))];
    }

    /// <summary>
    /// Whether the assembly is missing, or an input is newer than both the assembly and the stamp (an input that leaves the
    /// assembly alone, such as a .props edit, would otherwise rebuild on every run).
    /// </summary>
    public static bool IsStale(string assemblyPath, string stampPath, IEnumerable<string> inputs)
    {
        if (!File.Exists(assemblyPath))
        {
            return true;
        }

        // A missing stamp reads as 1601, older than anything.
        DateTime assembly = File.GetLastWriteTimeUtc(assemblyPath);
        DateTime stamp = File.GetLastWriteTimeUtc(stampPath);
        DateTime built = assembly > stamp ? assembly : stamp;
        return inputs.Any(input => File.GetLastWriteTimeUtc(input) > built);
    }

    /// <summary>
    /// Whether a Godot import is needed: a <c>.import</c> sidecar whose <c>dest_files</c> are not all present, whose source is
    /// not the one it imported or whose settings changed since the prep last saw them (<see cref="ImportOutdated"/>), or a uid
    /// <c>.godot/uid_cache.bin</c> does not hold (<see cref="IsUidCacheStale"/>), or a script declaring <c>class_name</c> that
    /// the class cache may not hold yet. A missing <c>.godot/</c> alone is not a reason.
    /// </summary>
    /// <param name="fingerprints">The md5 the prep last saw for each sidecar (<see cref="ImportFingerprints"/>).</param>
    public static bool ImportNeeded(string projectDir, ProjectFiles files, IReadOnlyDictionary<string, string> fingerprints, ILogger logger) =>
        IsUidCacheStale(projectDir, files, logger)
        || files.ImportFiles.Any(sidecar => HasMissingTarget(projectDir, sidecar) || ImportOutdated(projectDir, sidecar, fingerprints))
        || IsClassCacheStale(projectDir, files.Scripts);

    /// <summary>
    /// Whether <c>.godot/uid_cache.bin</c> lacks a uid the project's files record: with no cache, whether any <c>.uid</c>
    /// file exists; else whether a <c>.uid</c> file's id or a <c>.tscn</c>/<c>.tres</c> header's <c>uid="…"</c> is not among
    /// the cache's ids. Contents are compared, not times: the headless tools append the uids they mint to the cache, and an
    /// editor open on the project rewrites the cache from its own list on its next save, dropping them (4.7.2
    /// <c>core/io/resource_uid.cpp</c> L343-375). A cache that does not parse is stale, and logged. Only files the scan
    /// reaches count (<see cref="IsScanned"/>): an import records no other, so one elsewhere would make it due on every run.
    /// </summary>
    private static bool IsUidCacheStale(string projectDir, ProjectFiles files, ILogger logger)
    {
        string cache = Path.Combine(projectDir, ".godot", "uid_cache.bin");
        string[] uidFiles = [.. files.UidFiles.Where(file => File.Exists(file) && IsScanned(projectDir, file))];
        if (!File.Exists(cache))
        {
            return uidFiles.Length > 0;
        }

        HashSet<long>? cached = ReadUidCache(cache, logger);
        if (cached is null)
        {
            return true;
        }

        IEnumerable<string> recorded = uidFiles
            .Select(ReadUidFile)
            .Concat(files.TextResources.Where(file => File.Exists(file) && IsScanned(projectDir, file)).Select(ReadHeaderUid));
        return recorded.Where(text => text.Length > 0).Select(text => DecodeUid(text, logger)).Any(id => id is { } known && !cached.Contains(known));
    }

    /// <summary>
    /// The full path of the outermost folder between <paramref name="projectDir"/> and <paramref name="file"/> that Godot's
    /// scan skips for its name starting with "." or for holding a <c>.gdignore</c> (4.7.2
    /// <c>editor/file_system/editor_file_system.cpp</c> L1179-1185 and L3502-3505) or for holding a
    /// <c>project.godot</c> of its own, a nested project (L3494-3500), or null.
    /// </summary>
    internal static string? UnscannedFolder(string projectDir, string file) =>
        FoldersBetween(projectDir, file)
            .FirstOrDefault(folder =>
                Path.GetFileName(folder).StartsWith('.')
                || File.Exists(Path.Combine(folder, ".gdignore"))
                || File.Exists(Path.Combine(folder, "project.godot"))
            );

    /// <summary>
    /// Whether Godot's scan, and so an import, reaches <paramref name="file"/>: no folder on its way skips it
    /// (<see cref="UnscannedFolder"/>).
    /// </summary>
    private static bool IsScanned(string projectDir, string file) => UnscannedFolder(projectDir, file) is null;

    /// <summary>The folders from <paramref name="projectDir"/>'s child down to the one holding <paramref name="file"/>, outermost first.</summary>
    private static IEnumerable<string> FoldersBetween(string projectDir, string file)
    {
        string folder = projectDir;
        string relative = Path.GetRelativePath(projectDir, Path.GetDirectoryName(file)!);
        foreach (string name in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Where(name => name is not ("." or "")))
        {
            folder = Path.Combine(folder, name);
            yield return folder;
        }
    }

    /// <summary>
    /// The ids in a uid cache, in the engine's layout (4.7.2 <c>core/io/resource_uid.cpp</c> L305-341, <c>load_from_cache</c>;
    /// written by <c>encode_binary_cache</c> L259-272 and appended to by <c>update_cache</c> L343-375): a little-endian 32-bit
    /// entry count, then per entry a 64-bit id, a 32-bit byte length and that many bytes of UTF-8 <c>res://</c> path; bytes
    /// after the last entry are not read. Null, logged, when the file does not parse or cannot be read.
    /// </summary>
    internal static HashSet<long>? ReadUidCache(string cache, ILogger logger)
    {
        try
        {
            using FileStream stream = File.OpenRead(cache);
            using BinaryReader reader = new(stream);
            uint count = reader.ReadUInt32();
            HashSet<long> ids = [];
            for (uint entry = 0; entry < count; entry++)
            {
                ids.Add(reader.ReadInt64());
                int length = reader.ReadInt32();
                if (length < 0 || length > stream.Length - stream.Position)
                {
                    throw new InvalidDataException($"entry {entry} of {count} claims a {length}-byte path past the end of the file");
                }

                stream.Seek(length, SeekOrigin.Current);
            }

            return ids;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            UidCacheUnreadable(logger, e, cache);
            return null;
        }
    }

    /// <summary>
    /// The 64-bit id of <c>uid://…</c> text, ported from 4.7.2 <c>ResourceUID::text_to_id</c> (<c>core/io/resource_uid.cpp</c>
    /// L92-111, with <c>char_count</c> and <c>base</c> from L44-45): base 34, <c>a</c>-<c>z</c> worth 0-25 and <c>0</c>-<c>9</c>
    /// worth 25-34, the top bit cleared. Null for text the engine reads as invalid.
    /// </summary>
    internal static long? TextToId(string text)
    {
        if (!text.StartsWith(UidPrefix, StringComparison.Ordinal) || text == "uid://<invalid>")
        {
            return null;
        }

        ulong uid = 0;
        foreach (char c in text.AsSpan(UidPrefix.Length))
        {
            ulong digit;
            if (c is >= 'a' and <= 'z')
            {
                digit = (ulong)(c - 'a');
            }
            else if (c is >= '0' and <= '9')
            {
                digit = (ulong)(c - '0') + UidCharCount;
            }
            else
            {
                return null;
            }

            uid = unchecked((uid * UidBase) + digit);
        }

        return (long)(uid & 0x7FFFFFFFFFFFFFFF);
    }

    private static long? DecodeUid(string text, ILogger logger)
    {
        long? id = TextToId(text);
        if (id is null)
        {
            UidUndecodable(logger, text);
        }

        return id;
    }

    /// <summary>The <c>uid://</c> text a <c>.uid</c> file holds (its first non-blank line), or "".</summary>
    private static string ReadUidFile(string uidFile) =>
        File.ReadLines(uidFile).Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty;

    /// <summary>The <c>uid="…"</c> of a text scene's or resource's header (its first line only), or "" when it has none.</summary>
    private static string ReadHeaderUid(string resource)
    {
        string? header = File.ReadLines(resource).FirstOrDefault();
        Match match = header is null ? Match.Empty : HeaderUid().Match(header);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>
    /// Whether a file a request loads must be imported first: it is not one that loads without an import (<c>.tres</c>,
    /// <c>.res</c>, <c>.dds</c>, <c>.ktx</c>), and it has no <c>.import</c> sidecar, or one whose <c>dest_files</c> are not
    /// all present or whose source is not the one it imported (<see cref="ImportOutdated"/>) (an ignored sidecar is not in
    /// <see cref="ProjectFiles.ImportFiles"/>). A never-imported image fails to load with "No loader found" (4.7.2
    /// <c>core/io/resource_loader.cpp</c> L332).
    /// </summary>
    public static bool AssetNeedsImport(string projectDir, string assetPath, IReadOnlyDictionary<string, string> fingerprints)
    {
        string sidecar = assetPath + ImportSuffix;
        return !LoadsWithoutImportExtensions.Contains(Path.GetExtension(assetPath))
            && (!File.Exists(sidecar) || HasMissingTarget(projectDir, sidecar) || ImportOutdated(projectDir, sidecar, fingerprints));
    }

    /// <summary>
    /// Whether a sidecar changed since it was imported, which the editor asks by the <c>.md5</c> its import wrote under
    /// <c>.godot/imported/</c> and by the md5 it holds in its own file cache. A sidecar whose source is not there is never due,
    /// since the editor never imports one without a source; otherwise it is due when the md5 the prep recorded differs from the
    /// sidecar's own (<see cref="SettingsChanged"/>), or its <c>.md5</c> is missing or the source differs from the one that
    /// import recorded (<see cref="SourceChanged"/>) (4.7.2 <c>editor/file_system/editor_file_system.cpp</c> L673-760). The
    /// sidecar is hashed only when the record holds one for it, since the editor's own cache of import parameters cannot be read
    /// outside the editor. A sidecar the editor never imports, its <c>importer</c> "keep" or "skip" or its <c>valid=false</c>
    /// (L673, L643), is never due, and a sidecar missing from the working tree names nothing to import.
    /// </summary>
    public static bool ImportOutdated(string projectDir, string sidecar, IReadOnlyDictionary<string, string> fingerprints)
    {
        if (!File.Exists(sidecar))
        {
            return false;
        }

        (string? importer, bool invalid) = SidecarState(sidecar);
        if (importer is "keep" or "skip" || invalid)
        {
            return false;
        }

        string source = sidecar[..^ImportSuffix.Length];
        if (!File.Exists(source))
        {
            return false;
        }

        return SettingsChanged(projectDir, sidecar, fingerprints) || SourceChanged(projectDir, source);
    }

    /// <summary>
    /// Whether a source differs from the one its import recorded: a missing <c>.md5</c> is due, else a <c>source_md5</c> that
    /// differs from the source's own md5. The source is read only when it is newer than the <c>.md5</c>, so a warm project does
    /// not hash every asset.
    /// </summary>
    private static bool SourceChanged(string projectDir, string source)
    {
        string recorded = ImportMd5Path(projectDir, source);
        if (!File.Exists(recorded))
        {
            return true;
        }

        if (File.GetLastWriteTimeUtc(source) <= File.GetLastWriteTimeUtc(recorded))
        {
            return false;
        }

        string? imported = ReadImportMd5(recorded);
        return imported is null || !string.Equals(imported, SourceMd5(source), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a sidecar's own import settings changed since the prep recorded them: it has a recorded md5 and it differs from
    /// the sidecar's. A sidecar with no record is not due on this ground, since there is nothing to compare yet.
    /// </summary>
    private static bool SettingsChanged(string projectDir, string sidecar, IReadOnlyDictionary<string, string> fingerprints) =>
        fingerprints.TryGetValue(ImportFingerprints.KeyFor(projectDir, sidecar), out string? seen) && seen != ImportFingerprints.Md5Of(sidecar);

    /// <summary>The <c>.md5</c> an import writes for a source: <c>.godot/imported/&lt;source file name&gt;-&lt;md5 of the
    /// res:// path&gt;.md5</c> (4.7.2 <c>core/io/resource_importer.cpp</c> L541-543), the path hashed as UTF-8 with forward
    /// slashes, lowercase hex.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA5351:Do not use broken cryptographic algorithms",
        Justification = "Godot names its imported files and the .md5 it writes them by md5; nothing here is a security use."
    )]
    private static string ImportMd5Path(string projectDir, string source)
    {
        string relative = Path.GetRelativePath(projectDir, source).Replace('\\', '/');
        string hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("res://" + relative)));
        return Path.Combine(projectDir, ".godot", "imported", $"{Path.GetFileName(source)}-{hash}.md5");
    }

    /// <summary>The md5 of a file's bytes, in the lowercase hex Godot's <c>FileAccess::get_md5</c> writes.</summary>
    [SuppressMessage(
        "Security",
        "CA5351:Do not use broken cryptographic algorithms",
        Justification = "The .md5 an import writes holds the source's md5; nothing here is a security use."
    )]
    private static string SourceMd5(string source)
    {
        using FileStream stream = File.OpenRead(source);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    /// <summary>The <c>source_md5="…"</c> of the <c>.md5</c> an import wrote, or null when it holds none.</summary>
    private static string? ReadImportMd5(string md5File) =>
        File.ReadLines(md5File)
            .Where(line => line.StartsWith("source_md5=", StringComparison.Ordinal))
            .Select(line => line["source_md5=".Length..].Trim().Trim('"'))
            .FirstOrDefault(text => text.Length > 0);

    /// <summary>A sidecar's <c>importer</c> (unquoted) and whether it marks itself <c>valid=false</c>.</summary>
    private static (string? Importer, bool Invalid) SidecarState(string sidecar)
    {
        string? importer = null;
        bool invalid = false;
        foreach (string raw in File.ReadLines(sidecar))
        {
            string line = raw.Trim();
            if (line.StartsWith("importer=", StringComparison.Ordinal))
            {
                importer = line["importer=".Length..].Trim().Trim('"');
            }
            else if (line is "valid=false")
            {
                invalid = true;
            }
        }

        return (importer, invalid);
    }

    /// <summary>
    /// Whether a script declaring <c>class_name</c> is newer than the class cache, or the cache is missing while any script
    /// declares one: a run reads the globals from the cache only, and only the editor's scan writes it.
    /// </summary>
    public static bool IsClassCacheStale(string projectDir, IEnumerable<string> scripts)
    {
        string cache = ClassCachePath(projectDir);
        bool cached = File.Exists(cache);
        DateTime written = File.GetLastWriteTimeUtc(cache);
        return scripts.Where(File.Exists).Where(script => !cached || File.GetLastWriteTimeUtc(script) > written).Any(DeclaresClassName);
    }

    private static bool DeclaresClassName(string script) => File.ReadLines(script).Any(line => ClassNameLine().IsMatch(line));

    private static bool HasMissingTarget(string projectDir, string sidecar)
    {
        // git lists a tracked sidecar deleted from the working tree; it names nothing to import.
        if (!File.Exists(sidecar))
        {
            return false;
        }

        string? destLine = File.ReadLines(sidecar).FirstOrDefault(line => line.StartsWith("dest_files=", StringComparison.Ordinal));
        if (destLine is null)
        {
            return false;
        }

        return ResourcePath().Matches(destLine).Select(match => Path.Combine(projectDir, match.Groups[1].Value)).Any(target => !File.Exists(target));
    }

    private static string? ReadAssemblyName(string projectFile)
    {
        string section = string.Empty;
        foreach (string raw in File.ReadLines(projectFile))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = line;
            }
            else if (section == "[dotnet]" && line.StartsWith("project/assembly_name=", StringComparison.Ordinal))
            {
                string value = line["project/assembly_name=".Length..].Trim().Trim('"');
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    /// <summary>The top level's and the project folder's files from git, or null outside a repository.</summary>
    private static (List<string> TopFiles, List<string> ProjectOnly)? ListWithGit(string projectDir, ILogger logger)
    {
        // The prefix is the project folder's path from the top level, as git spells it, whatever form projectDir takes.
        string[]? located = GitRunner
            .Run(projectDir, logger, "rev-parse", "--show-toplevel", "--show-prefix")
            ?.Split('\n', StringSplitOptions.TrimEntries);
        if (located is null || located[0].Length == 0)
        {
            return null;
        }

        string topLevel = Path.GetFullPath(located[0]);
        string prefix = located.Length > 1 ? located[1] : string.Empty;
        string? listed = GitRunner.Run(topLevel, logger, "ls-files", "-co", "--exclude-standard", "-z");
        if (listed is null)
        {
            return null;
        }

        string[] relative = listed.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<string> topFiles = [.. relative.Select(path => Path.GetFullPath(Path.Combine(topLevel, path)))];
        List<string> projectOnly =
        [
            .. relative
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(path => Path.Combine(projectDir, path[prefix.Length..])),
        ];
        return (topFiles, projectOnly);
    }

    private static List<string> WalkFolder(string projectDir)
    {
        List<string> files = [];
        Stack<string> pending = new([projectDir]);
        while (pending.TryPop(out string? folder))
        {
            files.AddRange(Directory.EnumerateFiles(folder));
            foreach (string child in Directory.EnumerateDirectories(folder).Where(child => !SkippedFolders.Contains(Path.GetFileName(child))))
            {
                pending.Push(child);
            }
        }

        return files;
    }

    private static bool IsBuildInput(string path) => InputExtensions.Contains(Path.GetExtension(path)) || InputNames.Contains(Path.GetFileName(path));

    private static bool IsCsproj(string path) => HasExtension(path, ".csproj");

    private static bool HasExtension(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("\"res://([^\"]*)\"")]
    private static partial Regex ResourcePath();

    // The header of a text scene or resource and the uid it carries, e.g. [gd_scene format=3 uid="uid://b1234"].
    [GeneratedRegex("^\\[gd_(?:scene|resource)\\b[^\\]]*\\suid=\"([^\"]*)\"")]
    private static partial Regex HeaderUid();

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Cache} does not parse as a uid cache, so the prep imports to rewrite it.")]
    private static partial void UidCacheUnreadable(ILogger logger, Exception exception, string cache);

    [LoggerMessage(Level = LogLevel.Debug, Message = "'{Text}' is not uid:// text Godot decodes, so the uid cache check skips it.")]
    private static partial void UidUndecodable(ILogger logger, string text);

    // class_name at the start of a line, after any annotations (@tool, @icon("res://x.svg")) or an extends clause on the same line.
    [GeneratedRegex(@"^\s*(?:@\w+(?:\([^)]*\))?\s+)*(?:extends\s+\S+\s+)?class_name\s+[A-Za-z_]")]
    private static partial Regex ClassNameLine();
}
