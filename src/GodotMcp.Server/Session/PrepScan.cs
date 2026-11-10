using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// outside git), and the project folder's <c>.import</c> sidecars, files an importer can import, <c>.uid</c> files, GDScript
/// files and text scenes and resources (<c>.tscn</c>, <c>.tres</c>).
/// </summary>
internal sealed record ProjectFiles(
    IReadOnlyList<string> BuildInputs,
    IReadOnlyList<string> ImportFiles,
    IReadOnlyList<string> ImportSources,
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

    // The extensions Godot 4.7.2's built-in importers recognise, which its editor scan therefore imports: ImageLoader's image
    // formats (the texture importers import whatever ImageLoader reads), the scene and model importers' formats, and the
    // audio, font, csv and shader importers' (editor/import/*, modules/*/resource_importer_*). An addon's EditorImportPlugin
    // extensions and scene importers are not listed. .dds and .ktx are left out: 4.7.2 has no image loader for them, so no
    // texture importer takes them, and modules/dds and modules/ktx register resource loaders that read them without an import.
    // .blend is importable only while project.godot's [filesystem] import/blender/enabled is not false (BlenderEnabled).
    private static readonly HashSet<string> ImportableExtensions = new(
        [
            ".png",
            ".jpg",
            ".jpeg",
            ".webp",
            ".bmp",
            ".tga",
            ".exr",
            ".hdr",
            ".svg",
            ".gltf",
            ".glb",
            ".fbx",
            ".blend",
            ".dae",
            ".escn",
            ".obj",
            ".wav",
            ".mp3",
            ".ogg",
            ".ttf",
            ".ttc",
            ".otf",
            ".otc",
            ".woff",
            ".woff2",
            ".pfb",
            ".pfm",
            ".font",
            ".fnt",
            ".csv",
            ".glsl",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    private const string UidPrefix = "uid://";

    // How two paths on this system's file system compare: without case on Windows.
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>What every import sidecar of a source is named: the source's own path plus this.</summary>
    internal const string ImportSuffix = ".import";

    /// <summary>The extension of a Blender file, which Godot imports only while Blender import is on (<see cref="BlenderEnabled"/>).</summary>
    internal const string BlendExtension = ".blend";

    // The project.godot values Godot reads as false for a bool setting: the Variant they parse to converts to false
    // (4.7.2 core/variant/variant.cpp L879-895: nil, false, 0, 0.0 and the empty string).
    private static readonly HashSet<string> FalseSettingValues = new(["false", "0", "0.0", "null", "nil", "\"\""], StringComparer.Ordinal);

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
        if (ReadSetting(Path.Combine(projectDir, "project.godot"), "[dotnet]", "project/assembly_name") is { } named)
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

    /// <summary>The file the prep records each green build in (<see cref="WriteStamp"/>).</summary>
    public static string StampPath(string projectDir) => Path.Combine(ProjectPrep.LogFolder(projectDir), "build.stamp");

    /// <summary>
    /// The project's files: in git, the versioned and untracked-but-not-ignored files of the whole top level (a csproj
    /// builds sibling projects), and a project folder git lists nothing under walked like one outside git; outside git, the
    /// project folder walked without its VCS, cache and build folders.
    /// </summary>
    public static ProjectFiles Scan(string projectDir, ILogger logger)
    {
        (List<string> TopFiles, List<string> ProjectOnly)? listed = ListWithGit(projectDir, logger);
        List<string> topFiles = listed?.TopFiles ?? WalkFolder(projectDir);
        List<string> projectFiles = listed?.ProjectOnly ?? topFiles;
        bool blenderEnabled = BlenderEnabled(projectDir);
        return new ProjectFiles(
            [.. topFiles.Where(IsBuildInput)],
            [.. projectFiles.Where(file => HasExtension(file, ImportSuffix) && BlenderAllows(file[..^ImportSuffix.Length], blenderEnabled))],
            [.. projectFiles.Where(file => ImportableExtensions.Contains(Path.GetExtension(file)) && BlenderAllows(file, blenderEnabled))],
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
    /// Every <c>.uid</c> and <c>.import</c> file of the project as it is now, by full path with its size and last-write time
    /// (UTC): a folder walk, so a git-ignored sidecar counts too, through the folders Godot's scan visits (the rules of
    /// <see cref="UnscannedFolder"/>, applied as it walks, and <see cref="SkippedFolders"/>).
    /// </summary>
    internal static IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> Sidecars(string projectDir)
    {
        Dictionary<string, (long Length, DateTime LastWriteUtc)> sidecars = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> pending = new([projectDir]);
        while (pending.TryPop(out string? folder))
        {
            foreach (string file in Directory.EnumerateFiles(folder).Where(IsSidecar))
            {
                FileInfo info = new(file);
                sidecars[info.FullName] = (info.Length, info.LastWriteTimeUtc);
            }

            foreach (string child in Directory.EnumerateDirectories(folder).Where(IsVisitedFolder))
            {
                pending.Push(child);
            }
        }

        return sidecars;
    }

    /// <summary>
    /// What an import changed between two <see cref="Sidecars"/> snapshots of the project: the <c>.uid</c> and <c>.import</c>
    /// files it created or changed and the ones it deleted, each as sorted <c>res://</c> paths, null when it did neither.
    /// </summary>
    internal static (IReadOnlyList<string>? Written, IReadOnlyList<string>? Removed) SidecarChanges(
        string projectDir,
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before,
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> after
    )
    {
        List<string> written = [];
        foreach ((string file, (long Length, DateTime LastWriteUtc) state) in after)
        {
            if (!before.TryGetValue(file, out (long Length, DateTime LastWriteUtc) old) || old != state)
            {
                written.Add(ResourcePathOf(projectDir, file));
            }
        }

        List<string> removed = [.. before.Keys.Where(file => !after.ContainsKey(file)).Select(file => ResourcePathOf(projectDir, file))];
        written.Sort(StringComparer.Ordinal);
        removed.Sort(StringComparer.Ordinal);
        return (written.Count == 0 ? null : written, removed.Count == 0 ? null : removed);
    }

    /// <summary>A file's <c>res://</c> path in the project, with forward slashes.</summary>
    internal static string ResourcePathOf(string projectDir, string file) => "res://" + Path.GetRelativePath(projectDir, file).Replace('\\', '/');

    private static bool IsSidecar(string file) => HasExtension(file, ".uid") || HasExtension(file, ImportSuffix);

    private static bool IsVisitedFolder(string folder) =>
        !SkippedFolders.Contains(Path.GetFileName(folder))
        && !Path.GetFileName(folder).StartsWith('.')
        && !IsHidden(folder)
        && !File.Exists(Path.Combine(folder, ".gdignore"))
        && !File.Exists(Path.Combine(folder, "project.godot"));

    /// <summary>
    /// Whether the assembly is missing, or differs from what the last green prep build recorded (<see cref="WriteStamp"/>): no
    /// record, one that is not the record (an empty stamp from an older version included), an assembly whose size or
    /// last-write time is not the recorded one, or build inputs whose hash is not. The assembly's time alone proves nothing,
    /// since any writer can set it; and an input edit that leaves the assembly alone, such as a .props edit, is recorded by
    /// the build after it, so it does not rebuild on every run.
    /// </summary>
    public static bool IsStale(string assemblyPath, string stampPath, string projectDir, IEnumerable<string> inputs)
    {
        FileInfo assembly = new(assemblyPath);
        if (!assembly.Exists || !File.Exists(stampPath))
        {
            return true;
        }

        try
        {
            using var record = JsonDocument.Parse(File.ReadAllBytes(stampPath));
            return !RecordsAssembly(record.RootElement, assembly) || StringProperty(record.RootElement, "inputs") != InputsHash(projectDir, inputs);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            // A stamp that cannot be read or parsed says nothing about which build left the assembly, so only a build can.
            return true;
        }
    }

    /// <summary>
    /// Whether the prep's build of <paramref name="assemblyName"/> can be skipped: the assembly is not stale against the prep's
    /// own record (<see cref="IsStale"/>), or another builder left it and MSBuild's own intermediate corroborates it
    /// (<see cref="CorroboratedByMsBuild"/>), which is then recorded as a green prep build would be (<see cref="WriteStamp"/>).
    /// </summary>
    internal static bool BuildIsCurrent(string projectDir, string assemblyName, IReadOnlyList<string> inputs, bool buildWhenUnsaved, ILogger logger)
    {
        string assembly = AssemblyPath(projectDir, assemblyName);
        string stamp = StampPath(projectDir);
        if (!IsStale(assembly, stamp, projectDir, inputs))
        {
            return true;
        }

        if (!MayAdopt(projectDir, assembly, buildWhenUnsaved) || !CorroboratedByMsBuild(projectDir, assemblyName, inputs, logger))
        {
            return false;
        }

        WriteStamp(assembly, stamp, projectDir, inputs);
        return true;
    }

    /// <summary>
    /// Whether an assembly the prep did not record may be adopted at all: there is one, the run does not report the build
    /// (<see cref="PrepContext.BuildWhenUnsaved"/>, validate's), and no saved build (<see cref="SavedBuild"/>) is older than it,
    /// since only a build refreshes that record and validate would read the older build's diagnostics as the last build.
    /// </summary>
    private static bool MayAdopt(string projectDir, string assembly, bool buildWhenUnsaved)
    {
        string saved = SavedBuild.PathIn(projectDir);
        return !buildWhenUnsaved
            && File.Exists(assembly)
            && (!File.Exists(saved) || File.GetLastWriteTimeUtc(saved) >= File.GetLastWriteTimeUtc(assembly));
    }

    /// <summary>
    /// Whether MSBuild's own intermediate shows the assembly current, whoever built it: the bin assembly is byte-identical to
    /// the intermediate one in <c>.godot/mono/temp/obj/Debug</c>, every listed build input is on disk, the intermediate is at
    /// least as new as the newest of them, and the portable pdb beside it compiled exactly the project's <c>.cs</c> inputs, at
    /// least one (its documents and the inputs each taken under the project folder and outside its <c>.godot</c> folder). The
    /// pdb catches a project source deleted or put back with its old time, which the times alone accept. False, logged at
    /// Debug with the reason, on any other outcome, a read failure included.
    /// </summary>
    private static bool CorroboratedByMsBuild(string projectDir, string assemblyName, IReadOnlyList<string> inputs, ILogger logger)
    {
        string assembly = AssemblyPath(projectDir, assemblyName);
        // AssemblyPath's .godot/mono/temp/bin/Debug/<name>.dll has MSBuild's intermediate at .godot/mono/temp/obj/Debug/<name>.dll.
        string temp = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(assembly)))!;
        string intermediate = Path.Combine(temp, "obj", ProjectPrep.Configuration, assemblyName + ".dll");
        string reading = assembly;
        string? reason;
        try
        {
            reason = UncorroboratedReason(assembly, intermediate, projectDir, inputs, file => reading = file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
        {
            reason = $"{reading} could not be read ({e.GetType().Name}: {e.Message})";
        }

        if (reason is not null)
        {
            NotCorroborated(logger, reason);
        }

        return reason is null;
    }

    /// <summary>Why MSBuild's intermediate does not corroborate the assembly (<see cref="CorroboratedByMsBuild"/>), or null when it does.</summary>
    /// <param name="reading">Told each file before it is read, so a read failure names it.</param>
    private static string? UncorroboratedReason(
        string assembly,
        string intermediate,
        string projectDir,
        IReadOnlyList<string> inputs,
        Action<string> reading
    )
    {
        if (!SameBytes(assembly, intermediate, reading))
        {
            return $"{assembly} is not byte-identical to MSBuild's intermediate {intermediate}, or one is missing";
        }

        if (inputs.FirstOrDefault(input => !File.Exists(input)) is { } gone)
        {
            return $"the build input {gone} is listed but not on disk";
        }

        DateTime newest = inputs.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        if (File.GetLastWriteTimeUtc(intermediate) < newest)
        {
            return $"MSBuild's intermediate {intermediate} is older than the newest build input";
        }

        string pdb = Path.ChangeExtension(intermediate, ".pdb");
        reading(pdb);
        return SameSources(projectDir, inputs, PdbDocuments(pdb))
            ? null
            : $"the sources {pdb} names under {projectDir} are not the project's .cs build inputs there, or there are none";
    }

    /// <summary>
    /// Whether a pdb's documents and the build inputs name the same <c>.cs</c> files under the project folder and outside its
    /// <c>.godot</c> folder (<see cref="ProjectSources"/>), and at least one: a folder spelled otherwise than the paths (a subst
    /// drive, a junction, an 8.3 name) leaves both sides empty, which proves nothing.
    /// </summary>
    private static bool SameSources(string projectDir, IReadOnlyList<string> inputs, IReadOnlyList<string> documents)
    {
        HashSet<string> sources = new(
            ProjectSources(projectDir, inputs.Where(input => HasExtension(input, ".cs"))),
            StringComparer.FromComparison(PathComparison)
        );
        return sources.Count > 0 && sources.SetEquals(ProjectSources(projectDir, documents));
    }

    /// <summary>Whether both files exist and hold the same bytes, compared in chunks up to the first difference.</summary>
    private static bool SameBytes(string first, string second, Action<string> reading)
    {
        FileInfo a = new(first);
        FileInfo b = new(second);
        if (!a.Exists || !b.Exists || a.Length != b.Length)
        {
            return false;
        }

        reading(first);
        using FileStream left = a.OpenRead();
        reading(second);
        using FileStream right = b.OpenRead();
        byte[] leftChunk = new byte[81920];
        byte[] rightChunk = new byte[leftChunk.Length];
        int read;
        while ((read = left.ReadAtLeast(leftChunk, leftChunk.Length, throwOnEndOfStream: false)) > 0)
        {
            if (
                right.ReadAtLeast(rightChunk.AsSpan(0, read), read, throwOnEndOfStream: false) != read
                || !leftChunk.AsSpan(0, read).SequenceEqual(rightChunk.AsSpan(0, read))
            )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The full paths of <paramref name="files"/> that are rooted, under the project folder and outside its <c>.godot</c> folder.</summary>
    private static IEnumerable<string> ProjectSources(string projectDir, IEnumerable<string> files)
    {
        StringComparison comparison = PathComparison;
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDir)) + Path.DirectorySeparatorChar;
        string godot = Path.Combine(folder, ".godot") + Path.DirectorySeparatorChar;
        return files
            .Where(Path.IsPathFullyQualified)
            .Select(Path.GetFullPath)
            .Where(file => file.StartsWith(folder, comparison) && !file.StartsWith(godot, comparison));
    }

    /// <summary>The document names a portable pdb lists; a pdb that is not portable throws <see cref="BadImageFormatException"/>.</summary>
    private static List<string> PdbDocuments(string pdb)
    {
        using FileStream stream = File.OpenRead(pdb);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        MetadataReader reader = provider.GetMetadataReader();
        return [.. reader.Documents.Select(handle => reader.GetString(reader.GetDocument(handle).Name))];
    }

    /// <summary>Whether a record's <c>assembly</c> object holds the assembly's own size and last-write time.</summary>
    private static bool RecordsAssembly(JsonElement record, FileInfo assembly)
    {
        if (
            record.ValueKind != JsonValueKind.Object
            || !record.TryGetProperty("assembly", out JsonElement built)
            || built.ValueKind != JsonValueKind.Object
        )
        {
            return false;
        }

        long length = -1;
        bool sized = built.TryGetProperty("size", out JsonElement size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out length);
        return sized && length == assembly.Length && StringProperty(built, "writtenUtc") == WrittenUtc(assembly);
    }

    /// <summary>A JSON object's string property, or null when it is missing or not a string.</summary>
    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Records what a green prep build left: the assembly's size and last-write time and the hash of the build inputs
    /// (<see cref="InputsHash"/>), as <c>{"assembly": {"size": …, "writtenUtc": "…"}, "inputs": "…"}</c>. With no assembly to
    /// record, any earlier record is deleted, since it no longer describes the assembly.
    /// </summary>
    public static void WriteStamp(string assemblyPath, string stampPath, string projectDir, IEnumerable<string> inputs)
    {
        FileInfo assembly = new(assemblyPath);
        if (!assembly.Exists)
        {
            File.Delete(stampPath);
            return;
        }

        JsonObject record = new()
        {
            ["assembly"] = new JsonObject { ["size"] = assembly.Length, ["writtenUtc"] = WrittenUtc(assembly) },
            ["inputs"] = InputsHash(projectDir, inputs),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(stampPath)!);
        File.WriteAllText(stampPath, record.ToJsonString());
    }

    private static string WrittenUtc(FileInfo file) => file.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// The lowercase hex SHA-256 of the build inputs, sorted by ordinal path, each as <c>path\tsize\tticks\n</c>: its path
    /// relative to the project folder with '/' separators, its size and its last-write ticks (UTC); a listed input that is
    /// gone contributes a size of -1 and 0 ticks.
    /// </summary>
    private static string InputsHash(string projectDir, IEnumerable<string> inputs)
    {
        string root = Path.GetFullPath(projectDir);
        IEnumerable<(string Relative, string Full)> sorted = inputs
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .Select(full => (Path.GetRelativePath(root, full).Replace('\\', '/'), full))
            .OrderBy(input => input.Item1, StringComparer.Ordinal);
        StringBuilder lines = new();
        foreach ((string relative, string full) in sorted)
        {
            FileInfo info = new(full);
            (long size, long ticks) = info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1L, 0L);
            lines.Append(CultureInfo.InvariantCulture, $"{relative}\t{size}\t{ticks}\n");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString())));
    }

    /// <summary>
    /// Whether a Godot import is needed: a <c>.import</c> sidecar whose <c>dest_files</c> are not all present, whose source is
    /// not the one it imported or whose settings changed since the prep last saw them (<see cref="ImportOutdated"/>), or an
    /// importable source the scan reaches that has no sidecar (<see cref="SourcesWithoutSidecars"/>) and no earlier import
    /// recorded for it, or a uid <c>.godot/uid_cache.bin</c> does not hold (<see cref="IsUidCacheStale"/>), or a script
    /// declaring <c>class_name</c> that the class cache may not hold yet, or a <c>[GlobalClass]</c> loader or saver it lacks
    /// (<see cref="IsFormatClassStale"/>). A missing <c>.godot/</c> alone is not a reason.
    /// </summary>
    /// <param name="missingSidecars">
    /// The prep's <see cref="MissingSidecarSources"/>, computed once for the whole prep, so no recorded source is hashed twice.
    /// </param>
    /// <param name="fingerprints">The md5 the prep last saw for each sidecar (<see cref="ImportFingerprints"/>).</param>
    public static bool ImportNeeded(
        string projectDir,
        ProjectFiles files,
        IReadOnlyList<string> missingSidecars,
        IReadOnlyDictionary<string, string> fingerprints,
        ILogger logger
    ) =>
        IsUidCacheStale(projectDir, files, logger)
        || files.ImportFiles.Any(sidecar => HasMissingTarget(projectDir, sidecar) || ImportOutdated(projectDir, sidecar, fingerprints))
        || missingSidecars.Count > 0
        || IsClassCacheStale(projectDir, files.Scripts)
        || IsFormatClassStale(projectDir, files);

    /// <summary>
    /// The scanned importable sources that have no <c>.import</c> sidecar on disk: a source git lists but the working tree
    /// lacks is skipped, and so is one Godot's scan never reaches (<see cref="IsScanned"/>). The sidecar is looked for on disk,
    /// not in <see cref="ProjectFiles.ImportFiles"/>, which omits a gitignored one. Such a source makes an import due
    /// (<see cref="ImportNeeded"/>), and an import writes its sidecar. A <c>.blend</c> it writes none for, with Blender's path
    /// not set, is what the record keeps a source's md5 for (<see cref="ImportFingerprints"/>); any other source still without
    /// one stays due.
    /// </summary>
    public static IReadOnlyList<string> SourcesWithoutSidecars(string projectDir, ProjectFiles files) =>
        [.. files.ImportSources.Where(source => File.Exists(source) && !File.Exists(source + ImportSuffix) && IsScanned(projectDir, source))];

    /// <summary>
    /// The sources that make an import due for having no sidecar, in scan order, the first the one the prep's note names:
    /// <paramref name="withoutSidecars"/> (<see cref="SourcesWithoutSidecars"/>) less the ones an import already found so, with
    /// the bytes they have now. Each recorded source is hashed once here, and <see cref="ImportNeeded"/> takes the result.
    /// </summary>
    public static IReadOnlyList<string> MissingSidecarSources(
        string projectDir,
        IReadOnlyList<string> withoutSidecars,
        IReadOnlyDictionary<string, string> fingerprints
    ) => [.. withoutSidecars.Where(source => !ImportFingerprints.RecordedWithoutSidecar(projectDir, source, fingerprints))];

    /// <summary>
    /// Whether project.godot leaves Godot's Blender import on, absent meaning true (4.7.2 <c>modules/gltf/register_types.cpp</c>
    /// L154); with it off, no importer takes a <c>.blend</c> (L91-100). It is off only when <c>[filesystem]</c>'s
    /// <c>import/blender/enabled</c> holds a value Godot reads as false (<see cref="FalseSettingValues"/>): its own key, not a
    /// feature override such as <c>.windows</c>, spaces around <c>=</c> and a trailing <c>; comment</c> ignored, a quoted
    /// value kept quoted, since a non-empty string is true.
    /// </summary>
    internal static bool BlenderEnabled(string projectDir)
    {
        string projectFile = Path.Combine(projectDir, "project.godot");
        if (!File.Exists(projectFile) || RawSetting(projectFile, "[filesystem]", "import/blender/enabled") is not { } value)
        {
            return true;
        }

        int comment = value.IndexOf(';', StringComparison.Ordinal);
        return !FalseSettingValues.Contains(comment < 0 ? value : value[..comment].TrimEnd());
    }

    private static bool IsBlend(string file) => HasExtension(file, BlendExtension);

    /// <summary>
    /// Whether Godot imports a source of this kind, as far as Blender import decides it: a <c>.blend</c> only while it is on
    /// (<see cref="BlenderEnabled"/>), so a <c>.blend</c>'s sidecar is left out too while it is off.
    /// </summary>
    private static bool BlenderAllows(string source, bool blenderEnabled) => blenderEnabled || !IsBlend(source);

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
    /// scan skips for its name starting with ".", for its hidden attribute (4.7.2 <c>editor/file_system/editor_file_system.cpp</c>
    /// L1174-1176), for holding a <c>.gdignore</c> (L1179-1185 and L3502-3505) or for holding a <c>project.godot</c> of its
    /// own, a nested project (L3494-3500), or null.
    /// </summary>
    internal static string? UnscannedFolder(string projectDir, string file) =>
        FoldersBetween(projectDir, file)
            .FirstOrDefault(folder =>
                Path.GetFileName(folder).StartsWith('.')
                || IsHidden(folder)
                || File.Exists(Path.Combine(folder, ".gdignore"))
                || File.Exists(Path.Combine(folder, "project.godot"))
            );

    /// <summary>
    /// Whether Godot's scan, and so an import, reaches <paramref name="file"/>: the file is not hidden and no folder on its way
    /// skips it (<see cref="UnscannedFolder"/>; 4.7.2 <c>editor/file_system/editor_file_system.cpp</c> L1174-1176).
    /// </summary>
    private static bool IsScanned(string projectDir, string file) => !IsHidden(file) && UnscannedFolder(projectDir, file) is null;

    /// <summary>Whether a file or folder has the hidden attribute; false when it cannot be read, a missing one included.</summary>
    internal static bool IsHidden(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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
    /// Whether a file a request loads must be imported first: it is one an importer recognises (<see cref="ImportableExtensions"/>),
    /// and it has no <c>.import</c> sidecar, or one whose <c>dest_files</c> are not all present or whose source is not the one it
    /// imported (<see cref="ImportOutdated"/>) (an ignored sidecar is not in <see cref="ProjectFiles.ImportFiles"/>). A
    /// never-imported image fails to load with "No loader found" (4.7.2 <c>core/io/resource_loader.cpp</c> L332).
    /// </summary>
    public static bool AssetNeedsImport(string projectDir, string assetPath, IReadOnlyDictionary<string, string> fingerprints)
    {
        string sidecar = assetPath + ImportSuffix;
        return ImportableExtensions.Contains(Path.GetExtension(assetPath))
            && (!IsBlend(assetPath) || BlenderEnabled(projectDir))
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
    /// (L673, L643), is never due, and a sidecar missing from the working tree is not due here: its source, when there, has no
    /// sidecar, so <see cref="SourcesWithoutSidecars"/> makes it due.
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
    /// the sidecar's. A sidecar with no record is not due on this ground, since there is nothing to compare yet; a recorded one
    /// that cannot be read is taken as changed, so the import becomes due.
    /// </summary>
    internal static bool SettingsChanged(string projectDir, string sidecar, IReadOnlyDictionary<string, string> fingerprints) =>
        fingerprints.TryGetValue(ImportFingerprints.KeyFor(projectDir, sidecar), out string? seen) && seen != ImportFingerprints.TryMd5Of(sidecar);

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

    /// <summary>
    /// Whether a <c>[GlobalClass]</c> loader or saver the project's built assembly declares (<see cref="FormatClassesIn"/>) is
    /// missing from the class cache, which is where a run learns of a C# resource loader. An import before the first build
    /// writes an empty list and a build does not change it, so the import is due again once an assembly exists. Never with no
    /// assembly to register a class from, and never for a cache written since the assembly: an import that left a class out
    /// would leave it out again. Other global classes never make an import due: only a loader or saver changes what a headless
    /// run can read, and an import rewrites the cache only when a global class exists (4.7.2
    /// <c>editor/file_system/editor_file_system.cpp</c> L342, L2161-2166).
    /// </summary>
    public static bool IsFormatClassStale(string projectDir, ProjectFiles files)
    {
        CsprojLookup csproj = FindCsproj(projectDir);
        if (csproj.Kind != CsprojKind.Found)
        {
            return false;
        }

        string assembly = AssemblyPath(projectDir, csproj.AssemblyName!);
        if (!File.Exists(assembly))
        {
            return false;
        }

        string cache = ClassCachePath(projectDir);
        bool cached = File.Exists(cache);
        if (cached && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(assembly))
        {
            return false;
        }

        string listed = cached ? File.ReadAllText(cache) : string.Empty;
        return DeclaredFormatClasses(projectDir, files).Any(name => !Names(listed, name));
    }

    /// <summary>
    /// The <c>[GlobalClass]</c> loaders and savers the project's C# sources declare that the class cache names now; empty with
    /// no cache.
    /// An import whose scan started without a class registers it in the cache but cannot use it: compared before and after
    /// an import, a name added means the import must run once more for the scan to see the files that class loads.
    /// </summary>
    public static IReadOnlySet<string> CachedFormatClasses(string projectDir, ProjectFiles files)
    {
        string cache = ClassCachePath(projectDir);
        string listed = File.Exists(cache) ? File.ReadAllText(cache) : string.Empty;
        return DeclaredFormatClasses(projectDir, files).Where(name => Names(listed, name)).ToHashSet(StringComparer.Ordinal);
    }

    private static bool Names(string classCache, string name) => classCache.Contains($"\"{name}\"", StringComparison.Ordinal);

    /// <summary>The <c>[GlobalClass]</c> loaders and savers the C# build inputs under the project folder declare.</summary>
    private static IEnumerable<string> DeclaredFormatClasses(string projectDir, ProjectFiles files)
    {
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDir)) + Path.DirectorySeparatorChar;
        return files
            .BuildInputs.Where(file => HasExtension(file, ".cs") && file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
            .SelectMany(file => FormatClassesIn(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file)));
    }

    /// <summary>
    /// The name of each class <paramref name="source"/>, a file named <paramref name="stem"/>, declares under a
    /// <c>[GlobalClass]</c> attribute with <c>ResourceFormatLoader</c> or <c>ResourceFormatSaver</c> as a direct base. Only a
    /// class named after its file counts: Godot gives no other a script path (GodotSharp <c>ScriptPathAttributeGenerator.cs</c>
    /// L54-56). A class deriving through a base of its own is not seen.
    /// </summary>
    internal static IReadOnlyList<string> FormatClassesIn(string source, string stem) =>
        [.. FormatClassDeclaration().Matches(source).Select(match => match.Groups[1].Value).Where(name => name == stem)];

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

    /// <summary>
    /// A project.godot setting's value, unquoted, from the <paramref name="section"/> header (with its brackets) and the
    /// <paramref name="key"/> under it; null when it is absent or empty.
    /// </summary>
    private static string? ReadSetting(string projectFile, string section, string key) =>
        RawSetting(projectFile, section, key)?.Trim('"') is { Length: > 0 } value ? value : null;

    /// <summary>
    /// A project.godot setting's text after its <c>=</c>, trimmed and otherwise as written, from the <paramref name="section"/>
    /// header (with its brackets) and the <paramref name="key"/> under it, spaces around the <c>=</c> allowed; null when absent.
    /// </summary>
    private static string? RawSetting(string projectFile, string section, string key)
    {
        string current = string.Empty;
        foreach (string raw in File.ReadLines(projectFile))
        {
            string line = raw.Trim();
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (line.StartsWith('['))
            {
                current = line;
            }
            else if (current == section && equals > 0 && line[..equals].TrimEnd() == key)
            {
                return line[(equals + 1)..].Trim();
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
        if (projectOnly.Count == 0)
        {
            // Git lists nothing under the project folder: it is ignored, so it is walked like a project outside git.
            List<string> walked = WalkFolder(projectDir);
            topFiles.AddRange(walked);
            projectOnly = walked;
        }

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "The prep builds the C# assembly another builder left: {Reason}.")]
    private static partial void NotCorroborated(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "'{Text}' is not uid:// text Godot decodes, so the uid cache check skips it.")]
    private static partial void UidUndecodable(ILogger logger, string text);

    // class_name at the start of a line, after any annotations (@tool, @icon("res://x.svg")) or an extends clause on the same line.
    [GeneratedRegex(@"^\s*(?:@\w+(?:\([^)]*\))?\s+)*(?:extends\s+\S+\s+)?class_name\s+[A-Za-z_]")]
    private static partial Regex ClassNameLine();

    // A line opening an attribute list naming GlobalClass, any attributes after it, and the class they sit on when its base
    // list names ResourceFormatLoader or ResourceFormatSaver.
    [GeneratedRegex(
        @"^[ \t]*\[[^\]\r\n]*\bGlobalClass\b[^\]\r\n]*\](?:\s*\[[^\]]*\])*\s*"
            + @"(?:(?:public|internal|sealed|partial|abstract)\s+)*class\s+(\w+)\s*:[^{]*\bResourceFormat(?:Loader|Saver)\b",
        RegexOptions.Multiline
    )]
    private static partial Regex FormatClassDeclaration();
}
