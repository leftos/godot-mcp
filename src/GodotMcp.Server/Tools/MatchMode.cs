using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>
/// How a <c>{text}</c> condition or target compares its text with a Control's shown text, both trimmed and case kept:
/// <c>exact</c> (the whole shown text, the default), <c>prefix</c> (the shown text starts with it) or <c>contains</c>.
/// </summary>
internal static class MatchMode
{
    /// <summary>The default: the whole shown text equals the text.</summary>
    public const string Exact = "exact";

    /// <summary>The refusal of a match on a target given without a text.</summary>
    public const string NoTextRefusal = "match applies only to a text condition or target; this one has no text.";

    /// <summary>The refusal of a match on a condition given without a text or an equals.</summary>
    public const string NoTextOrEqualsRefusal = "match applies only to a text condition or target, or a string equals; this one has neither.";

    /// <summary>The sentence each text field's description carries about match.</summary>
    public const string DescriptionSentence =
        "match picks how it is compared: \"exact\" (the default, the whole shown text), \"prefix\" (the shown text starts with "
        + "it, so a multi-line label matches by its first line's opening words) or \"contains\".";

    private static readonly string[] Modes = [Exact, "prefix", "contains"];

    /// <summary>Refuses a target's match that is not one of the three modes, or one given where there is no text.</summary>
    /// <exception cref="McpException">match is given without a text, or is not exact, prefix or contains.</exception>
    public static void Check(string? match, bool hasText)
    {
        if (match is null)
        {
            return;
        }

        if (!hasText)
        {
            throw new McpException(NoTextRefusal);
        }

        CheckKnown(match);
    }

    /// <summary>
    /// Refuses a match that is not one of the three modes, one given with neither a text nor an equals, or a prefix or
    /// contains match on an equals that is not a string.
    /// </summary>
    /// <exception cref="McpException">match is given without a text or an equals, is not exact, prefix or contains, or is
    /// prefix or contains with no text and an equals that is not a string.</exception>
    public static void Check(string? match, bool hasText, JsonElement? equals)
    {
        if (match is null)
        {
            return;
        }

        if (!hasText && equals is null)
        {
            throw new McpException(NoTextOrEqualsRefusal);
        }

        CheckKnown(match);
        if (!hasText && equals is { } value)
        {
            CheckComparesStrings(match, value);
        }
    }

    /// <exception cref="McpException">match is not exact, prefix or contains.</exception>
    private static void CheckKnown(string match)
    {
        if (!Modes.Contains(match, StringComparer.Ordinal))
        {
            throw new McpException($"match is \"{match}\"; it must be \"exact\", \"prefix\" or \"contains\".");
        }
    }

    /// <exception cref="McpException">match is prefix or contains and equals is not a string.</exception>
    private static void CheckComparesStrings(string match, JsonElement equals)
    {
        if (match != Exact && equals.ValueKind != JsonValueKind.String)
        {
            throw new McpException($"match \"{match}\" compares strings; equals here is {KindOf(equals)}.");
        }
    }

    private static string KindOf(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            JsonValueKind.Object => "an object",
            JsonValueKind.Array => "an array",
            _ => "null",
        };

    /// <summary>Sets <c>match</c> on a bridge object when it is not exact, and leaves it off otherwise.</summary>
    public static void ToBridge(string? match, JsonObject bridge)
    {
        if (match is null or Exact)
        {
            bridge.Remove("match");
            return;
        }

        bridge["match"] = match;
    }
}
