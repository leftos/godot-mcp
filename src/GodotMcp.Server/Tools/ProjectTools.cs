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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "run_project")]
    [Description(
        "Runs a Godot project with the godot-mcp bridge injected through a temporary override.cfg (never project.godot), "
            + "and returns once the bridge has connected. One session at a time: stop_project ends it."
    )]
    public async Task<string> RunProjectAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description("A scene to run instead of the main scene, e.g. res://levels/test.tscn.")] string? scene = null,
        [Description("Arguments for the game, passed after --; the game reads them with OS.get_cmdline_user_args().")] string[]? userArgs = null,
        [Description("Arguments for the engine, placed before --, e.g. [\"--resolution\", \"1280x720\"].")] string[]? engineArgs = null,
        [Description("Park the window off-screen, unfocusable and click-through, so it never takes the user's mouse or keyboard.")]
            bool background = false,
        CancellationToken cancellationToken = default
    )
    {
        LaunchRequest request = new(projectPath, scene, engineArgs ?? [], userArgs ?? [], background);
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
            + "output), and detach_project, not stop_project, ends the session, leaving the game running."
    )]
    public async Task<string> AttachProjectAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description("How long to wait for the game's bridge to connect, 1 to 600 seconds.")] int waitSeconds = 60,
        CancellationToken cancellationToken = default
    )
    {
        if (waitSeconds is < 1 or > MaxAttachWaitSeconds)
        {
            throw new McpException($"waitSeconds must be 1 to {MaxAttachWaitSeconds}; got {waitSeconds}.");
        }

        AttachResult result = await RunAsync(() => session.AttachAsync(projectPath, TimeSpan.FromSeconds(waitSeconds), cancellationToken));
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
