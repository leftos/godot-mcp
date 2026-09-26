using System.ComponentModel;
using System.Text.Json;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>Running and stopping a Godot project, and reading its output.</summary>
[McpServerToolType]
internal sealed class ProjectTools(GodotSession session)
{
    private const int MaxDebugLines = GodotRun.OutputCapacity;
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

    [McpServerTool(Name = "stop_project")]
    [Description("Stops the running Godot project (asks it to quit, kills it after 3 s) and removes the injected override.cfg.")]
    public async Task<string> StopProjectAsync(CancellationToken cancellationToken = default)
    {
        StopResult result = await RunAsync(() => session.StopAsync(cancellationToken));
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "get_debug_output")]
    [Description("The newest stdout and stderr lines of the current or last run, whether it is still running, and its exit code.")]
    public string GetDebugOutput([Description("How many of the newest lines of each stream to return, 1 to 500.")] int limit = 200)
    {
        DebugOutput output = session.GetDebugOutput(Math.Clamp(limit, 1, MaxDebugLines));
        return JsonSerializer.Serialize(output, Json);
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
