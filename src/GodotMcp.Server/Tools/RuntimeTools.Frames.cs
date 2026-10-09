using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// capture_frames: frames of the running game at set points of game time, grabbed inside the game in one call. The bridge
/// (bridge/godot_mcp_time.gd) sums each process frame's scaled delta from the call's start and grabs the frame in which each
/// point falls due.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const int MaxCaptureFrames = 1000;
    internal const double MaxCaptureSeconds = 120;
    internal const int MaxCaptureTimeoutMs = 600_000;
    internal const string CaptureFormsMessage = "pass at (a list of seconds) or options.every and options.for, not both.";
    internal const string CaptureStartPauseRefusal =
        "capture_frames' start.then cannot pause: a paused game adds no game time, so the capture would never advance.";

    // A point k x every counts as within for when it passes it by no more than this, so every 0.1 for 0.3 gives three points.
    private const double CapturePointTolerance = 1e-9;

    // The default allowance on top of the last point's game time: the time to reach the first frame and save the files.
    private static readonly TimeSpan CaptureBaseAllowance = TimeSpan.FromSeconds(10);

    [McpServerTool(Name = "capture_frames", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Captures the running game's frames at set moments of an animation or a timer-driven sequence, in one call, with no "
            + "round trip per frame: at is a list of seconds, or options.every and options.for space them evenly. The seconds are "
            + "game time from the call's start, the sum of each process frame's delta, so they follow Engine.time_scale as the "
            + "game's timers do. Each point is taken in the first frame at or after it; a late frame is still taken, and late says "
            + "by how much. Points due in the same frame share one file. At most 1000 points and 120 s. Each frame is saved as "
            + "take_screenshot saves it, path only, no image. Returns {folder, files, width, height, points: [{at, frame, file, "
            + "gameSeconds, late}], shared}: folder is the directory of the PNGs, files names each distinct one once in the "
            + "order first taken, a point's file indexes files, and shared is how many points took a file an earlier point "
            + "took. When options.timeoutMs (by default the last point's seconds + 10 s + 100 ms per point, load-adjusted) "
            + "passes first, for example under a small time_scale, it answers the frames taken with stopped: true and missed, "
            + "the points not reached. Refused while the game is paused, since its game time does not advance (frame_control "
            + "step with options.screenshot captures a paused game), and while a frame_control step runs; while it runs, pause, "
            + "resume and a step are refused. options.call {node, method, args}, or {tool, args} "
            + "for a game tool, calls it in the frame the clock starts, so the points count from its entry (a coroutine or a "
            + "Task is not awaited), and adds call: {value}, its return value as call_method returns it, with tool and type for "
            + "a game tool; a refused call, or an error it raises, fails the capture with no frames. options.start {node, "
            + "property, equals | exists | expression, edge?, timeoutMs?, then?}, a condition laid out as wait_for's, makes the "
            + "clock wait for it: the points count from the frame it is met, where start.then runs (call, then timeScale; pause is "
            + "refused, as a paused game adds no game time) and "
            + "then options.call; a start already true at the call starts the clock at the next frame, as without one. Its "
            + "timeoutMs, real time, 10000 when left out, is added to the capture's, at most 600000 in all. Met, the result adds start: "
            + "{met: true, frame, then?}; a start that times out is still a success, with stopped: true, no frames and start: "
            + "{met: false, last}."
    )]
    public async Task<string> CaptureFramesAsync(
        [Description(
            "The points to capture, in seconds of game time from the call's start (or from the frame options.start is met): 1 to "
                + "1000 of them, each 0 to 120, ascending."
        )]
            double[]? at = null,
        [Description(
            "{every, for, crop, timeoutMs, call, start}: every and for instead of at; no crop, the default timeout, no call and no "
                + "start when left out."
        )]
            CaptureFramesOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        double[] points = CapturePoints(at, options);
        TimeSpan allowance = CaptureAllowance(points, options?.TimeoutMs, options?.Start);
        JsonObject parameters = BuildCaptureParameters(points, allowance, options);
        await PrepareGameToolCallsAsync("capture_frames", parameters, session, cancellationToken);
        BridgeCall call = new("capture_frames", "frames", parameters, allowance + WaitReplyAllowance, allowance);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        return ErrorReport.AddTo(CompactFrames(CaptureReply(result)), result.Errors).ToJsonString();
    }

    /// <summary>A copy of the bridge's capture_frames reply, with the values of its call and its start's then.call cut.</summary>
    /// <exception cref="McpException">The reply is not an object.</exception>
    private static JsonObject CaptureReply(BridgeResult result)
    {
        JsonObject reply =
            result.Reply?.DeepClone() as JsonObject
            ?? throw new McpException($"The bridge's capture_frames reply is not an object: {result.Reply?.ToJsonString() ?? "null"}.");
        CutMethodValue(reply["call"] as JsonObject);
        CutMethodValue((reply["start"]?["then"] as JsonObject)?["call"] as JsonObject);
        return reply;
    }

    /// <summary>The points capture_frames takes: at as given, or every, 2 x every and so on up to and including for.</summary>
    /// <exception cref="McpException">Neither form or both, or a point, every or for out of its bounds.</exception>
    internal static double[] CapturePoints(double[]? at, CaptureFramesOptions? options)
    {
        return (at, options?.Every, options?.For) switch
        {
            ({ } points, null, null) => CheckAtPoints(points),
            (null, { } every, { } span) => EvenPoints(every, span),
            _ => throw new McpException(CaptureFormsMessage),
        };
    }

    /// <summary>
    /// The bridge's frames parameters: the path-only screenshot's with options.crop, {points, deadlineMs}, call when given,
    /// and start (<see cref="StartParameters"/>) when given.
    /// </summary>
    /// <exception cref="McpException">
    /// The crop is refused, the call gives both forms or neither, args of the other form's shape, or an empty node, method
    /// or tool name, or the start is refused.
    /// </exception>
    internal static JsonObject BuildCaptureParameters(double[] points, TimeSpan allowance, CaptureFramesOptions? options)
    {
        JsonObject parameters = BuildScreenshotParameters(ScreenshotMode.PathOnly, options?.Crop, 0);
        parameters["points"] = new JsonArray([.. points.Select(point => JsonValue.Create(point))]);
        parameters["deadlineMs"] = (long)allowance.TotalMilliseconds;
        if (options?.Call is { } call)
        {
            parameters["call"] = CallOptionParameters(call, "options.call");
        }

        if (options?.Start is { } start)
        {
            parameters["start"] = StartParameters(start);
        }

        return parameters;
    }

    /// <summary>
    /// The bridge's start parameters: the condition's fields with its kind and the timeoutMs it runs under, as a wait's
    /// (<see cref="BuildWaitParameters(WaitCondition?, int?)"/>), plus then and edge when given.
    /// </summary>
    /// <exception cref="McpException">
    /// The condition is not exactly one kind, or a kind other than exists, property or expression; timeoutMs is out of range;
    /// then is refused as wait_for's options.then is; or edge is given with timeoutMs 0.
    /// </exception>
    internal static JsonObject StartParameters(CaptureStart start)
    {
        WaitCondition condition = new(
            Node: start.Node,
            Exists: start.Exists,
            Property: start.Property,
            EqualsValue: start.EqualsValue,
            Signal: start.Signal,
            Expression: start.Expression,
            UiChanged: start.UiChanged,
            GameMs: start.GameMs,
            Frames: start.Frames
        );
        string kind = CheckCondition(condition);
        if (kind is not ("exists" or "property" or "expression"))
        {
            throw new McpException($"options.start takes an exists, property or expression condition; this condition is {kind}.");
        }

        JsonObject parameters = BuildWaitParameters(condition, start.TimeoutMs);
        if (start.Then is { } then)
        {
            parameters["then"] = ThenParameters(then, allowPause: false);
        }

        if (start.Edge is true)
        {
            AddEdge(parameters);
        }

        return parameters;
    }

    /// <summary>
    /// How long capture_frames may run: the capture's own allowance (<see cref="CaptureOwnAllowance"/>), plus the start's
    /// timeoutMs (10 s when left out) when it has a start, at most <see cref="MaxCaptureTimeoutMs"/> in all.
    /// </summary>
    /// <exception cref="McpException">timeoutMs is outside 1 to <see cref="MaxCaptureTimeoutMs"/>.</exception>
    internal static TimeSpan CaptureAllowance(double[] points, int? timeoutMs, CaptureStart? start)
    {
        TimeSpan allowance = CaptureOwnAllowance(points, timeoutMs);
        if (start is null)
        {
            return allowance;
        }

        TimeSpan withStart = allowance + TimeSpan.FromMilliseconds(Math.Max(start.TimeoutMs ?? DefaultWaitMs, 0));
        var cap = TimeSpan.FromMilliseconds(MaxCaptureTimeoutMs);
        return withStart < cap ? withStart : cap;
    }

    /// <summary>How long the capture of the points may run: timeoutMs when given, else the last point's seconds + 10 s + 100 ms a point.</summary>
    /// <exception cref="McpException">timeoutMs is outside 1 to <see cref="MaxCaptureTimeoutMs"/>.</exception>
    private static TimeSpan CaptureOwnAllowance(double[] points, int? timeoutMs)
    {
        if (timeoutMs is not null)
        {
            return timeoutMs is >= 1 and <= MaxCaptureTimeoutMs
                ? TimeSpan.FromMilliseconds(timeoutMs.Value)
                : throw new McpException(Invariant($"options.timeoutMs must be between 1 and {MaxCaptureTimeoutMs}; got {timeoutMs}."));
        }

        return TimeSpan.FromSeconds(points[^1]) + CaptureBaseAllowance + (PerFrameAllowance * points.Length);
    }

    private static double[] CheckAtPoints(double[] at)
    {
        if (at.Length is < 1 or > MaxCaptureFrames)
        {
            throw new McpException(Invariant($"at takes 1 to {MaxCaptureFrames} points; got {at.Length}."));
        }

        for (int index = 0; index < at.Length; index++)
        {
            double point = at[index];
            if (point is not (>= 0 and <= MaxCaptureSeconds))
            {
                throw new McpException(Invariant($"at[{index}] must be a number of seconds from 0 to {MaxCaptureSeconds}; got {point}."));
            }

            if (index > 0 && point < at[index - 1])
            {
                throw new McpException(Invariant($"at must be ascending; at[{index}] is {point}, less than at[{index - 1}], {at[index - 1]}."));
            }
        }

        return at;
    }

    private static double[] EvenPoints(double every, double span)
    {
        if (every is not > 0 || double.IsInfinity(every))
        {
            throw new McpException(Invariant($"options.every must be a number of seconds greater than 0; got {every}."));
        }

        if (span is not (> 0 and <= MaxCaptureSeconds))
        {
            throw new McpException(Invariant($"options.for must be greater than 0 and at most {MaxCaptureSeconds} seconds; got {span}."));
        }

        double count = Math.Floor((span + CapturePointTolerance) / every);
        if (count > MaxCaptureFrames)
        {
            throw new McpException(
                Invariant($"options.every {every} over options.for {span} makes {count:0} points; at most {MaxCaptureFrames} are allowed.")
            );
        }

        if (count < 1)
        {
            throw new McpException(Invariant($"options.every {every} is longer than options.for {span}, so there is no point to capture."));
        }

        return [.. Enumerable.Range(1, (int)count).Select(step => step * every)];
    }

    /// <summary>
    /// The bridge's frames as the compact result: the folder and the distinct file names once, a point per frame point
    /// naming its file by index, and shared, the points that took a file an earlier point took.
    /// </summary>
    internal static JsonObject CompactFrames(JsonObject reply)
    {
        List<JsonObject> frames = [.. (reply["frames"] as JsonArray ?? []).OfType<JsonObject>()];
        List<string> paths = [];
        Dictionary<string, int> taken = new(StringComparer.Ordinal);
        JsonArray points = [.. frames.Select(frame => CompactPoint(frame, paths, taken))];

        JsonObject result = [];
        AddCapture(result, frames, paths);
        result["points"] = points;
        result["shared"] = points.Count - paths.Count;
        foreach (string key in (string[])["stopped", "missed", "call", "start"])
        {
            if (reply[key] is { } carried)
            {
                result[key] = carried.DeepClone();
            }
        }

        return result;
    }

    /// <summary>
    /// The capture's own fields: the folder its frames sit in, their size, and the distinct file names in first-taken
    /// order. A capture stopped before its first point took no frame, so it has none of these.
    /// </summary>
    private static void AddCapture(JsonObject result, List<JsonObject> frames, List<string> paths)
    {
        if (frames.Count > 0)
        {
            result["folder"] = Path.GetDirectoryName(paths[0]);
            result["width"] = frames[0]["width"]?.DeepClone();
            result["height"] = frames[0]["height"]?.DeepClone();
        }

        result["files"] = new JsonArray([.. paths.Select(path => JsonValue.Create(Path.GetFileName(path)))]);
    }

    /// <summary>
    /// One point of the result: its seconds and engine frame, the index of the file its path names, and how late it was.
    /// A path no earlier point took adds the next file.
    /// </summary>
    private static JsonObject CompactPoint(JsonObject frame, List<string> paths, Dictionary<string, int> taken)
    {
        string path = Path.GetFullPath(frame["path"]!.GetValue<string>());
        if (!taken.TryGetValue(path, out int file))
        {
            file = paths.Count;
            paths.Add(path);
            taken[path] = file;
        }

        return new JsonObject
        {
            ["at"] = frame["at"]?.DeepClone(),
            ["frame"] = frame["frame"]?.DeepClone(),
            ["file"] = file,
            ["gameSeconds"] = frame["gameSeconds"]?.DeepClone(),
            ["late"] = frame["late"]?.DeepClone(),
        };
    }

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
