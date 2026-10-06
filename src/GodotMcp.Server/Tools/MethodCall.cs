using System.ComponentModel;
using System.Text.Json;

namespace GodotMcp.Server.Tools;

/// <summary>
/// What an options.call (capture_frames, wait_for's call and then.call, watch) calls in the running game: a node's method with
/// positional arguments, or a game tool by name with named arguments.
/// </summary>
internal sealed record MethodCall(
    [property: Description(
        "The node, for a method: an absolute path (/root/Main/Button), a path under the root (Main/Button), a name, or %Name for "
            + "a node saved with a unique name (alone, looked up in every scene; or after its owner's path), as call_method takes it."
    )]
        string? Node = null,
    [property: Description("The method's name, with node; a script's methods and the engine class's alike.")] string? Method = null,
    [property: Description(
        "For a method, an array of the arguments, as JSON, in order, converted by the parameters' declared types as call_method "
            + "converts them; for a game tool, an object of its arguments by parameter name, as call_game_tool takes them; none "
            + "when left out."
    )]
        JsonElement? Args = null,
    [property: Description(
        "Instead of node and method: the name of a game tool, a C# method the game marks with [GodotMcpTool], as list_game_tools "
            + "lists it, called through the C# helper with args by name as call_game_tool calls it."
    )]
        string? Tool = null
);
