using Godot;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>
/// Resolves a value's markers in the running game: <c>{"$handle": id}</c> from the handle table the ops share, and
/// <c>{"$node": path}</c> by the bridge's rule for a node's path or name.
/// </summary>
internal sealed class GodotResolver : IValueResolver
{
    public object? ResolveHandle(string id)
    {
        object? value = Targets.Handles.Get(id);
        return value is GodotObject it && !GodotObject.IsInstanceValid(it)
            ? throw new ValueConversionException($"handle {id} is a freed {TypeNames.Format(value.GetType())}")
            : value;
    }

    public object? ResolveNode(string path) =>
        Targets.Find(path) is { } node && !Targets.InBridge(node)
            ? node
            : throw new ValueConversionException($"no node '{path}' in the running game; get_scene_tree lists the nodes' paths");
}
