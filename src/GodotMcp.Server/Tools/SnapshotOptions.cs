using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>What snapshot_subtree captures of each node, and how large a subtree it takes.</summary>
internal sealed record SnapshotOptions(
    [property: Description(
        "Only these properties, where a node has them (groups too, when named); every inspector property and groups when left out."
    )]
        string[]? Properties = null,
    [property: Description("Property names to leave out of every node (groups included).")] string[]? Ignore = null,
    [property: Description("The most nodes the subtree may hold, at least 1; 2000 by default. A larger subtree is refused with its count.")]
        int? MaxNodes = null
);
