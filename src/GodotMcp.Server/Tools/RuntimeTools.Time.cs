using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The running game's clock: pausing, resuming and stepping its scene tree, its time scale, and waiting on a condition
/// checked each frame. The bridge (bridge/godot_mcp_time.gd) processes while the tree is paused, so it answers throughout.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const int MaxStepCount = 1000;
    internal const double MaxTimeScale = 100;
    internal const int MaxWaitMs = 120_000;
    private const int StepPreviewMaxWidth = 480;
    private const string ConditionMessage = "condition needs exactly one of: {node, exists}, {node, property, equals}, {node, signal}, {expression}.";
    private static readonly string[] FrameActions = ["pause", "resume", "step", "time_scale"];
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    // A generous allowance per stepped frame on top of FrameTimeout: a frame at 60 fps takes about 17 ms.
    private static readonly TimeSpan PerFrameAllowance = TimeSpan.FromMilliseconds(100);

    // The bridge ends a wait at its timeoutMs and a step at its StepAllowance deadline, each with its own answer; the send
    // waits this much longer for that answer.
    private static readonly TimeSpan WaitReplyAllowance = TimeSpan.FromSeconds(5);

    [McpServerTool(Name = "frame_control", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Controls the running game's clock. pause and resume set SceneTree.paused: nodes whose process_mode lets them "
            + "pause stop, the bridge keeps answering. step advances exactly count whole frames (unit process) or count "
            + "physics ticks (unit physics) from the end of the next drawn frame, and leaves the game paused; with "
            + "options.screenshot it also returns the last frame as take_screenshot does. A step sends the game "
            + "NOTIFICATION_UNPAUSED, then NOTIFICATION_PAUSED; it fails if the game pauses itself before count frames have "
            + "run. A step counts drawn frames, so it is refused while the window cannot draw (minimized) or the game runs in "
            + "low-processor mode, and while another step runs, pause and resume are refused too. A step whose frames stop "
            + "being drawn stops at its deadline (10 s + 100 ms per frame), leaves the game paused and fails. time_scale sets "
            + "Engine.time_scale, which scales process and physics delta. Returns {paused, timeScale, processFrames, "
            + "physicsFrames}, the frames and ticks the step ran (0 for other actions), plus screenshot when captured."
    )]
    public async Task<IEnumerable<ContentBlock>> FrameControlAsync(
        [Description("pause, resume, step or time_scale.")] string action,
        [Description("step only: how many frames (or physics ticks) to advance, 1 to 1000; 1 when left out.")] int? count = null,
        [Description("time_scale only: the new Engine.time_scale, greater than 0 and at most 100; 1 is normal speed.")] double? scale = null,
        [Description("step only: {unit, screenshot}; unit is process (the default) or physics.")] StepOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = BuildFrameParameters(action, count, scale, options);
        TimeSpan timeout = StepAllowance(count ?? 1) + WaitReplyAllowance;
        BridgeCall call = new("frame_control", "frame", parameters, timeout);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        return await ShapeFrameResultAsync(result, cancellationToken);
    }

    [McpServerTool(Name = "wait_for", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Waits in the running game until a condition holds, checking it each frame: a node present or absent, a property "
            + "equal to a value, a signal's next emission, or a Godot Expression returning true. Returns {met, elapsedMs, "
            + "frames, value} (args instead of value for a signal); on timeout {met: false, elapsedMs, frames, last}, the last "
            + "value seen (a signal wait's timeout has no last), which is not an error. While the game is paused only a "
            + "signal wait is accepted. A property the node does not have fails the call once the node is found. An "
            + "expression that does not parse fails the call; one that fails while it runs counts as not met, and its error "
            + "is in errors."
    )]
    public async Task<string> WaitForAsync(
        [Description("Exactly one of {node, exists}, {node, property, equals}, {node, signal}, {expression} (with node optional).")]
            WaitCondition condition,
        [Description("How long to wait, in milliseconds, 1 to 120000.")] int timeoutMs = 10_000,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = BuildWaitParameters(condition, timeoutMs);
        BridgeCall call = new("wait_for", "wait_for", parameters, TimeSpan.FromMilliseconds(timeoutMs) + WaitReplyAllowance);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        JsonObject reply =
            result.Reply?.DeepClone() as JsonObject
            ?? throw new McpException($"The bridge's wait_for reply is not an object: {result.Reply?.ToJsonString() ?? "null"}.");
        return ErrorReport.AddTo(reply, result.Errors).ToJsonString();
    }

    /// <summary>The bridge's frame parameters: {action}, plus {count, unit, screenshot} for step and {scale} for time_scale.</summary>
    /// <exception cref="McpException">An unknown action, or an argument the action does not take or needs and lacks.</exception>
    internal static JsonObject BuildFrameParameters(string action, int? count, double? scale, StepOptions? options)
    {
        if (!FrameActions.Contains(action))
        {
            throw new McpException("action must be one of pause, resume, step, time_scale.");
        }

        JsonObject parameters = new() { ["action"] = action };
        StepOptions stepOptions = options ?? new StepOptions();
        if (action == "step")
        {
            AddStepParameters(parameters, count, stepOptions);
        }
        else
        {
            RefuseStepArguments(count, stepOptions);
        }

        AddScale(parameters, action, scale);
        return parameters;
    }

    /// <summary>The bridge's wait parameters: the condition's fields as given, its kind, and timeoutMs.</summary>
    /// <exception cref="McpException">The condition is not exactly one kind, or timeoutMs is out of range.</exception>
    internal static JsonObject BuildWaitParameters(WaitCondition? condition, int timeoutMs)
    {
        string kind = CheckCondition(condition);
        if (timeoutMs is < 1 or > MaxWaitMs)
        {
            throw new McpException($"timeoutMs must be between 1 and {MaxWaitMs}.");
        }

        JsonObject parameters = JsonSerializer.SerializeToNode(condition, Json)!.AsObject();
        parameters["kind"] = kind;
        parameters["timeoutMs"] = timeoutMs;
        return parameters;
    }

    /// <summary>How long a step of <paramref name="frames"/> may take; the bridge stops the step itself at this deadline.</summary>
    private static TimeSpan StepAllowance(int frames) => FrameTimeout + (PerFrameAllowance * frames);

    private static void AddStepParameters(JsonObject parameters, int? count, StepOptions options)
    {
        int frames = count ?? 1;
        if (frames is < 1 or > MaxStepCount)
        {
            throw new McpException($"count must be between 1 and {MaxStepCount}.");
        }

        string unit = options.Unit ?? "process";
        if (unit is not ("process" or "physics"))
        {
            throw new McpException("unit must be process or physics.");
        }

        parameters["count"] = frames;
        parameters["deadlineMs"] = (long)StepAllowance(frames).TotalMilliseconds;
        parameters["unit"] = unit;
        if (options.Screenshot is true)
        {
            parameters["screenshot"] = true;
            parameters["previewMaxWidth"] = StepPreviewMaxWidth;
        }
    }

    private static void AddScale(JsonObject parameters, string action, double? scale)
    {
        if (action != "time_scale" && scale is not null)
        {
            throw new McpException("scale applies to time_scale only.");
        }

        if (action != "time_scale")
        {
            return;
        }

        parameters["scale"] = scale is > 0 and <= MaxTimeScale
            ? scale
            : throw new McpException("time_scale needs scale, greater than 0 and at most 100.");
    }

    private static void RefuseStepArguments(int? count, StepOptions options)
    {
        string? refused =
            count is not null ? "count"
            : options.Unit is not null ? "unit"
            : options.Screenshot is not null ? "screenshot"
            : null;
        if (refused is not null)
        {
            throw new McpException($"{refused} applies to step only.");
        }
    }

    /// <summary>The condition's one kind: exists, property, signal or expression.</summary>
    private static string CheckCondition(WaitCondition? condition)
    {
        string? kind = condition is null ? null : KindOf(condition);
        return kind is not null && HasCompanions(kind, condition!) ? kind : throw new McpException(ConditionMessage);
    }

    private static string? KindOf(WaitCondition condition)
    {
        (string Kind, bool Given)[] kinds =
        [
            ("exists", condition.Exists is not null),
            ("property", condition.Property is not null || HasEquals(condition)),
            ("signal", condition.Signal is not null),
            ("expression", condition.Expression is not null),
        ];
        string[] given = [.. kinds.Where(kind => kind.Given).Select(kind => kind.Kind)];
        return given.Length == 1 ? given[0] : null;
    }

    private static bool HasCompanions(string kind, WaitCondition condition) =>
        kind switch
        {
            "expression" => true,
            "property" => !string.IsNullOrEmpty(condition.Node) && !string.IsNullOrEmpty(condition.Property) && HasEquals(condition),
            _ => !string.IsNullOrEmpty(condition.Node),
        };

    // A JSON null arrives as a null JsonElement?, the same as a missing equals, so a null to compare with is not supported
    // either way; an explicit Null element is treated alike.
    private static bool HasEquals(WaitCondition condition) => condition.EqualsValue is { ValueKind: not JsonValueKind.Null };

    /// <summary>The frame reply as {paused, timeScale, processFrames, physicsFrames[, screenshot]}, and the preview image when captured.</summary>
    private static async Task<IEnumerable<ContentBlock>> ShapeFrameResultAsync(BridgeResult result, CancellationToken cancellationToken)
    {
        JsonObject reply =
            result.Reply as JsonObject
            ?? throw new McpException($"The bridge's frame reply is not an object: {result.Reply?.ToJsonString() ?? "null"}.");
        JsonObject text = FrameState(reply);
        if (reply["screenshot"] is not JsonObject screenshot)
        {
            return [new TextContentBlock { Text = ErrorReport.AddTo(text, result.Errors).ToJsonString() }];
        }

        ScreenshotFiles files = ReadScreenshotFiles(screenshot);
        text["screenshot"] = JsonSerializer.SerializeToNode(files, Json);
        byte[] image = await ReadImageAsync(files.PreviewPath ?? files.Path, cancellationToken);
        return
        [
            new TextContentBlock { Text = ErrorReport.AddTo(text, result.Errors).ToJsonString() },
            ImageContentBlock.FromBytes(image, "image/png"),
        ];
    }

    private static JsonObject FrameState(JsonObject reply) =>
        new()
        {
            ["paused"] = reply["paused"]?.GetValue<bool>(),
            ["timeScale"] = reply["timeScale"]?.GetValue<double>(),
            ["processFrames"] = ReadInt(reply["processFrames"]) ?? 0,
            ["physicsFrames"] = ReadInt(reply["physicsFrames"]) ?? 0,
        };
}
