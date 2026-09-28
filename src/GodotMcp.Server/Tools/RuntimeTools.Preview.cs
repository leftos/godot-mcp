using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// preview_scene: what a scene looks like, without a session of the caller's. The registry starts the project quiet on the
/// scene under a session of its own and stops it after; the bridge pauses the game before the scene's first frame, frames a
/// 3D scene that has no current camera, and captures it as take_screenshot does. The tool is a type of its own, apart from
/// <see cref="RuntimeTools"/>, because batch_drive plays every RuntimeTools tool on a session, and a preview has none.
/// </summary>
[McpServerToolType]
internal sealed class PreviewTools(SessionRegistry sessions)
{
    internal const string ToolName = "preview_scene";
    private const string BridgeCommand = "preview";
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);

    // The positional scene Godot runs; any other ending makes it run the main scene without a word (4.7.2 main.cpp L4090-4106).
    private static readonly string[] SceneEndings = [".tscn", ".scn", ".escn", ".res", ".tres"];
    private static readonly HeadlessTools.PathRule SceneRule = new(
        "scene",
        SceneEndings,
        "is not a scene file: Godot runs a scene named on its command line only when the name ends .tscn, .scn, .escn, .res or "
            + ".tres (lowercase), and runs the main scene otherwise."
    );
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = ToolName, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Shows what a scene looks like without running the game: starts the project on that scene alone in a hidden, unfocused "
            + "window, pauses it before the scene's first frame, waits two drawn frames and returns a screenshot, then stops. Use "
            + "it after editing a scene headless. The scene's _ready and the project's autoloads still run; if one of them changes "
            + "scenes, the result's scene says which was shown. A 3D scene with no current camera gets a temporary one framing its "
            + "visible geometry (cameraAdded). Returns {scene, path, width, height, previewPath?, cameraAdded, prep, errors?} and "
            + "the image, as take_screenshot does."
    )]
    public async Task<IEnumerable<ContentBlock>> PreviewSceneAsync(
        [Description("The folder that holds the project's project.godot.")] string projectPath,
        [Description(
            "The scene to show: a res:// path or a path relative to the project folder, inside it, ending .tscn, .scn, .escn, .res "
                + "or .tres, e.g. res://levels/test.tscn."
        )]
            string scene,
        [Description(
            "{resolution, prepare}; when left out, the window has the project's own size and prepare is auto (a stale C# assembly "
                + "is built and missing imports are run first, within the call's 60 s, load-adjusted; the result's prep says what was done)."
        )]
            PreviewOptions? options = null,
        [Description(RuntimeTools.ResponseModeDescription)] string responseMode = "preview",
        [Description(RuntimeTools.PreviewMaxWidthDescription)] int previewMaxWidth = 480,
        CancellationToken cancellationToken = default
    )
    {
        PreviewPlan plan = Plan(projectPath, scene, options ?? new PreviewOptions(), responseMode, previewMaxWidth);
        try
        {
            return await sessions.PreviewAsync(plan.Launch, (session, prep, limit) => CaptureAsync(session, plan, prep, limit), cancellationToken);
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

    /// <summary>The checked arguments: the quiet preview launch on the scene, the response mode and the bridge's capture parameters.</summary>
    /// <exception cref="McpException">
    /// An argument is refused: the mode, previewMaxWidth, prepare, the project, the scene or the resolution.
    /// </exception>
    internal static PreviewPlan Plan(string projectPath, string scene, PreviewOptions options, string responseMode, int previewMaxWidth)
    {
        ScreenshotMode mode = RuntimeTools.ParseMode(responseMode);
        JsonObject parameters = RuntimeTools.BuildScreenshotParameters(mode, crop: null, previewMaxWidth);
        bool prepare = RunOptions.ParsePrepare(options.Prepare);
        string projectDir = ProjectDir(projectPath);
        string resScene = CheckScene(projectDir, scene);
        string[] engineArgs = CheckResolution(options.Resolution) is { } resolution ? ["--resolution", resolution] : [];
        LaunchRequest launch = new(projectDir, resScene, engineArgs, [], Quiet: true, ShutOutRealGamepads: false, Prepare: prepare)
        {
            Preview = true,
        };
        return new PreviewPlan(launch, mode, parameters);
    }

    /// <summary>
    /// The scene as a res:// path, resolved as the headless scene tools resolve theirs, then checked to end as Godot needs:
    /// its ending test is case-sensitive, where the shared resolution's is not.
    /// </summary>
    /// <exception cref="McpException">The scene is empty, outside the project, of another kind, not in the case on disk, or missing.</exception>
    internal static string CheckScene(string projectDir, string scene)
    {
        string resScene = HeadlessTools.ToResPath(projectDir, scene, SceneRule);
        return SceneEndings.Any(ending => resScene.EndsWith(ending, StringComparison.Ordinal))
            ? resScene
            : throw new McpException($"scene '{scene}' {SceneRule.Refusal}");
    }

    /// <summary>The resolution when it is WIDTHxHEIGHT in pixels, as godot-mcp.json's is checked; null when left out.</summary>
    /// <exception cref="McpException">The resolution has another form.</exception>
    internal static string? CheckResolution(string? resolution) =>
        resolution is null || ProjectProfile.ResolutionPattern().IsMatch(resolution)
            ? resolution
            : throw new McpException(
                $"options.resolution is \"{resolution}\"; it must be WIDTHxHEIGHT in pixels, with a lowercase x and no spaces, e.g. \"1280x720\"."
            );

    /// <summary>take_screenshot's result from the bridge's preview command, plus the scene shown, cameraAdded and the prep.</summary>
    private static Task<IEnumerable<ContentBlock>> CaptureAsync(GodotSession session, PreviewPlan plan, PrepResult prep, CancellationToken limit)
    {
        RuntimeTools.BridgeCall call = new(ToolName, BridgeCommand, plan.Parameters, CaptureTimeout);
        return RuntimeTools.CaptureAsync(
            session,
            call,
            plan.Mode,
            (text, reply) =>
            {
                text["scene"] = reply?["scene"]?.DeepClone();
                text["cameraAdded"] = reply?["cameraAdded"]?.DeepClone();
                text["prep"] = JsonSerializer.SerializeToNode(prep, Json);
            },
            limit
        );
    }

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

/// <summary>preview_scene's window size and whether the project is prepared first.</summary>
internal sealed record PreviewOptions(
    [property: Description(
        "The window's size, WIDTHxHEIGHT in pixels (e.g. 1280x720), passed to Godot as --resolution; the project's own size when left out."
    )]
        string? Resolution = null,
    [property: Description(RunOptions.PrepareDescription)] string? Prepare = null
);

/// <summary>A checked preview: the launch, what the result carries besides the text, and the bridge's capture parameters.</summary>
internal sealed record PreviewPlan(LaunchRequest Launch, ScreenshotMode Mode, JsonObject Parameters);
