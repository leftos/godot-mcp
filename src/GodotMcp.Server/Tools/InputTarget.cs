using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Where an input tool aims: a node (a Control, or a 2D or 3D world node with an optional offset inside it), a visible
/// Control by the text it shows (optionally only under a node), or a point in viewport coordinates; exactly one of the three.
/// </summary>
internal sealed record InputTarget(
    [property: Description(
        "A node to aim at: a Control (the centre of its rect) or a 2D or 3D world node (its origin plus offset, through its "
            + "viewport's Camera2D, CanvasLayer or current Camera3D, and out through any SubViewportContainer). An absolute node "
            + "path (/root/Main/Button), a path under the root (Main/Button), a node name, found breadth first, or %Name for a "
            + "node saved with a unique name (alone, looked up in every scene; or after its owner's path). Leave x and y out when "
            + "this is set."
    )]
        string? Element = null,
    [property: Description("A point's x in viewport coordinates, as get_ui_elements reports rects; needs y, and no element or text.")]
        double? X = null,
    [property: Description("A point's y in viewport coordinates, as get_ui_elements reports rects; needs x, and no element or text.")]
        double? Y = null,
    [property: Description(
        "Only with element on a 2D or 3D world node: a point in the node's local space (2D pixels, 3D units) to aim at "
            + "instead of its origin; x and y required, z on a 3D node only. Refused on a Control."
    )]
        InputOffset? Offset = null,
    [property: Description(
        "The text a visible Control shows, matched exactly (case kept, surrounding whitespace trimmed), aimed at its centre as "
            + "an element Control is: a Button's (CheckBox, OptionButton, MenuButton…), Label's or LinkButton's text as "
            + "drawn, translated, a LineEdit's text or its placeholder while empty, a RichTextLabel's text without BBCode; "
            + "get_ui_elements reports the same text. A miss lists near misses and nodes of that name; several matches are "
            + "refused, listed, narrow them with under. Leave element, x and y out when this is set."
    )]
        string? Text = null,
    [property: Description(
        "Only with text: look for it only at or under this node, named as element names a node (a path, a unique bare name, or %Name)."
    )]
        string? Under = null
)
{
    /// <summary>The target as the bridge reads it: <c>{element, offset?}</c>, <c>{text, under?}</c> or <c>{x, y}</c>.</summary>
    /// <exception cref="McpException">
    /// The target is missing, names none or more than one of an element, a text and a point, has an offset without an
    /// element or without its x or y, or has under without a text.
    /// </exception>
    public static JsonObject ToBridge(InputTarget? target, string parameter)
    {
        CheckOffset(target, parameter);
        CheckUnder(target, parameter);
        return target?.ToBridgeOrNull()
            ?? throw new McpException($"{parameter} needs exactly one of element, text, or both x and y; got {Show(target)}.");
    }

    private static void CheckUnder(InputTarget? target, string parameter)
    {
        if (!string.IsNullOrWhiteSpace(target?.Under) && string.IsNullOrWhiteSpace(target.Text))
        {
            throw new McpException($"{parameter}.under narrows a text target, so it needs text; got {Show(target)}.");
        }
    }

    private static void CheckOffset(InputTarget? target, string parameter)
    {
        if (target?.Offset is not InputOffset offset)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(target.Element))
        {
            throw new McpException($"{parameter}.offset aims inside the node element names, so it needs element; got {Show(target)}.");
        }

        if (offset.X is null || offset.Y is null)
        {
            throw new McpException($"{parameter}.offset needs x and y; got {Show(target)}.");
        }
    }

    private JsonObject? ToBridgeOrNull()
    {
        bool hasElement = !string.IsNullOrWhiteSpace(Element);
        bool hasText = !string.IsNullOrWhiteSpace(Text);
        if (AnchorCount(hasElement, hasText) != 1)
        {
            return null;
        }

        if (hasElement)
        {
            return ElementToBridge();
        }

        return hasText ? TextToBridge() : PointToBridge();
    }

    private int AnchorCount(bool hasElement, bool hasText) => (hasElement ? 1 : 0) + (hasText ? 1 : 0) + (X is not null || Y is not null ? 1 : 0);

    private JsonObject? PointToBridge() => X is double x && Y is double y ? new JsonObject { ["x"] = x, ["y"] = y } : null;

    private JsonObject TextToBridge()
    {
        JsonObject bridge = new() { ["text"] = Text };
        if (!string.IsNullOrWhiteSpace(Under))
        {
            bridge["under"] = Under;
        }

        return bridge;
    }

    private JsonObject ElementToBridge()
    {
        JsonObject bridge = new() { ["element"] = Element };
        if (Offset is not null)
        {
            bridge["offset"] = Offset.ToBridge();
        }

        return bridge;
    }

    private static string Show(InputTarget? target)
    {
        string offset = ShowOffset(target?.Offset);
        string point = $"x: {Show(target?.X)}, y: {Show(target?.Y)}";
        return $"{{element: {target?.Element ?? "null"}, {point}{ShowText("text", target?.Text)}{ShowText("under", target?.Under)}{offset}}}";
    }

    private static string Show(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string ShowOffset(InputOffset? offset) =>
        offset is null ? "" : $", offset: {{x: {Show(offset.X)}, y: {Show(offset.Y)}, z: {Show(offset.Z)}}}";

    private static string ShowText(string key, string? value) => value is null ? "" : $", {key}: '{value}'";
}

/// <summary>
/// A point in a world node's local space that an element target aims at instead of the node's origin. X and Y are nullable
/// only so a missing one is refused by name rather than bound as 0.
/// </summary>
internal sealed record InputOffset(
    [property: Description("x in the node's local space: pixels on a 2D node, units on a 3D node. Required.")] double? X = null,
    [property: Description("y in the node's local space: pixels on a 2D node, units on a 3D node. Required.")] double? Y = null,
    [property: Description("z in a 3D node's local space, 0 when left out; refused on a 2D node.")] double? Z = null
)
{
    /// <summary>The offset as the bridge reads it: <c>{x, y}</c>, or <c>{x, y, z}</c> when z is given.</summary>
    public JsonObject ToBridge()
    {
        JsonObject bridge = new() { ["x"] = X, ["y"] = Y };
        if (Z is double z)
        {
            bridge["z"] = z;
        }

        return bridge;
    }
}
