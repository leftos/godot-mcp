using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>
/// What a watch samples each frame and the signals it records: properties of nodes and Godot Expressions (at most 32 together),
/// signals (at most 16 tracks) and performance monitors (at most 16).
/// </summary>
internal sealed record WatchTracks(
    [property: Description(
        "Property tracks, each {node, property, name?, minDelta?}: a property of a node, or a path into one as wait_for takes it "
            + "(position:y, modulate:a), read each frame with get_indexed, so a non-exported C# field Godot can marshal reads too."
    )]
        WatchPropertyTrack[]? Properties = null,
    [property: Description(
        "Expression tracks, each {name, expression, node?, minDelta?}: a Godot Expression run each frame with wait_for's inputs "
            + "(node when given, root, tree, Input, Engine), such as root.gui_get_focus_owner() or node.get_children()."
    )]
        WatchExpressionTrack[]? Expressions = null,
    [property: Description(
        "Signal tracks, each {node, signal} or {group, signal}: every emission is recorded with its frame, game time, emitter and "
            + "arguments; a group is resolved once at start, and a member without the signal is listed in skipped. At most 16, "
            + "connecting at most 200 nodes."
    )]
        WatchSignalTrack[]? Signals = null,
    [property: Description(
        "Monitors read once a sampled frame and returned as a summary {name, samples, p50, p95, p99, max, maxAt, mean, spikes?}: "
            + "frame_ms (the wall time between two frames, with over: {budget, count, frames} against options.budgetMs), a "
            + "built-in monitor by its Performance.get_monitor_name path (object/nodes, raster/total_draw_calls), or a custom "
            + "monitor's id as Performance.add_custom_monitor registered it. At most 16; time/fps, time/process, "
            + "time/physics_process and time/navigation_process are refused, since Godot sets them once a second."
    )]
        string[]? Monitors = null
);

/// <summary>A signal a watch records each emission of, on one node or on every member of a group.</summary>
internal sealed record WatchSignalTrack(
    [property: Description("The emitting node, named as a property track's node; give node or group, not both.")] string? Node = null,
    [property: Description("A group: every node in it at start that has the signal is connected.")] string? Group = null,
    [property: Description("The signal's name, as Godot lists it (a C# [Signal] without its EventHandler suffix).")] string Signal = ""
);

/// <summary>A property of a node a watch samples each frame.</summary>
internal sealed record WatchPropertyTrack(
    [property: Description(
        "The node: an absolute path (/root/Main/Player), a path under the root (Main/Player), a name, or %Name for a node saved "
            + "with a unique name. It must exist when the watch starts."
    )]
        string Node,
    [property: Description("The property, or a path into one: position, position:x, modulate:a.")] string Property,
    [property: Description("The track's key in the result instead of its node and property, which are still listed beside it; optional.")]
        string? Name = null,
    [property: Description(
        "Numeric tracks only (an int, a float or a vector), greater than 0: a sample is kept only when it moved at least this far "
            + "from the last one kept (a vector when any component did); left out, any change over 1e-6 is kept."
    )]
        double? MinDelta = null
);

/// <summary>A Godot Expression a watch runs each frame.</summary>
internal sealed record WatchExpressionTrack(
    [property: Description("The track's key in the result.")] string Name,
    [property: Description(
        "A Godot Expression with wait_for's inputs: node (when node is given, also the base instance), root, tree, Input and "
            + "Engine. It runs every frame, so it should only read."
    )]
        string Expression,
    [property: Description("The node the expression sees as node; it must exist when the watch starts. Optional.")] string? Node = null,
    [property: Description("As a property track's minDelta: numeric values only, greater than 0.")] double? MinDelta = null
);

/// <summary>How long a watch samples: unpaused process frames, or game milliseconds over them.</summary>
internal sealed record WatchWindow(
    [property: Description("Unpaused frames (physics ticks with unit physics) to sample, 1 to 7200.")] int? Frames = null,
    [property: Description(
        "Milliseconds of game time to sample, 1 to 120000: the sum of each unpaused frame's delta, so it follows "
            + "Engine.time_scale as the game's timers do."
    )]
        int? GameMs = null
);

/// <summary>When a watch samples, the method it calls in its first frame, and the frame_ms monitor's budget.</summary>
internal sealed record WatchOptions(
    [property: Description(
        "{node, method, args}: a method the bridge calls in the watch's first frame, before its first sample, so the window "
            + "counts from the method's entry; a coroutine is not awaited. start's and run's replies add call: {value}; a "
            + "refused call, or an error the method raises, fails the call and ends the watch."
    )]
        MethodCall? Call = null,
    [property: Description(
        "process (the default): sample at the start of each process frame, before the nodes' _process; physics: at the start "
            + "of each physics tick, with frames counted in ticks."
    )]
        string? Unit = null,
    [property: Description(
        "The frame_ms monitor's budget in ms, above 0 and at most 10000: frames over it are counted in over and listed in "
            + "spikes; left out, 1.5 frames at Engine.max_fps (60 when uncapped), 25 ms at 60."
    )]
        double? BudgetMs = null
);
