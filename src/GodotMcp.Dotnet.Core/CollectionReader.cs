using System.Collections;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Reads arrays, lists and the list interfaces (as a <see cref="List{T}"/>), the immutable arrays and lists, hash sets,
/// and string-keyed dictionaries from a JSON object.
/// </summary>
internal static class CollectionReader
{
    private static readonly Dictionary<Type, Func<IList, Type, object>> ListBuilders = new()
    {
        [typeof(List<>)] = (list, _) => list,
        [typeof(IList<>)] = (list, _) => list,
        [typeof(IReadOnlyList<>)] = (list, _) => list,
        [typeof(IEnumerable<>)] = (list, _) => list,
        [typeof(ICollection<>)] = (list, _) => list,
        [typeof(HashSet<>)] = (list, element) => Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(element), list)!,
        [typeof(ImmutableArray<>)] = (list, element) => CreateRange(typeof(ImmutableArray), element, list),
        [typeof(ImmutableList<>)] = (list, element) => CreateRange(typeof(ImmutableList), element, list),
    };

    private static readonly HashSet<Type> Dictionaries = [typeof(Dictionary<,>), typeof(IReadOnlyDictionary<,>)];

    /// <summary>Whether <paramref name="type"/> is an array, a list or a set this reader builds.</summary>
    public static bool Builds(Type type) => type.IsSZArray || (type.IsGenericType && ListBuilders.ContainsKey(type.GetGenericTypeDefinition()));

    /// <summary>Whether <paramref name="type"/> is the hash set this reader builds duplicate-free.</summary>
    public static bool IsSet(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>);

    /// <summary>Whether <paramref name="type"/> is a dictionary this reader builds, keyed by string.</summary>
    public static bool IsDictionary(Type type) =>
        type.IsGenericType && Dictionaries.Contains(type.GetGenericTypeDefinition()) && type.GetGenericArguments()[0] == typeof(string);

    /// <summary>The element type of an array, list or set, or the value type of a string-keyed dictionary.</summary>
    public static Type ElementOf(Type type) => type.IsSZArray ? type.GetElementType()! : type.GetGenericArguments()[IsDictionary(type) ? 1 : 0];

    /// <summary>True when <paramref name="type"/> is a collection this reader builds; throws for any other collection.</summary>
    public static bool TryRead(JsonNode json, Type type, IValueResolver resolver, out object? value)
    {
        if (type.IsSZArray)
        {
            value = ToArray(ReadList(json, type, type.GetElementType()!, resolver), type.GetElementType()!);
            return true;
        }
        if (type.IsGenericType && TryReadGeneric(json, type, resolver, out value))
        {
            return true;
        }
        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            throw new ValueConversionException(
                $"cannot convert to {TypeNames.Format(type)}: only arrays, lists, sets and string-keyed dictionaries convert"
            );
        }
        value = null;
        return false;
    }

    private static bool TryReadGeneric(JsonNode json, Type type, IValueResolver resolver, out object? value)
    {
        Type definition = type.GetGenericTypeDefinition();
        Type[] arguments = type.GetGenericArguments();
        if (ListBuilders.TryGetValue(definition, out Func<IList, Type, object>? build))
        {
            value = build(ReadList(json, type, arguments[0], resolver), arguments[0]);
            return true;
        }
        if (Dictionaries.Contains(definition) && arguments[0] == typeof(string))
        {
            value = ReadDictionary(json, type, arguments[1], resolver);
            return true;
        }
        value = null;
        return false;
    }

    private static IList ReadList(JsonNode json, Type type, Type element, IValueResolver resolver)
    {
        if (json is not JsonArray array)
        {
            throw ValueReader.Mismatch(type, json);
        }
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
        for (int i = 0; i < array.Count; i++)
        {
            list.Add(ValueReader.ReadWithin(array[i], element, resolver, $"[{i}]"));
        }
        return list;
    }

    private static IDictionary ReadDictionary(JsonNode json, Type type, Type valueType, IValueResolver resolver)
    {
        if (json is not JsonObject map)
        {
            throw ValueReader.Mismatch(type, json);
        }
        var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType))!;
        foreach ((string key, JsonNode? node) in map)
        {
            dictionary[key] = ValueReader.ReadWithin(node, valueType, resolver, $"[\"{key}\"]");
        }
        return dictionary;
    }

    private static Array ToArray(IList list, Type element)
    {
        var array = Array.CreateInstance(element, list.Count);
        list.CopyTo(array, 0);
        return array;
    }

    private static object CreateRange(Type factory, Type element, IList list)
    {
        Type sequence = typeof(IEnumerable<>).MakeGenericType(Type.MakeGenericMethodParameter(0));
        return factory.GetMethod(nameof(ImmutableArray.CreateRange), 1, [sequence])!.MakeGenericMethod(element).Invoke(null, [list])!;
    }
}
