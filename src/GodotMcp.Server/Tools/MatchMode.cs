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

    /// <summary>The refusal of a match given without a text.</summary>
    public const string NoTextRefusal = "match applies only to a text condition or target; this one has no text.";

    /// <summary>The sentence each text field's description carries about match.</summary>
    public const string DescriptionSentence =
        "match picks how it is compared: \"exact\" (the default, the whole shown text), \"prefix\" (the shown text starts with "
        + "it, so a multi-line label matches by its first line's opening words) or \"contains\".";

    private static readonly string[] Modes = [Exact, "prefix", "contains"];

    /// <summary>Refuses a match that is not one of the three modes, or one given where there is no text.</summary>
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

        if (!Modes.Contains(match, StringComparer.Ordinal))
        {
            throw new McpException($"match is \"{match}\"; it must be \"exact\", \"prefix\" or \"contains\".");
        }
    }

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
