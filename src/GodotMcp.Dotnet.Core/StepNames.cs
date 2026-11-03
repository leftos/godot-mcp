namespace GodotMcp.Dotnet.Core;

/// <summary>
/// A scratch root's steps as a call names them: the index of the one step whose name is the one given, or the refusal the
/// bridge's <c>_step_index</c> spells in the same words, so call_method and call_game_tool answer a name alike.
/// </summary>
public static class StepNames
{
    /// <summary>
    /// The index of the one of <paramref name="names"/> that is <paramref name="stepName"/>, compared ordinally and
    /// case-sensitively; a name matching none is refused with every step's index and name, and one matching several with the
    /// indices it matches.
    /// </summary>
    public static StepMatch Index(string stepName, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(stepName);
        ArgumentNullException.ThrowIfNull(names);
        List<int> matched = [];
        for (int index = 0; index < names.Count; index++)
        {
            if (string.Equals(names[index], stepName, StringComparison.Ordinal))
            {
                matched.Add(index);
            }
        }
        return matched switch
        {
            [int only] => new StepMatch(only, null),
            [] => new StepMatch(null, $"PlayStep \"{stepName}\" matches no step; {Listed(names)}"),
            _ => new StepMatch(null, Several(stepName, matched)),
        };
    }

    /// <summary>The steps by index, <c>its steps are 0: open, 1: move</c>; or that the root has none.</summary>
    private static string Listed(IReadOnlyList<string> names) =>
        names.Count == 0 ? "it has no steps" : "its steps are " + string.Join(", ", names.Select((name, index) => $"{index}: {name}"));

    private static string Several(string stepName, List<int> matched) =>
        $"PlayStep \"{stepName}\" names {matched.Count} steps ({string.Join(", ", matched)}); call it by index";
}

/// <summary>A step name's one index, or why it names no step or several: exactly one of the two is set.</summary>
public sealed record StepMatch(int? Index, string? Refusal);
