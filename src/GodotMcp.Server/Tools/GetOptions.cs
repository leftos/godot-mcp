using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>How cs_get writes the value it reads, and whether it keeps a handle to it.</summary>
internal sealed record GetOptions(
    [property: Description("How many levels of nested objects are written, 1 to 32; 8 by default.")] int? MaxDepth = null,
    [property: Description("Also return a handle to the value, usable as {handle} in a later C# call; false by default.")] bool? Keep = null
);
