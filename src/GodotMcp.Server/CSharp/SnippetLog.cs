using System.Text.Json.Nodes;

namespace GodotMcp.Server.CSharp;

/// <summary>
/// The lines a run_csharp snippet wrote with <c>Log</c>, and how many past the helper's cap it dropped. A successful run
/// carries it inside its result; a snippet that threw carries it beside the refusal, since a throw returns no result.
/// </summary>
internal sealed record SnippetLog(IReadOnlyList<string> Lines, int Dropped)
{
    /// <summary>A snippet that logged nothing: the reply carries no log at all.</summary>
    public static SnippetLog Empty { get; } = new([], 0);

    /// <summary>Whether the snippet logged no line.</summary>
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>
    /// The log <paramref name="owner"/> carries — <c>log</c>, and <c>logOmitted</c> when the helper dropped lines —
    /// or <see cref="Empty"/> when it carries none.
    /// </summary>
    public static SnippetLog Read(JsonNode? owner)
    {
        if (owner?["log"] is not JsonArray lines)
        {
            return Empty;
        }

        return new SnippetLog([.. lines.Select(line => line?.GetValue<string>() ?? "null")], owner["logOmitted"]?.GetValue<int>() ?? 0);
    }

    /// <summary>Adds the log to <paramref name="result"/> when the snippet wrote one, with the dropped count above 0.</summary>
    public JsonObject AddTo(JsonObject result)
    {
        if (IsEmpty)
        {
            return result;
        }

        result["log"] = new JsonArray([.. Lines.Select(line => (JsonNode?)JsonValue.Create(line))]);
        if (Dropped > 0)
        {
            result["logOmitted"] = Dropped;
        }

        return result;
    }
}
