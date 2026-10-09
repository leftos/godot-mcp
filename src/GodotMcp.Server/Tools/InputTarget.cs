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
            + "viewport's Camera2D, CanvasLayer or current Camera3D, and out through any SubViewportContainer); with item, also "
            + "a PopupMenu. An absolute node "
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
        string? Under = null,
    [property: Description(
        "Beside an element naming an ItemList, TabBar, TabContainer or Tree (beside text only for a Control that shows text "
            + "and draws items): an item drawn inside it, aimed at the item's centre (on a Tree's column 0, past the fold "
            + "arrow's indent); exactly one of text, index or path, and on a Tree an optional column. A hidden item is refused, "
            + "one scrolled out of view or under a collapsed Tree item is refused with what to do first, and a disabled one is "
            + "aimed at and reported disabled. Beside an element naming an OptionButton, a MenuButton or an open PopupMenu: an "
            + "item of its popup, by text or index, found by moving the pointer over the popup; click and a mouse_button press "
            + "open a closed button's popup first with a real click (aimedAt.opened), hover and the rest refuse it (click the "
            + "button first). An item with a submenu is hovered until the submenu opens and not pressed (aimedAt.item.submenu "
            + "names the submenu to target next). A separator, a disabled item, a popup filtered by its search bar, a native "
            + "(not embedded) popup and a drag end are refused. Beside an element or text naming a RichTextLabel: a tooltip "
            + "span (a run of text with a [hint], [url tooltip] or image tooltip), by its tooltip text or its reading-order "
            + "index, aimed at the centre of its first line (aimedAt.item.rect; rects one per line it wraps onto)."
    )]
        InputItem? Item = null
)
{
    /// <summary>
    /// The target as the bridge reads it: <c>{element, offset?, item?}</c>, <c>{text, under?, item?}</c> or <c>{x, y}</c>.
    /// </summary>
    /// <exception cref="McpException">
    /// The target is missing, names none or more than one of an element, a text and a point, has an offset without an
    /// element or without its x or y, has under without a text, or has an item without an element or a text, with other
    /// than one of text, index and path, with an empty path or a blank text in it, or with a negative index or column.
    /// </exception>
    public static JsonObject ToBridge(InputTarget? target, string parameter)
    {
        CheckOffset(target, parameter);
        CheckUnder(target, parameter);
        CheckItem(target, parameter);
        return target?.ToBridgeOrNull()
            ?? throw new McpException($"{parameter} needs exactly one of element, text, or both x and y; got {Show(target)}.");
    }

    private static void CheckItem(InputTarget? target, string parameter)
    {
        if (target?.Item is not InputItem item)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(target.Element) && string.IsNullOrWhiteSpace(target.Text))
        {
            throw new McpException($"{parameter}.item takes element or text beside it; got {Show(target)}.");
        }

        string? refusal = item.ShapeRefusal();
        if (refusal is not null)
        {
            throw new McpException($"{parameter}.{refusal}; got {Show(target)}.");
        }
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

        return WithItem(bridge);
    }

    private JsonObject ElementToBridge()
    {
        JsonObject bridge = new() { ["element"] = Element };
        if (Offset is not null)
        {
            bridge["offset"] = Offset.ToBridge();
        }

        return WithItem(bridge);
    }

    private JsonObject WithItem(JsonObject bridge)
    {
        if (Item is not null)
        {
            bridge["item"] = Item.ToBridge();
        }

        return bridge;
    }

    private static string Show(InputTarget? target)
    {
        string offset = ShowOffset(target?.Offset);
        string point = $"x: {Show(target?.X)}, y: {Show(target?.Y)}";
        string anchors = $"element: {target?.Element ?? "null"}, {point}{ShowText("text", target?.Text)}{ShowText("under", target?.Under)}";
        return $"{{{anchors}{offset}{ShowItem(target)}}}";
    }

    private static string ShowItem(InputTarget? target) => target?.Item is InputItem item ? $", item: {item.Show()}" : "";

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

/// <summary>
/// An item drawn inside the ItemList, TabBar, TabContainer or Tree an element or text target names, or in the popup of an
/// OptionButton, a MenuButton or a PopupMenu: by its text, its index, or on a Tree its path; a Tree also takes the column
/// aimed at.
/// </summary>
internal sealed record InputItem(
    [property: Description(
        "The item's text as drawn (translated as the list translates it), matched exactly (case kept, surrounding whitespace "
            + "trimmed): an ItemList item's, a tab's title, a Tree item's text in column at any depth, or a popup item's; on a "
            + "RichTextLabel, a tooltip span's tooltip text, not the text it shows. Several items reading it are refused, "
            + "listed; narrow with index or path."
    )]
        string? Text = null,
    [property: Description(
        "The item's index in an ItemList, the tab's index in a TabBar or TabContainer, the item's index in a popup "
            + "(separators counted, as OptionButton.selected counts them), or a RichTextLabel's tooltip span's in reading "
            + "order among the lines shown, from 0; refused on a Tree."
    )]
        int? Index = null,
    [property: Description(
        "A Tree only: the texts of the item and its ancestors in column 0, from the first level shown (the root's children "
            + "when the root is hidden), e.g. [\"Weapons\", \"Sword\"]."
    )]
        string[]? Path = null,
    [property: Description("A Tree only: the column aimed at and text is matched in, from 0; 0 when left out.")] int? Column = null
)
{
    /// <summary>The item as the bridge reads it: its non-null keys.</summary>
    public JsonObject ToBridge()
    {
        JsonObject bridge = [];
        if (Text is not null)
        {
            bridge["text"] = Text;
        }

        if (Index is int index)
        {
            bridge["index"] = index;
        }

        if (Path is not null)
        {
            bridge["path"] = new JsonArray([.. Path.Select(text => (JsonNode?)text)]);
        }

        if (Column is int column)
        {
            bridge["column"] = column;
        }

        return bridge;
    }

    /// <summary>
    /// Why the item's shape is refused, after the parameter's name and before what was given, or null when it is not: other
    /// than one of text, index and path; an empty path or one with a blank text; a negative index or column.
    /// </summary>
    public string? ShapeRefusal()
    {
        if (KeyCount() != 1)
        {
            return "item takes exactly one of text, index or path";
        }

        if (Text is not null && string.IsNullOrWhiteSpace(Text))
        {
            return "item.text is blank; give the item's shown text, or index or path";
        }

        return PathOrNumberRefusal();
    }

    private string? PathOrNumberRefusal()
    {
        if (Path is not null && (Path.Length == 0 || Path.Any(string.IsNullOrWhiteSpace)))
        {
            return "item.path needs at least one non-empty text";
        }

        return Index < 0 || Column < 0 ? "item.index and item.column must be 0 or more" : null;
    }

    private int KeyCount() => (Text is null ? 0 : 1) + (Index is null ? 0 : 1) + (Path is null ? 0 : 1);

    /// <summary>The item as a refusal quotes it, every key shown.</summary>
    public string Show()
    {
        string text = Text is null ? "null" : $"'{Text}'";
        string path = Path is null ? "null" : $"[{string.Join(", ", Path.Select(step => $"'{step}'"))}]";
        return $"{{text: {text}, index: {ShowInt(Index)}, path: {path}, column: {ShowInt(Column)}}}";
    }

    private static string ShowInt(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";
}
