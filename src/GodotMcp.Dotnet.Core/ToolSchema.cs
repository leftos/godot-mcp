using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// A marked method's argument schema, the reason no game tool can be built from it, and its return type as a signature
/// spells it. Exactly one of <paramref name="Arguments"/> and <paramref name="Unavailable"/> is set.
/// </summary>
public sealed record ToolSignature(JsonObject? Arguments, string? Unavailable, string Returns);

/// <summary>Turns a method's parameters into the JSON Schema object a game tool's named arguments are checked against.</summary>
public static class ToolSchema
{
    private static readonly HashSet<Type> Numbers = [typeof(float), typeof(double), typeof(decimal)];

    private static readonly HashSet<Type> Texts =
    [
        typeof(string),
        typeof(char),
        typeof(Guid),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(TimeSpan),
    ];

    /// <summary>
    /// The signature of <paramref name="method"/>: its arguments' schema, or why no game tool can be built from it, and
    /// the return type an agent reads. <paramref name="isNode"/> says whether a type is one of the game's nodes, which
    /// is passed on as a node path rather than built from an object.
    /// </summary>
    public static ToolSignature Describe(MethodInfo method, Func<Type, bool> isNode)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(isNode);
        NullabilityInfoContext context = new();
        string returns = TypeNames.Format(method.ReturnType, context.Create(method.ReturnParameter));
        return Passability.Unavailable(method) is { } unavailable
            ? new ToolSignature(null, unavailable, returns)
            : new ToolSignature(Arguments(method, isNode), null, returns);
    }

    /// <summary>The arguments object: one property per passable parameter, the required ones named, and no extras.</summary>
    private static JsonObject Arguments(MethodInfo method, Func<Type, bool> isNode)
    {
        JsonObject properties = [];
        JsonArray required = [];
        foreach (ParameterInfo parameter in method.GetParameters())
        {
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                continue;
            }
            properties[parameter.Name!] = Property(parameter, isNode);
            if (Passability.Required(parameter))
            {
                required.Add(parameter.Name);
            }
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    /// <summary>One parameter's schema, with the C# spelling cs_members prints and its description and default.</summary>
    private static JsonObject Property(ParameterInfo parameter, Func<Type, bool> isNode)
    {
        JsonObject schema = Schema(Defaults.ValueType(parameter), isNode, depth: 0);
        schema["x-csharp"] = Passability.Spelling(parameter);
        if (parameter.GetCustomAttribute<DescriptionAttribute>() is { } described)
        {
            schema["description"] = Described(described.Description, schema["description"]);
        }
        AddDefault(schema, parameter);
        return schema;
    }

    /// <summary>
    /// A parameter's own description, keeping the hint a schema for an interface or an abstract type carries, since that
    /// schema says nothing else about what to pass.
    /// </summary>
    private static string Described(string description, JsonNode? hint) => hint is null ? description : $"{description} ({(string?)hint})";

    /// <summary>
    /// Writes the parameter's default, and nothing when it has none or is null on a non-nullable value type, where
    /// reflection reports the type's own zero value as null.
    /// </summary>
    private static void AddDefault(JsonObject schema, ParameterInfo parameter)
    {
        if (!parameter.HasDefaultValue)
        {
            return;
        }
        JsonNode? value = Default(Defaults.Of(parameter));
        if (value is not null || Passability.AcceptsNull(parameter))
        {
            schema["default"] = value;
        }
    }

    private static JsonObject Schema(Type type, Func<Type, bool> isNode, int depth)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Schema(underlying, isNode, depth);
        }
        if (isNode(type))
        {
            return Node();
        }
        if (Scalar(type) is { } scalar)
        {
            return scalar;
        }
        return type.IsEnum ? Enumerated(type) : Composite(type, isNode, depth);
    }

    /// <summary>The schema of a scalar, a date or an unconstrained <c>object</c>; null for anything else.</summary>
    private static JsonObject? Scalar(Type type)
    {
        if (type == typeof(object))
        {
            return [];
        }
        if (type == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }
        if (ScalarReader.Integers.TryGetValue(type, out (decimal Min, decimal Max) bounds))
        {
            return new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = bounds.Min,
                ["maximum"] = bounds.Max,
            };
        }
        if (Numbers.Contains(type))
        {
            return new JsonObject { ["type"] = "number" };
        }
        return Texts.Contains(type) ? new JsonObject { ["type"] = "string" } : null;
    }

    private static JsonObject Enumerated(Type type)
    {
        JsonArray names = [.. Enum.GetNames(type).Select(name => JsonValue.Create(name))];
        return type.IsDefined(typeof(FlagsAttribute), inherit: false)
            ? new JsonObject { ["type"] = "string", ["x-flags"] = names }
            : new JsonObject { ["type"] = "string", ["enum"] = names };
    }

    /// <summary>A node argument is its path, never an object built from the JSON.</summary>
    private static JsonObject Node() =>
        new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["$node"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("$node"),
        };

    private static JsonObject Composite(Type type, Func<Type, bool> isNode, int depth)
    {
        if (CollectionReader.IsDictionary(type))
        {
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = Schema(CollectionReader.ElementOf(type), isNode, depth) };
        }
        if (CollectionReader.Builds(type))
        {
            return Sequence(type, isNode, depth);
        }
        if (type.IsInterface || type.IsAbstract)
        {
            return new JsonObject { ["type"] = "object", ["description"] = ObjectReader.MarkerHint };
        }
        return Built(type, isNode, depth);
    }

    private static JsonObject Sequence(Type type, Func<Type, bool> isNode, int depth)
    {
        JsonObject schema = new() { ["type"] = "array", ["items"] = Schema(CollectionReader.ElementOf(type), isNode, depth) };
        if (CollectionReader.IsSet(type))
        {
            schema["uniqueItems"] = true;
        }
        return schema;
    }

    /// <summary>
    /// A record, class or struct as an object: the union of its public constructors' parameters and the members a
    /// reader can set, under the names it looks them up by. One level of members is described; a member that is itself
    /// an object carries its type alone.
    /// </summary>
    private static JsonObject Built(Type type, Func<Type, bool> isNode, int depth)
    {
        if (depth > 0)
        {
            return new JsonObject { ["type"] = "object", ["x-csharp"] = TypeNames.Format(type) };
        }
        NullabilityInfoContext context = new();
        JsonObject properties = [];
        HashSet<string> named = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParameterInfo parameter in type.GetConstructors().SelectMany(constructor => constructor.GetParameters()))
        {
            if (named.Add(parameter.Name!))
            {
                properties[parameter.Name!] = Member(parameter.ParameterType, context.Create(parameter), isNode, depth);
            }
        }
        foreach (MemberInfo member in Settable(type))
        {
            if (named.Add(member.Name))
            {
                properties[member.Name] = Member(MemberType(member), Nullability(context, member), isNode, depth);
            }
        }
        JsonObject schema = new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        Require(schema, type);
        return schema;
    }

    private static JsonObject Member(Type type, NullabilityInfo? nullability, Func<Type, bool> isNode, int depth)
    {
        JsonObject schema = Schema(type, isNode, depth + 1);
        schema["x-csharp"] = TypeNames.Format(type, nullability);
        return schema;
    }

    /// <summary>Names an object must carry: the parameters of its one public constructor, which readers match by name.</summary>
    private static void Require(JsonObject schema, Type type)
    {
        ConstructorInfo[] constructors = type.GetConstructors();
        if (constructors.Length == 1)
        {
            schema["required"] = new JsonArray([
                .. constructors[0]
                    .GetParameters()
                    .Where(parameter => !parameter.HasDefaultValue)
                    .Select(parameter => JsonValue.Create(parameter.Name)),
            ]);
        }
    }

    private static IEnumerable<MemberInfo> Settable(Type type)
    {
        foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
        {
            switch (member)
            {
                case FieldInfo { IsLiteral: false, IsInitOnly: false } field:
                    yield return field;
                    break;
                case PropertyInfo { SetMethod.IsPublic: true } property when property.GetIndexParameters().Length == 0:
                    yield return property;
                    break;
            }
        }
    }

    private static Type MemberType(MemberInfo member) => member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;

    private static NullabilityInfo Nullability(NullabilityInfoContext context, MemberInfo member) =>
        member is FieldInfo field ? context.Create(field) : context.Create((PropertyInfo)member);

    /// <summary>A parameter's default as JSON: an enum by name, any other value as its own type writes it.</summary>
    private static JsonNode? Default(object? value) =>
        value switch
        {
            null => null,
            Enum member => JsonValue.Create(member.ToString()),
            _ => JsonSerializer.SerializeToNode(value, value.GetType()),
        };
}
