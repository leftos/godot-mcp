using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
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
    internal const int MaxWatchBuses = 16;
    internal const double MinWatchThresholdDb = -100;
    internal const double MaxWatchThresholdDb = 0;
    private const string EveryBus = "*";
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
            + "The timeline: {startFrame, frames, gameMs, wallMs, clockErrorMs?, paused?: [[from, to]], stopped?, call?, tracks: [{name?, "
            + "node?, property?, first, last, changes, points: [[frame, gameMs, value, at]], dropped?, cut?, min?, max?, minAt?, maxAt?}], "
            + "events?: [[frame, gameMs, node, signal, args, at]], eventCounts?: {\"<node>:<signal>\": n}, eventsDropped?, eventsCut?, "
            + "skipped?, skippedTotal?}. "
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
            + "signals stop. Every point, event and voice ends with at, its time on the drive clock in ms (null when the session's "
            + "clock cannot convert it); the timeline carries clockErrorMs once when any was converted. "
            + "tracks.monitors adds monitors?: [{name, samples, p50, p95, p99, max, maxAt, mean, spikes?: [[frame, "
            + "value]], over?, custom?, nonNumeric?}], each read once a sampled frame and summarised in the game: frame_ms (the wall "
            + "time between frames, over: {budget, count, frames} against options.budgetMs, a hitch reading as one long frame), "
            + "a built-in monitor by its Performance.get_monitor_name path (object/nodes, raster/total_draw_calls), or a custom "
            + "monitor id; warning? says when a reading misleads. "
            + "tracks.audio.players follows every AudioStreamPlayer, 2D and 3D at or under players.under (default the root), "
            + "found at start or added during the window (at most 200 at a time, the rest counted in playersSkipped), and adds "
            + "audio: {voices: [[frame, gameMs, event, path, {stream?, bus?, maxPolyphony?, position?, reason?}, at]], voiceCounts: "
            + "{\"<path>\": {starts, ends}}, voiceCountsCut?, voicesDropped?, voicesCut?, playersFollowed?, playersSkipped?}; "
            + "start's reply adds audio: {driver, mixRate, bridgeMuted, players?: {count, playing: [{path, stream, bus, ...}]}, "
            + "playersSkipped?}, the players keys with a players track. event is start (a new playback: with unit process, a "
            + "play() in frame N's processing reads at N+1, in a physics tick at N; it carries the stream, by its resource path "
            + "(res://x.tscn::<id> for one embedded in a scene) or {class} when it has none, the bus, maxPolyphony above 1 and "
            + "a 2D or 3D player's global position, not the playback position, null outside the tree), pause or resume "
            + "(stream_paused flipped; it follows the player's "
            + "oldest voice) or end (the player holds no playback; reason finished, freed or stopped; an end with no start is a "
            + "voice start's players.playing listed). Voices are read in paused frames too. At most 300 voices, shared fairly "
            + "across players (each keeps its earliest, the earliest players first), the rest counted in voicesDropped; "
            + "voiceCounts keeps the 100 paths with the most starts and ends and counts the rest in voiceCountsCut. A player "
            + "made, played and freed within one frame is never seen; a start says the game asked for the sound, not that it "
            + "was heard. "
            + "tracks.audio.buses reads each named bus (or [\"*\"]: every bus at start, Master included, the first 16 by index, "
            + "the rest counted in busesSkipped; a bus added later is not followed) once an unpaused sampled frame, resolved by "
            + "name each time, and adds audio.buses: "
            + "[{name, heard: [[fromFrame, toFrame]], heardFrames, sampledFrames, heardCut?, peakMaxDb?, peakMaxAt?, volumeDb?: "
            + "{first, last, changes}, mute?: {first, last, changes}}]; start's reply adds audio.buses: [{name, index, volumeDb, "
            + "mute, solo, send, effects: [class]}] ({name, missing: true} for a bus that does not exist at start, which is read "
            + "from any sampled frame it exists in). "
            + "The peak is the largest left or right peak over the bus's channels, after its volume, mute and solo, so a bus "
            + "that is not soloed while another is reads -200; heard lists the runs of sampled frames whose peak was above "
            + "thresholdDb (default -60), broken by a paused frame, at most 50 a bus (the first 25 and the last 25, the rest "
            + "counted in heardCut). A bus removed or renamed during the window is skipped while it is gone, so sampledFrames "
            + "counts the frames it was read. In a quiet or headless run audio is mixed in bursts of about 93 ms, so a sound "
            + "shorter than about 81 ms may never reach a peak read: a voice's start, not heard, says whether a sound fired. "
            + "warning? says when Master was read while the bridge muted it (the buses that send to it still read) and when "
            + "a sampled frame ran at an Engine.time_scale other than 1, which audio does not follow. "
            + "Expressions, options.call and custom monitors run game code. A restart, a stop_project "
            + "or a lost connection drops the watch."
    )]
    public async Task<string> WatchAsync(
        [Description("start, stop or run.")] string action,
        [Description(
            "start and run: {properties: [{node, property, name?, minDelta?}], expressions: [{name, expression, node?, minDelta?}], "
                + "signals: [{node | group, signal}], monitors: [\"frame_ms\" | a built-in monitor such as \"object/nodes\" | a custom "
                + "monitor id], audio: {players?: {under?}, buses?: [name] | [\"*\"], thresholdDb?}}, at least one track; at most 32 "
                + "property and expression tracks, each with a unique key (name, else node and property), at most 16 signal "
                + "tracks, at most 16 monitors and at most 16 buses; audio needs players or buses or both."
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
        await PrepareGameToolCallsAsync("watch", parameters, session, cancellationToken);
        GodotSession target = Find(session);
        BridgeResult result = await CallWithErrorsAsync(target, WatchCall(action, parameters), cancellationToken);
        JsonObject reply =
            result.Reply?.DeepClone() as JsonObject
            ?? throw new McpException($"The bridge's watch reply is not an object: {result.Reply?.ToJsonString() ?? "null"}.");
        CutMethodValue(reply["call"] as JsonObject);
        if (action == "start")
        {
            return ErrorReport.AddTo(reply, result.Errors).ToJsonString();
        }

        SessionClock clock = await target.RefreshClockAsync(cancellationToken);
        WatchStamps.Stamp(reply, clock);
        return ErrorReport.AddTo(WatchTimeline.Shape(reply, WatchTimeline.MaxResultLength), result.Errors).ToJsonString();
    }

    /// <summary>
    /// The bridge's watch parameters: {action} for stop; for start and run {action, properties?, expressions?, signals?,
    /// monitors?, audio?, frames | gameMs, unit, call?, budgetMs?, deadlineMs}, deadlineMs the window's allowance
    /// (<see cref="WatchAllowance"/>).
    /// </summary>
    /// <exception cref="McpException">An unknown action; stop given tracks, a window or options; no track, more than
    /// <see cref="MaxWatchTracks"/> property and expression tracks, <see cref="MaxWatchSignalTracks"/> signal tracks or
    /// <see cref="MaxWatchMonitors"/> monitors, an empty node, property, name, expression, group, signal or monitor, a minDelta
    /// not above 0, two tracks with one key, a signal track with both or neither of node and group, two signal tracks on one
    /// emitter and signal, a monitor named twice, a once-a-second time/* monitor, frame_ms with unit physics, an audio track
    /// with neither players nor buses, an empty under, an empty buses list, an empty bus name, a bus named twice, more than
    /// <see cref="MaxWatchBuses"/> buses, "*" beside another name, a thresholdDb outside -100 to 0 or without buses; a window
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
        CheckTrackCount(properties.Length + expressions.Length, signals.Length, OtherTrackCount(given));
        AddValueTracks(parameters, properties, expressions);
        if (signals.Length > 0)
        {
            HashSet<string> pairs = new(StringComparer.Ordinal);
            parameters["signals"] = new JsonArray([.. signals.Select(track => SignalTrackParameters(track, pairs))]);
        }

        AddWatchAudio(parameters, given.Audio);
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

    /// <summary>The monitors, and the audio track as one.</summary>
    private static int OtherTrackCount(WatchTracks tracks) => (tracks.Monitors?.Length ?? 0) + (tracks.Audio is null ? 0 : 1);

    private static void CheckTrackCount(int valueTracks, int signalTracks, int otherTracks)
    {
        if (valueTracks + signalTracks + otherTracks == 0)
        {
            throw new McpException(
                "tracks needs at least one track: {properties: [{node, property}]}, {expressions: [{name, expression}]}, "
                    + "{signals: [{node, signal}]}, {monitors: [\"frame_ms\"]}, {audio: {players: {}}} or {audio: {buses: [\"*\"]}}."
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
            parameters["call"] = CallOptionParameters(call, "options.call");
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

    /// <summary>Adds the audio track, {players?: {under?}, buses?, thresholdDb?}, with players or buses or both; the bridge
    /// resolves under and refuses a node that does not exist, and resolves each bus by name.</summary>
    private static void AddWatchAudio(JsonObject parameters, WatchAudioTrack? audio)
    {
        if (audio is null)
        {
            return;
        }

        if (audio.Players is null && audio.Buses is null)
        {
            throw new McpException(
                "tracks.audio needs players or buses: {players: {}} follows every audio player under the root, {players: {under: "
                    + "\"<node>\"}} those at or under a node, {buses: [\"Effects\"]} reads the named buses' levels and {buses: [\"*\"]} "
                    + "every bus's."
            );
        }

        JsonObject spec = [];
        if (audio.Players is { } players)
        {
            spec["players"] = PlayersParameters(players);
        }

        AddWatchBuses(spec, audio);
        parameters["audio"] = spec;
    }

    private static JsonObject PlayersParameters(WatchAudioPlayers players)
    {
        JsonObject spec = [];
        if (players.Under is { } under)
        {
            spec["under"] = CheckName(
                under,
                "under",
                "Pass a node's path (/root/Main/Level), a path under the root (Main/Level) or a name, or leave under out for the root."
            );
        }

        return spec;
    }

    /// <summary>Adds the audio track's buses, each named once and at most <see cref="MaxWatchBuses"/>, or ["*"] alone, and
    /// thresholdDb, which needs buses; a threshold left out is the bridge's default.</summary>
    private static void AddWatchBuses(JsonObject spec, WatchAudioTrack audio)
    {
        if (audio.Buses is not { } buses)
        {
            if (audio.ThresholdDb is not null)
            {
                throw new McpException("tracks.audio.thresholdDb applies to buses; add tracks.audio.buses or drop thresholdDb.");
            }

            return;
        }

        spec["buses"] = new JsonArray([.. CheckBuses(buses).Select(bus => (JsonNode)bus)]);
        if (audio.ThresholdDb is double threshold)
        {
            spec["thresholdDb"] = threshold is >= MinWatchThresholdDb and <= MaxWatchThresholdDb
                ? threshold
                : throw new McpException(
                    $"tracks.audio.thresholdDb must be between {MinWatchThresholdDb.ToString(CultureInfo.InvariantCulture)} and "
                        + $"{MaxWatchThresholdDb.ToString(CultureInfo.InvariantCulture)}; got {threshold.ToString(CultureInfo.InvariantCulture)}."
                );
        }
    }

    private static string[] CheckBuses(string[] buses)
    {
        if (buses.Length == 0)
        {
            throw new McpException("tracks.audio.buses is empty; name the buses to read, such as [\"Effects\"], or [\"*\"] for every bus.");
        }

        if (buses.Contains(EveryBus, StringComparer.Ordinal))
        {
            return buses.Length == 1
                ? buses
                : throw new McpException(
                    $"tracks.audio.buses takes \"*\" alone: [\"*\"] reads every bus, Master included, up to {MaxWatchBuses}; or name the "
                        + "buses without it."
                );
        }

        if (buses.Length > MaxWatchBuses)
        {
            throw new McpException(
                $"tracks.audio.buses names {buses.Length} buses; at most {MaxWatchBuses}, or [\"*\"] for every bus up to {MaxWatchBuses}."
            );
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string bus in buses)
        {
            if (string.IsNullOrWhiteSpace(bus))
            {
                throw new McpException(
                    "tracks.audio.buses has an empty name; pass a bus's name as the Audio bus layout shows it, such as Master or Effects."
                );
            }

            if (!names.Add(bus))
            {
                throw new McpException($"tracks.audio.buses names '{bus}' twice; each bus once.");
            }
        }

        return buses;
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
