using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>Whether describe_class adds a class's inherited members, and which page of its methods it returns.</summary>
internal sealed record DescribeOptions(
    [property: Description(
        "Also list the members the class inherits: an engine class's from every class above it, a script class's from its "
            + "engine base; false by default."
    )]
        bool? Inherited = null,
    [property: Description("How many methods of the sorted list to skip: 0 (the default), or the offset of the previous page plus its limit.")]
        int? Offset = null,
    [property: Description("How many methods to return, 1 to 500; 100 by default.")] int? Limit = null
);
