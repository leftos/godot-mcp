using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>What a watch samples each frame: properties of nodes and Godot Expressions, at most 32 together.</summary>
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
        WatchExpressionTrack[]? Expressions = null
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

/// <summary>When a watch samples and the method it calls in its first frame.</summary>
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
        string? Unit = null
);
