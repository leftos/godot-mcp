using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Converts a CLR value to JSON: primitives as JSON, enums by name, dates in ISO 8601, collections as arrays, dictionaries
/// as objects, and other objects as their public instance properties then fields. Cycles and depth are cut with a
/// marker string; no length is cut here.
/// </summary>
public static class ValueWriter
{
    /// <summary>
    /// Writes <paramref name="value"/>. <paramref name="formatter"/> is asked first on every value. An object or collection
    /// nested deeper than <paramref name="maxDepth"/> levels (the root is level 1) writes <c>"&lt;depth limit: Type&gt;"</c>.
    /// </summary>
    public static JsonNode? Write(object? value, IValueFormatter formatter, int maxDepth = 8)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        return new ValueWalk(formatter, maxDepth).Write(value, 0);
    }
}

/// <summary>One write: the objects on the current path, for cutting cycles.</summary>
internal sealed class ValueWalk(IValueFormatter formatter, int maxDepth)
{
    private static readonly Dictionary<Type, Func<object, JsonNode>> Scalars = new()
    {
        [typeof(string)] = value => JsonValue.Create((string)value),
        [typeof(bool)] = value => JsonValue.Create((bool)value),
        [typeof(char)] = value => JsonValue.Create(((char)value).ToString()),
        [typeof(sbyte)] = value => JsonValue.Create((sbyte)value),
        [typeof(byte)] = value => JsonValue.Create((byte)value),
        [typeof(short)] = value => JsonValue.Create((short)value),
        [typeof(ushort)] = value => JsonValue.Create((ushort)value),
        [typeof(int)] = value => JsonValue.Create((int)value),
        [typeof(uint)] = value => JsonValue.Create((uint)value),
        [typeof(long)] = value => JsonValue.Create((long)value),
        [typeof(ulong)] = value => JsonValue.Create((ulong)value),
        [typeof(nint)] = value => JsonValue.Create((long)(nint)value),
        [typeof(nuint)] = value => JsonValue.Create((ulong)(nuint)value),
        [typeof(float)] = value =>
            float.IsFinite((float)value) ? JsonValue.Create((float)value) : JsonValue.Create(((float)value).ToString(CultureInfo.InvariantCulture)),
        [typeof(double)] = value =>
            double.IsFinite((double)value)
                ? JsonValue.Create((double)value)
                : JsonValue.Create(((double)value).ToString(CultureInfo.InvariantCulture)),
        [typeof(decimal)] = value => JsonValue.Create((decimal)value),
        [typeof(Guid)] = value => JsonValue.Create(((Guid)value).ToString("D", CultureInfo.InvariantCulture)),
        [typeof(DateTime)] = value => JsonValue.Create(((DateTime)value).ToString("O", CultureInfo.InvariantCulture)),
        [typeof(DateTimeOffset)] = value => JsonValue.Create(((DateTimeOffset)value).ToString("O", CultureInfo.InvariantCulture)),
        [typeof(TimeSpan)] = value => JsonValue.Create(((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture)),
    };

    private readonly HashSet<object> _onPath = new(ReferenceEqualityComparer.Instance);

    public JsonNode? Write(object? value, int depth)
    {
        if (value is null)
        {
            return null;
        }
        if (formatter.TryFormat(value, out JsonNode? formatted))
        {
            return formatted;
        }
        if (Scalars.TryGetValue(value.GetType(), out Func<object, JsonNode>? scalar))
        {
            return scalar(value);
        }
        if (value is Enum)
        {
            return JsonValue.Create(value.ToString());
        }
        return IsOpaque(value) ? Marker(OpaqueName(value)) : WriteComposite(value, depth);
    }

    private static JsonValue Marker(string text) => JsonValue.Create($"<{text}>");

    private static bool IsOpaque(object value) => value is Delegate or Pointer or Type or Task;

    private static string OpaqueName(object value)
    {
        if (value is Type)
        {
            return nameof(Type);
        }
        Type type = value.GetType();
        if (value is Task)
        {
            while (type != typeof(Task) && !IsGenericTask(type))
            {
                type = type.BaseType!;
            }
        }
        return TypeNames.Format(type);
    }

    private static bool IsGenericTask(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>);

    private JsonNode WriteComposite(object value, int depth)
    {
        Type type = value.GetType();
        bool tracked = !type.IsValueType;
        if (tracked && _onPath.Contains(value))
        {
            return Marker($"cycle: {TypeNames.Format(type)}");
        }
        if (depth >= maxDepth)
        {
            return Marker($"depth limit: {TypeNames.Format(type)}");
        }
        if (tracked)
        {
            _onPath.Add(value);
        }
        try
        {
            return WriteShape(value, depth + 1);
        }
        finally
        {
            _onPath.Remove(value);
        }
    }

    private JsonNode WriteShape(object value, int childDepth)
    {
        if (value is IDictionary dictionary)
        {
            return WriteDictionary(dictionary, childDepth);
        }
        if (IsCollection(value))
        {
            JsonArray array = [];
            foreach (object? item in (IEnumerable)value)
            {
                array.Add(Write(item, childDepth));
            }
            return array;
        }
        return value is IEnumerable ? Marker(TypeNames.Format(value.GetType())) : WriteObject(value, childDepth);
    }

    /// <summary>A materialised collection; a lazy sequence is not enumerated, since enumerating it runs game code and may not end.</summary>
    private static bool IsCollection(object value) =>
        value is ICollection
        || value
            .GetType()
            .GetInterfaces()
            .Any(face =>
                face.IsGenericType
                && (face.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>) || face.GetGenericTypeDefinition() == typeof(ICollection<>))
            );

