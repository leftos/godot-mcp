using System.ComponentModel;

namespace GodotMcp.Server.Tools;

/// <summary>run_csharp's extra namespaces, how long it awaits the snippet, and how it writes the value.</summary>
internal sealed record RunCSharpOptions(
    [property: Description("Namespaces to add to the default usings, as dotted names: [\"System.Text\", \"System.Collections.Immutable\"].")]
        string[]? Usings = null,
    [property: Description("How long to wait for the snippet to finish, in milliseconds, 1 to 120000; 10000 by default.")] int? TimeoutMs = null,
    [property: Description("Also return a handle to the value, usable as {handle} in a later C# call; false by default.")] bool? Keep = null,
    [property: Description("How many levels of nested objects are written, 1 to 32; 8 by default.")] int? MaxDepth = null
);
