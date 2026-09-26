using System.ComponentModel;
using System.Text.Json;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>Running and stopping a Godot project, attaching to one started elsewhere, and reading its output.</summary>
[McpServerToolType]
internal sealed class ProjectTools(GodotSession session)
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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "run_project")]
    [Description(
        "Runs a Godot project with the godot-mcp bridge injected through a temporary override.cfg (never project.godot), "
            + "and returns once the bridge has connected. One session at a time: stop_project ends it. "
            + RealPadsNote
    )]
    public async Task<string> RunProjectAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description("A scene to run instead of the main scene, e.g. res://levels/test.tscn.")] string? scene = null,
        [Description("Arguments for the game, passed after --; the game reads them with OS.get_cmdline_user_args().")] string[]? userArgs = null,
        [Description("Arguments for the engine, placed before --, e.g. [\"--resolution\", \"1280x720\"].")] string[]? engineArgs = null,
        [Description("{background, shutOutRealGamepads}, both false when left out.")] RunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        RunOptions chosen = options ?? new RunOptions();
        LaunchRequest request = new(projectPath, scene, engineArgs ?? [], userArgs ?? [], chosen.Background, chosen.ShutOutRealGamepads);
        LaunchResult result = await RunAsync(() => session.LaunchAsync(request, cancellationToken));
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
            + "output), and detach_project, not stop_project, ends the session, leaving the game running. "
            + RealPadsNote
    )]
    public async Task<string> AttachProjectAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description("How long to wait for the game's bridge to connect, 1 to 600 seconds.")] int waitSeconds = 60,
        [Description(ShutOutDescription)] bool shutOutRealGamepads = false,
        CancellationToken cancellationToken = default
    )
    {
        if (waitSeconds is < 1 or > MaxAttachWaitSeconds)
        {
            throw new McpException($"waitSeconds must be 1 to {MaxAttachWaitSeconds}; got {waitSeconds}.");
        }

        var wait = TimeSpan.FromSeconds(waitSeconds);
        AttachResult result = await RunAsync(() => session.AttachAsync(projectPath, wait, shutOutRealGamepads, cancellationToken));
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "detach_project")]
    [Description(
        "Ends a session attach_project started: closes the connection and removes the injected override.cfg. The game keeps "
            + "running; its bridge goes idle."
    )]
    public async Task<string> DetachProjectAsync(CancellationToken cancellationToken = default)
    {
        DetachResult result = await RunAsync(() => session.DetachAsync(cancellationToken));
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "stop_project")]
    [Description(
        "Stops the Godot project run_project started (asks it to quit, kills it after 3 s) and removes the injected "
            + "override.cfg. An attached session is ended with detach_project instead."
    )]
    public async Task<string> StopProjectAsync(CancellationToken cancellationToken = default)
    {
        StopResult result = await RunAsync(() => session.StopAsync(cancellationToken));
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "get_debug_output")]
    [Description(
        "The newest stdout and stderr lines of the current or last run, whether it is still running, and its exit code. "
            + "An attached session has no captured output."
    )]
    public string GetDebugOutput([Description("How many of the newest lines of each stream to return, 1 to 500.")] int limit = 200)
    {
        try
        {
            DebugOutput output = session.GetDebugOutput(Math.Clamp(limit, 1, MaxDebugLines));
            return JsonSerializer.Serialize(output, Json);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
    }

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
