using System.Reflection;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Builds a record, class or struct from a JSON object: the public constructor whose parameters match the most keys
/// (case-insensitive) with every required one present, then settable properties and fields for the remaining keys.
/// </summary>
internal static class ObjectReader
{
    private const string MarkerHint = "pass {\"$handle\": id} or {\"$node\": path}";

    public static object Read(JsonNode json, Type type, IValueResolver resolver)
    {
        RefuseUnbuildable(type);
        if (json is not JsonObject map)
        {
            throw ValueReader.Mismatch(type, json);
        }
        ConstructorInfo? constructor = ChooseConstructor(type, map);
        HashSet<string> consumed = new(StringComparer.Ordinal);
        object instance = constructor is null ? Activator.CreateInstance(type)! : Construct(constructor, map, resolver, consumed);
        foreach ((string key, JsonNode? node) in map)
        {
            if (!consumed.Contains(key))
            {
                SetMember(instance, key, node, resolver);
            }
        }
        return instance;
    }

    private static void RefuseUnbuildable(Type type)
    {
        string name = TypeNames.Format(type);
        if (type.IsInterface)
        {
            throw new ValueConversionException($"{name} is an interface: {MarkerHint}");
        }
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            throw new ValueConversionException($"{name} is a delegate: pass {{\"$handle\": id}}");
        }
        if (type.IsAbstract)
        {
            throw new ValueConversionException($"{name} is abstract: {MarkerHint}");
        }
    }

    private static ConstructorInfo? ChooseConstructor(Type type, JsonObject map)
    {
        ConstructorInfo? best = null;
        int bestScore = -1;
        foreach (ConstructorInfo constructor in type.GetConstructors())
        {
            int score = Score(constructor.GetParameters(), map);
            if (score < 0)
            {
                continue;
            }
            if (score > bestScore || (score == bestScore && constructor.GetParameters().Length < best!.GetParameters().Length))
            {
                (best, bestScore) = (constructor, score);
            }
        }
        return best is null && !type.IsValueType ? throw NoConstructor(type, map) : best;
    }

    private static ValueConversionException NoConstructor(Type type, JsonObject map)
    {
        string keys = map.Count == 0 ? "(none)" : string.Join(", ", map.Select(pair => pair.Key));
        return new ValueConversionException($"{TypeNames.Format(type)} has no constructor whose required parameters are all among the keys {keys}");
    }

    /// <summary>How many parameters the keys name, or -1 when a required one is missing.</summary>
    private static int Score(ParameterInfo[] parameters, JsonObject map)
    {
        int matched = 0;
        foreach (ParameterInfo parameter in parameters)
        {
            if (FindKey(map, parameter.Name ?? "") is not null)
            {
                matched++;
            }
            else if (!parameter.IsOptional && !parameter.HasDefaultValue)
            {
                return -1;
            }
        }
        return matched;
    }

    private static object Construct(ConstructorInfo constructor, JsonObject map, IValueResolver resolver, HashSet<string> consumed)
    {
        ParameterInfo[] parameters = constructor.GetParameters();
        object?[] values = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            string? key = FindKey(map, parameters[i].Name ?? "");
            values[i] = key is null ? Defaults.Of(parameters[i]) : ValueReader.ReadWithin(map[key], parameters[i].ParameterType, resolver, key);
            if (key is not null)
            {
                consumed.Add(key);
            }
        }
        try
        {
            return constructor.Invoke(values);
        }
        catch (TargetInvocationException e) when (e.InnerException is Exception inner)
        {
            throw new ValueConversionException(
                $"{TypeNames.Format(constructor.DeclaringType!)}'s constructor threw {inner.GetType().Name}: {inner.Message}",
                e
            );
        }
    }

    private static void SetMember(object instance, string key, JsonNode? node, IValueResolver resolver)
    {
        Type type = instance.GetType();
        MemberInfo? member = type.GetMember(
                key,
                MemberTypes.Property | MemberTypes.Field,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
            )
            .FirstOrDefault(found => found is FieldInfo || ((PropertyInfo)found).GetIndexParameters().Length == 0);
        try
        {
            Assign(instance, type, member, key, node, resolver);
        }
        catch (TargetInvocationException e) when (e.InnerException is Exception inner)
        {
            throw new ValueConversionException($"setting {TypeNames.Format(type)}.{member!.Name} threw {inner.GetType().Name}: {inner.Message}", e);
        }
    }

    private static void Assign(object instance, Type type, MemberInfo? member, string key, JsonNode? node, IValueResolver resolver)
    {
        switch (member)
        {
            case PropertyInfo { SetMethod.IsPublic: true } property:
                property.SetValue(instance, ValueReader.ReadWithin(node, property.PropertyType, resolver, key));
                break;
            case FieldInfo { IsInitOnly: false, IsLiteral: false } field:
                field.SetValue(instance, ValueReader.ReadWithin(node, field.FieldType, resolver, key));
                break;
            case null:
                throw new ValueConversionException($"{TypeNames.Format(type)} has no member '{key}'");
            default:
                throw new ValueConversionException($"{TypeNames.Format(type)}.{member.Name} is read-only");
        }
    }

    private static string? FindKey(JsonObject map, string name) =>
        map.Select(pair => pair.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}
