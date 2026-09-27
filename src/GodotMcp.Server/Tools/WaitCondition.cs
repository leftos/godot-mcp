using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodotMcp.Server.Tools;

/// <summary>
/// What wait_for waits for, exactly one kind: {node, exists}, {node, property, equals}, {node, signal}, {expression}
/// (node optional) or {uiChanged: true}.
/// </summary>
internal sealed record WaitCondition(
    [property: Description(
        "The node: an absolute path (/root/Main/Player), a path under the root (Main/Player), or else the first node of that "
            + "name, breadth first from the root."
    )]
        string? Node = null,
    [property: Description("With node: wait until the node is present (true) or absent (false).")] bool? Exists = null,
    [property: Description("With node and equals: the property to read each frame; a subproperty path such as position:x works too.")]
        string? Property = null,
    [property: JsonPropertyName("equals")]
    [property: Description(
        "The JSON value the property must equal, compared as run_script returns values (Vector2 as {x, y}, and so on); "
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
        bool? UiChanged = null
);
