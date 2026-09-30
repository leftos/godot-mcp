using System.Reflection;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// The mark a game puts on a method with its own <c>GodotMcpToolAttribute</c>: the description an agent reads, and the
/// optional tool name, the note on when the tool is taken, and whether it changes the game.
/// </summary>
public sealed record ToolMark(string Description, string Name, string? When, bool ReadOnly)
{
    private const string AttributeName = "GodotMcpToolAttribute";

    /// <summary>
    /// The mark on <paramref name="method"/>, read from metadata by attribute name so the game needs no assembly in
    /// common with this one; null when the method carries none. An unset name takes the method's own, an unset
    /// <c>When</c> null, and an unset <c>ReadOnly</c> false.
    /// </summary>
    public static ToolMark? Read(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        CustomAttributeData? mark = method.GetCustomAttributesData().FirstOrDefault(attribute => attribute.AttributeType.Name == AttributeName);
        if (mark is null)
        {
            return null;
        }
        return new ToolMark(
            (string)mark.ConstructorArguments[0].Value!,
            Named(mark, nameof(Name)) is string name ? name : method.Name,
            Named(mark, nameof(When)) as string,
            Named(mark, nameof(ReadOnly)) is bool readOnly && readOnly
        );
    }

    private static object? Named(CustomAttributeData mark, string member) =>
        mark.NamedArguments.FirstOrDefault(argument => argument.MemberName == member).TypedValue.Value;
}
