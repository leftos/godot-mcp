using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// An editor session's own actions through EditorInterface, run by the bridge's editor module (bridge/godot_mcp_editor.gd): open,
/// save, save all, close, play, stop and select a file, and the editor's state; and build, which the server runs itself before the
/// bridge waits for the editor's reload. Both tools refuse any session but an editor's before asking its bridge.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const string EditorToolName = "editor";
    internal const string GetEditorStateToolName = "get_editor_state";

    /// <summary>The editor tool's actions, in the order its refusal lists them.</summary>
    internal static readonly IReadOnlyList<string> EditorActions = ["open", "save", "save_all", "close", "play", "stop", "select_file", "build"];

    private const string PlayingBuildWarning = "a scene is playing; the running game keeps its old assembly until it is played again.";

    /// <summary>
    /// An action that may show a progress dialog or build: the bridge waits up to 30 s for its effect after the call returns, and
    /// play may build a C# project inside the call.
    /// </summary>
    private static readonly TimeSpan EditorActionTimeout = TimeSpan.FromSeconds(120);

    /// <summary>A read or an action that replies at once: get_editor_state, stop and select_file.</summary>
    private static readonly TimeSpan EditorReadTimeout = TimeSpan.FromSeconds(10);

    private const string EditorStateNote =
        " The state is {openScenes, currentScene, unsavedScenes, playingScene, scanning, importing}: the open scenes' paths, "
        + "the edited scene's path (null with none, empty for an untitled one), the paths of the scenes with unsaved changes, the "
        + "playing scene's path (empty when none plays), and whether the editor is scanning or importing files.";

    [McpServerTool(Name = EditorToolName, ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Acts on the editor of an editor session (run_editor) as its menus do, through EditorInterface. action is one of: open "
            + "(scene, a res:// .tscn or .scn that exists, becomes the edited scene); save (scene, an open scene, or the edited "
            + "one when left out; done once it leaves the unsaved list and its file is written); save_all (every unsaved scene); "
            + "close (scene, an open scene, or the edited one; an unsaved scene is refused unless discard is true, which drops "
            + "its changes); play (scene left out or \"current\" plays the edited scene, \"main\" the project's main scene, a "
            + "res:// path that scene; done once the editor says a scene plays, without waiting for the game's process); stop "
            + "(stops the playing scene; wasPlaying says whether one played); select_file (path, a res:// file or folder that "
            + "exists, selected in the FileSystem dock); build (builds the project's C# assembly as run_project's prep does, "
            + "skipped when it is up to date, then waits for the editor to reload it; refused for a project with no .csproj, "
            + "and a red build fails with its compiler errors). Returns the editor's state with action and the action's own "
            + "fields (scene, saved, wasPlaying, selected; for build: build built or up-to-date, reloadWaited when built, log "
            + "the build log's path, and a warning when a scene plays, since the running game keeps its old assembly)."
            + EditorStateNote
            + " An action that shows a progress dialog holds every other command to this session until its effect shows; one "
            + "whose effect does not show within 30 s fails saying so, and a dialog the editor opened may be waiting: look "
            + "with take_screenshot and click it. The tool never dismisses a dialog."
    )]
    public async Task<string> EditorAsync(
        [Description("One of open, save, save_all, close, play, stop, select_file, build.")] string action,
        [Description(
            "The scene: a res:// path for open, save and close (the edited scene when left out), or current, main or a res:// "
                + "path for play (current when left out)."
        )]
            string? scene = null,
        [Description("The res:// file or folder select_file selects.")] string? path = null,
        [Description("For close: close a scene with unsaved changes, dropping them. Default false, which refuses it.")] bool discard = false,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = BuildEditorParameters(action, scene, path, discard);
        GodotSession game = FindEditor(EditorToolName, session);
        if (action == "build")
        {
            return await EditorBuildAsync(game, cancellationToken);
        }

        TimeSpan timeout = action is "stop" or "select_file" ? EditorReadTimeout : EditorActionTimeout;
        BridgeResult result = await CallWithErrorsAsync(game, new BridgeCall(EditorToolName, "editor", parameters, timeout), cancellationToken);
        return ErrorReport.AddTo(ReplyObject(EditorToolName, result.Reply), result.Errors).ToJsonString();
    }

    [McpServerTool(Name = GetEditorStateToolName, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reads the state of the editor of an editor session (run_editor)." + EditorStateNote)]
    public async Task<string> GetEditorStateAsync(
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        GodotSession game = FindEditor(GetEditorStateToolName, session);
        BridgeCall call = new(GetEditorStateToolName, "editor_state", [], EditorReadTimeout);
        BridgeResult result = await CallWithErrorsAsync(game, call, cancellationToken);
        return ErrorReport.AddTo(ReplyObject(GetEditorStateToolName, result.Reply), result.Errors).ToJsonString();
    }

    /// <summary>The bridge's editor params: {action, scene?, path?, discard?}.</summary>
    /// <exception cref="McpException">An unknown action, or open without scene or select_file without path.</exception>
    internal static JsonObject BuildEditorParameters(string action, string? scene, string? path, bool discard)
    {
        if (!EditorActions.Contains(action))
        {
            throw new McpException($"action must be one of {string.Join(", ", EditorActions)}; got '{action}'.");
        }

        if (action == "open" && string.IsNullOrWhiteSpace(scene))
        {
            throw new McpException("open needs scene, the res:// path of the scene to open.");
        }

        if (action == "select_file" && string.IsNullOrWhiteSpace(path))
        {
            throw new McpException("select_file needs path, the res:// file or folder to select.");
        }

        JsonObject parameters = new() { ["action"] = action, ["discard"] = discard };
        if (scene is not null)
        {
            parameters["scene"] = scene;
        }

        if (path is not null)
        {
            parameters["path"] = path;
        }

        return parameters;
    }

    /// <summary>
    /// editor build: the project's C# build, run by the server as run_project's prep runs it under the folder's prep lock, then,
    /// when it wrote a new assembly, the bridge's await_reload, which holds the editor's other commands until the editor's own
    /// watcher has had its time to reload it. The reply is the editor's state with the build's fields.
    /// </summary>
    /// <exception cref="McpException">The project has no C# project, the build failed or was stopped, or the bridge refused.</exception>
    private async Task<string> EditorBuildAsync(GodotSession editor, CancellationToken cancellationToken)
    {
        string projectDir = editor.ProjectDir;
        CsprojLookup lookup = BuildableProject(projectDir);
        string assembly = PrepScan.AssemblyPath(projectDir, lookup.AssemblyName!);
        DateTime? before = WrittenAt(assembly);
        BuildOutcome built = await BuildUnderPrepLockAsync(projectDir, cancellationToken);
        string log = ProjectPrep.BuildLog(projectDir);
        if (built.Errors is not null)
        {
            throw new McpException(RedBuildMessage(built.Errors, log));
        }

        bool reload = built.Build == "built" && WrittenAt(assembly) != before;
        BridgeCall call = reload
            ? new BridgeCall(EditorToolName, "editor", new JsonObject { ["action"] = "await_reload", ["assembly"] = assembly }, EditorActionTimeout)
            : new BridgeCall(EditorToolName, "editor_state", [], EditorReadTimeout);
        BridgeResult result = await CallWithErrorsAsync(editor, call, cancellationToken);
        JsonObject reply = ReplyObject(EditorToolName, result.Reply);
        reply["action"] = "build";
        reply["build"] = built.Build;
        if (built.Build == "built")
        {
            reply["reloadWaited"] = reload;
        }

        reply["log"] = log;
        if (reply["playingScene"]?.GetValue<string>() is { Length: > 0 })
        {
            reply["warning"] = PlayingBuildWarning;
        }

        return ErrorReport.AddTo(reply, result.Errors).ToJsonString();
    }

    /// <summary>The C# project editor build builds in <paramref name="projectDir"/>.</summary>
    /// <exception cref="McpException">The folder has no .csproj, or several and project.godot picks none.</exception>
    internal static CsprojLookup BuildableProject(string projectDir)
    {
        CsprojLookup lookup = PrepScan.FindCsproj(projectDir);
        return lookup.Kind switch
        {
            CsprojKind.Found => lookup,
            CsprojKind.None => throw new McpException($"build needs a C# project; {projectDir} has no .csproj."),
            _ => throw new McpException(lookup.Note ?? $"build cannot tell which C# project in {projectDir} to build."),
        };
    }

    /// <summary>A red editor build's refusal: the editor keeps the assembly it loaded, and the errors and the log say why.</summary>
    internal static string RedBuildMessage(CompilerErrorList errors, string log) =>
        $"the C# build failed, so the editor keeps its previous assembly: {errors.Quote()}; full log: {log}";

    /// <summary>The build alone (<see cref="ProjectPrep.RunBuildAsync"/>) under the folder's prep lock, so no other prep overlaps it.</summary>
    /// <exception cref="McpException">The build hit its ceiling, or the prep wrapper stopped it.</exception>
    private async Task<BuildOutcome> BuildUnderPrepLockAsync(string projectDir, CancellationToken cancellationToken)
    {
        SemaphoreSlim folderLock = sessions.PrepLock(projectDir);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            // A build alone never imports, so it asks for no running session and stops no headless host.
            PrepContext context = new(projectDir, sessions.Logger, () => [], () => { })
            {
                KilledBuildOutcome = "the editor keeps its previous assembly",
            };
            return await ProjectPrep.RunBuildAsync(context, cancellationToken);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
        finally
        {
            folderLock.Release();
        }
    }

    /// <summary>When the file was last written, or null when it does not exist.</summary>
    private static DateTime? WrittenAt(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

    /// <summary>The editor session a tool addresses.</summary>
    /// <exception cref="McpException">The session is not an editor session.</exception>
    private GodotSession FindEditor(string tool, string? session)
    {
        GodotSession game = Find(session);
        if (game.Kind != SessionKind.Editor)
        {
            string kind = SessionRegistry.KindName(game.Kind);
            string article = "aeiou".Contains(kind[0], StringComparison.Ordinal) ? "an" : "a";
            throw new McpException(
                $"{tool} works only in an editor session; session '{game.Name}' is {article} {kind} session. Start one with run_editor."
            );
        }

        return game;
    }

    /// <summary>The bridge's reply, which is always an object.</summary>
    /// <exception cref="McpException">The reply is not an object.</exception>
    private static JsonObject ReplyObject(string tool, JsonNode? reply) =>
        reply as JsonObject ?? throw new McpException($"{tool} failed: the bridge replied {reply?.ToJsonString() ?? "null"}, not an object.");
}
