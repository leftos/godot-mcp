using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>
/// validate's .cs targets: each is checked through the prep's C# build, whose saved diagnostics are split by file, and through
/// the versioned scenes and resources that attach it, which Godot checks as ordinary targets.
/// </summary>
internal sealed partial class HeadlessTools
{
    internal const int MaxAttachingScenes = 20;
    private const string NoBuildYet = "no build: options.prepare is never and no earlier build was saved";
    private static readonly string[] AttachingExtensions = [".tscn", ".tres"];

    /// <summary>
    /// The .cs targets among the checked files, once each, with the versioned scenes and resources that attach each one.
    /// </summary>
    /// <exception cref="McpException">The project has no csproj or several, or a target is outside the csproj's Compile items.</exception>
    /// <exception cref="SessionException">The Compile items could not be listed.</exception>
    internal static async Task<IReadOnlyList<CsTarget>> CSharpTargetsAsync(
        string projectDir,
        IReadOnlyList<string> checkedFiles,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        string[] scripts = [.. checkedFiles.Where(IsCSharp).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (scripts.Length == 0)
        {
            return [];
        }

        string csproj = CsprojFor(projectDir, scripts[0]);
        IReadOnlyList<string> compiled = await ProjectPrep.CompileItemsAsync(projectDir, csproj, logger, cancellationToken);
        IReadOnlyList<(string Scene, string[] Attached)> scenes = AttachingFiles(projectDir, logger);
        return [.. scripts.Select(script => ToCsTarget(projectDir, script, new CsBuild(csproj, compiled), scenes))];
    }

    /// <summary>The csproj that compiles a .cs target: the project's only one, or the one project.godot names.</summary>
    /// <exception cref="McpException">The project has none, or several and names none.</exception>
    internal static string CsprojFor(string projectDir, string resPath)
    {
        CsprojLookup lookup = PrepScan.FindCsproj(projectDir);
        return lookup.Kind switch
        {
            CsprojKind.Found => lookup.ProjectFile!,
            CsprojKind.None => throw new McpException($"{resPath} is a C# script, but {projectDir} has no .csproj, so nothing compiles it."),
            _ => throw new McpException(
                $"{resPath} is a C# script, but {projectDir} has several .csproj files; validate builds only a project with one."
            ),
        };
    }

    /// <summary>
    /// The files Godot checks: the targets that are not .cs, then the scenes and resources attaching a .cs target, each once.
    /// </summary>
    internal static IReadOnlyList<string> GodotTargets(IReadOnlyList<string> checkedFiles, IReadOnlyList<CsTarget> csTargets)
    {
        List<string> loaded = [.. checkedFiles.Where(path => !IsCSharp(path))];
        foreach (string scene in csTargets.SelectMany(target => target.Scenes))
        {
            if (!loaded.Contains(scene, StringComparer.OrdinalIgnoreCase))
            {
                loaded.Add(scene);
            }
        }

        return loaded;
    }

    private static bool IsCSharp(string path) => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    /// <exception cref="McpException">The script is not one of the csproj's Compile items.</exception>
    private static CsTarget ToCsTarget(string projectDir, string script, CsBuild build, IReadOnlyList<(string Scene, string[] Attached)> scenes)
    {
        string full = Path.GetFullPath(Path.Combine(projectDir, script["res://".Length..]));
        if (!build.Compiled.Any(item => SamePath(item, full)))
        {
            throw new McpException($"{script} is not compiled by {Path.GetFileName(build.Csproj)}: it is outside its Compile items.");
        }

        string[] attaching =
        [
            .. scenes.Where(scene => scene.Attached.Contains(script, StringComparer.OrdinalIgnoreCase)).Select(scene => scene.Scene),
        ];
        return new CsTarget(script, full, [.. attaching.Take(MaxAttachingScenes)], Math.Max(0, attaching.Length - MaxAttachingScenes));
    }

    /// <summary>
    /// The project folder's git-versioned .tscn and .tres files that exist, in ordinal order, each with the res:// paths its
    /// <c>[ext_resource</c> lines name; none outside git.
    /// </summary>
    private static IReadOnlyList<(string Scene, string[] Attached)> AttachingFiles(string projectDir, ILogger logger)
    {
        string? listed = GitRunner.Run(projectDir, logger, "ls-files", "-z");
        if (listed is null)
        {
            return [];
        }

        return
        [
            .. listed
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(path => AttachingExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Where(path => File.Exists(Path.Combine(projectDir, path)))
                .Order(StringComparer.Ordinal)
                .Select(path => ("res://" + path, ExtResourcePaths(Path.Combine(projectDir, path)))),
        ];
    }

    private static string[] ExtResourcePaths(string file) =>
        [
            .. File.ReadLines(file)
                .Where(line => line.StartsWith("[ext_resource", StringComparison.Ordinal))
                .Select(line => ExtResourcePath().Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value),
        ];

    /// <summary>
    /// csharp's <c>files</c> (one per .cs target: its errors and warnings, which build they came from, and the scenes checked) and
    /// <c>otherErrors</c> (the errors in every other file), from the last saved build; each list at most <see cref="CompilerErrors.Limit"/>.
    /// </summary>
    internal static CsReport ReportCSharp(HeadlessResult run, IReadOnlyList<CsTarget> csTargets)
    {
        IReadOnlyList<BuildDiagnostic> diagnostics = run.LastBuild?.Diagnostics ?? [];
        string from = BuildSource(run);
        JsonArray files = [];
        foreach (CsTarget target in csTargets)
        {
            BuildDiagnostic[] own = [.. diagnostics.Where(diagnostic => IsIn(diagnostic, target.FullPath))];
            JsonObject file = new() { ["path"] = target.ResPath };
            AddCapped(file, "errors", [.. own.Where(IsError)]);
            AddCapped(file, "warnings", [.. own.Where(diagnostic => !IsError(diagnostic))]);
            file["from"] = from;
            file["scenes"] = new JsonArray([.. target.Scenes.Select(scene => (JsonNode)scene)]);
            if (target.ScenesOmitted > 0)
            {
                file["scenesOmitted"] = target.ScenesOmitted;
            }

            files.Add(file);
        }

        BuildDiagnostic[] errors = [.. diagnostics.Where(IsError)];
        BuildDiagnostic[] other = [.. errors.Where(error => !csTargets.Any(target => IsIn(error, target.FullPath)))];
        JsonObject report = new() { ["files"] = files };
        AddCapped(report, "otherErrors", other);
        return new CsReport(report, errors.Length > 0);
    }

    /// <summary>Which build the diagnostics came from: this call's, the last saved one's, or none.</summary>
    private static string BuildSource(HeadlessResult run)
    {
        if (run.Prep.Build is "built" or "failed")
        {
            return "this build";
        }

        return run.LastBuild is { } last
            ? "last build at " + last.BuiltAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : NoBuildYet;
    }

    private static bool IsError(BuildDiagnostic diagnostic) => diagnostic.Severity == BuildDiagnostic.Error;

    /// <summary>Whether the diagnostic names the file at full, compared case-insensitively.</summary>
    private static bool IsIn(BuildDiagnostic diagnostic, string full) =>
        diagnostic.File is { } file && Path.IsPathRooted(file) && SamePath(file, full);

    private static bool SamePath(string path, string full) => string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase);

    /// <summary>Sets key to the first <see cref="CompilerErrors.Limit"/> diagnostics, and <c>keyOmitted</c> to how many more there were.</summary>
    private static void AddCapped(JsonObject target, string key, BuildDiagnostic[] diagnostics)
    {
        target[key] = new JsonArray([
            .. diagnostics.Take(CompilerErrors.Limit).Select(diagnostic => JsonSerializer.SerializeToNode(diagnostic, Json)),
        ]);
        if (diagnostics.Length > CompilerErrors.Limit)
        {
            target[key + "Omitted"] = diagnostics.Length - CompilerErrors.Limit;
        }
    }

    [GeneratedRegex(@"\spath=""([^""]*)""")]
    private static partial Regex ExtResourcePath();

    /// <summary>The csproj that compiles the .cs targets, and the full paths of its Compile items.</summary>
    private sealed record CsBuild(string Csproj, IReadOnlyList<string> Compiled);
}

/// <summary>
/// A .cs target: its res:// and full paths, and the versioned scenes and resources that attach it (at most
/// <see cref="HeadlessTools.MaxAttachingScenes"/>, with how many more there were).
/// </summary>
internal sealed record CsTarget(string ResPath, string FullPath, IReadOnlyList<string> Scenes, int ScenesOmitted);

/// <summary>csharp's <c>files</c> and <c>otherErrors</c>, and whether the build had any error, which makes validate's result invalid.</summary>
internal sealed record CsReport(JsonObject Shaped, bool HasErrors);
