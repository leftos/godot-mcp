using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>Where an input tool aims: a Control, or a point in viewport coordinates; exactly one of the two.</summary>
internal sealed record InputTarget(
    [property: Description(
        "A Control to aim at: an absolute node path (/root/Main/Button), a path under the root (Main/Button), a node "
            + "name, found breadth first, or %Name for a node saved with a unique name (alone, looked up in every scene; or after "
            + "its owner's path). Its point is the centre of its global rect. Leave x and y out when this is set."
    )]
        string? Element = null,
    [property: Description("A point's x in viewport coordinates, as get_ui_elements reports rects; needs y and no element.")] double? X = null,
    [property: Description("A point's y in viewport coordinates, as get_ui_elements reports rects; needs x and no element.")] double? Y = null
)
{
    /// <summary>The target as the bridge reads it: <c>{element}</c> or <c>{x, y}</c>.</summary>
    /// <exception cref="McpException">The target is missing, or names neither or both of an element and a point.</exception>
    public static JsonObject ToBridge(InputTarget? target, string parameter) =>
        target?.ToBridgeOrNull()
        ?? throw new McpException(
            $"{parameter} needs either element, or both x and y, and not both; got "
                + $"{{element: {target?.Element ?? "null"}, x: {Show(target?.X)}, y: {Show(target?.Y)}}}."
        );

    private JsonObject? ToBridgeOrNull()
    {
        bool hasElement = !string.IsNullOrWhiteSpace(Element);
        if (hasElement && X is null && Y is null)
        {
            return new JsonObject { ["element"] = Element };
        }

        return !hasElement && X is double x && Y is double y ? new JsonObject { ["x"] = x, ["y"] = y } : null;
    }

    private static string Show(double? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
}
