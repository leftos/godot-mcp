using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GodotMcp.Server.Tools;

/// <summary>
/// One step of batch_drive: a runtime tool {tool, args} or an assertion {assert, …}, either with an optional session.
/// </summary>
internal sealed record BatchStep(
    [property: Description("A runtime tool's name, e.g. click or take_screenshot; leave out for an assertion.")] string? Tool = null,
    [property: Description("The tool's arguments, as the tool takes them on its own.")] JsonObject? Args = null,
    [property: Description("An assertion: property, expression, wait, no_errors, screenshot or fired; leave out for a tool step.")]
        string? Assert = null,
    [property: Description(
        "property, wait: the node, as wait_for takes it; expression: its node input, optional; fired: the node the nearest "
            + "earlier tool step's fired list must carry the signal on, its absolute path or a path tail."
    )]
        string? Node = null,
    [property: Description(
        "wait: with node or text, the node present or a visible Control showing the text (true), or the node absent or none "
            + "showing it (false), as wait_for's exists."
    )]
        bool? Exists = null,
    [property: Description("property, wait: the property to compare; a subproperty path such as position:x works too.")] string? Property = null,
    [property: JsonPropertyName("equals")]
    [property: Description(
        "property, wait: the JSON value the property must equal, as wait_for compares it; null is not supported; fired: the "
            + "array of arguments the emission must have carried, any arguments matching when left out."
    )]
        JsonElement? EqualsValue = null,
    [property: Description(
        "wait: with node, the signal whose next emission is waited for; fired: the signal the nearest earlier tool step's "
            + "fired list must carry for that node."
    )]
        string? Signal = null,
    [property: Description("expression, wait: a Godot Expression that must return true, as wait_for evaluates it.")] string? Expression = null,
    [property: Description(
        "wait: true, alone, to wait for the UI to change from the snapshot taken when the first input gesture since launch, or "
            + "since the last met uiChanged wait, started, as wait_for's uiChanged."
    )]
        bool? UiChanged = null,
    [property: Description("wait: alone, the milliseconds of game time to wait for, 1 to 120000, as wait_for's gameMs.")] int? GameMs = null,
    [property: Description("wait: alone, the unpaused process frames to wait for, 1 to 7200, as wait_for's frames.")] int? Frames = null,
    [property: Description(
        "property, expression: 0 by default, checked once, now, even while paused; wait: 10000 by default (gameMs + 10000 for "
            + "gameMs, 10 s + 100 ms a frame for frames). 0 to 120000, load-adjusted, as wait_for takes it."
    )]
        int? TimeoutMs = null,
    [property: Description("screenshot: the baseline's name, as save_screenshot_baseline saved it.")] string? Name = null,
    [property: Description("screenshot: how many levels (0-255) a channel may differ by and still count as unchanged; 2 by default.")]
        int? Tolerance = null,
    [property: Description("screenshot: the largest share of changed pixels, 0 to 1, that still matches; 0 by default.")]
        double? MaxChangedRatio = null,
    [property: Description("The session this step addresses; the batch's session when left out.")] string? Session = null,
    [property: Description("wait: without node, the text exactly one visible Control must show, as wait_for's condition.text.")] string? Text = null,
    [property: Description("wait: with text, the node whose visible Controls are scanned, as wait_for's condition.under.")] string? Under = null
);
