using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodotMcp.Server.Tools;

/// <summary>
/// What wait_for waits for, exactly one kind: {node, exists}, {node, property, equals}, {node, signal}, {expression}
/// (node optional), {uiChanged: true}, {gameMs}, {frames}, {text} (under and exists optional), {node, stable} (property
/// optional) or {node, state, equals}.
/// </summary>
internal sealed record WaitCondition(
    [property: Description(
        "The node: an absolute path (/root/Main/Player), a path under the root (Main/Player), the first node of that "
            + "name, breadth first from the root, or %Name for a node saved with a unique name (alone, looked up in every scene; "
            + "or after its owner's path)."
    )]
        string? Node = null,
    [property: Description(
        "With node or text: wait until the node is present, or a visible Control shows the text (true), or until the node is "
            + "absent, or no visible Control shows the text (false); with text, true when left out."
    )]
        bool? Exists = null,
    [property: Description(
        "With node and equals, or with node and stable: the property to read each frame; a subproperty path such as position:x works too."
    )]
        string? Property = null,
    [property: JsonPropertyName("equals")]
    [property: Description(
        "The JSON value the property, or with state the state's key, must equal, compared as run_script returns values "
            + "(Vector2 as {x, y}, and so on); "
            + "numbers match within 1e-6. null is not supported: to wait for a reference to clear, use {expression}, e.g. "
            + "node.target == null."
    )]
        JsonElement? EqualsValue = null,
    [property: Description("With node: wait for the node's next emission of this signal; the result carries its args.")] string? Signal = null,
    [property: Description(
        "A Godot Expression evaluated each frame, met when it returns true. Its inputs are node (when node is given, which is "
            + "also its base instance), root, tree, Input and Engine; other singletons are out of its reach."
    )]
        string? Expression = null,
    [property: Description(
        "true, alone: wait until the UI (the visible Controls, the focus owner, the top popup) differs from the snapshot taken "
            + "when the first input gesture since launch, or since the last met uiChanged wait, started. Met, it carries "
            + "{appeared, disappeared, appearedCount, disappearedCount}, plus focus and popup {before, after} when they changed."
    )]
        bool? UiChanged = null,
    [property: Description(
        "Alone: wait until this many milliseconds of game time, 1 to 120000, have passed since the wait started: process delta "
            + "summed over unpaused frames, so it follows Engine.time_scale and stops while the game is paused. Met, value is the "
            + "game milliseconds reached, which may pass it by up to one frame."
    )]
        int? GameMs = null,
    [property: Description(
        "Alone: wait until this many process frames, 1 to 7200, have run unpaused since the wait started. Met, value is the frames counted."
    )]
        int? Frames = null,
    [property: Description(
        "Without node: wait until exactly one visible Control shows this text, matched as a click's {text} target matches it: "
            + "the shown text trimmed equals this text trimmed, case kept. Met, value is {path, shown, rect}, rect in viewport "
            + "coordinates, as get_ui_elements gives it. Several Controls showing it fail the wait with their list; narrow it with "
            + "under. With exists: false, wait until no visible Control shows it."
    )]
        string? Text = null,
    [property: Description(
        "With text: the node whose visible Controls are scanned, named as node is; the whole tree when left out. While it is "
            + "missing, hidden or being freed, no Control under it shows the text."
    )]
        string? Under = null,
    [property: Description(
        "With node: wait until the node's global rect (a Control), global_transform (a Node2D or Node3D) or property holds "
            + "unchanged for this many process frames, 1 to 600, so a press can wait for a layout to settle. Met, value is "
            + "{frames, value}; while it moves, last shows the value so far."
    )]
        int? Stable = null,
    [property: Description(
        "With node and equals: a key path into the state get_game_state reads for the node, which must be in the mcp_state "
            + "group, read each frame: a key, or a dotted path with list indexes (seats[0].hp), as get_game_state's keys take "
            + "it; $ is the whole state. A path the state lacks is not met, its last null; a node outside the group, or one "
            + "whose state cannot be read, fails the wait."
    )]
        string? State = null
);