    private JsonNode WriteDictionary(IDictionary dictionary, int childDepth)
    {
        List<DictionaryEntry> entries = [];
        IDictionaryEnumerator enumerator = dictionary.GetEnumerator();
        while (enumerator.MoveNext())
        {
            entries.Add(enumerator.Entry);
        }
        if (entries.TrueForAll(entry => IsStringLike(entry.Key)))
        {
            JsonObject map = [];
            foreach (DictionaryEntry entry in entries)
            {
                map[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!] = Write(entry.Value, childDepth);
            }
            return map;
        }
        JsonArray pairs = [];
        foreach (DictionaryEntry entry in entries)
        {
            pairs.Add(new JsonObject { ["key"] = Write(entry.Key, childDepth), ["value"] = Write(entry.Value, childDepth) });
        }
        return pairs;
    }

    private static bool IsStringLike(object key) => key is string or Enum or Guid or decimal || key.GetType().IsPrimitive;

    private JsonObject WriteObject(object value, int childDepth)
    {
        JsonObject map = [];
        List<Type> hierarchy = [];
        for (Type? type = value.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            hierarchy.Insert(0, type);
        }
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        IEnumerable<PropertyInfo> properties = hierarchy.SelectMany(type =>
            type.GetProperties(Declared)
                .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.MetadataToken)
        );
        foreach (PropertyInfo property in properties)
        {
            map[property.Name] = ReadProperty(value, property, childDepth);
        }
        foreach (FieldInfo field in hierarchy.SelectMany(type => type.GetFields(Declared).OrderBy(f => f.MetadataToken)))
        {
            map[field.Name] = Write(field.GetValue(value), childDepth);
        }
        return map;
    }

    private JsonNode? ReadProperty(object value, PropertyInfo property, int childDepth)
    {
        if (property.PropertyType.IsByRefLike)
        {
            return Marker(TypeNames.Format(property.PropertyType));
        }
        try
        {
            return Write(property.GetValue(value), childDepth);
        }
        catch (TargetInvocationException e) when (e.InnerException is Exception inner)
        {
            return JsonValue.Create($"<threw {inner.GetType().Name}: {inner.Message}>");
        }
    }
}
