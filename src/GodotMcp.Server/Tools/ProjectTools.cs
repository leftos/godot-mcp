using System.ComponentModel;
using System.Text.Json;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>Running and stopping a Godot project, attaching to one started elsewhere, and reading its output.</summary>
[McpServerToolType]
internal sealed class ProjectTools(SessionRegistry sessions)
{
    private const int MaxDebugLines = GodotRun.OutputCapacity;
    private const int MaxAttachWaitSeconds = 600;

    /// <summary>What run_project's and attach_project's descriptions say about the machine's own gamepads.</summary>
    internal const string RealPadsNote =
        "By default the machine's real gamepads stay live and feed the same actions and ui_* bindings as the gamepad tools' "
        + "injected pad; shutOutRealGamepads keeps them out.";

    /// <summary>The shutOutRealGamepads option, for run_project's options and attach_project.</summary>
    internal const string ShutOutDescription =
        "Keep the machine's real gamepads out of the game, so only the gamepad tools' injected pad reaches it: the bridge "
        + "marks the game unfocused, at startup and again after every real focus change, and Godot then drops real pad input. "
        + "Costs: the game's nodes receive application focus-out notifications (a game that pauses or mutes on focus loss "
        + "will), held injected keys are released on each real focus change, and held injected pad buttons and axes are sent "
        + "again after it, so their actions fire again. Default false.";

    /// <summary>The session parameter of every tool that addresses an existing session.</summary>
    internal const string SessionDescription = "The session's name; may be omitted while only one session is live, or only one exists.";

    /// <summary>The session parameter of run_project's options and attach_project, which name a new session.</summary>
    internal const string NewSessionDescription =
        "The new session's name, 1 to 64 characters of letters, digits, '.', '_' and '-'; the project folder's name when "
        + "left out. A live name is refused; a new name starts another session alongside the live ones.";

    /// <summary>What run_project's and attach_project's descriptions say about running several sessions.</summary>
    internal const string SessionsNote =
        " Several sessions can be live at once, on one project folder or several, each under its own name: a live name is "
        + "refused, a new name starts another session, and a name whose session has ended is reused. The other tools take "
        + "session to pick one, and may omit it while only one session exists; list_sessions lists them.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "run_project")]
    [Description(
        "Runs a Godot project with the godot-mcp bridge injected through a temporary override.cfg (never project.godot), "
            + "and returns once the bridge has connected; stop_project ends the session."
            + SessionsNote
            + " "
            + RealPadsNote
    )]
    public async Task<string> RunProjectAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description("A scene to run instead of the main scene, e.g. res://levels/test.tscn.")] string? scene = null,
        [Description("Arguments for the game, passed after --; the game reads them with OS.get_cmdline_user_args().")] string[]? userArgs = null,
        [Description("Arguments for the engine, placed before --, e.g. [\"--resolution\", \"1280x720\"].")] string[]? engineArgs = null,
        [Description(
            "{background, shutOutRealGamepads, session}; the flags are false and the session is named after the project folder when left out."
        )]
            RunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        RunOptions chosen = options ?? new RunOptions();
        LaunchRequest request = new(projectPath, scene, engineArgs ?? [], userArgs ?? [], chosen.Background, chosen.ShutOutRealGamepads);
        LaunchResult result = await RunAsync(() => sessions.LaunchAsync(request, chosen.Session, cancellationToken));
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "attach_project")]
    [Description(
        "Attaches to a Godot game that run_project does not start (a second client, a --server run, a smoke script, the "
            + "editor's Play button): injects the bridge through a temporary override.cfg plus a one-use attach file under "
            + ".godot/godot-mcp/, then blocks until a game started on the project connects, or waitSeconds pass. Only a game "
            + "that starts after the files are written attaches, so start the launch in the background, delayed a second or "
            + "two (e.g. Start-Sleep 2; godot --path <project>), just before this call, or launch within waitSeconds after it. "
            + "The runtime tools then work as with run_project; get_debug_output does not (the game's own console has its "
            + "output), and detach_project, not stop_project, ends the session, leaving the game running."
            + SessionsNote
            + " "
            + RealPadsNote
    )]
    public async Task<string> AttachProjectAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description("How long to wait for the game's bridge to connect, 1 to 600 seconds.")] int waitSeconds = 60,
        [Description(ShutOutDescription)] bool shutOutRealGamepads = false,
        [Description(NewSessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        if (waitSeconds is < 1 or > MaxAttachWaitSeconds)
        {
            throw new McpException($"waitSeconds must be 1 to {MaxAttachWaitSeconds}; got {waitSeconds}.");
        }

        var wait = TimeSpan.FromSeconds(waitSeconds);
        AttachResult result = await RunAsync(() => sessions.AttachAsync(projectPath, session, wait, shutOutRealGamepads, cancellationToken));
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "detach_project")]
    [Description(
        "Ends a session attach_project started: closes the connection and removes the injected override.cfg, unless another "
            + "live session uses the project folder. The game keeps running; its bridge goes idle."
    )]
    public async Task<string> DetachProjectAsync(
        [Description(SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        DetachResult result = await RunAsync(() => sessions.DetachAsync(session, cancellationToken));
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "stop_project")]
    [Description(
        "Stops a session run_project started (asks the game to quit, kills it after 3 s) and removes the injected "
            + "override.cfg, unless another live session uses the project folder. An attached session is ended with "
            + "detach_project instead."
    )]
    public async Task<string> StopProjectAsync(
        [Description(SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        StopResult result = await RunAsync(() => sessions.StopAsync(session, cancellationToken));
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "get_debug_output")]
    [Description(
        "The newest stdout and stderr lines of a session's run (the current or the last one under its name), whether it is "
            + "still running, and its exit code. Lines are numbered from 1 across everything a stream has printed; "
            + "stdoutFirstLine and stderrFirstLine give the number of the first line returned, to pass as before for the lines "
            + "ahead of it (the server keeps the last 500 of each stream). A line longer than 1000 characters is cut, ending "
            + "\"… (+N chars)\". An attached session has no captured output."
    )]
    public string GetDebugOutput(
        [Description("How many lines of each stream to return, 1 to 500.")] int limit = 100,
        [Description("Return the lines just before this line number (in each stream) instead of the newest.")] long? before = null,
        [Description(SessionDescription)] string? session = null
    )
    {
        if (before < 1)
        {
            throw new McpException($"before must be a line number, 1 or more; got {before}.");
        }

        try
        {
            DebugOutput output = sessions.GetDebugOutput(session, Math.Clamp(limit, 1, MaxDebugLines), before);
            return JsonSerializer.Serialize(output, Json);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
    }

    [McpServerTool(Name = "list_sessions")]
    [Description(
        "Lists the server's sessions, ordered by name: each one's name, project folder, kind (run or attach), whether it is "
            + "live, and its process id (null for an attached game). A session stays listed after its run ends, until its name "
            + "is reused; detach_project removes an attached one."
    )]
    public string ListSessions() => JsonSerializer.Serialize(new SessionList(sessions.List()), Json);

    private static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
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
}
