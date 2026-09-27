using System.Reflection;

namespace GodotMcp.Dotnet.Core;

/// <summary>Short C# spellings of types: keywords for the built-ins, generics with their arguments, <c>T?</c> for nullables.</summary>
internal static class TypeNames
{
    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void",
        [typeof(object)] = "object",
        [typeof(string)] = "string",
        [typeof(bool)] = "bool",
        [typeof(char)] = "char",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(nint)] = "nint",
        [typeof(nuint)] = "nuint",
    };

    public static string Format(Type type) => Format(type, null);

    /// <summary>
    /// Spells <paramref name="type"/>; <paramref name="nullability"/>, when known, adds <c>?</c> to the reference types it
    /// marks nullable, generic arguments and array elements included.
    /// </summary>
    public static string Format(Type type, NullabilityInfo? nullability)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return Format(underlying) + "?";
        }
        string name = Spell(type, nullability);
        bool nullable = nullability?.ReadState == NullabilityState.Nullable && !type.IsValueType && !type.IsGenericParameter;
        return nullable ? name + "?" : name;
    }

    /// <summary>The name with its indefinite article, e.g. <c>an int</c>, <c>a List&lt;int&gt;</c>.</summary>
    public static string WithArticle(Type type)
    {
        string name = Format(type);
        return ("aeioAEIOU".Contains(name[0], StringComparison.Ordinal) ? "an " : "a ") + name;
    }

    private static string Spell(Type type, NullabilityInfo? nullability)
    {
        if (Keywords.TryGetValue(type, out string? keyword))
        {
            return keyword;
        }
        if (type.IsArray)
        {
            return Array(type, nullability);
        }
        if (type.IsPointer || type.IsByRef)
        {
            return Format(type.GetElementType()!) + (type.IsPointer ? "*" : "");
        }
        return type.IsGenericType ? Generic(type, nullability) : type.Name;
    }

    private static string Array(Type type, NullabilityInfo? nullability)
    {
        string rank = type.IsSZArray ? "[]" : $"[{new string(',', type.GetArrayRank() - 1)}]";
        return Format(type.GetElementType()!, nullability?.ElementType) + rank;
    }

    private static string[] ArgumentNames(Type type, NullabilityInfo? nullability)
    {
        Type[] arguments = type.GetGenericArguments();
        NullabilityInfo[] infos = nullability?.GenericTypeArguments ?? [];
        string[] names = new string[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            names[i] = Format(arguments[i], i < infos.Length ? infos[i] : null);
        }
        return names;
    }

    private static string Generic(Type type, NullabilityInfo? nullability)
    {
        string[] names = ArgumentNames(type, nullability);
        string name = type.Name;
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }
        bool tuple = name == "ValueTuple" && type.Namespace == "System" && names.Length > 1;
        return tuple ? $"({string.Join(", ", names)})" : $"{name}<{string.Join(", ", names)}>";
    }
}
