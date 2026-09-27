using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>
/// Converts JSON to a CLR value of a given type: primitives, enums, nullables, collections, records and classes, and the
/// <c>{"$handle": id}</c> and <c>{"$node": path}</c> markers anywhere a value is expected.
/// </summary>
public static class ValueReader
{
    private const int ShownLength = 80;

    private static readonly JsonSerializerOptions ShowOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Converts <paramref name="json"/> to <paramref name="type"/>; throws <see cref="ValueConversionException"/>.</summary>
    public static object? Read(JsonNode? json, Type type, IValueResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(resolver);
        if (TryResolveMarker(json, resolver, out object? resolved, out string described))
        {
            return CheckResolved(resolved, described, type);
        }
        if (json is null)
        {
            return ReadNull(type);
        }
        return ReadValue(json, Nullable.GetUnderlyingType(type) ?? type, resolver);
    }

    /// <summary>The refusal for a value of the wrong shape: <c>expected an int, got "abc"</c>.</summary>
    internal static ValueConversionException Mismatch(Type type, JsonNode? json) => new($"expected {TypeNames.WithArticle(type)}, got {Show(json)}");

    /// <summary>The JSON as the agent would write it, cut to a readable length.</summary>
    internal static string Show(JsonNode? json)
    {
        string text = json?.ToJsonString(ShowOptions) ?? "null";
        return text.Length <= ShownLength ? text : text[..(ShownLength - 3)] + "...";
    }

    /// <summary>Reads <paramref name="json"/> as the element <paramref name="segment"/> of a larger value, naming it on failure.</summary>
    internal static object? ReadWithin(JsonNode? json, Type type, IValueResolver resolver, string segment)
    {
        try
        {
            return Read(json, type, resolver);
        }
        catch (ValueConversionException e)
        {
            throw e.Within(segment);
        }
    }

    private static object? ReadValue(JsonNode json, Type type, IValueResolver resolver)
    {
        if (type == typeof(object))
        {
            return ReadNatural(json, resolver);
        }
        if (ScalarReader.TryRead(json, type, out object? scalar))
        {
            return scalar;
        }
        if (type.IsEnum)
        {
            return EnumReader.Read(json, type);
        }
        if (CollectionReader.TryRead(json, type, resolver, out object? collection))
        {
            return collection;
        }
        return ObjectReader.Read(json, type, resolver);
    }

    private static object? ReadNull(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) is not null ? null : throw Mismatch(type, null);

    private static bool TryResolveMarker(JsonNode? json, IValueResolver resolver, out object? value, out string described)
    {
        value = null;
        described = "";
        if (json is not JsonObject { Count: 1 } marker)
        {
            return false;
        }
        (string key, JsonNode? node) = marker.First();
        if (node is not JsonValue text || text.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }
        string reference = text.GetValue<string>();
        (value, described) = key switch
        {
            "$handle" => (resolver.ResolveHandle(reference), $"handle {reference}"),
            "$node" => (resolver.ResolveNode(reference), $"node {reference}"),
            _ => (null, ""),
        };
        return described.Length > 0;
    }

    private static object? CheckResolved(object? value, string described, Type type)
    {
        if (value is null)
        {
            return ReadNull(type);
        }
        Type target = Nullable.GetUnderlyingType(type) ?? type;
        return target.IsInstanceOfType(value)
            ? value
            : throw new ValueConversionException(
                $"{described} is {TypeNames.WithArticle(value.GetType())}, not assignable to {TypeNames.Format(type)}"
            );
    }

    private static object? ReadNatural(JsonNode json, IValueResolver resolver) =>
        json switch
        {
            JsonArray array => array.Select(item => Read(item, typeof(object), resolver)).ToList(),
            JsonObject map => map.ToDictionary(pair => pair.Key, pair => Read(pair.Value, typeof(object), resolver)),
            _ => NaturalScalar(json),
        };

    private static object? NaturalScalar(JsonNode json)
    {
        switch (json.GetValueKind())
        {
            case JsonValueKind.String:
                return json.GetValue<string>();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return json.GetValue<bool>();
            default:
                string text = json.ToJsonString();
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole)
                    ? (object)whole
                    : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
