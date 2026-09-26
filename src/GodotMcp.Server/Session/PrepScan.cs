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
/// outside git), and the project folder's <c>.import</c> sidecars, whether it holds <c>.uid</c> files, and its GDScript files.
/// </summary>
internal sealed record ProjectFiles(
    IReadOnlyList<string> BuildInputs,
    IReadOnlyList<string> ImportFiles,
    bool HasUidFiles,
    IReadOnlyList<string> Scripts
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

    /// <summary>Where Godot loads a Debug run's assembly from (GodotSharpDirs' <c>.godot/mono/temp/bin/Debug</c>).</summary>
    public static string AssemblyPath(string projectDir, string assemblyName) =>
        Path.Combine(projectDir, ".godot", "mono", "temp", "bin", "Debug", assemblyName + ".dll");

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
            [.. projectFiles.Where(file => HasExtension(file, ".import"))],
            projectFiles.Any(file => HasExtension(file, ".uid")),
            [.. projectFiles.Where(file => HasExtension(file, ".gd"))]
        );
    }

    /// <summary>Where the editor's scan writes the <c>class_name</c> globals a run reads (<c>project_settings.cpp</c> L1469).</summary>
    public static string ClassCachePath(string projectDir) => Path.Combine(projectDir, ".godot", "global_script_class_cache.cfg");

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
    /// Whether a Godot import is needed: a <c>.import</c> sidecar whose <c>dest_files</c> are not all present, or
    /// <c>.uid</c> files with no <c>.godot/uid_cache.bin</c>, or a script declaring <c>class_name</c> that the class cache
    /// may not hold yet. A missing <c>.godot/</c> alone is not a reason.
    /// </summary>
    public static bool ImportNeeded(string projectDir, ProjectFiles files)
    {
        if (files.HasUidFiles && !File.Exists(Path.Combine(projectDir, ".godot", "uid_cache.bin")))
        {
            return true;
        }

        return files.ImportFiles.Any(sidecar => HasMissingTarget(projectDir, sidecar)) || IsClassCacheStale(projectDir, files.Scripts);
    }

    /// <summary>
    /// Whether a file a request loads must be imported first: it is not one that loads without an import (<c>.tres</c>,
    /// <c>.res</c>, <c>.dds</c>, <c>.ktx</c>), and it has no <c>.import</c> sidecar or one whose <c>dest_files</c> are not
    /// all present (an ignored sidecar is not in <see cref="ProjectFiles.ImportFiles"/>). A never-imported image fails to
    /// load with "No loader found" (4.7.2 <c>core/io/resource_loader.cpp</c> L332).
    /// </summary>
    public static bool AssetNeedsImport(string projectDir, string assetPath)
    {
        string sidecar = assetPath + ".import";
        return !LoadsWithoutImportExtensions.Contains(Path.GetExtension(assetPath))
            && (!File.Exists(sidecar) || HasMissingTarget(projectDir, sidecar));
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

    // class_name at the start of a line, after any annotations (@tool, @icon("res://x.svg")) or an extends clause on the same line.
    [GeneratedRegex(@"^\s*(?:@\w+(?:\([^)]*\))?\s+)*(?:extends\s+\S+\s+)?class_name\s+[A-Za-z_]")]
    private static partial Regex ClassNameLine();
}
