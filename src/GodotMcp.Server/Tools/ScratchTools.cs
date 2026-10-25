using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.CSharp;
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
internal sealed class ScratchTools(SessionRegistry sessions, CSharpBridge csharp)
{
    internal const string ToolName = "run_scratches";

    internal const string StatusToolName = "scratch_status";

    /// <summary>A step's pace when neither options.pace nor the profile's scratch.pace sets one, in seconds.</summary>
    internal const double DefaultPace = 0.5;

    /// <summary>The smallest options.timeScale: a wait's wall time is its pace divided by the scale.</summary>
    internal const double MinTimeScale = 0.01;

    /// <summary>What only does, for scratch_status' parameter and run_scratches' options.only alike.</summary>
    internal const string OnlyDescription =
        "\"all\" (default) returns the whole result; \"red\" keeps every field of every scene and prunes only its details to its "
        + "red steps (ok false), each with its full readback (status, error, errors, errorsOmitted, lines, cut): a green scene, "
        + "or a red one with no red step (a boot error, a failed exit, a kill), lists no steps. Applied as the result is read: "
        + "a background run's status file stays whole, so a later read with \"all\" still sees every step, and a running run's "
        + "scenes are returned as they are.";

    private const string SceneEnding = ".tscn";
    private const string ScratchAuto = "--scratch-auto";
    private const string UserArgsSeparator = "--";
    private const string AlternateUserArgsSeparator = "++";
    private static readonly string[] RunnerEngineArgs = ["--headless", "--time-scale", ScratchAuto];

