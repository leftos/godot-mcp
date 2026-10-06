using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The running game's clock: pausing, resuming and stepping its scene tree, its time scale, waiting on a condition
/// checked each frame, and sampling a property each frame. The bridge (bridge/godot_mcp_time.gd) processes while the tree
/// is paused, so it answers throughout.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const int MaxStepCount = 1000;
    internal const double MaxTimeScale = 100;
    internal const int MaxWaitMs = 120_000;
    internal const int MaxWaitGameMs = 120_000;
    internal const int MaxWaitFrames = 7200;
    internal const int MaxMonitorSamples = 600;
    private const int DefaultWaitMs = 10_000;
    private const int MaxDerivedWaitMs = 600_000;
    private const int CapturePreviewMaxWidth = 480;
    private const string ConditionMessage =
        "condition needs exactly one of: {node, exists}, {node, property, equals}, {node, signal}, {expression}, {uiChanged: true}, "
        + "{gameMs}, {frames}.";
    private static readonly string[] FrameActions = ["pause", "resume", "step", "time_scale"];
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    // A generous allowance per stepped frame on top of FrameTimeout: a frame at 60 fps takes about 17 ms.
    private static readonly TimeSpan PerFrameAllowance = TimeSpan.FromMilliseconds(100);

    // A wait's timeoutMs (in a recording, the StepAllowance of its frames) and a step's or monitor's StepAllowance are its
    // release: once that much load-adjusted time has passed, the server cancels it and the bridge ends it with its own answer
    // (at its backstopMs, 5 x the release, counted in clip time while a recording runs, when the cancel is lost). The send
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
            + "being drawn stops at its deadline (10 s + 100 ms per frame, load-adjusted), leaves the game paused and fails. time_scale sets "
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
        TimeSpan allowance = StepAllowance(count ?? 1);
        BridgeCall call = new("frame_control", "frame", parameters, allowance + WaitReplyAllowance, action == "step" ? allowance : null);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        return await ShapeFrameResultAsync(result, cancellationToken);
    }

    [McpServerTool(Name = "wait_for", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Waits in the running game until a condition holds, checking it each frame: a node present or absent, a property "
            + "equal to a value, a signal's next emission, a Godot Expression returning true, gameMs milliseconds of game time "
            + "(process delta over unpaused frames, so it follows Engine.time_scale) or frames unpaused process frames. Returns "
            + "{met, elapsedMs, frames, value} (args instead of value for a signal; the game ms or frames reached for gameMs and "
            + "frames); on timeout {met: false, elapsedMs, frames, last}, the last "
            + "value seen (a signal wait's timeout has no last), which is not an error. elapsedMs is real time; in a recording "
            + "the result adds clipMs, the clip time waited (frames x 1000 / 60). While the game is paused only a "
            + "signal wait or a check-once wait (timeoutMs 0) is accepted; a pause during a gameMs or frames wait stops its "
            + "count. A property the node does not have fails the call "
            + "once the node is found. An expression that does not parse fails the call; one that fails while it runs counts "
            + "as not met, and its error is in errors. failedChecks {count, error} counts the checks whose expression "
            + "failed; with met: true they came before the met check. uiChanged compares the UI with the snapshot the bridge takes when the "
            + "first input gesture since launch, or since the last met uiChanged wait, starts (later gestures keep it; a met "
            + "wait uses it up, a timeout keeps it), and is refused when no gesture has taken one. options.screenshot: true "
            + "captures the frame the condition was met on as take_screenshot does, for a scene that changes faster than a "
            + "following take_screenshot can catch, adding screenshot to the result (a timed-out wait captures nothing, and a "
            + "frame that was not drawn gives a warning instead). options.call {node, method, args}, with a gameMs or frames wait "
            + "only, calls a method in the frame the count starts, so the count runs from its entry (a coroutine is not "
            + "awaited), and adds call: {value}, its return value as call_method returns it; a refused call, or an error the "
            + "method raises, fails the wait. options.then {call?, timeScale?}, with any condition kind, runs once in the frame "
            + "the condition is met: call is a method the bridge calls there, timeScale sets Engine.time_scale right after it, and "
            + "the result adds then: {frame, call?: {value}, timeScale?}. A refused or failing call fails the wait and leaves "
            + "timeScale unset; a timeout runs nothing."
    )]
    public async Task<IEnumerable<ContentBlock>> WaitForAsync(
        [Description(
            "Exactly one of {node, exists}, {node, property, equals}, {node, signal}, {expression} (with node optional), {uiChanged: true}, "
                + "{gameMs}, {frames}."
        )]
            WaitCondition condition,
        [Description(
            "How long to wait, in milliseconds, 0 to 120000, load-adjusted: under load it waits longer in wall time. Left out: "
                + "10000, gameMs + 10000 for a gameMs wait, 10 s + 100 ms a frame for a frames wait. In a recording "
                + "(run_project options.record) it counts clip time instead, 60 movie frames a second, however slowly the game runs, "
                + "and the result adds clipMs. 0 checks the condition once, now, and works while the game is paused; it is refused "
                + "for a signal, gameMs or frames wait."
        )]
            int? timeoutMs = null,
        [Description("{screenshot, call, then}: screenshot false and no call or then when left out.")] WaitOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = BuildWaitParameters(condition, timeoutMs, options);
        BridgeResult result = await CallWaitAsync(parameters, session, cancellationToken);
        JsonObject reply = WaitReply(result);
        CutMethodValue(reply["call"] as JsonObject);
        CutMethodValue((reply["then"] as JsonObject)?["call"] as JsonObject);
        return await WithCaptureAsync(reply, reply, result.Errors, cancellationToken);
    }

    /// <summary>wait_for without a capture, answered as its JSON text: the form a batch step runs.</summary>
    /// <exception cref="McpException">The condition or timeoutMs is refused, or the call failed.</exception>
    internal async Task<string> WaitForAsync(WaitCondition condition, int? timeoutMs, string? session, CancellationToken cancellationToken)
    {
        JsonObject parameters = BuildWaitParameters(condition, timeoutMs);
        BridgeResult result = await CallWaitAsync(parameters, session, cancellationToken);
        return ErrorReport.AddTo(WaitReply(result), result.Errors).ToJsonString();
    }

    [McpServerTool(Name = "monitor_property", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Samples a node's property in the running game once a frame (or physics tick) for options.samples frames and returns "
            + "how it went: {samples: [{frame, value}], requested, droppedDuplicates, elapsedMs[, pausedAtFrame]}; it stops early, "
            + "with pausedAtFrame, if the game pauses itself. Each sample is taken at the "
            + "start of its frame, before the nodes process it; frame counts from 0 at the first, taken at the next frame. Values "
            + "come as run_script returns them. With changesOnly (the default) a sample equal to the last one kept (numbers within "
            + "1e-6) is dropped and counted in droppedDuplicates; the first is always kept. A missing node or property is refused "
            + "up front; a node freed mid-way samples as null. Refused while the game is paused and while a frame_control step "
            + "or another monitor runs; while it runs, pause, resume and a step are refused. It fails at its deadline (10 s + "
            + "100 ms per sample, load-adjusted) naming the frames it got."
    )]
    public async Task<string> MonitorPropertyAsync(
        [Description("The node: its path (/root/Main/Player), a path under the root (Main/Player) or a name.")] string node,
        [Description("The property, or a path into one as wait_for takes it: position, position:x, modulate:a.")] string property,
        [Description("{samples, unit, changesOnly}: 60 samples, unit process and changesOnly true when left out.")] MonitorOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = BuildMonitorParameters(node, property, options);
        int samples = parameters["samples"]!.GetValue<int>();
        TimeSpan allowance = StepAllowance(samples);
        BridgeCall call = new("monitor_property", "monitor", parameters, allowance + WaitReplyAllowance, allowance);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        JsonObject reply =
            result.Reply?.DeepClone() as JsonObject
            ?? throw new McpException($"The bridge's monitor reply is not an object: {result.Reply?.ToJsonString() ?? "null"}.");
        return ErrorReport.AddTo(reply, result.Errors).ToJsonString();
    }

    /// <summary>The bridge's monitor parameters: {node, property, samples, unit, changesOnly, deadlineMs}.</summary>
    /// <exception cref="McpException">An empty node or property, samples out of range, or a unit other than process or physics.</exception>
    internal static JsonObject BuildMonitorParameters(string node, string property, MonitorOptions? options)
    {
        MonitorOptions checkedOptions = options ?? new MonitorOptions();
        CheckNode(node);
        CheckName(property, "property", "Pass a property name as inspect_node lists it, or a path into one such as position:x.");
        if (checkedOptions.Samples is < 1 or > MaxMonitorSamples)
        {
            throw new McpException($"samples must be between 1 and {MaxMonitorSamples}.");
        }

        if (checkedOptions.Unit is not ("process" or "physics"))
        {
            throw new McpException("unit must be process or physics.");
        }

        return new JsonObject
        {
            ["node"] = node,
            ["property"] = property,
            ["samples"] = checkedOptions.Samples,
            ["unit"] = checkedOptions.Unit,
            ["changesOnly"] = checkedOptions.ChangesOnly,
            ["deadlineMs"] = (long)StepAllowance(checkedOptions.Samples).TotalMilliseconds,
        };
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

    /// <summary>The bridge's wait parameters: the condition's fields as given, its kind, and the timeoutMs it runs under
    /// (<see cref="WaitTimeoutMs"/>).</summary>
    /// <exception cref="McpException">The condition is not exactly one kind, gameMs or frames is out of range, timeoutMs is
    /// out of range, or 0 for a signal, gameMs or frames wait.</exception>
    internal static JsonObject BuildWaitParameters(WaitCondition? condition, int? timeoutMs)
    {
        string kind = CheckCondition(condition);
        CheckGameTime(condition!);
        if (timeoutMs is < 0 or > MaxWaitMs)
        {
            throw new McpException($"timeoutMs must be between 0 and {MaxWaitMs}.");
        }

        if (timeoutMs == 0 && kind is "signal" or "gameMs" or "frames")
        {
            throw new McpException($"timeoutMs 0 checks once, which a {kind} wait cannot do; give it a timeout.");
        }

        JsonObject parameters = JsonSerializer.SerializeToNode(condition, Json)!.AsObject();
        parameters["kind"] = kind;
        parameters["timeoutMs"] = WaitTimeoutMs(condition!, timeoutMs);
        return parameters;
    }

    /// <summary>The bridge's wait parameters as <see cref="BuildWaitParameters(WaitCondition?, int?)"/> builds them, plus
    /// {screenshot, previewMaxWidth} when <paramref name="options"/> asks for the capture, call when it gives one, and then
    /// when it gives one.</summary>
    /// <exception cref="McpException">The condition, gameMs, frames or timeoutMs is refused as the other form refuses them, a
    /// call is given to a wait other than gameMs or frames, then is empty or its timeScale is out of range, or a node or
    /// method is empty.</exception>
    internal static JsonObject BuildWaitParameters(WaitCondition? condition, int? timeoutMs, WaitOptions? options)
    {
        JsonObject parameters = BuildWaitParameters(condition, timeoutMs);
        AddScreenshot(parameters, options?.Screenshot);
        if (options?.Call is { } call)
        {
            string kind = parameters["kind"]!.GetValue<string>();
            parameters["call"] = kind is "gameMs" or "frames"
                ? MethodCallParameters(call)
                : throw new McpException($"options.call is taken only by a gameMs or frames wait; this condition is {kind}.");
        }

        if (options?.Then is { } then)
        {
            parameters["then"] = ThenParameters(then);
        }

        return parameters;
    }

    /// <summary>The bridge's then parameters {call?, timeScale?}, at least one.</summary>
    /// <exception cref="McpException">Neither call nor timeScale is given, timeScale is not above 0 and at most
    /// <see cref="MaxTimeScale"/>, or the call's node or method is empty.</exception>
    internal static JsonObject ThenParameters(WaitThen then)
    {
        if (then.Call is null && then.TimeScale is null)
        {
            throw new McpException("options.then needs call, timeScale or both.");
        }

        JsonObject parameters = [];
        if (then.Call is { } call)
        {
            parameters["call"] = MethodCallParameters(call);
        }

        if (then.TimeScale is { } timeScale)
        {
            parameters["timeScale"] = timeScale is > 0 and <= MaxTimeScale
                ? timeScale
                : throw new McpException(
                    $"options.then.timeScale must be greater than 0 and at most 100; got " + $"{timeScale.ToString(CultureInfo.InvariantCulture)}."
                );
        }

        return parameters;
    }

    /// <summary>
    /// The timeout a wait runs under: <paramref name="timeoutMs"/> when given; else gameMs + 10 s for a gameMs wait, the step
    /// allowance of its frames for a frames wait (either at most 600 s), and 10 s for any other.
    /// </summary>
    internal static int WaitTimeoutMs(WaitCondition condition, int? timeoutMs) =>
        timeoutMs
        ?? condition switch
        {
            { GameMs: int gameMs } => (int)Math.Min((long)gameMs + DefaultWaitMs, MaxDerivedWaitMs),
            { Frames: int frames } => (int)Math.Min(StepAllowance(frames).TotalMilliseconds, MaxDerivedWaitMs),
            _ => DefaultWaitMs,
        };

    private static void CheckGameTime(WaitCondition condition)
    {
        if (condition.GameMs is < 1 or > MaxWaitGameMs)
        {
            throw new McpException($"gameMs must be between 1 and {MaxWaitGameMs}; got {condition.GameMs}.");
        }

        if (condition.Frames is < 1 or > MaxWaitFrames)
        {
            throw new McpException($"frames must be between 1 and {MaxWaitFrames}; got {condition.Frames}.");
        }
    }

    private async Task<BridgeResult> CallWaitAsync(JsonObject parameters, string? session, CancellationToken cancellationToken)
    {
        GodotSession target = Find(session);
        int timeoutMs = parameters["timeoutMs"]!.GetValue<int>();
        TimeSpan release = WaitRelease(parameters, timeoutMs, target.ActiveRecording is not null);
        BridgeCall call = new("wait_for", "wait_for", parameters, release + WaitReplyAllowance, timeoutMs > 0 ? release : null);
        return await CallWithErrorsAsync(target, call, cancellationToken);
    }

    /// <summary>
    /// A wait's release: timeoutMs of load-adjusted time; in a recording, timeoutMs of clip time instead, which it adds to
    /// <paramref name="parameters"/> as timeoutFrames (the bridge counts those frames), released at the step rule's allowance.
    /// </summary>
    internal static TimeSpan WaitRelease(JsonObject parameters, int timeoutMs, bool recording)
    {
        if (!recording || timeoutMs == 0)
        {
            return TimeSpan.FromMilliseconds(timeoutMs);
        }

        int frames = ClipFrames(timeoutMs);
        parameters["timeoutFrames"] = frames;
        return StepAllowance(frames);
    }

    /// <summary>The movie frames <paramref name="milliseconds"/> of clip time take in a recording, rounded up.</summary>
    internal static int ClipFrames(long milliseconds) => (int)(((milliseconds * GodotCommandLine.MovieFramesPerSecond) + 999) / 1000);

    private static JsonObject WaitReply(BridgeResult result) =>
        result.Reply?.DeepClone() as JsonObject
        ?? throw new McpException($"The bridge's wait_for reply is not an object: {result.Reply?.ToJsonString() ?? "null"}.");

    /// <summary>How long a step of <paramref name="frames"/> may take; the bridge stops the step itself at this deadline.</summary>
    internal static TimeSpan StepAllowance(int frames) => FrameTimeout + (PerFrameAllowance * frames);

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
        AddScreenshot(parameters, options.Screenshot);
    }

    private static void AddScreenshot(JsonObject parameters, bool? screenshot)
    {
        if (screenshot is true)
        {
            parameters["screenshot"] = true;
            parameters["previewMaxWidth"] = CapturePreviewMaxWidth;
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

    /// <summary>The condition's one kind: exists, property, signal, expression, uiChanged, gameMs or frames.</summary>
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
            ("uiChanged", condition.UiChanged is not null),
            ("gameMs", condition.GameMs is not null),
            ("frames", condition.Frames is not null),
        ];
        string[] given = [.. kinds.Where(kind => kind.Given).Select(kind => kind.Kind)];
        return given.Length == 1 ? given[0] : null;
    }

    private static bool HasCompanions(string kind, WaitCondition condition) =>
        kind switch
        {
            "expression" => true,
            "uiChanged" => IsUiChangedAlone(condition),
            "gameMs" or "frames" => condition.Node is null,
            "property" => !string.IsNullOrEmpty(condition.Node) && !string.IsNullOrEmpty(condition.Property) && HasEquals(condition),
            _ => !string.IsNullOrEmpty(condition.Node),
        };

    // Only true is a kind, and it takes no node: the other kinds' fields already make a second kind.
    private static bool IsUiChangedAlone(WaitCondition condition) => condition.UiChanged is true && condition.Node is null;

    // A JSON null arrives as a null JsonElement?, the same as a missing equals, so a null to compare with is not supported
    // either way; an explicit Null element is treated alike.
    private static bool HasEquals(WaitCondition condition) => condition.EqualsValue is { ValueKind: not JsonValueKind.Null };

    /// <summary>The frame reply as {paused, timeScale, processFrames, physicsFrames[, screenshot]}, and the preview image when captured.</summary>
    private static async Task<IEnumerable<ContentBlock>> ShapeFrameResultAsync(BridgeResult result, CancellationToken cancellationToken)
    {
        JsonObject reply =
            result.Reply as JsonObject
            ?? throw new McpException($"The bridge's frame reply is not an object: {result.Reply?.ToJsonString() ?? "null"}.");
        return await WithCaptureAsync(FrameState(reply), reply, result.Errors, cancellationToken);
    }

    /// <summary>
    /// <paramref name="text"/> with the errors as one text block; when <paramref name="reply"/> carries a screenshot, text's
    /// screenshot is its saved files as take_screenshot names them, and the preview image follows.
    /// </summary>
    private static async Task<IEnumerable<ContentBlock>> WithCaptureAsync(
        JsonObject text,
        JsonObject reply,
        IReadOnlyList<ErrorEntry> errors,
        CancellationToken cancellationToken
    )
    {
        if (reply["screenshot"] is not JsonObject screenshot)
        {
            return [new TextContentBlock { Text = ErrorReport.AddTo(text, errors).ToJsonString() }];
        }

        ScreenshotFiles files = ReadScreenshotFiles(screenshot);
        text["screenshot"] = JsonSerializer.SerializeToNode(files, Json);
        byte[] image = await ReadImageAsync(files.PreviewPath ?? files.Path, cancellationToken);
        return [new TextContentBlock { Text = ErrorReport.AddTo(text, errors).ToJsonString() }, ImageContentBlock.FromBytes(image, "image/png")];
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
