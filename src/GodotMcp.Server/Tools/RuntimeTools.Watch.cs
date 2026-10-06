using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// watch: properties and expressions of the running game sampled once a frame over a window, beside every other tool, and
/// returned as each track's change points, with the emissions of the signals it watches. The bridge
/// (bridge/godot_mcp_watch.gd) samples and keeps the points and events; the server checks the request and shapes the
/// timeline (<see cref="WatchTimeline"/>).
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const string WatchToolName = "watch";
    internal const int MaxWatchTracks = 32;
    internal const int MaxWatchSignalTracks = 16;
    internal const int MaxWatchMonitors = 16;
    internal const double MaxWatchBudgetMs = 10_000;
    internal const int DefaultWatchFrames = 600;
    private const string WatchCommand = "watch";
    private const string FrameMsMonitor = "frame_ms";
    private static readonly string[] WatchActions = ["start", "stop", "run"];

    /// <summary>The built-in monitors Godot sets once a second (main/main.cpp L5121-5141 in 4.7.2, time/navigation_process at
    /// L5138), which a watch refuses.</summary>
    private static readonly string[] OnceASecondMonitors = ["time/fps", "time/process", "time/physics_process", "time/navigation_process"];

    [McpServerTool(Name = WatchToolName, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Watches properties and expressions of the running game once a frame over a window, and records the signals it emits, "
            + "and returns how they changed. "
            + "start begins a watch and answers after its first frame with {startFrame, tracks, signals?, skipped?[, call]}; any other tool runs "
            + "meanwhile (input, waits, captures, run_script, frame_control steps); stop ends it (or collects one whose window "
            + "ended) and returns its timeline; run starts one, waits for its window and returns the timeline in one call. One "
            + "watch per session: a second start or run is refused while one runs or holds an uncollected timeline. Each frame is "
            + "sampled at its start, before the nodes' _process, so a value set in frame N shows at sample N+1. A signal is "
            + "stamped with the frame it fires in, so a signal emitted beside that value in frame N's _process is stamped N. "
            + "Frames that find "
            + "the game paused are not sampled nor counted toward the window, and are listed in paused; frame_control steps count. "
            + "The timeline: {startFrame, frames, gameMs, wallMs, paused?: [[from, to]], stopped?, call?, tracks: [{name?, node?, "
            + "property?, first, last, changes, points: [[frame, gameMs, value]], dropped?, cut?, min?, max?, minAt?, maxAt?}], "
            + "events?: [[frame, gameMs, node, signal, args]], eventCounts?: {\"<node>:<signal>\": n}, eventsDropped?, eventsCut?, skipped?, "
            + "skippedTotal?}. "
            + "startFrame is Engine.get_process_frames() at the watch's first frame (Engine.get_physics_frames() with unit "
            + "physics) and every frame counts from 0 there; gameMs "
            + "is game time since then; frames and gameMs count the unpaused frames sampled. points are the change points: a "
            + "sample kept when it differs from the last one kept (numbers within 1e-6, or by minDelta), the first always; a "
            + "track keeps its first 200 and last 50 and counts the rest in dropped, while first, last, changes and the numeric "
            + "range (per component for a vector) cover every sample. A value over 200 characters of JSON comes back as "
            + "{valuePreview, valueLength}, and past 40000 characters the longest tracks' points are cut from the middle, "
            + "counted in cut. A node freed during the window reads {\"$freed\": true} once and its track stops; an expression "
            + "that fails reads {\"$error\": text} and goes on. stopped is stop (a stop ended it early) or deadline (its "
            + "deadline passed: the window's allowance, 10 s + 100 ms a frame or gameMs + 10 s, at most 600 s, plus the time a "
            + "started watch spends paused, up to 600 s). events lists every signal track's emissions in order, at most 300 shared "
            + "fairly across signal tracks (each keeps its earliest), and eventCounts counts every emission; a freed emitter's "
            + "signals stop. tracks.monitors adds monitors?: [{name, samples, p50, p95, p99, max, maxAt, mean, spikes?: [[frame, "
            + "value]], over?, custom?, nonNumeric?}], each read once a sampled frame and summarised in the game: frame_ms (the wall "
            + "time between frames, over: {budget, count, frames} against options.budgetMs, a hitch reading as one long frame), "
            + "a built-in monitor by its Performance.get_monitor_name path (object/nodes, raster/total_draw_calls), or a custom "
            + "monitor id; warning? says when a reading misleads. "
            + "Expressions, options.call and custom monitors run game code. A restart, a stop_project "
            + "or a lost connection drops the watch."
    )]
    public async Task<string> WatchAsync(
        [Description("start, stop or run.")] string action,
        [Description(
            "start and run: {properties: [{node, property, name?, minDelta?}], expressions: [{name, expression, node?, minDelta?}], "
                + "signals: [{node | group, signal}], monitors: [\"frame_ms\" | a built-in monitor such as \"object/nodes\" | a custom "
                + "monitor id]}, at least one track; at most 32 property and expression tracks, each with a unique key (name, else "
                + "node and property), at most 16 signal tracks and at most 16 monitors."
        )]
            WatchTracks? tracks = null,
        [Description(
            "start and run: exactly one of {frames} (1 to 7200) or {gameMs} (1 to 120000); start defaults to {frames: 600}, run " + "needs one."
        )]
            WatchWindow? window = null,
        [Description("start and run: {call, unit, budgetMs}; no call, unit process and the default budget when left out.")]
            WatchOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = BuildWatchParameters(action, tracks, window, options);
        BridgeResult result = await CallWithErrorsAsync(Find(session), WatchCall(action, parameters), cancellationToken);
        JsonObject reply =
            result.Reply?.DeepClone() as JsonObject
            ?? throw new McpException($"The bridge's watch reply is not an object: {result.Reply?.ToJsonString() ?? "null"}.");
        CutMethodValue(reply["call"] as JsonObject);
        JsonObject shaped = action == "start" ? reply : WatchTimeline.Shape(reply, WatchTimeline.MaxResultLength);
        return ErrorReport.AddTo(shaped, result.Errors).ToJsonString();
    }

    /// <summary>
    /// The bridge's watch parameters: {action} for stop; for start and run {action, properties?, expressions?, signals?,
    /// monitors?, frames | gameMs, unit, call?, budgetMs?, deadlineMs}, deadlineMs the window's allowance
    /// (<see cref="WatchAllowance"/>).
    /// </summary>
    /// <exception cref="McpException">An unknown action; stop given tracks, a window or options; no track, more than
    /// <see cref="MaxWatchTracks"/> property and expression tracks, <see cref="MaxWatchSignalTracks"/> signal tracks or
    /// <see cref="MaxWatchMonitors"/> monitors, an empty node, property, name, expression, group, signal or monitor, a minDelta
    /// not above 0, two tracks with one key, a signal track with both or neither of node and group, two signal tracks on one
    /// emitter and signal, a monitor named twice, a once-a-second time/* monitor, or frame_ms with unit physics; a window
    /// without exactly one of frames or gameMs, or out of range; run without a window; a unit other than process or physics;
    /// or a budgetMs out of range or without frame_ms.</exception>
    internal static JsonObject BuildWatchParameters(string action, WatchTracks? tracks, WatchWindow? window, WatchOptions? options)
    {
        if (!WatchActions.Contains(action))
        {
            throw new McpException("action must be start, stop or run.");
        }

        if (action == "stop")
        {
            return tracks is null && window is null && options is null
                ? new JsonObject { ["action"] = action }
                : throw new McpException("stop takes no tracks, window or options: it ends the watch that runs and returns its timeline.");
        }

        JsonObject parameters = new() { ["action"] = action };
        AddWatchTracks(parameters, tracks);
        AddWatchWindow(parameters, action, window);
        AddWatchOptions(parameters, options);
        AddWatchMonitors(parameters, tracks?.Monitors ?? [], options);
        return parameters;
    }

    /// <summary>How long a watch of <paramref name="window"/> may run: the step rule's allowance for frames, gameMs + 10 s for
    /// gameMs, either at most 600 s.</summary>
    internal static TimeSpan WatchAllowance(WatchWindow window)
    {
        double milliseconds = window.GameMs is int gameMs
            ? gameMs + FrameTimeout.TotalMilliseconds
            : StepAllowance(window.Frames ?? DefaultWatchFrames).TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxDerivedWaitMs));
    }

    /// <summary>
    /// The bridge call for <paramref name="action"/>: a run holds its request for the window and is released (cancelled) at its
    /// allowance, so the bridge answers what it has; start (which answers after the watch's first frame) and stop answer
    /// within the frame timeout.
    /// </summary>
    private static BridgeCall WatchCall(string action, JsonObject parameters)
    {
        if (action != "run")
        {
            return new BridgeCall(WatchToolName, WatchCommand, parameters, FrameTimeout);
        }

        var allowance = TimeSpan.FromMilliseconds(parameters["deadlineMs"]!.GetValue<long>());
        return new BridgeCall(WatchToolName, WatchCommand, parameters, allowance + WaitReplyAllowance, allowance);
    }

    private static void AddWatchTracks(JsonObject parameters, WatchTracks? tracks)
    {
        WatchTracks given = tracks ?? new WatchTracks();
        WatchPropertyTrack[] properties = given.Properties ?? [];
        WatchExpressionTrack[] expressions = given.Expressions ?? [];
        WatchSignalTrack[] signals = given.Signals ?? [];
        CheckTrackCount(properties.Length + expressions.Length, signals.Length, given.Monitors?.Length ?? 0);
        AddValueTracks(parameters, properties, expressions);
        if (signals.Length > 0)
        {
            HashSet<string> pairs = new(StringComparer.Ordinal);
            parameters["signals"] = new JsonArray([.. signals.Select(track => SignalTrackParameters(track, pairs))]);
        }
    }

    private static void AddValueTracks(JsonObject parameters, WatchPropertyTrack[] properties, WatchExpressionTrack[] expressions)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        if (properties.Length > 0)
        {
            parameters["properties"] = new JsonArray([.. properties.Select(track => PropertyTrackParameters(track, keys))]);
        }

        if (expressions.Length > 0)
        {
            parameters["expressions"] = new JsonArray([.. expressions.Select(track => ExpressionTrackParameters(track, keys))]);
        }
    }

    private static void CheckTrackCount(int valueTracks, int signalTracks, int monitors)
    {
        if (valueTracks + signalTracks + monitors == 0)
        {
            throw new McpException(
                "tracks needs at least one track: {properties: [{node, property}]}, {expressions: [{name, expression}]}, "
                    + "{signals: [{node, signal}]} or {monitors: [\"frame_ms\"]}."
            );
        }

        if (valueTracks > MaxWatchTracks)
        {
            throw new McpException($"tracks holds {valueTracks} property and expression tracks; at most {MaxWatchTracks} together.");
        }

        if (signalTracks > MaxWatchSignalTracks)
        {
            throw new McpException($"tracks holds {signalTracks} signal tracks; at most {MaxWatchSignalTracks}.");
        }
    }

    /// <summary>A signal track's parameters, {node, signal} or {group, signal}; <paramref name="pairs"/> holds the emitter and
    /// signal of every track before it, a node and a group of one name apart.</summary>
    private static JsonObject SignalTrackParameters(WatchSignalTrack? track, HashSet<string> pairs)
    {
        if (track is null)
        {
            throw new McpException("signals holds a null track; each is {node, signal} or {group, signal}.");
        }

        if ((track.Node is null) == (track.Group is null))
        {
            throw new McpException("a signal track takes node or group, not both: {node, signal} or {group, signal}.");
        }

        JsonObject parameters = track.Node is { } node
            ? new JsonObject { ["node"] = CheckNode(node) }
            : new JsonObject { ["group"] = CheckName(track.Group!, "group", "Pass a group's name, as add_to_group takes it.") };
        string signal = CheckName(
            track.Signal,
            "signal",
            "Pass a signal's name as Godot lists it, such as pressed (a C# [Signal] without its EventHandler suffix)."
        );
        parameters["signal"] = signal;
        string emitter = track.Node ?? track.Group!;
        string kind = track.Node is null ? "group" : "node";
        return pairs.Add($"{kind}\n{emitter}\n{signal}")
            ? parameters
            : throw new McpException($"two signal tracks watch {emitter} and {signal}; each pair once.");
    }

    private static JsonObject PropertyTrackParameters(WatchPropertyTrack? track, HashSet<string> keys)
    {
        if (track is null)
        {
            throw new McpException("properties holds a null track; each is {node, property, name?, minDelta?}.");
        }

        JsonObject parameters = new()
        {
            ["node"] = CheckNode(track.Node),
            ["property"] = CheckName(
                track.Property,
                "property",
                "Pass a property name as inspect_node lists it, or a path into one such as position:x."
            ),
        };
        string key = track.Name is null ? $"{track.Node}:{track.Property}" : CheckTrackName(track.Name);
        AddTrackKey(parameters, track.Name, key, keys);
        AddMinDelta(parameters, track.MinDelta, key);
        return parameters;
    }

    private static JsonObject ExpressionTrackParameters(WatchExpressionTrack? track, HashSet<string> keys)
    {
        if (track is null)
        {
            throw new McpException("expressions holds a null track; each is {name, expression, node?, minDelta?}.");
        }

        JsonObject parameters = new()
        {
            ["expression"] = CheckName(track.Expression, "expression", "Pass a Godot Expression, such as root.gui_get_focus_owner()."),
        };
        if (track.Node is not null)
        {
            parameters["node"] = CheckNode(track.Node);
        }

        string key = CheckTrackName(track.Name);
        AddTrackKey(parameters, key, key, keys);
        AddMinDelta(parameters, track.MinDelta, key);
        return parameters;
    }

    private static string CheckTrackName(string name) => CheckName(name, "name", "A track's name keys it in the result.");

    private static void AddTrackKey(JsonObject parameters, string? name, string key, HashSet<string> keys)
    {
        if (!keys.Add(key))
        {
            throw new McpException($"Two tracks are keyed '{key}'; give one of them a different name.");
        }

        if (name is not null)
        {
            parameters["name"] = name;
        }
    }

    private static void AddMinDelta(JsonObject parameters, double? minDelta, string key)
    {
        if (minDelta is null)
        {
            return;
        }

        parameters["minDelta"] =
            minDelta > 0
                ? minDelta
                : throw new McpException(
                    $"minDelta must be greater than 0; got {minDelta.Value.ToString(CultureInfo.InvariantCulture)} on track '{key}'."
                );
    }

    private static void AddWatchWindow(JsonObject parameters, string action, WatchWindow? window)
    {
        if (window is null && action == "run")
        {
            throw new McpException(
                $"run needs a window: {{frames}} (1 to {MaxWaitFrames}) or {{gameMs}} (1 to {MaxWaitGameMs}); start defaults to "
                    + $"{DefaultWatchFrames} frames."
            );
        }

        WatchWindow checkedWindow = window ?? new WatchWindow(Frames: DefaultWatchFrames);
        if ((checkedWindow.Frames is null) == (checkedWindow.GameMs is null))
        {
            throw new McpException("window needs exactly one of frames or gameMs.");
        }

        if (checkedWindow.Frames is int frames)
        {
            parameters["frames"] = frames is >= 1 and <= MaxWaitFrames
                ? frames
                : throw new McpException($"frames must be between 1 and {MaxWaitFrames}; got {frames}.");
        }
        else
        {
            int gameMs = checkedWindow.GameMs!.Value;
            parameters["gameMs"] = gameMs is >= 1 and <= MaxWaitGameMs
                ? gameMs
                : throw new McpException($"gameMs must be between 1 and {MaxWaitGameMs}; got {gameMs}.");
        }

        parameters["deadlineMs"] = (long)WatchAllowance(checkedWindow).TotalMilliseconds;
    }

    private static void AddWatchOptions(JsonObject parameters, WatchOptions? options)
    {
        string unit = options?.Unit ?? "process";
        parameters["unit"] = unit is "process" or "physics" ? unit : throw new McpException("unit must be process or physics.");
        if (options?.Call is { } call)
        {
            parameters["call"] = MethodCallParameters(call);
        }
    }

    /// <summary>Adds the monitors, each named once and none of Godot's once-a-second time/* monitors, and budgetMs, which
    /// needs frame_ms; frame_ms measures process frames, so it is refused with unit physics.</summary>
    private static void AddWatchMonitors(JsonObject parameters, string[] monitors, WatchOptions? options)
    {
        if (monitors.Length > MaxWatchMonitors)
        {
            throw new McpException($"tracks holds {monitors.Length} monitors; at most {MaxWatchMonitors}.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string monitor in monitors)
        {
            CheckMonitor(monitor, names);
        }

        if (monitors.Length > 0)
        {
            parameters["monitors"] = new JsonArray([.. monitors.Select(monitor => (JsonNode)monitor)]);
        }

        bool frameMs = names.Contains(FrameMsMonitor);
        if (frameMs && options?.Unit == "physics")
        {
            throw new McpException("frame_ms measures process frames; drop options.unit \"physics\" or the frame_ms monitor.");
        }

        AddBudget(parameters, options?.BudgetMs, frameMs);
    }

    private static void CheckMonitor(string monitor, HashSet<string> names)
    {
        string name = CheckName(
            monitor,
            "monitor",
            "Pass frame_ms, a built-in monitor's path as Performance.get_monitor_name gives it (object/nodes, "
                + "raster/total_draw_calls), or a custom monitor's id."
        );
        if (OnceASecondMonitors.Contains(name, StringComparer.Ordinal))
        {
            throw new McpException(
                $"'{name}' is set once a second, so read per frame it repeats one value and hides which frame was slow; watch "
                    + "'frame_ms' instead."
            );
        }

        if (!names.Add(name))
        {
            throw new McpException($"tracks.monitors names '{name}' twice; each monitor once.");
        }
    }

    private static void AddBudget(JsonObject parameters, double? budgetMs, bool frameMs)
    {
        if (budgetMs is not double budget)
        {
            return;
        }

        if (budget is not (> 0 and <= MaxWatchBudgetMs))
        {
            throw new McpException(
                $"options.budgetMs must be greater than 0 and at most {MaxWatchBudgetMs.ToString(CultureInfo.InvariantCulture)}; got "
                    + $"{budget.ToString(CultureInfo.InvariantCulture)}."
            );
        }

        parameters["budgetMs"] = frameMs
            ? budget
            : throw new McpException("options.budgetMs applies to the frame_ms monitor; add \"frame_ms\" to tracks.monitors or drop budgetMs.");
    }
}