    [McpServerTool(Name = ToolName, ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = false)]
    [Description(
        "Plays scratch scenes and reports a verdict for each: every scene runs in a fresh headless game session "
            + "(<folder>.scratch-<scene>, or '<options.session>.scratch-<scene>'), its root is called through the scratch protocol "
            + "(GetStepCount, GetStepName(i), then per step "
            + "PlayStep(i), a wait of the pace in game time, GetStatus). A root that also has a public GetStepUntil(i) gives each step a "
            + "wait_for condition (an empty dictionary, or null, for a paced step): such a step is played inside one wait_for on it, "
            + "PlayStep(i) as the wait's options.call, until the condition holds or its pace has passed in game time; a condition not "
            + "met in that time, or refused, is red and stops the scene, even with options.keepGoing; the step's details carry until "
            + "{met, gameMs, elapsedMs, frames} in place of gameMs. A scene stops at its first failed step unless "
            + "options.keepGoing; options.steps [3, \"name\", \"2-5\"] plays only those steps, by index, name or inclusive range, in "
            + "ascending order, and never runs the others beyond the setup steps its profile's scratch.setup declares, which a run "
            + "given options.steps adds to the selection, so a later step that depends on a skipped one is the caller's choice. "
            + "A step fails "
            + "on an error the game logs (push_error, an engine error, a C# exception) or a stdout or stderr line matching the "
            + "profile's scratch.patterns; after the steps the game is stopped and a non-zero exit or an ObjectDB leak turns the "
            + "scene red. Settings live in godot-mcp.json's scratch section "
            + "{folder, userArgs, engineArgs, pace, ceilings, setup, known, patterns, parallel}, where setup {<scene>: [step, ...]} "
            + "names a scene's declared setup steps, which a run given options.steps adds to the selection; a key is resolved only "
            + "then, so one naming no step of its scene turns that scene red only on a partial run. Scenes "
            + "start parallel at a time: those listed in scenes in that order, a folder run's largest pace (a scene's or "
            + "one of its steps') divided by the scene's time scale first; with more than one, a red or killed scene (not known, "
            + "not refused before its first "
            + "step) is played once more alone after the others: green alone, its entry is the replay's with alone: true and counts "
            + "green; else the first entry with alone: false and the replay's failure as aloneFailedAt. Returns "
            + "{passed, green, red, known, noSteps, killed, prep: {build, buildMs?, buildLog?, import, importMs?, importLog?, note?}, "
            + "scenes: [{scene, verdict, steps: {played, total}, selected?, setup?, timeScale?, pace, paceReason?, seconds, session, "
            + "failedAt?, failures?, details?, exit: {code, leaked?, lines?, error?, killed?, killReason?, warning?}, known?, alone?, "
            + "aloneFailedAt?}], timeScale?}, scenes in the order given; selected lists the indices options.steps selected, setup the "
            + "setup steps the run added to them, both fixed when the steps resolve, so a scene stopped at a step, at the boot or by "
            + "a kill still lists them; "
            + "timeScale is options.timeScale when it is not 1, and a scene's own timeScale (1) shows its game ran at another speed "
            + "than the call's, its scratch.pace entry having \"timeScale\": 1; "
            + "failedAt is the first failed step and failures [{index, name, error}] "
            + "every one; failedAt.pattern {pattern, reason?} names the scratch.patterns entry a failing line matched; details "
            + "lists every played step of a red or killed scene, or of any scene with options.details, by its status line "
            + "{index, name, ok, gameMs?, until?, status}; without details, a failing step carries its error, its first 3 errors and 5 "
            + "lines in at most 1500 characters; details: true gives every step's full detail, 20 errors, 20 lines, 4000 "
            + "characters ({error, errors, errorsOmitted?, lines, cut?}); errors [{message, file, line, count?}] are folded with "
            + "a count (errorsOmitted counts the rest), messages cut at 500 characters without details and 2000 with; "
            + "failures[].error is cut at 200 characters without details, 500 with; a step's "
            + "first error entry is always kept; cut names the budget a step's detail ran over and the session whose "
            + "get_debug_output holds the full log; exit.error is an error in the pace "
            + "after the last step, killed a game the stop had to kill; seconds covers both plays of a scene played again. The "
            + "project is prepared once for the whole run. options.background true returns {runId, statusFile, logFile, scenes} "
            + "at once and plays the run on in the server; read or stop it with scratch_status. While a background run is live on "
            + "the project, every run_scratches on it is refused."
    )]
    public async Task<string> RunScratchesAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description(
            "The scenes to play, in order: names of .tscn files in scratch.folder (the file name without .tscn) or res:// paths; "
                + "every .tscn directly in scratch.folder, by name, when left out."
        )]
            string[]? scenes = null,
        [Description(
            "{pace, timeScale, userArgs, engineArgs, ceilings, prepare, details, parallel, session, keepGoing, steps, background, "
                + "only}: pace in seconds for every "
                + "step (else scratch.pace's for the step, else for the scene, else 0.5); timeScale the games' time scale, from 0.01 to "
                + "100 (default 1), each game launched with --time-scale, so a pace of p s takes p / timeScale s of wall time and "
                + "the ceiling derived from the paces scales with it, while a ceiling given by ceilings or scratch.ceilings is fixed "
                + "seconds and does not; the wait after the last step lasting p s of wall time above 1, a scene whose "
                + "scratch.pace entry has \"timeScale\": 1 kept at normal speed; userArgs "
                + "appended after the profile's; "
                + "engineArgs placed before -- after the profile's scratch.engineArgs; "
                + "ceilings {<scene>: seconds} the scene's own ceiling of load-adjusted time from its first step, replacing the one "
                + "derived from the paces and beating the profile's scratch.ceilings, and not scaled by timeScale; "
                + "prepare as run_project's; "
                + "details true lists every scene's steps and gives every step its full detail; parallel the scenes at once, 1 to 4 "
                + "(else scratch.parallel, else 1); "
                + "session a prefix for the scenes' session names, in place of the project folder's; keepGoing true (default false) "
                + "plays a scene's next step after one red on a pushed error or a matching line, but a step whose own call failed "
                + "(the game gone or stuck) still stops the scene; steps [3, \"name\", \"2-5\"] plays only those steps, by index, name or "
                + "inclusive range, in ascending order, and never runs the others, so a later step that depends on a skipped one is "
                + "the caller's choice; background true returns {runId, statusFile, logFile, scenes} at once and plays the run on "
                + "in the server, read and ended with scratch_status; only \"red\" (default \"all\") lists in each scene's details "
                + "only its red steps, with their full readback, and no step of a green scene, every other scene field kept; it is "
                + "refused with background, whose result scratch_status's only filters."
        )]
            ScratchOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            ScratchOptions given = options ?? new ScratchOptions();
            ScratchPlan plan = Plan(projectPath, scenes, given);
            if (given.Background ?? false)
            {
                return StartBackground(plan);
            }

            sessions.ScratchRuns.RefuseLive(plan.ProjectDir);
            ScratchRunResult result = await ScratchRun.RunAsync(sessions, csharp, plan, observer: null, cancellationToken);
            if (!RedOnly(given.Only))
            {
                return JsonSerializer.Serialize(result, ToolJson.Options);
            }

            JsonObject built = JsonSerializer.SerializeToNode(result, ToolJson.Options)!.AsObject();
            return ScratchView.Result(built, redOnly: true).ToJsonString(ToolJson.Options);
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

    [McpServerTool(Name = StatusToolName, ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = false)]
    [Description(
        "Reads a background scratch run (run_scratches with options.background), or ends it with stop: true. Returns its status "
            + "{runId, state, serverPid, startedAt, updatedAt, scenes: [{scene, state, step?, stepName?, total?, elapsedSeconds?, "
            + "remainingSecondsAtMost?, verdict?, timeScale?}], timeScale?, result?, error?, live}: state is running, done, stopped, "
            + "interrupted (the server exited) or failed (with error); scenes in play order, each queued, playing (its step index, "
            + "name and the scene's step count, remainingSecondsAtMost the wall seconds the paces still to come take at the scene's "
            + "time scale, launch left out) or done (its verdict), or the run's own final word for a scene it ended mid-step; "
            + "timeScale is options.timeScale when it is not 1, and a scene's own timeScale (1) shows its game runs at another "
            + "speed than the run's, its scratch.pace entry having \"timeScale\": 1; "
            + "result, once done, is the object run_scratches returns without background; only: \"red\" keeps every scene's line in "
            + "it and lists in its details only the red steps, with their full readback, the status file left whole; "
            + "startedAt and updatedAt are UTC ISO 8601. live is true while this server still plays the run. A run of this server is "
            + "read from memory; any other run's status file, <project>/.godot/godot-mcp/scratch-<runId>.json, is returned as it "
            + "is, with live false: one a server killed outright left says running, and its serverPid tells whose it was. "
            + "stop: true ends a live run of this server as a cancelled call ends (its games stopped), writes stopped and the "
            + "completion line, and returns the final status once the run has ended; a run this server finished is returned "
            + "unchanged, and a stop of a run read from its status file (another server's, or one this server no longer keeps) is "
            + "refused."
    )]
    public async Task<string> ScratchStatusAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description("The run's id, as run_scratches returned it: <UTC yyyyMMddTHHmmss>-<4 hex>, e.g. 20261019T222144-3f2a.")] string runId,
        [Description("true ends the run and returns its final status; default false only reads it.")] bool? stop = null,
        [Description(OnlyDescription)] string? only = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!ScratchBackgroundRun.IsRunId(runId))
        {
            throw new McpException(
                $"runId '{runId}' is not a scratch run id; one looks like 20261019T222144-3f2a, as run_scratches with options.background "
                    + "returned it."
            );
        }

        bool redOnly = RedOnly(only);
        try
        {
            string projectDir = ProjectDir(projectPath);
            JsonObject status = await StatusAsync(projectDir, runId, stop ?? false, cancellationToken);
            return ScratchView.Status(status, redOnly).ToJsonString(ToolJson.Options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"A file operation failed: {e.Message}", e);
        }
        catch (JsonException e)
        {
            throw new McpException($"Scratch run {runId}'s status file is not JSON: {e.Message}", e);
        }
    }

    /// <summary>
    /// Registers a background run of the plan, creates its files and starts its task with no token; returns its id, files and
    /// scenes. A failure after registering removes the entry.
    /// </summary>
    /// <exception cref="SessionException">A background run is live on the project, or the server is shutting down.</exception>
    private string StartBackground(ScratchPlan plan)
    {
        ScratchBackgroundRun run = sessions.ScratchRuns.Register(plan, sessions.ServerProcessId, ScratchRunIo.System);
        try
        {
            run.CreateFiles();
        }
        catch
        {
            sessions.ScratchRuns.Remove(run);
            run.EndTask();
            throw;
        }

        run.Start(() => run.PlayAsync(sessions, csharp, plan));
        return JsonSerializer.Serialize(new ScratchBackgroundStart(run.RunId, run.StatusFile, run.LogFile, run.Scenes), ToolJson.Options);
    }

    /// <summary>
    /// The run's status with live: this server's live run (stopped first when asked), else its finished run, else the status
    /// file as it is.
    /// </summary>
    private async Task<JsonObject> StatusAsync(string projectDir, string runId, bool stop, CancellationToken cancellationToken)
    {
        if (sessions.ScratchRuns.Find(projectDir, runId) is { } run)
        {
            (ScratchRunStatus status, bool live) = stop ? await run.StopAsync(cancellationToken) : run.Read();
            return WithLive(JsonSerializer.SerializeToNode(status, ToolJson.Options)!.AsObject(), live);
        }

        return sessions.ScratchRuns.FindFinished(projectDir, runId) is { } finished
            ? WithLive(JsonSerializer.SerializeToNode(finished, ToolJson.Options)!.AsObject(), live: false)
            : WithLive(StatusFile(projectDir, runId, stop), live: false);
    }

    /// <summary>The run's status file as it is; a stop is refused, since the run is not live in this server.</summary>
    /// <exception cref="McpException">No status file, one that is not a JSON object, or a stop.</exception>
    private JsonObject StatusFile(string projectDir, string runId, bool stop)
    {
        string path = ScratchBackgroundRun.StatusPath(projectDir, runId);
        if (!File.Exists(path))
        {
            throw new McpException(
                $"No scratch run {runId} on {projectDir}: looked for its status file {path}. Pass the runId run_scratches returned, "
                    + "with the same projectPath."
            );
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        JsonObject status =
            (JsonNode.Parse(stream) as JsonObject) ?? throw new McpException($"{path} is not a scratch run's status file: it holds no JSON object.");
        if (stop)
        {
            throw new McpException(NotLive(status, runId, path));
        }

        return status;
    }

    /// <summary>Why a stop of a run read from its status file is refused, whichever server's process id the file names.</summary>
    private string NotLive(JsonObject status, string runId, string path)
    {
        int? serverPid = status["serverPid"] is JsonValue pid && pid.TryGetValue(out int held) ? held : null;
        return $"scratch run {runId} is not live in this server ({sessions.ServerProcessId}); its status file {path} names process id "
            + $"{serverPid?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}, and only a live run can be stopped.";
    }

    private static JsonObject WithLive(JsonObject status, bool live)
    {
        status["live"] = live;
        return status;
    }

    /// <summary>
    /// The checked call: the project, each scene with its pace, user arguments, engine arguments and ceiling, and the profile's
    /// patterns.
    /// </summary>
    /// <exception cref="McpException">
    /// The project, the profile, a scene, the pace, the user arguments, the engine arguments, the ceilings, prepare or
    /// options.steps is refused; nothing has launched.
    /// </exception>
    internal static ScratchPlan Plan(string projectPath, IReadOnlyList<string>? scenes, ScratchOptions options)
    {
        string projectDir = ProjectDir(projectPath);
        var profile = ProjectProfile.Load(projectDir);
        ScratchProfile scratch = profile.Scratch ?? ScratchProfile.None;
        CheckPace(options.Pace);
        CheckTimeScale(options.TimeScale);
        CheckParallel(options.Parallel);
        CheckOnly(options);
        bool prepare = RunOptions.ParsePrepare(options.Prepare);
        string prefix = SessionPrefix(options.Session, projectDir);
        List<string> userArgs = UserArgs(profile, scratch, options);
        List<string> engineArgs = EngineArgs(profile, scratch, options);
        List<string> resPaths = ResolveScenes(profile, scratch, scenes);
        CheckCeilings(options.Ceilings, resPaths);
        List<ScratchScenePlan> plans = [];
        foreach (string resPath in resPaths)
        {
            plans.Add(ScenePlan(Path.GetFileNameWithoutExtension(resPath), resPath, scratch, options, userArgs, engineArgs));
        }

        return new ScratchPlan(projectDir, plans, scratch.Patterns, prepare, options.Details ?? false)
        {
            Parallel = options.Parallel ?? scratch.Parallel,
            Listed = scenes is not null,
            SessionPrefix = prefix,
            KeepGoing = options.KeepGoing ?? false,
            Steps = StepTokens(options.Steps),
            TimeScale = options.TimeScale ?? 1,
        };
    }

    /// <summary>options.steps as tokens, in the order given; null when left out.</summary>
    /// <exception cref="McpException">
    /// The list is empty, a token is neither a number nor a string, a number is not a whole number of 0 or more, a range runs
    /// backwards, or two tokens are the same.
    /// </exception>
    internal static IReadOnlyList<ScratchStepToken>? StepTokens(JsonElement[]? steps)
    {
        if (steps is null)
        {
            return null;
        }

        if (steps.Length == 0)
        {
            throw new McpException(
                "options.steps is empty; list the steps to play by index, name or range (\"2-5\"), or leave it out to play every step."
            );
        }

        List<ScratchStepToken> tokens = [];
        foreach (JsonElement element in steps)
        {
            ScratchStepToken token = StepToken(element);
            int same = tokens.FindIndex(other => string.Equals(other.Text, token.Text, StringComparison.Ordinal));
            if (same >= 0)
            {
                throw new McpException($"options.steps tokens {tokens[same]} and {token} are the same token; keep one.");
            }

            tokens.Add(token);
        }

        return tokens;
    }

    /// <exception cref="McpException">The token is neither a number nor a string, a bad index, or a backwards range.</exception>
    private static ScratchStepToken StepToken(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => new ScratchStepToken(StepIndex(element), IsNumber: true),
            JsonValueKind.String => ForwardRange(new ScratchStepToken(element.GetString()!, IsNumber: false)),
            _ => throw new McpException(
                $"options.steps token {element.GetRawText()} is neither a number nor a string; a token is a step index, a step's name, "
                    + "or a range such as \"2-5\"."
            ),
        };

    /// <summary>A number token's index, written plainly.</summary>
    /// <exception cref="McpException">The number is negative, not whole, or past the largest index.</exception>
    private static string StepIndex(JsonElement element) =>
        element.TryGetDouble(out double number) && number >= 0 && number <= int.MaxValue && number == Math.Floor(number)
            ? ((int)number).ToString(CultureInfo.InvariantCulture)
            : throw new McpException(
                $"options.steps token {element.GetRawText()} is not a step index; a number names a step by its index, a whole number from 0 up."
            );

    /// <exception cref="McpException">The token is a range whose start is after its end.</exception>
    private static ScratchStepToken ForwardRange(ScratchStepToken token) =>
        token.IsRange(out string first, out string last)
        && BigInteger.Parse(first, CultureInfo.InvariantCulture) > BigInteger.Parse(last, CultureInfo.InvariantCulture)
            ? throw new McpException($"options.steps token {token} is a range whose start is after its end; write it \"{last}-{first}\".")
            : token;

    /// <summary>The prefix the plan names its sessions with: options.session when given, else the project folder's name.</summary>
    /// <exception cref="McpException">The given name breaks the name rule.</exception>
    private static string SessionPrefix(string? session, string projectDir)
    {
        try
        {
            return SessionRegistry.NameFor(session, projectDir);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
    }

    /// <summary>
    /// One scene's plan: options.pace else the profile's pace for the scene else the default, and the profile's reason for the
    /// pace and its step paces, kept only when the pace came from the profile rather than from options.pace, which paces every
    /// step; and options.timeScale, else 1, unless the profile's pace has "timeScale": 1, which keeps the scene at normal speed
    /// even beside options.pace; the ceiling options.ceilings or the profile's scratch.ceilings gives the scene, if either does;
    /// the setup steps the profile's scratch.setup declares for the scene; and the engine arguments its game launches with.
    /// </summary>
    private static ScratchScenePlan ScenePlan(
        string name,
        string resPath,
        ScratchProfile scratch,
        ScratchOptions options,
        IReadOnlyList<string> userArgs,
        IReadOnlyList<string> engineArgs
    )
    {
        ScratchPace? entry = scratch.Pace.GetValueOrDefault(name);
        return new ScratchScenePlan(name, resPath, options.Pace ?? entry?.Seconds ?? DefaultPace, userArgs)
        {
            Known = scratch.Known.GetValueOrDefault(name),
            PaceReason = options.Pace is null ? entry?.Reason : null,
            StepPaces = StepPaces(entry, options),
            TimeScale = entry is { NormalSpeed: true } ? 1 : options.TimeScale ?? 1,
            EngineArgs = engineArgs,
            Ceiling = CeilingFor(name, scratch, options),
            Setup = SetupFor(name, scratch),
        };
    }

    /// <summary>The setup steps the profile's scratch.setup declares for the scene; empty when it declares none.</summary>
    private static IReadOnlyList<string> SetupFor(string name, ScratchProfile scratch) =>
        scratch.Setup.TryGetValue(name, out IReadOnlyList<string>? setup) ? setup : [];

    /// <summary>
    /// The scene's own ceiling: options.ceilings for it, else the profile's scratch.ceilings, else none, which derives it from the
    /// paces.
    /// </summary>
    private static ScratchCeiling? CeilingFor(string name, ScratchProfile scratch, ScratchOptions options)
    {
        if (options.Ceilings is { } ceilings && ceilings.TryGetValue(name, out double given))
        {
            return new ScratchCeiling(given, ScratchCeiling.OptionsSource);
        }

        return scratch.Ceilings.TryGetValue(name, out double profiled) ? new ScratchCeiling(profiled, ScratchCeiling.ProfileSource) : null;
    }

    /// <summary>Checks options.ceilings: each key a planned scene, each value a number of seconds above 0 and at most 3600.</summary>
    /// <exception cref="McpException">A key names no scene of the plan, or a value is outside (0, 3600].</exception>
    private static void CheckCeilings(Dictionary<string, double>? ceilings, List<string> resPaths)
    {
        if (ceilings is null)
        {
            return;
        }

        List<string> names = [.. resPaths.Select(Path.GetFileNameWithoutExtension).Select(name => name!)];
        foreach ((string scene, double seconds) in ceilings)
        {
            if (!names.Contains(scene))
            {
                throw new McpException(
                    $"options.ceilings scene '{scene}' is not in the plan; its scenes are {string.Join(", ", names)}. Pass one of those, "
                        + "or leave the scene's ceiling to its paces."
                );
            }

            if (!ScratchProfile.IsCeiling(seconds))
            {
                throw new McpException(
                    $"options.ceilings.{scene} must be a number of seconds above 0 and at most {ScratchProfile.MaxCeilingSeconds}; "
                        + $"got {seconds.ToString(CultureInfo.InvariantCulture)}."
                );
            }
        }
    }

    /// <summary>The profile's step paces for the scene, none when options.pace paces every step.</summary>
    private static IReadOnlyDictionary<string, ScratchPace> StepPaces(ScratchPace? entry, ScratchOptions options) =>
        options.Pace is null && entry is not null ? entry.Steps : ReadOnlyDictionary<string, ScratchPace>.Empty;

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

    /// <exception cref="McpException">
    /// The time scale is below <see cref="MinTimeScale"/>, whose waits would overflow the deadlines, or above
    /// <see cref="RuntimeTools.MaxTimeScale"/>.
    /// </exception>
    private static void CheckTimeScale(double? timeScale)
    {
        if (timeScale is { } scale && scale is not (>= MinTimeScale and <= RuntimeTools.MaxTimeScale))
        {
            throw new McpException(
                $"options.timeScale must be at least {MinTimeScale.ToString(CultureInfo.InvariantCulture)} and at most "
                    + $"{RuntimeTools.MaxTimeScale.ToString(CultureInfo.InvariantCulture)}; got {scale.ToString(CultureInfo.InvariantCulture)}."
            );
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

    /// <summary>
    /// The profile's scratch.engineArgs, then options.engineArgs, each refused when it holds a null entry or an argument the
    /// runner sets itself.
    /// </summary>
    /// <exception cref="McpException">
    /// An argument is null, or is or, trimmed, is -- or ++, or is or, trimmed, starts with --headless, --time-scale or
    /// --scratch-auto.
    /// </exception>
    private static List<string> EngineArgs(ProjectProfile profile, ScratchProfile scratch, ScratchOptions options)
    {
        CheckEngineArgs(scratch.EngineArgs, profile.FilePath);
        CheckEngineArgs(options.EngineArgs, "options.engineArgs");
        return [.. scratch.EngineArgs ?? [], .. options.EngineArgs ?? []];
    }

    /// <summary>Refuses one source's engine arguments: a null entry, or one the runner sets itself.</summary>
    /// <exception cref="McpException">An argument is null, is a separator, or starts with an argument the runner sets.</exception>
    private static void CheckEngineArgs(IReadOnlyList<string>? engineArgs, string source)
    {
        if (engineArgs is null)
        {
            return;
        }

        if (engineArgs.Any(arg => arg is null))
        {
            throw new McpException($"engineArgs holds a null entry; it came from {source}.");
        }

        if (RefusedEngineArg(engineArgs) is { } arg)
        {
            throw EngineArgRefusal(arg, source);
        }
    }

    /// <summary>
    /// The first engine argument the runner cannot take, as it was given: -- or ++, or one that starts with an argument it sets
    /// itself, each compared as Godot trims it before parsing (main/main.cpp:1109).
    /// </summary>
    private static string? RefusedEngineArg(IReadOnlyList<string> engineArgs)
    {
        foreach (string arg in engineArgs)
        {
            string trimmed = arg.Trim();
            if (trimmed is UserArgsSeparator or AlternateUserArgsSeparator)
            {
                return arg;
            }

            if (RunnerEngineArgs.Any(flag => trimmed.StartsWith(flag, StringComparison.Ordinal)))
            {
                return arg;
            }
        }

        return null;
    }

    /// <summary>The refusal of an engine argument, naming where it came from.</summary>
    private static McpException EngineArgRefusal(string arg, string source) =>
        new(
            $"run_scratches sets --headless and --time-scale itself and puts the scene's own arguments after --, so engineArgs "
                + $"cannot hold {arg}; it came from {source}."
        );

    /// <exception cref="McpException">options.only is neither "all" nor "red", or is given with background, which never reads it.</exception>
    private static void CheckOnly(ScratchOptions options)
    {
        _ = RedOnly(options.Only);
        if (options.Only is not null && (options.Background ?? false))
        {
            throw new McpException("options.only is not read by a background run; pass only to scratch_status, which returns the run's result");
        }
    }

    /// <summary>Whether only asks for the red steps alone: false for "all" or left out, true for "red".</summary>
    /// <exception cref="McpException">The value is neither "all" nor "red".</exception>
    private static bool RedOnly(string? only) =>
        only switch
        {
            null or ScratchView.All => false,
            ScratchView.Red => true,
            _ => throw new McpException($"only must be \"all\" or \"red\", not \"{only}\""),
        };

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

/// <summary>run_scratches' pace, time scale, user arguments, engine arguments, prepare, whether every scene lists its steps, how
/// many scenes play at once, the prefix for the scenes' session names, and whether a scene plays on past a failed step.</summary>
internal sealed record ScratchOptions(
    [property: Description(
        "Seconds of game time every step plays before the next, above 0 and at most 120, its scratch.pace steps entries "
            + "included; else the profile's scratch.pace for the step, else for the scene, else 0.5. For a step whose scene gives a "
            + "GetStepUntil condition, the pace is the most game time the step may take before its condition holds."
    )]
        double? Pace = null,
    [property: Description(
        "Time scale the scenes' games run at, from 0.01 to 100 (default 1): each game launches with --time-scale, so a pace "
            + "of p s of game time takes p / timeScale s of wall time and the ceiling derived from the paces scales with it, while "
            + "a ceiling given by options.ceilings or the profile's scratch.ceilings is fixed seconds and does not; above 1 the "
            + "wait after the last step still lasts p s of wall time. A scene whose profile pace "
            + "entry has \"timeScale\": 1 runs at normal speed."
    )]
        double? TimeScale = null,
    [property: Description("User arguments appended after the profile's scratch.userArgs (else its top-level userArgs); --scratch-auto is refused.")]
        string[]? UserArgs = null,
    [property: Description(
        "Engine arguments placed before -- after the profile's scratch.engineArgs (the top-level engineArgs are not used), both "
            + "after the runner's own --headless and --time-scale; --, ++ and any argument starting with --headless, --time-scale "
            + "or --scratch-auto are refused."
    )]
        string[]? EngineArgs = null,
    [property: Description(
        "Per-scene ceilings of load-adjusted time, scene name to seconds from that scene's first step: each above 0 and at most "
            + "3600, fixed seconds that options.timeScale does not scale, and replacing the ceiling derived from the paces, lower "
            + "or higher. A scene's entry wins over the profile's scratch.ceilings for the scene; a scene in neither keeps the "
            + "derived ceiling, and a name that is not a planned scene is refused."
    )]
        Dictionary<string, double>? Ceilings = null,
    [property: Description(RunOptions.PrepareDescription)] string? Prepare = null,
    [property: Description(
        "true lists every scene's steps and gives every step, green or red, its full detail (error, errors, lines with its "
            + "[scratch] notes): 20 folded errors, 20 lines, 4000 characters. Without details, a red or killed scene still lists "
            + "every step by its status line, and a failing step carries its error, its first 3 errors and 5 lines in 1500 "
            + "characters, each error message cut at 500. Errors are folded with a count, messages cut at 2000 characters with details."
    )]
        bool? Details = null,
    [property: Description(
        "How many scenes play at once, 1 to 4; else the profile's scratch.parallel, else 1. Above 1, a red or killed scene is "
            + "played once more alone after the others."
    )]
        int? Parallel = null,
    [property: Description(
        "Prefix for the scratch runs' session names, in place of the project folder's: each scene runs as '<session>.scratch-<scene>'. "
            + "Give each worktree's agent its own, so parallel runs are told apart in list_sessions."
    )]
        string? Session = null,
    [property: Description(
        "true plays a scene's next step after one that failed on a pushed error or a matching line, but a step whose own call "
            + "failed (the game gone or stuck) still stops the scene; default false, the scene stops at its first failed step."
    )]
        bool? KeepGoing = null,
    [property: Description(
        "The steps to play, the others never run: an array of tokens, each a step index (3, or \"3\" spelled plainly), a step's "
            + "exact name, or an inclusive range of indices (\"2-5\"); a string is read as an index first, then a name, then a range. "
            + "The union plays in ascending order, each step once, and one list applies to every scene: a token naming no step of a "
            + "scene turns that scene red before any step plays. A later step that depends on a skipped one is the caller's choice. "
            + "Each scene adds the steps its profile's scratch.setup declares for it to this selection, ascending with them; its "
            + "result lists the selected indices as selected and the added setup steps as setup."
    )]
        JsonElement[]? Steps = null,
    [property: Description(
        "true returns at once, once the plan is checked, with {runId, statusFile, logFile, scenes} (scenes the names in play "
            + "order), and the run plays on in the server, ended only by scratch_status {stop: true} or the server's exit, never by "
            + "the call's end. statusFile is <project>/.godot/godot-mcp/scratch-<runId>.json, the run's status rewritten whole at "
            + "each scene's start and end and each step's start; logFile is scratch-<runId>.log beside it, one line per event, each "
            + "starting 'godot-mcp scratch run <runId> ', the last the completion line, matching ^godot-mcp scratch run \\S+ "
            + "finished: (done with passed and the counts, stopped, interrupted, or failed with the error). Read the run with "
            + "scratch_status; open, read and close the files, never hold them open. While it runs, every run_scratches on the "
            + "project is refused. Default false."
    )]
        bool? Background = null,
    [property: Description(
        ScratchTools.OnlyDescription + " Refused with background, which never reads it: pass only to scratch_status, which returns the run's result."
    )]
        string? Only = null
);
