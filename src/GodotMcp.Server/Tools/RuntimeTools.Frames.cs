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
            + "take_screenshot saves it, path only, no image. Returns {frames: [{at, frame, gameSeconds, late, path, width, "
            + "height}]}. When options.timeoutMs (by default the last point's seconds + 10 s + 100 ms per point, load-adjusted) "
            + "passes first, for example under a small time_scale, it answers the frames taken with stopped: true and missed, "
            + "the points not reached. Refused while the game is paused, since its game time does not advance (frame_control "
            + "step with options.screenshot captures a paused game), and while a frame_control step or a monitor_property runs; "
            + "while it runs, pause, resume, a step and a monitor are refused. options.call {node, method, args} calls a method "
            + "in the frame the clock starts, so the points count from its entry (a coroutine is not awaited), and adds call: "
            + "{value}, its return value as call_method returns it; a refused call, or an error the method raises, fails the "
            + "capture with no frames."
    )]
    public async Task<string> CaptureFramesAsync(
        [Description("The points to capture, in seconds of game time from the call's start: 1 to 1000 of them, each 0 to 120, ascending.")]
            double[]? at = null,
        [Description("{every, for, crop, timeoutMs, call}: every and for instead of at; no crop, the default timeout and no call when left out.")]
            CaptureFramesOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        double[] points = CapturePoints(at, options);
        TimeSpan allowance = CaptureAllowance(points, options?.TimeoutMs);
        JsonObject parameters = BuildCaptureParameters(points, allowance, options);
        BridgeCall call = new("capture_frames", "frames", parameters, allowance + WaitReplyAllowance, allowance);
        BridgeResult result = await CallWithErrorsAsync(Find(session), call, cancellationToken);
        JsonObject reply =
            result.Reply?.DeepClone() as JsonObject
            ?? throw new McpException($"The bridge's capture_frames reply is not an object: {result.Reply?.ToJsonString() ?? "null"}.");
        NativeFramePaths(reply);
        CutMethodValue(reply["call"] as JsonObject);
        return ErrorReport.AddTo(reply, result.Errors).ToJsonString();
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

    /// <summary>The bridge's frames parameters: the path-only screenshot's with options.crop, {points, deadlineMs}, and call when given.</summary>
    /// <exception cref="McpException">The crop, or the call's node or method, is refused.</exception>
    internal static JsonObject BuildCaptureParameters(double[] points, TimeSpan allowance, CaptureFramesOptions? options)
    {
        JsonObject parameters = BuildScreenshotParameters(ScreenshotMode.PathOnly, options?.Crop, 0);
        parameters["points"] = new JsonArray([.. points.Select(point => JsonValue.Create(point))]);
        parameters["deadlineMs"] = (long)allowance.TotalMilliseconds;
        if (options?.Call is { } call)
        {
            parameters["call"] = MethodCallParameters(call);
        }

        return parameters;
    }

    /// <summary>How long capture_frames may run: timeoutMs when given, else the last point's seconds + 10 s + 100 ms a point.</summary>
    /// <exception cref="McpException">timeoutMs is outside 1 to <see cref="MaxCaptureTimeoutMs"/>.</exception>
    internal static TimeSpan CaptureAllowance(double[] points, int? timeoutMs)
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

    /// <summary>Each frame's path as a native path, as take_screenshot returns it.</summary>
    private static void NativeFramePaths(JsonObject reply)
    {
        if (reply["frames"] is not JsonArray frames)
        {
            return;
        }

        foreach (JsonObject frame in frames.OfType<JsonObject>())
        {
            if (frame["path"] is JsonValue path && path.TryGetValue(out string? text))
            {
                frame["path"] = Path.GetFullPath(text);
            }
        }
    }

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
