using System.Reflection;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// The methods a call by name chooses among: those of that name the type and its bases up to <c>stopAt</c> declare, as
/// <see cref="MemberListing"/> keeps them (no accessors, operators, compiler-generated or hidden members), an override or a
/// <c>new</c> hide once, as the most derived type declares it; <see cref="Constructor"/> names the type's own
/// constructors. Extension methods are never candidates. A name that is not a method it can call throws
/// <see cref="OverloadException"/> saying what the name is instead.
/// </summary>
public static class CallCandidates
{
    /// <summary>The member name that asks for a type's constructors.</summary>
    public const string Constructor = ".ctor";

    private const BindingFlags Declared =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>
    /// The candidates for <paramref name="name"/> on <paramref name="type"/>; <paramref name="staticsOnly"/> keeps the static
    /// ones alone, as a target naming a type rather than an instance needs.
    /// </summary>
    public static IReadOnlyList<MethodBase> Find(Type type, string name, bool staticsOnly, Func<Type, bool> stopAt)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stopAt);
        if (name == Constructor)
        {
            return Constructors(type);
        }
        List<MethodInfo> methods = Methods(type, name, stopAt);
        if (methods.Count == 0)
        {
            throw NotAMethod(type, name, stopAt);
        }
        if (!staticsOnly)
        {
            return methods;
        }
        List<MethodInfo> statics = [.. methods.Where(method => method.IsStatic)];
        return statics.Count > 0
            ? statics
            : throw new OverloadException($"'{name}' is an instance method of {TypeNames.Format(type)}; target a {{node}} or {{handle}}");
    }

    private static MethodBase[] Constructors(Type type)
    {
        if (type.IsAbstract)
        {
            string kind = type.IsSealed ? "static" : "abstract";
            throw new OverloadException($"{TypeNames.Format(type)} is {kind}; it has no constructor to call");
        }
        BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        MethodBase[] constructors = [.. type.GetConstructors(flags).Where(MemberListing.Kept)];
        return constructors.Length > 0 ? constructors : throw new OverloadException($"{TypeNames.Format(type)} has no constructor");
    }

    /// <summary>
    /// The kept methods of that name on the type's own levels, each parameter list once, the most derived first: an override
    /// with a covariant return or a <c>new</c> method with another access hides its base as the call sees it.
    /// </summary>
    private static List<MethodInfo> Methods(Type type, string name, Func<Type, bool> stopAt)
    {
        List<MethodInfo> found = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        NullabilityInfoContext context = new();
        foreach (Type level in Levels(type).TakeWhile(level => !stopAt(level)))
        {
            foreach (MethodInfo method in level.GetMethods(Declared).Where(method => method.Name == name && MemberListing.Kept(method)))
            {
                if (seen.Add(CallKey(method, context)))
                {
                    found.Add(method);
                }
            }
        }
        return found;
    }

    /// <summary>What tells two methods of one name apart to a call: the generic arity and each parameter's type as a signature spells it.</summary>
    private static string CallKey(MethodInfo method, NullabilityInfoContext context)
    {
        IEnumerable<string> parameters = method.GetParameters().Select(parameter => Signatures.ParameterType(parameter, context));
        return $"{method.GetGenericArguments().Length}({string.Join(", ", parameters)})";
    }

    /// <summary>What a name that is no method of the type's own levels is instead: a property, a field, a Godot method, or nothing.</summary>
    private static OverloadException NotAMethod(Type type, string name, Func<Type, bool> stopAt)
    {
        MemberInfo? data = Levels(type)
            .TakeWhile(level => !stopAt(level))
            .SelectMany(level => level.GetMember(name, MemberTypes.Property | MemberTypes.Field, Declared))
            .FirstOrDefault();
        if (data is not null)
        {
            string kind = data is PropertyInfo ? "property" : "field";
            return new OverloadException($"'{name}' is a {kind} of {TypeNames.Format(type)}; cs_get reads it");
        }
        bool godot = Levels(type).SkipWhile(level => !stopAt(level)).Any(level => level.GetMember(name, MemberTypes.Method, Declared).Length > 0);
        return godot
            ? new OverloadException($"'{name}' is a Godot method; call_method calls it")
            : new OverloadException($"{TypeNames.Format(type)} has no method '{name}'");
    }

    /// <summary>The type and its bases, <see cref="object"/> left out.</summary>
    private static IEnumerable<Type> Levels(Type type)
    {
        for (Type? level = type; level is not null && level != typeof(object); level = level.BaseType)
        {
            yield return level;
        }
    }
}
