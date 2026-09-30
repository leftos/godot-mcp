using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>
/// What Godot logs for each C# script and C# autoload while the project assembly is missing: lines a failed C# build
/// causes, which a result or a refusal that reports the build leaves out.
/// </summary>
internal static class MissingAssembly
{
    private const string CSharpClassMissing = "Cannot instantiate C# script because the associated class could not be found";
    private const string AutoloadNotInstantiated = "Failed to instantiate an autoload, script '";
    private const string CSharpAutoloadNotANode = ".cs' does not inherit from 'Node'";

    /// <summary>
    /// Whether Godot logged the entry because the project assembly did not load: a C# autoload that "does not inherit from
    /// 'Node'" (Godot 4.7.2 <c>main.cpp</c>), or a C# script whose class could not be found (<c>csharp_script.cpp</c>).
    /// </summary>
    public static bool IsSymptom(JsonObject entry)
    {
        string message = entry["message"] is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;
        return message.Contains(CSharpClassMissing, StringComparison.Ordinal)
            || (
                message.Contains(AutoloadNotInstantiated, StringComparison.Ordinal)
                && message.Contains(CSharpAutoloadNotANode, StringComparison.Ordinal)
            );
    }
}
