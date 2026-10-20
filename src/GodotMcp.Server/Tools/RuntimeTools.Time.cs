using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
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
    internal const int MaxWaitGameMs = 120_000;
    internal const int MaxWaitFrames = 7200;
    internal const int MaxStableFrames = 600;
    private const int DefaultWaitMs = 10_000;
    private const int MaxDerivedWaitMs = 600_000;
    private const int CapturePreviewMaxWidth = 480;
    private const string ConditionMessage =
        "condition needs exactly one of: {node, exists}, {node, property, equals}, {node, signal}, {expression}, {uiChanged: true | \"now\"}, "
        + "{gameMs}, {frames}, {text}, {node, stable}, {node, state, equals}, {output} (with stream and after optional).";
    private const string UntilFramesRefusal =
        "frame_control step's until takes any wait_for condition but frames: count is the most frames a step runs.";
    private const string UntilOutputRefusal =
        "frame_control step's until takes any wait_for condition but output: a step waits on the game's frames, not its printed lines.";
    private const string EmptyThenRefusal = "options.then needs call, timeScale, pause or a mix.";
    private static readonly string[] FrameActions = ["pause", "resume", "step", "time_scale"];

    // Each condition kind: its name, whether a condition gives the kind's own field, and whether it gives the fields that
    // kind needs with it and none it refuses. A condition is of a kind when exactly one row's field is given.
    private static readonly ConditionKind[] ConditionKinds =
    [
        new("exists", IsExistsGiven, HasNode),
        new("property", IsPropertyGiven, HasPropertyCompanions),
        new("signal", condition => condition.Signal is not null, HasNode),
        new("expression", condition => condition.Expression is not null, _ => true),
        new("uiChanged", condition => condition.UiChanged is not null, IsUiChangedAlone),
        new("gameMs", condition => condition.GameMs is not null, condition => condition.Node is null),
        new("frames", condition => condition.Frames is not null, condition => condition.Node is null),
        new("text", condition => condition.Text is not null, _ => true),
        new("settled", condition => condition.Stable is not null, HasNode),
        new("state", condition => condition.State is not null, HasStateCompanions),
        new("output", condition => condition.Output is not null, HasOutputCompanions),
    ];

    // What a step's reply carries beyond its counts: the frame it stopped on, its until's answer as wait_for gives one, and
    // what its riding input left: the events not sent, what is still held and the real-pad warning.
    private static readonly string[] StepReportKeys =
    [
        "frame",
        "met",
        "value",
        "last",
        "args",
        "failedChecks",
        "mutedErrors",
        "inputNotSent",
        "held",
        "warning",
    ];
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    // A generous allowance per stepped frame on top of FrameTimeout: a frame at 60 fps takes about 17 ms.
    private static readonly TimeSpan PerFrameAllowance = TimeSpan.FromMilliseconds(100);

    // A wait's timeoutMs (in a recording, the StepAllowance of its frames) and a step's StepAllowance are its
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
            + "being drawn stops at its deadline (10 s + 100 ms per frame, load-adjusted), leaves the game paused and fails. "
            + "options.until, a wait_for condition of any kind but frames, stops the step on the first stepped frame that meets "
            + "it, checked after each frame (never before the first) at its draw, or as the next tick starts with unit physics, "
            + "and leaves the game paused on that frame; count is then the most frames, 1000 when left out. options.input sends "
            + "simulate_input's raw events inside chosen stepped frames (process unit only), under the session's input gate as an "
            + "input tool plays; the result adds inputNotSent, held and warning as its description says. time_scale sets "
            + "Engine.time_scale, which scales process and physics delta. Returns {paused, timeScale, processFrames, "
            + "physicsFrames}, the frames and ticks the step ran (0 for other actions), plus a step's frame "
            + "(Engine.get_process_frames() where it stopped), screenshot when captured, and with until met plus value (args "
            + "for a signal) or last as wait_for reports them: never met, it runs count frames and answers met: false. Like "
            + "wait_for's, an expression until adds failedChecks and mutedErrors when it has them."
    )]
    public async Task<IEnumerable<ContentBlock>> FrameControlAsync(
        [Description("pause, resume, step or time_scale.")] string action,
        [Description("step only: how many frames (or physics ticks) to advance, 1 to 1000; 1 when left out, 1000 with options.until.")]
            int? count = null,
        [Description("time_scale only: the new Engine.time_scale, greater than 0 and at most 100; 1 is normal speed.")] double? scale = null,
        [Description("step only: {unit, screenshot, until, input}; unit is process (the default) or physics.")] StepOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = BuildFrameParameters(action, count, scale, options);
        TimeSpan allowance = StepAllowance(parameters["count"]?.GetValue<int>() ?? 1);
        BridgeCall call = new("frame_control", "frame", parameters, allowance + WaitReplyAllowance, action == "step" ? allowance : null);
        GodotSession target = Find(session);
        AddConditionStateHelper(parameters["until"] as JsonObject, target);
        BridgeResult result = parameters.ContainsKey("input")
            ? await CallUnderInputGateAsync(target, call, cancellationToken)
            : await CallWithErrorsAsync(target, call, cancellationToken);
        return await ShapeFrameResultAsync(result, cancellationToken);
    }

    /// <summary>A step with riding input, run once any earlier input call on the session has finished, as an input tool
    /// plays.</summary>
    private static async Task<BridgeResult> CallUnderInputGateAsync(GodotSession target, BridgeCall call, CancellationToken cancellationToken)
    {
        await target.InputGate.WaitAsync(cancellationToken);
        try
        {
            return await CallWithErrorsAsync(target, call, cancellationToken);
        }
        finally
        {
            target.InputGate.Release();
        }
    }

    [McpServerTool(Name = "wait_for", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Waits in the running game until a condition holds, checking it each frame: a node present or absent, a property "
            + "equal to a value, a signal's next emission, a Godot Expression returning true, gameMs milliseconds of game time "
            + "(process delta over unpaused frames, so it follows Engine.time_scale), frames unpaused process frames, or a "
            + "visible Control showing a text, matched as a click's {text} target is, or none showing it (exists: false); a text "
            + "shown by several Controls fails the wait with their list, so narrow it with under. {node, stable: N} waits "
            + "until the node's global rect (a Control), global_transform (a Node2D or Node3D) or the property named holds "
            + "unchanged for N process frames, so a press can wait for a layout to settle; met, its value is {frames, value}. "
            + "{node, state, equals} reads the node's state as get_game_state does each frame (the node must be in the mcp_state "
            + "group; its state method runs at every check) and waits until the value at the key path state (seats[0].hp, or $ for "
            + "the whole state) equals equals, compared as a property is; a path the state lacks is not met, and a node with no "
            + "state to read fails the call. "
            + "An output condition waits for a line the run prints: {output} is a regular expression matched against stdout and/or "
            + "stderr, with stream (stdout, stderr, or any by default) and after (a line number to scan after). The server answers "
            + "it from the captured output, so it works while the game is paused; met, its value is {line, stream, lineNumber, "
            + "groups}. "
            + "Returns "
            + "{met, elapsedMs, value}, with frames as well except for an output wait (args instead of value for a signal; the "
            + "game ms or frames reached for gameMs and frames); on timeout {met: false, elapsedMs, last}, with frames as well "
            + "except for an output wait, the last value seen (a signal wait's timeout has no last), which is not an error. "
            + "elapsedMs is real time; in a recording "
            + "the result adds clipMs, the clip time waited (frames x 1000 / 60). While the game is paused only a "
            + "signal wait, an output wait or a check-once wait (timeoutMs 0) is accepted; a pause during a gameMs or frames wait stops its "
            + "count. A property the node does not have fails the call "
            + "once the node is found. An expression that does not parse fails the call; one that fails while it runs counts "
            + "as not met, and its error is in errors. failedChecks {count, error} counts the checks whose expression "
            + "failed; with met: true they came before the met check. An engine error a native method the expression calls "
            + "raises on the main thread (get_child(0) on a node with no children: Expression evaluates both sides of and) "
            + "is kept out of errors and counted in mutedErrors {count, error}, error the first one's text, met or not. "
            + "uiChanged compares the UI with the snapshot the bridge takes when the first input gesture since launch, or "
            + "since the last met uiChanged wait, starts (later gestures keep it; a met "
            + "wait uses it up, a timeout keeps it), and is refused when no gesture has taken one. {uiChanged: \"now\"} takes its "
            + "snapshot when the wait starts instead, replacing one a gesture left, for a wait in a parallel group beside an input "
            + "in another session. options.screenshot: true "
            + "captures the frame the condition was met on as take_screenshot does, for a scene that changes faster than a "
            + "following take_screenshot can catch, adding screenshot to the result (a timed-out wait captures nothing, and a "
            + "frame that was not drawn gives a warning instead). options.call {node, method, args}, or {tool, args} for a game "
            + "tool, with any wait but a signal or uiChanged one, calls it in the frame the checks start: a gameMs or frames count "
            + "runs from its entry, and another wait's first check follows it in that frame, its frames and elapsedMs counted "
            + "from there (a coroutine or a Task is not awaited). It adds call: {value}, its return value as call_method returns "
            + "it, with tool and type for a game tool; a refused call, or an error it raises, fails the wait. options.then {call?, "
            + "timeScale?, pause?}, with any condition kind, runs once in the frame the condition is met: call is a method the bridge "
            + "calls there, timeScale sets Engine.time_scale right after it, and pause: true leaves the game paused on that frame, as "
            + "frame_control pause does, so a later read sees the state the condition did. A plain wait pauses before the met "
            + "frame's _process, after its physics, so values read are the ones the wait met, but anything the game updates in "
            + "_process (a shader, an animation) may still show the previous frame; screenshot: true pauses after the met frame is "
            + "drawn, so a picture matches the values. The result adds then: {frame, call?: "
            + "{value}, timeScale?, paused?}. A refused or failing call fails the wait and leaves timeScale unset and the game "
            + "running; a timeout runs nothing. options.edge: true, with an exists, property, expression, text or state wait and a "
            + "timeout above 0, meets it only on a check that finds the condition true after one that found it false, so a "
            + "condition already true waits for its next rise (for a text wait with exists: false, the text's next going)."
    )]
    public async Task<IEnumerable<ContentBlock>> WaitForAsync(
        [Description(
            "Exactly one of {node, exists}, {node, property, equals}, {node, signal}, {expression} (with node optional), "
                + "{uiChanged: true | \"now\"}, "
                + "{gameMs}, {frames}, {text} (with under and exists optional), {node, stable} (with property optional), "
                + "{node, state, equals}, {output} (with stream and after optional)."
        )]
            WaitCondition condition,
        [Description(
            "How long to wait, in milliseconds, 0 to 120000, load-adjusted: under load it waits longer in wall time. Left out: "
                + "10000, gameMs + 10000 for a gameMs wait, 10 s + 100 ms a frame for a frames wait, and 10 s + 100 ms for each of "
                + "the stable + 1 frames a settled wait reads, so it grows with stable. In a recording "
                + "(run_project options.record) it counts clip time instead, 60 movie frames a second, however slowly the game runs, "
                + "and the result adds clipMs. 0 checks the condition once, now (after options.call, in the next frame), and works "
                + "while the game is paused; it is refused for a signal, gameMs, frames or settled wait, and for an output wait "
                + "without after."
        )]
            int? timeoutMs = null,
        [Description("{screenshot, call, then, edge}: screenshot and edge false and no call or then when left out.")] WaitOptions? options = null,
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

    /// <summary>The bridge's frame parameters: {action}, plus {count, unit, screenshot, until, input} for step and {scale} for
    /// time_scale.</summary>
    /// <exception cref="McpException">An unknown action, an argument the action does not take or needs and lacks, or an until
    /// condition wait_for would refuse or of the frames kind.</exception>
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
    /// out of range, or 0 for a signal, gameMs, frames or settled wait, or for an output wait without after.</exception>
    internal static JsonObject BuildWaitParameters(WaitCondition? condition, int? timeoutMs)
    {
        string kind = CheckCondition(condition);
        CheckGameTime(condition!);
        if (timeoutMs is < 0 or > MaxWaitMs)
        {
            throw new McpException($"timeoutMs must be between 0 and {MaxWaitMs}.");
        }

        if (timeoutMs == 0)
        {
            RefuseCheckOnce(kind, condition!);
        }

        JsonObject parameters = ConditionParameters(condition!, kind);
        parameters["timeoutMs"] = WaitTimeoutMs(condition!, timeoutMs);
        return parameters;
    }

    /// <summary>Refuses timeoutMs 0 for a wait that cannot answer on one check.</summary>
    /// <exception cref="McpException">The kind cannot meet on one check, or an output wait gave no after.</exception>
    private static void RefuseCheckOnce(string kind, WaitCondition condition)
    {
        switch (kind)
        {
            case "signal" or "gameMs" or "frames":
                throw new McpException($"timeoutMs 0 checks once, which a {kind} wait cannot do; give it a timeout.");
            case "settled":
                throw new McpException("timeoutMs 0 checks once, which a settled wait cannot meet; give it a timeout.");
            case "uiChanged" when condition.UiChanged is { ValueKind: JsonValueKind.String }:
                throw new McpException("timeoutMs 0 checks once, which a {uiChanged: \"now\"} wait cannot meet; give it a timeout.");
            case "output" when condition.After is null:
                throw new McpException("timeoutMs 0 checks once, which an output wait needs after for: without it there is nothing new to check.");
        }
    }

    /// <summary>A condition as the bridge reads it: its fields as given, and its kind.</summary>
    private static JsonObject ConditionParameters(WaitCondition condition, string kind)
    {
        JsonObject parameters = JsonSerializer.SerializeToNode(condition, Json)!.AsObject();
        parameters["kind"] = kind;
        return parameters;
    }

    /// <summary>A step's until as the bridge reads it, checked as wait_for checks its condition.</summary>
    /// <exception cref="McpException">The condition names frames, is not exactly one kind otherwise, or its gameMs is out of
    /// range.</exception>
    private static JsonObject UntilParameters(WaitCondition until)
    {
        if (until.Output is not null || until.Stream is not null || until.After is not null)
        {
            throw new McpException(UntilOutputRefusal);
        }

        if (until.Frames is not null)
        {
            throw new McpException(UntilFramesRefusal);
        }

        string kind = CheckCondition(until);
        CheckGameTime(until);
        return ConditionParameters(until, kind);
    }

    /// <summary>The bridge's wait parameters as <see cref="BuildWaitParameters(WaitCondition?, int?)"/> builds them, plus
    /// {screenshot, previewMaxWidth} when <paramref name="options"/> asks for the capture, call when it gives one, and then
    /// when it gives one.</summary>
    /// <exception cref="McpException">The condition, gameMs, frames or timeoutMs is refused as the other form refuses them, a
    /// call is given to a signal or uiChanged wait, then is empty or its timeScale is out of range, or a call or
    /// then.call gives both forms or neither, args of the other form's shape, or an empty node, method or tool name.</exception>
    internal static JsonObject BuildWaitParameters(WaitCondition? condition, int? timeoutMs, WaitOptions? options)
    {
        JsonObject parameters = BuildWaitParameters(condition, timeoutMs);
        if (options is not null)
        {
            AddWaitOptions(parameters, options);
        }

        return parameters;
    }

    private static void AddWaitOptions(JsonObject parameters, WaitOptions options)
    {
        if (parameters["kind"]!.GetValue<string>() == "output")
        {
            RefuseOutputOptions(options);
        }

        AddScreenshot(parameters, options.Screenshot);
        if (options.Call is { } call)
        {
            AddWaitCall(parameters, call);
        }

        if (options.Then is { } then)
        {
            parameters["then"] = ThenParameters(then, allowPause: true);
        }

        if (options.Edge is true)
        {
            AddEdge(parameters);
        }
    }

    /// <summary>Refuses the options an output wait cannot honour: both need a met game frame.</summary>
    /// <exception cref="McpException">screenshot or then is given.</exception>
    private static void RefuseOutputOptions(WaitOptions options)
    {
        if (options.Screenshot is true)
        {
            throw new McpException("options.screenshot needs a wait that meets on a game frame; an output wait meets on a printed line.");
        }

        if (options.Then is not null)
        {
            throw new McpException("options.then needs a wait that meets on a game frame; an output wait meets on a printed line.");
        }
    }

    private static void AddWaitCall(JsonObject parameters, MethodCall call)
    {
        string kind = parameters["kind"]!.GetValue<string>();
        parameters["call"] = kind is not ("signal" or "uiChanged" or "output")
            ? CallOptionParameters(call, "options.call")
            : throw new McpException(
                "options.call is taken only by a gameMs, frames, exists, property, expression, text, settled or state wait; "
                    + $"this condition is {kind}."
            );
    }

    /// <summary>Adds edge: true to an exists, property, expression, text or state wait's parameters.</summary>
    /// <exception cref="McpException">The wait is of another kind, or checks once (timeoutMs 0).</exception>
    private static void AddEdge(JsonObject parameters)
    {
        string kind = parameters["kind"]!.GetValue<string>();
        if (kind is not ("exists" or "property" or "expression" or "text" or "state"))
        {
            throw new McpException($"options.edge applies to exists, property, expression, text and state waits; this condition is {kind}.");
        }

        if (parameters["timeoutMs"]!.GetValue<int>() == 0)
        {
            throw new McpException("timeoutMs 0 checks once, which an edge wait cannot meet; give it a timeout.");
        }

        parameters["edge"] = true;
    }

    /// <summary>The bridge's then parameters {call?, timeScale?, pause?}, at least one of them.</summary>
    /// <exception cref="McpException">None of call, timeScale or pause: true is given, timeScale is not above 0 and at most
    /// <see cref="MaxTimeScale"/>, the call gives both forms or neither, args of the other form's shape, or an empty node,
    /// method or tool name, or <paramref name="allowPause"/> is false and a pause is asked for.</exception>
    internal static JsonObject ThenParameters(WaitThen then, bool allowPause)
    {
        if (then.Call is null && then.TimeScale is null && then.Pause is not true)
        {
            throw new McpException(EmptyThenRefusal);
        }

        JsonObject parameters = [];
        if (then.Call is { } call)
        {
            parameters["call"] = CallOptionParameters(call, "options.then.call");
        }

        AddThenTimeScale(parameters, then.TimeScale);
        AddThenPause(parameters, then.Pause, allowPause);
        return parameters;
    }

    /// <summary>Adds timeScale to a then's parameters.</summary>
    /// <exception cref="McpException">It is not above 0 and at most <see cref="MaxTimeScale"/>.</exception>
    private static void AddThenTimeScale(JsonObject parameters, double? timeScale)
    {
        if (timeScale is not { } scale)
        {
            return;
        }

        parameters["timeScale"] = scale is > 0 and <= MaxTimeScale
            ? scale
            : throw new McpException(
                $"options.then.timeScale must be greater than 0 and at most 100; got {scale.ToString(CultureInfo.InvariantCulture)}."
            );
    }

    /// <summary>Adds pause: true to a then's parameters.</summary>
    /// <exception cref="McpException"><paramref name="allowPause"/> is false: a paused game adds no game time, so the capture
    /// whose start this is would never advance.</exception>
    private static void AddThenPause(JsonObject parameters, bool? pause, bool allowPause)
    {
        if (pause is not true)
        {
            return;
        }

        if (!allowPause)
        {
            throw new McpException(CaptureStartPauseRefusal);
        }

        parameters["pause"] = true;
    }

    /// <summary>
    /// The timeout a wait runs under: <paramref name="timeoutMs"/> when given; else gameMs + 10 s for a gameMs wait, the step
    /// allowance of its frames for a frames wait, the step allowance of its stable + 1 readings for a settled wait (each at
    /// most 600 s), and 10 s for any other.
    /// </summary>
    internal static int WaitTimeoutMs(WaitCondition condition, int? timeoutMs) =>
        timeoutMs
        ?? condition switch
        {
            { GameMs: int gameMs } => (int)Math.Min((long)gameMs + DefaultWaitMs, MaxDerivedWaitMs),
            { Frames: int frames } => (int)Math.Min(StepAllowance(frames).TotalMilliseconds, MaxDerivedWaitMs),
            { Stable: int stable } => (int)Math.Min(StepAllowance(stable + 1).TotalMilliseconds, MaxDerivedWaitMs),
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
        if (parameters["kind"]!.GetValue<string>() == "output")
        {
            return await WaitForOutputAsync(
                Find(session, allowProcess: true),
                parameters,
                parameters["timeoutMs"]!.GetValue<int>(),
                cancellationToken
            );
        }

        await PrepareGameToolCallsAsync("wait_for", parameters, session, cancellationToken);
        GodotSession target = Find(session);
        AddConditionStateHelper(parameters, target);
        int timeoutMs = parameters["timeoutMs"]!.GetValue<int>();
        TimeSpan release = WaitRelease(parameters, timeoutMs, target.ActiveRecording is not null);
        BridgeCall call = new("wait_for", "wait_for", parameters, release + WaitReplyAllowance, timeoutMs > 0 ? release : null);
        return await CallWithErrorsAsync(target, call, cancellationToken);
    }

    /// <summary>
    /// An output wait: the attached refusal, the line marks, and the poll of the session's captured output until a line
    /// matches or <paramref name="timeoutMs"/> passes in load-adjusted time. It sends nothing to the bridge, so it works
    /// while the game is paused.
    /// </summary>
    /// <exception cref="McpException">The session is attached, or the pattern took over its match timeout.</exception>
    private static async Task<BridgeResult> WaitForOutputAsync(
        GodotSession target,
        JsonObject parameters,
        int timeoutMs,
        CancellationToken cancellationToken
    )
    {
        RefuseAttachedOutput(target);

        string stream = OutputStream(parameters);
        Regex regex = ScratchProfile.Compile(parameters["output"]!.GetValue<string>());
        long? after = parameters["after"]?.GetValue<long>();
        long started = Stopwatch.GetTimestamp();
        using LoadDeadline deadline = LoadClock.Shared.Start(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
        (OutputPage stdout, OutputPage stderr, long stdoutAfter, long stderrAfter) = OutputStart(target, after);
        WriteWatch.Ready();
        while (true)
        {
            (OutputMatch? match, string? error) = OutputWait.Scan(stdout, stderr, stream, regex, stdoutAfter, stderrAfter);
            if (error is not null)
            {
                throw new McpException(error);
            }

            if (match is { } found)
            {
                return new BridgeResult(OutputMet(found, ElapsedMs(started)), []);
            }

            if (OutputExpired(timeoutMs, deadline) || !await PollOutputAsync(deadline, cancellationToken))
            {
                return new BridgeResult(OutputTimeout(stdout, stderr, ElapsedMs(started)), []);
            }

            (stdout, stderr) = OutputPages(target);
        }
    }

    /// <exception cref="McpException">The session is attached, which captures no output.</exception>
    private static void RefuseAttachedOutput(GodotSession target)
    {
        if (target.Kind == SessionKind.Attach)
        {
            throw new McpException(
                $"Session '{target.Name}' is attached: the server captures no output from a game it did not start; "
                    + "wait on a node, a property or an expression instead."
            );
        }
    }

    private static string OutputStream(JsonObject parameters) => parameters["stream"]?.GetValue<string>() ?? "any";

    // Line numbers carry over a restart (GodotRun.Stdout and Stderr continue their buffers), so an after from before a
    // restart still scans the lines printed since it.
    private static (OutputPage Stdout, OutputPage Stderr, long StdoutAfter, long StderrAfter) OutputStart(GodotSession target, long? after)
    {
        (OutputPage stdout, OutputPage stderr, long stdoutTotal, long stderrTotal) = target.GetRawOutput();
        return (stdout, stderr, after ?? stdoutTotal, after ?? stderrTotal);
    }

    private static (OutputPage Stdout, OutputPage Stderr) OutputPages(GodotSession target)
    {
        (OutputPage stdout, OutputPage stderr, _, _) = target.GetRawOutput();
        return (stdout, stderr);
    }

    private static bool OutputExpired(int timeoutMs, LoadDeadline deadline) => timeoutMs == 0 || deadline.Expired;

    private static async Task<bool> PollOutputAsync(LoadDeadline deadline, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(OutputWait.Poll, deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The met answer: {met, elapsedMs, value: {line, stream, lineNumber, groups}}.</summary>
    private static JsonObject OutputMet(OutputMatch match, int elapsedMs)
    {
        JsonArray groups = [];
        foreach (string? group in match.Groups)
        {
            groups.Add((JsonNode?)group);
        }

        return new JsonObject
        {
            ["met"] = true,
            ["elapsedMs"] = elapsedMs,
            ["value"] = new JsonObject
            {
                ["line"] = OutputBuffer.Cut(match.Line, OutputBuffer.MaxLineLength),
                ["stream"] = match.Stream,
                ["lineNumber"] = match.LineNumber,
                ["groups"] = groups,
            },
        };
    }

    /// <summary>The timeout answer: {met: false, elapsedMs, last: {stdout, stderr}}, each stream's newest line or null.</summary>
    private static JsonObject OutputTimeout(OutputPage stdout, OutputPage stderr, int elapsedMs) =>
        new()
        {
            ["met"] = false,
            ["elapsedMs"] = elapsedMs,
            ["last"] = new JsonObject { ["stdout"] = LastOutputLine(stdout), ["stderr"] = LastOutputLine(stderr) },
        };

    private static string? LastOutputLine(OutputPage page) =>
        page.Lines.Count == 0 ? null : OutputBuffer.Cut(page.Lines[^1], OutputBuffer.MaxLineLength);

    private static int ElapsedMs(long started) => (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    /// <summary>
    /// Adds to a state condition's parameters the C# helper's extension, or csharpError, why it cannot run, as a state read
    /// takes them (<see cref="AddStateHelper"/>), so the bridge reads a C# node's _McpState at each check; any other condition,
    /// or none, is left as it is.
    /// </summary>
    private void AddConditionStateHelper(JsonObject? condition, GodotSession target)
    {
        if (condition?["kind"]?.GetValue<string>() == "state")
        {
            AddStateHelper(condition, csharp.PrepareForState(target.ProjectDir));
        }
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
        int frames = count ?? (options.Until is null ? 1 : MaxStepCount);
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
        AddStepConditions(parameters, options, frames, unit);
    }

    /// <summary>Adds a step's until and its riding input (<see cref="StepInput.Parameters"/>) when the options give them.</summary>
    private static void AddStepConditions(JsonObject parameters, StepOptions options, int frames, string unit)
    {
        if (options.Until is { } until)
        {
            parameters["until"] = UntilParameters(until);
        }

        if (options.Input is { } input)
        {
            parameters["input"] = StepInput.Parameters(input, frames, unit);
        }
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

        string? refusedOption =
            options.Until is not null ? "options.until"
            : options.Input is not null ? "options.input"
            : null;
        if (refusedOption is not null)
        {
            throw new McpException($"{refusedOption} applies to step only.");
        }
    }

    /// <summary>The condition's one kind: the name of the one <see cref="ConditionKinds"/> row whose field is given, when its
    /// companions are given too.</summary>
    private static string CheckCondition(WaitCondition? condition)
    {
        CheckUiChangedValue(condition);
        CheckTextFields(condition);
        CheckStateFields(condition);
        CheckSettledFields(condition);
        CheckOutputFields(condition);
        ConditionKind[] given = condition is null ? [] : [.. ConditionKinds.Where(kind => kind.Given(condition))];
        return given is [var kind] && kind.HasCompanions(condition!) ? kind.Name : throw new McpException(ConditionMessage);
    }

    /// <exception cref="McpException">uiChanged is given as anything but true or "now", the JSON it got quoted.</exception>
    private static void CheckUiChangedValue(WaitCondition? condition)
    {
        if (condition?.UiChanged is { ValueKind: not JsonValueKind.Undefined } value && !IsUiChangedValue(value))
        {
            throw new McpException($"uiChanged takes true or \"now\"; got {value.GetRawText()}.");
        }
    }

    private static bool IsUiChangedValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && value.GetString() == "now");

    /// <exception cref="McpException">under is given without text, text or under is blank, or text comes with a node.</exception>
    private static void CheckTextFields(WaitCondition? condition)
    {
        string? refusal = condition switch
        {
            { Text: null, Under: not null } => "condition.under scopes a text condition; give text with it.",
            { Text: { } text } when string.IsNullOrWhiteSpace(text) => "condition.text is blank; give the text a visible Control shows.",
            { Under: { } under } when string.IsNullOrWhiteSpace(under) =>
                "condition.under is blank; name the node to scope the text to, or leave under out.",
            { Text: not null, Node: not null } => "a text condition takes no node; scope it with under.",
            _ => null,
        };
        if (refusal is not null)
        {
            throw new McpException(refusal);
        }
    }

    /// <exception cref="McpException">stable is outside 1 to <see cref="MaxStableFrames"/>, or comes with equals.</exception>
    private static void CheckSettledFields(WaitCondition? condition)
    {
        string? refusal = condition switch
        {
            { Stable: < 1 or > MaxStableFrames } => $"condition.stable must be 1 to {MaxStableFrames} frames; got {condition.Stable}.",
            { Stable: not null } when HasEquals(condition) => "a settled condition takes no equals; it waits for the value to hold, whatever it is.",
            _ => null,
        };
        if (refusal is not null)
        {
            throw new McpException(refusal);
        }
    }

    /// <exception cref="McpException">state is blank, comes with property or stable, or comes without equals.</exception>
    private static void CheckStateFields(WaitCondition? condition)
    {
        string? refusal = condition switch
        {
            { State: { } state } when string.IsNullOrWhiteSpace(state) =>
                "condition.state is blank; give a key path into the node's state, such as seats[0].hp, or $ for the whole state.",
            { State: not null, Property: not null } or { State: not null, Stable: not null } =>
                "a state condition takes no property or stable; state names the key path to read.",
            { State: not null } when !HasEquals(condition) =>
                "a state condition needs equals, the JSON value the key must equal; null is not supported.",
            _ => null,
        };
        if (refusal is not null)
        {
            throw new McpException(refusal);
        }
    }

    /// <exception cref="McpException">output is blank or not a valid regular expression, stream is not stdout, stderr or any,
    /// or after is negative.</exception>
    private static void CheckOutputFields(WaitCondition? condition)
    {
        if (condition?.Output is not { } output)
        {
            string? companion = condition switch
            {
                { Stream: not null } => "condition.stream scopes an output condition; give output with it.",
                { After: not null } => "condition.after scopes an output condition; give output with it.",
                _ => null,
            };
            if (companion is not null)
            {
                throw new McpException(companion);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new McpException("condition.output is blank; give a regular expression to match a printed line.");
        }

        try
        {
            _ = ScratchProfile.Compile(output);
        }
        catch (ArgumentException e)
        {
            throw new McpException($"condition.output is not a valid regular expression: {e.Message.TrimEnd('.')}.");
        }

        if (condition.Stream is { } stream && stream is not ("stdout" or "stderr" or "any"))
        {
            throw new McpException($"condition.stream must be stdout, stderr or any; got {stream}.");
        }

        if (condition.After is < 0)
        {
            throw new McpException($"condition.after must be 0 or more; got {condition.After}.");
        }
    }

    // exists names the exists kind only without text, where it says whether the text is to show or to go.
    private static bool IsExistsGiven(WaitCondition condition) => condition.Exists is not null && condition.Text is null;

    // property names the property kind only without stable, where it names the value a settled wait watches; equals names it
    // only without state, where it is the value the state key must equal.
    private static bool IsPropertyGiven(WaitCondition condition) =>
        (condition.Property is not null && condition.Stable is null) || (HasEquals(condition) && condition.State is null);

    private static bool HasNode(WaitCondition condition) => !string.IsNullOrEmpty(condition.Node);

    private static bool HasPropertyCompanions(WaitCondition condition) =>
        HasNode(condition) && !string.IsNullOrEmpty(condition.Property) && HasEquals(condition);

    // CheckStateFields has already refused a state with property or stable, or without equals.
    private static bool HasStateCompanions(WaitCondition condition) => HasNode(condition) && HasEquals(condition);

    // CheckOutputFields has already refused a blank or invalid output, a bad stream and a negative after.
    private static bool HasOutputCompanions(WaitCondition condition) =>
        condition
            is {
                Node: null,
                Exists: null,
                Property: null,
                EqualsValue: null,
                Signal: null,
                Expression: null,
                UiChanged: null,
                GameMs: null,
                Frames: null,
                Text: null,
                Under: null,
                Stable: null,
                State: null,
            };

    // CheckUiChangedValue has already refused a value but true or "now"; neither takes a node, and the other kinds' fields
    // already make a second kind.
    private static bool IsUiChangedAlone(WaitCondition condition) => condition.UiChanged is not null && condition.Node is null;

    // A JSON null arrives as a null JsonElement?, the same as a missing equals, so a null to compare with is not supported
    // either way; an explicit Null element is treated alike.
    private static bool HasEquals(WaitCondition condition) => condition.EqualsValue is { ValueKind: not JsonValueKind.Null };

    /// <summary>The frame reply as {paused, timeScale, processFrames, physicsFrames[, frame, met, value | args | last,
    /// failedChecks, mutedErrors, screenshot]}, and the preview image when captured.</summary>
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

    private static JsonObject FrameState(JsonObject reply)
    {
        JsonObject state = new()
        {
            ["paused"] = reply["paused"]?.GetValue<bool>(),
            ["timeScale"] = reply["timeScale"]?.GetValue<double>(),
            ["processFrames"] = ReadInt(reply["processFrames"]) ?? 0,
            ["physicsFrames"] = ReadInt(reply["physicsFrames"]) ?? 0,
        };
        foreach (string key in StepReportKeys)
        {
            if (reply.TryGetPropertyValue(key, out JsonNode? value))
            {
                state[key] = value?.DeepClone();
            }
        }

        return state;
    }

    /// <summary>
    /// A condition kind: its name, whether a condition gives the kind's own field, and whether it gives the fields that kind
    /// needs with it and none it refuses.
    /// </summary>
    private sealed record ConditionKind(string Name, Func<WaitCondition, bool> Given, Func<WaitCondition, bool> HasCompanions);
}
