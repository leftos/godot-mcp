using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>Which keys of each marked node's state get_game_state returns, how many nodes it reads and how deep it writes.</summary>
internal sealed record StateOptions(
    [property: Description(
        "Only these keys of each node's state, each a key or a dotted path into it (seats[0].hp), returned under the key "
            + "as given; a key a node's state lacks is left out of it. A path that reaches a \"<depth limit: …>\" or "
            + "\"<size limit>\" mark keeps the mark as its value: raise maxDepth to read past it. Every key when left out."
    )]
        string[]? Keys = null,
    [property: Description("The most marked nodes to read, in tree order, 1 to 500; 50 by default. The rest are listed in omitted.")]
        int? MaxNodes = null,
    [property: Description(
        "How many levels of nested Dictionaries and Arrays to write, the returned value being the first, 1 to 8; 4 by default. "
            + "A deeper one is written as \"<depth limit: Dictionary>\" or \"<depth limit: Array>\"."
    )]
        int? MaxDepth = null,
    [property: Description(
        "Also hold the read in the session for diff_snapshots and return its stateId. The read is held flattened, one value per "
            + "leaf of each node's state keyed by its dotted path (seats[1].hp), a state that is not a Dictionary or Array with "
            + "entries under $, an errored node as its error; it shares snapshot_subtree's 16 held snapshots and ids. False by "
            + "default."
    )]
        bool? Keep = null
);
