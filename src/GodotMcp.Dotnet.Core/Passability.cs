using System.Collections;
using System.Reflection;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// What a method's signature carries that a JSON argument cannot fill: a pointer or by-ref-like type, which a
/// <c>cs_call</c> refuses too, and a <c>ref</c>, <c>out</c>, delegate, native-sized integer or unreadable collection,
/// which only a game tool refuses. A form is refused wherever it sits: inside a nullable, an array element or a
/// dictionary value too. Also the parameter rules the schema and the binding must agree on.
/// </summary>
internal static class Passability
{
    /// <summary>Whether <paramref name="type"/> is a pointer or a function pointer.</summary>
    public static bool IsPointer(Type type) => type.IsPointer || type.IsFunctionPointer;

    /// <summary>Whether <paramref name="type"/> is by-ref-like: a <c>ref struct</c> such as <c>Span&lt;T&gt;</c>.</summary>
    public static bool IsByRefLike(Type type) => type.IsByRefLike;

    /// <summary>Whether <paramref name="type"/> is a delegate, which a game tool has no handle for.</summary>
    public static bool IsDelegate(Type type) => typeof(Delegate).IsAssignableFrom(type);

    /// <summary>Whether <paramref name="type"/> is <c>nint</c> or <c>nuint</c>.</summary>
    public static bool IsNativeInt(Type type) => type == typeof(nint) || type == typeof(nuint);

    /// <summary>Whether <paramref name="parameter"/> is passed by reference: a <c>ref</c> or <c>out</c>, but not an <c>in</c>.</summary>
    public static bool IsByReference(ParameterInfo parameter) => parameter.ParameterType.IsByRef && !parameter.IsIn;

    /// <summary>Whether <paramref name="parameter"/> is declared <c>params</c>.</summary>
    public static bool IsParams(ParameterInfo parameter) => parameter.IsDefined(typeof(ParamArrayAttribute), inherit: false);

    /// <summary>Whether the parameter's type takes null, so an omitted one binds null rather than its default.</summary>
    public static bool AcceptsNull(ParameterInfo parameter) =>
        Nullable.GetUnderlyingType(parameter.ParameterType) is not null
        || new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable;

    /// <summary>
    /// Whether a game tool's schema lists <paramref name="parameter"/> as required: not optional, defaulted, variadic or
    /// nullable, since a call that omits it takes null.
    /// </summary>
    public static bool Required(ParameterInfo parameter) =>
        !parameter.IsOptional && !parameter.HasDefaultValue && !IsParams(parameter) && !AcceptsNull(parameter);

    /// <summary>A parameter's type as a signature spells it, with its <c>ref</c>, <c>out</c>, <c>in</c> or <c>params</c>.</summary>
    public static string Spelling(ParameterInfo parameter) => Signatures.ParameterType(parameter, new NullabilityInfoContext());

    /// <summary>
    /// Why no game tool can be built from <paramref name="method"/>, or null when one can: a generic method definition,
    /// a method on an open generic type, a parameter no JSON argument can fill, or a by-ref-like return.
    /// </summary>
    public static string? Unavailable(MethodInfo method)
    {
        if (method.IsGenericMethodDefinition)
        {
            return $"{method.Name} is generic, which a game tool cannot call";
        }
        if (method.ContainsGenericParameters)
        {
            return $"{method.Name} is on generic {TypeNames.Format(method.DeclaringType!)}, which a game tool cannot call";
        }
        foreach (ParameterInfo parameter in method.GetParameters())
        {
            if (!Fills(parameter))
            {
                return $"parameter '{parameter.Name}' is {Spelling(parameter)}, which a game tool cannot pass";
            }
        }
        return IsByRefLike(method.ReturnType) ? $"it returns {TypeNames.Format(method.ReturnType)}, which a game tool cannot return" : null;
    }

    /// <summary>Whether every value the parameter takes, and everything its type nests, can be read from JSON.</summary>
    private static bool Fills(ParameterInfo parameter) => !IsByReference(parameter) && Passable(Defaults.ValueType(parameter));

    private static bool Passable(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Passable(underlying);
        }
        if (Unfillable(type))
        {
            return false;
        }
        return !CollectionReader.Builds(type) && !CollectionReader.IsDictionary(type) || Passable(CollectionReader.ElementOf(type));
    }

    /// <summary>Whether the type itself is a form no JSON argument can be read as.</summary>
    private static bool Unfillable(Type type) => IsPointer(type) || IsByRefLike(type) || IsDelegate(type) || IsNativeInt(type) || Unreadable(type);

    /// <summary>Whether <paramref name="type"/> is an enumerable the value reader has no form for, such as <c>ISet&lt;T&gt;</c>.</summary>
    private static bool Unreadable(Type type) =>
        typeof(IEnumerable).IsAssignableFrom(type)
        && type != typeof(string)
        && !CollectionReader.Builds(type)
        && !CollectionReader.IsDictionary(type);
}
