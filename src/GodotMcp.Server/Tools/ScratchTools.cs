using System.ComponentModel;
using System.Text.Json;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// run_scratches: plays a project's scratch scenes, each in a headless game session of its own, step by step through the
/// scratch protocol, and reports a verdict a scene. The tool is a type of its own, apart from <see cref="RuntimeTools"/>,
/// because batch_drive plays every RuntimeTools tool on a session, and a scratch run starts its own.
/// </summary>
[McpServerToolType]
internal sealed class ScratchTools(SessionRegistry sessions)
{
    internal const string ToolName = "run_scratches";

    /// <summary>A step's pace when neither options.pace nor the profile's scratch.pace sets one, in seconds.</summary>
    internal const double DefaultPace = 0.5;

    private const string SceneEnding = ".tscn";
    private const string ScratchAuto = "--scratch-auto";

    [McpServerTool(Name = ToolName, ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = false)]
    [Description(
        "Plays scratch scenes and reports a verdict for each: every scene runs in a fresh headless game session "
            + "(<folder>.scratch-<scene>), its root is called through the scratch protocol (GetStepCount, GetStepName(i), then per step "
            + "PlayStep(i), a wait of the pace in game time, GetStatus), and the run stops at a scene's first failed step. A step fails "
            + "on an error the game logs (push_error, an engine error, a C# exception) or a stdout or stderr line matching the "
            + "profile's scratch.patterns; after the steps the game is stopped and a non-zero exit or an ObjectDB leak turns the "
            + "scene red. Settings live in godot-mcp.json's scratch section {folder, userArgs, pace, known, patterns, parallel}. Scenes "
            + "start in order, parallel at a time; with more than one, a red or killed scene (not known, not refused before its first "
            + "step) is played once more alone after the others: green alone, its entry is the replay's with alone: true and counts "
            + "green; else the first entry with alone: false and the replay's failure as aloneFailedAt. Returns "
            + "{passed, green, red, known, noSteps, killed, scenes: [{scene, verdict, steps: {played, total}, pace, paceReason?, seconds, session, "
            + "failedAt?, details?, exit: {code, leaked?, lines?, error?, killed?, killReason?, warning?}, known?, alone?, "
            + "aloneFailedAt?}]}, scenes in the order given; failedAt.pattern {pattern, reason?} names the scratch.patterns entry a "
            + "failing line matched; exit.error is an error in the pace after the last step, killed a game the "
            + "stop had to kill; details lists each step for a red or killed scene, or with options.details; seconds covers both "
            + "plays of a scene played again. The project is prepared once for the whole run."
    )]
    public async Task<string> RunScratchesAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description(
            "The scenes to play, in order: names of .tscn files in scratch.folder (the file name without .tscn) or res:// paths; "
                + "every .tscn directly in scratch.folder, by name, when left out."
        )]
            string[]? scenes = null,
        [Description(
            "{pace, userArgs, prepare, details, parallel}: pace in seconds a step (else scratch.pace's for the scene, else 0.5); "
                + "userArgs appended after the profile's; prepare as run_project's; details true lists every scene's steps; parallel "
                + "the scenes at once, 1 to 4 (else scratch.parallel, else 1)."
        )]
            ScratchOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            ScratchPlan plan = Plan(projectPath, scenes, options ?? new ScratchOptions());
            ScratchRunResult result = await ScratchRun.RunAsync(sessions, plan, cancellationToken);
            return JsonSerializer.Serialize(result, ToolJson.Options);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"A file operation failed: {e.Message}", e);
        }
    }

    /// <summary>The checked call: the project, each scene with its pace and user arguments, and the profile's patterns.</summary>
    /// <exception cref="McpException">
    /// The project, the profile, a scene, the pace, the user arguments or prepare is refused; nothing has launched.
    /// </exception>
    internal static ScratchPlan Plan(string projectPath, IReadOnlyList<string>? scenes, ScratchOptions options)
    {
        string projectDir = ProjectDir(projectPath);
        var profile = ProjectProfile.Load(projectDir);
        ScratchProfile scratch = profile.Scratch ?? ScratchProfile.None;
        CheckPace(options.Pace);
        CheckParallel(options.Parallel);
        bool prepare = RunOptions.ParsePrepare(options.Prepare);
        List<string> userArgs = UserArgs(profile, scratch, options);
        List<ScratchScenePlan> plans = [];
        foreach (string resPath in ResolveScenes(profile, scratch, scenes))
        {
            plans.Add(ScenePlan(Path.GetFileNameWithoutExtension(resPath), resPath, scratch, options, userArgs));
        }

        return new ScratchPlan(projectDir, plans, scratch.Patterns, prepare, options.Details ?? false)
        {
            Parallel = options.Parallel ?? scratch.Parallel,
        };
    }

    /// <summary>
    /// One scene's plan: options.pace else the profile's pace for the scene else the default, and the profile's reason for the
    /// pace, kept only when the pace came from the profile rather than from options.pace.
    /// </summary>
    private static ScratchScenePlan ScenePlan(
        string name,
        string resPath,
        ScratchProfile scratch,
        ScratchOptions options,
        IReadOnlyList<string> userArgs
    )
    {
        ScratchPace? entry = scratch.Pace.GetValueOrDefault(name);
        return new ScratchScenePlan(name, resPath, options.Pace ?? entry?.Seconds ?? DefaultPace, userArgs)
        {
            Known = scratch.Known.GetValueOrDefault(name),
            PaceReason = options.Pace is null ? entry?.Reason : null,
        };
    }

    /// <summary>The scenes as res:// paths: those given, each checked, or every .tscn directly in scratch.folder by ordinal name.</summary>
    /// <exception cref="McpException">No scenes and no folder, an empty list, a name not in the folder, or a path that is not a scene.</exception>
    private static List<string> ResolveScenes(ProjectProfile profile, ScratchProfile scratch, IReadOnlyList<string>? scenes)
    {
        if (scenes is { Count: 0 })
        {
            throw new McpException("scenes is empty; pass scene names or res:// paths, or leave scenes out to play every scene in scratch.folder.");
        }

        if (scenes is null)
        {
            string folder = ScratchFolder(profile);
            return [.. FolderScenes(profile.ProjectDir, folder).Select(name => ResPath(folder, name))];
        }

        return [.. scenes.Select(scene => ResolveScene(profile, scene))];
    }

    private static string ResolveScene(ProjectProfile profile, string scene)
    {
        if (scene.StartsWith("res://", StringComparison.Ordinal))
        {
            return PreviewTools.CheckScene(profile.ProjectDir, scene);
        }

        string folder =
            profile.Scratch?.Folder
            ?? throw new McpException(
                $"scene '{scene}' is a name, which run_scratches looks up in scratch.folder, and {profile.FilePath} sets none; pass its "
                    + "res:// path, or add scratch.folder."
            );
        List<string> names = FolderScenes(profile.ProjectDir, folder);
        return names.Contains(scene, StringComparer.Ordinal)
            ? ResPath(folder, scene)
            : throw new McpException(
                $"scene '{scene}' is not in the scratch folder {FolderResPath(folder)}; its scenes are {string.Join(", ", names)}. Pass one "
                    + "of those, or a res:// path."
            );
    }

    /// <exception cref="McpException">The profile has no scratch section, or the section no folder.</exception>
    private static string ScratchFolder(ProjectProfile profile)
    {
        if (profile.Scratch is null)
        {
            throw new McpException($"{profile.FilePath} has no scratch section; pass scenes, or add scratch.folder.");
        }

        return profile.Scratch.Folder
            ?? throw new McpException($"{profile.FilePath} (scratch): \"folder\" is not set; pass scenes, or add scratch.folder.");
    }

    /// <summary>The names of the .tscn files directly in the folder, in ordinal order.</summary>
    /// <exception cref="McpException">The folder is outside the project, missing, differs in case from the disk, or holds no scene.</exception>
    private static List<string> FolderScenes(string projectDir, string folder)
    {
        string directory = Path.GetFullPath(Path.Combine(projectDir, StripRes(folder)));
        string inProject = Path.GetRelativePath(projectDir, directory);
        if (HeadlessTools.IsOutside(inProject) || !Directory.Exists(directory))
        {
            throw new McpException(
                $"scratch.folder \"{folder}\" is not a folder in the project: looked for {directory}. Set it to a folder relative to the "
                    + "project, e.g. \"Scratch\"."
            );
        }

        // Windows finds a case variant, and Godot would then fail to load the scenes by the path as it was typed.
        string onDisk = inProject == "." ? directory : HeadlessTools.OnDiskCase(projectDir, directory);
        if (!string.Equals(onDisk.TrimEnd(Path.DirectorySeparatorChar), directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
        {
            throw new McpException(
                $"scratch.folder \"{folder}\" differs in case from the folder on disk, {HeadlessTools.ResOf(projectDir, onDisk)}; use the exact case."
            );
        }

        List<string> names =
        [
            .. Directory
                .EnumerateFiles(directory, "*" + SceneEnding)
                .Where(file => file.EndsWith(SceneEnding, StringComparison.Ordinal))
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.Ordinal),
        ];
        return names.Count > 0
            ? names
            : throw new McpException($"scratch.folder {FolderResPath(folder)} holds no .tscn scene: looked in {directory}.");
    }

    /// <exception cref="McpException">The pace is not above 0 and at most the longest wait.</exception>
    private static void CheckPace(double? pace)
    {
        if (pace is { } seconds && !ScratchProfile.IsPace(seconds))
        {
            throw new McpException($"options.pace is {seconds}; it must be a number of seconds above 0 and at most {ScratchProfile.MaxPaceSeconds}.");
        }
    }

    /// <summary>The profile's scratch.userArgs, else its top-level userArgs, then options.userArgs.</summary>
    /// <exception cref="McpException">The user arguments hold --scratch-auto.</exception>
    private static List<string> UserArgs(ProjectProfile profile, ScratchProfile scratch, ScratchOptions options)
    {
        List<string> userArgs = [.. scratch.UserArgs ?? profile.UserArgs, .. options.UserArgs ?? []];
        return userArgs.Contains(ScratchAuto, StringComparer.Ordinal)
            ? throw new McpException(
                $"The user arguments hold {ScratchAuto}, which starts the scene's own step clock beside run_scratches' walk. Remove it "
                    + $"from options.userArgs or from {profile.FilePath}."
            )
            : userArgs;
    }

    /// <exception cref="McpException">The number of scenes at once is not from 1 to the most.</exception>
    private static void CheckParallel(int? parallel)
    {
        if (parallel is { } count && !ScratchProfile.IsParallel(count))
        {
            throw new McpException($"options.parallel is {count}; it must be a whole number from 1 to {ScratchProfile.MaxParallel}.");
        }
    }

    private static string ResPath(string folder, string name)
    {
        string relative = StripRes(folder).Replace('\\', '/').Trim('/');
        return relative.Length == 0 ? $"res://{name}{SceneEnding}" : $"res://{relative}/{name}{SceneEnding}";
    }

    private static string FolderResPath(string folder) => "res://" + StripRes(folder).Replace('\\', '/').Trim('/');

    private static string StripRes(string folder) => folder.StartsWith("res://", StringComparison.Ordinal) ? folder["res://".Length..] : folder;

    /// <exception cref="McpException">The path is empty or holds no project.godot.</exception>
    private static string ProjectDir(string projectPath)
    {
        try
        {
            return SessionRegistry.NormaliseProjectDir(projectPath);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
    }
}

/// <summary>run_scratches' pace, user arguments, prepare, whether every scene lists its steps, and how many scenes play at once.</summary>
internal sealed record ScratchOptions(
    [property: Description(
        "Seconds of game time each step plays before the next, above 0 and at most 120; else the profile's scratch.pace for the " + "scene, else 0.5."
    )]
        double? Pace = null,
    [property: Description("User arguments appended after the profile's scratch.userArgs (else its top-level userArgs); --scratch-auto is refused.")]
        string[]? UserArgs = null,
    [property: Description(RunOptions.PrepareDescription)] string? Prepare = null,
    [property: Description("true lists every scene's steps; a red or killed scene lists them anyway.")] bool? Details = null,
    [property: Description(
        "How many scenes play at once, 1 to 4; else the profile's scratch.parallel, else 1. Above 1, a red or killed scene is "
            + "played once more alone after the others."
    )]
        int? Parallel = null
);
