using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>Which overload cs_call calls and with which type arguments, how long it waits, and how it writes the value.</summary>
internal sealed record CsCallOptions(
    [property: Description(
        "The overload's parameter types in cs_members' spelling, [\"float\"] or [\"CsProbe.IGreeter\", \"string\"]; needed only when "
            + "more than one overload fits the arguments."
    )]
        string[]? Signature = null,
    [property: Description("A generic method's type arguments, as C# keywords or full names: [\"int\"], [\"CsProbe.Point2\"].")]
        string[]? TypeArgs = null,
    [property: Description("Also return a handle to the value, usable as {handle} in a later C# call; false by default.")] bool? Keep = null,
    [property: Description("How long to wait for a returned Task or ValueTask, in milliseconds, 1 to 120000; 10000 by default.")]
        int? TimeoutMs = null,
    [property: Description("How many levels of nested objects are written, 1 to 32; 8 by default.")] int? MaxDepth = null
);
