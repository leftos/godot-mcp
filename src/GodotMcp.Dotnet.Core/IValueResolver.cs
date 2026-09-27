namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Resolves the markers a JSON value may carry in place of a value: <c>{"$handle": id}</c> and <c>{"$node": path}</c>.
/// </summary>
public interface IValueResolver
{
    /// <summary>The value a handle id stands for; throws when the id is not one the table holds.</summary>
    object? ResolveHandle(string id);

    /// <summary>The managed instance of the node at <paramref name="path"/>.</summary>
    object? ResolveNode(string path);
}
