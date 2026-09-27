using System.Globalization;
using System.Reflection;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Spells a member as C# with short type names: <c>int Hit(int amount)</c>, <c>static string Label { get; }</c>,
/// <c>private readonly int _seed</c>, <c>.ctor(int a, string b)</c>. Reference types carry <c>?</c> where the nullability
/// context marks them nullable. Methods, properties and constructors name their accessibility only when it is not
/// public; fields always do.
/// </summary>
public static class Signatures
{
    private static readonly string[] AccessNames = ["", "private", "private protected", "internal", "protected", "protected internal", "public"];

    public static string Format(MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(member);
        NullabilityInfoContext context = new();
        return member switch
        {
            ConstructorInfo constructor => $"{Access(constructor)}{constructor.Name}({Parameters(constructor, context)})",
            MethodInfo method => Method(method, context),
            PropertyInfo property => Property(property, context),
            FieldInfo field => Field(field, context),
            EventInfo handler => $"event {TypeNames.Format(handler.EventHandlerType!, context.Create(handler))} {handler.Name}",
            _ => member.Name,
        };
    }

    /// <summary>A parameter's type as a signature spells it, with its <c>ref</c>, <c>out</c>, <c>in</c> or <c>params</c>.</summary>
    internal static string ParameterType(ParameterInfo parameter, NullabilityInfoContext context) =>
        Modifier(parameter) + TypeNames.Format(Defaults.ValueType(parameter), context.Create(parameter));

    private static string Method(MethodInfo method, NullabilityInfoContext context)
    {
        string returns = TypeNames.Format(method.ReturnType, context.Create(method.ReturnParameter));
        string generics = method.IsGenericMethod ? $"<{string.Join(", ", method.GetGenericArguments().Select(TypeNames.Format))}>" : "";
        string modifiers = Access(method) + (method.IsStatic ? "static " : "");
        return $"{modifiers}{returns} {method.Name}{generics}({Parameters(method, context)})";
    }

    private static string Property(PropertyInfo property, NullabilityInfoContext context)
    {
        MethodInfo[] accessors = property.GetAccessors(nonPublic: true);
        int widest = accessors.Max(AccessCode);
        string access = widest == (int)MethodAttributes.Public ? "" : AccessNames[widest] + " ";
        string modifiers = access + (accessors[0].IsStatic ? "static " : "");
        ParameterInfo[] index = property.GetIndexParameters();
        string name = index.Length == 0 ? property.Name : $"this[{string.Join(", ", index.Select(parameter => Parameter(parameter, context)))}]";
        string get = property.GetMethod is MethodInfo getter ? Narrower(getter, widest) + "get; " : "";
        string set = property.SetMethod is MethodInfo setter ? Narrower(setter, widest) + (IsInit(setter) ? "init; " : "set; ") : "";
        return $"{modifiers}{TypeNames.Format(property.PropertyType, context.Create(property))} {name} {{ {get}{set}}}";
    }

    private static string Field(FieldInfo field, NullabilityInfoContext context)
    {
        string storage = field.IsLiteral ? "const " : (field.IsStatic ? "static " : "") + (field.IsInitOnly ? "readonly " : "");
        string access = AccessNames[(int)(field.Attributes & FieldAttributes.FieldAccessMask)];
        return $"{access} {storage}{TypeNames.Format(field.FieldType, context.Create(field))} {field.Name}";
    }

    private static string Parameters(MethodBase method, NullabilityInfoContext context) =>
        string.Join(", ", method.GetParameters().Select(parameter => Parameter(parameter, context)));

    private static string Parameter(ParameterInfo parameter, NullabilityInfoContext context)
    {
        string text = $"{ParameterType(parameter, context)} {parameter.Name}";
        return parameter.HasDefaultValue ? $"{text} = {Literal(Defaults.Of(parameter), Defaults.ValueType(parameter))}" : text;
    }

    private static string Modifier(ParameterInfo parameter)
    {
        if (!parameter.ParameterType.IsByRef)
        {
            return parameter.IsDefined(typeof(ParamArrayAttribute), inherit: false) ? "params " : "";
        }
        return parameter.IsOut ? "out "
            : parameter.IsIn ? "in "
            : "ref ";
    }

    private static string Literal(object? value, Type type) =>
        value switch
        {
            null => type.IsValueType && Nullable.GetUnderlyingType(type) is null ? "default" : "null",
            string text => $"\"{text}\"",
            char letter => $"'{letter}'",
            bool flag => flag ? "true" : "false",
            Enum member => $"{TypeNames.Format(member.GetType())}.{member}",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };

    private static int AccessCode(MethodBase method) => (int)(method.Attributes & MethodAttributes.MemberAccessMask);

    private static string Access(MethodBase method) => method.IsPublic ? "" : AccessNames[AccessCode(method)] + " ";

    private static string Narrower(MethodInfo accessor, int widest) => AccessCode(accessor) < widest ? AccessNames[AccessCode(accessor)] + " " : "";

    private static bool IsInit(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");
}
