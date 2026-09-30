using System.ComponentModel;
using System.Text.Json;

namespace GodotMcp.Server.Tools;

/// <summary>A method of a node in the running game that capture_frames or a gameMs or frames wait calls as its clock starts.</summary>
internal sealed record MethodCall(
    [property: Description(
        "The node: an absolute path (/root/Main/Button), a path under the root (Main/Button), a name, or %Name for a node "
            + "saved with a unique name (alone, looked up in every scene; or after its owner's path), as call_method takes it."
    )]
        string Node,
    [property: Description("The method's name; a script's methods and the engine class's alike.")] string Method,
    [property: Description(
        "The arguments, as JSON, in order, converted by the parameters' declared types as call_method converts them; none when left out."
    )]
        JsonElement[]? Args = null
);
