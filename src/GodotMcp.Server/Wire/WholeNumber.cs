using System.Text.Json.Nodes;

namespace GodotMcp.Server.Wire;

/// <summary>A whole number read from the bridge's JSON, where GDScript's parser reads every number as a float.</summary>
internal static class WholeNumber
{
    /// <summary>
    /// Reads <paramref name="node"/> as a whole number: an integer, or a float with no fractional part (a bridge may echo 7 as
    /// 7.0). Any sign is accepted; a caller that needs a non-negative number checks it.
    /// </summary>
    /// <returns>Whether <paramref name="node"/> is a whole number; <paramref name="number"/> is 0 when it is not.</returns>
    public static bool TryRead(JsonNode? node, out long number)
    {
        number = 0;
        if (node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue(out number))
        {
            return true;
        }

        if (value.TryGetValue(out double floating) && floating == Math.Floor(floating))
        {
            number = (long)floating;
            return true;
        }

        return false;
    }
}
