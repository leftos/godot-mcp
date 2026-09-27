namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Where a <see cref="MemberPath"/> walk starts: an instance, whose members the first segment names, or a type, whose
/// static members alone the first segment names.
/// </summary>
public sealed class MemberRoot
{
    private MemberRoot(object? instance, Type? staticType)
    {
        Instance = instance;
        StaticType = staticType;
    }

    /// <summary>The root instance; null for a type root, and a null instance root is refused as "the target is null".</summary>
    public object? Instance { get; }

    /// <summary>The type whose statics the first segment names, or null for an instance root.</summary>
    public Type? StaticType { get; }

    public static MemberRoot Of(object? instance) => new(instance, null);

    public static MemberRoot Statics(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new MemberRoot(null, type);
    }
}

/// <summary>A value a walk read, and the type its last segment declares (a property's, field's, element's or entry's type).</summary>
public sealed record MemberValue(object? Value, Type DeclaredType);
