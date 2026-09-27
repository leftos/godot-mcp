using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>Reads the primitive types, <see cref="Guid"/>, the ISO 8601 dates and <c>c</c>-format time spans.</summary>
internal static class ScalarReader
{
    private delegate bool ElementReader<T>(JsonElement element, out T value);

    private static readonly Dictionary<Type, (decimal Min, decimal Max)> Integers = new()
    {
        [typeof(sbyte)] = (sbyte.MinValue, sbyte.MaxValue),
        [typeof(byte)] = (byte.MinValue, byte.MaxValue),
        [typeof(short)] = (short.MinValue, short.MaxValue),
        [typeof(ushort)] = (ushort.MinValue, ushort.MaxValue),
        [typeof(int)] = (int.MinValue, int.MaxValue),
        [typeof(uint)] = (uint.MinValue, uint.MaxValue),
        [typeof(long)] = (long.MinValue, long.MaxValue),
        [typeof(ulong)] = (ulong.MinValue, ulong.MaxValue),
    };

    private static readonly Dictionary<Type, Func<JsonNode, object?>> Readers = new()
    {
        [typeof(float)] = json =>
            NumberText(json) is string text
            && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            && float.IsFinite(value)
                ? value
                : null,
        [typeof(double)] = json =>
            NumberText(json) is string text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value)
                ? value
                : null,
        [typeof(decimal)] = json =>
            NumberText(json) is string text && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
                ? value
                : null,
        [typeof(bool)] = json =>
            json.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            },
        [typeof(string)] = Text,
        [typeof(char)] = json => Text(json) is [char only] ? only : null,
        [typeof(Guid)] = json => Guid.TryParse(Text(json), CultureInfo.InvariantCulture, out Guid value) ? value : null,
        [typeof(DateTime)] = json => FromElement(json, static (JsonElement element, out DateTime value) => element.TryGetDateTime(out value)),
        [typeof(DateTimeOffset)] = json =>
            FromElement(json, static (JsonElement element, out DateTimeOffset value) => element.TryGetDateTimeOffset(out value)),
        [typeof(TimeSpan)] = json => TimeSpan.TryParseExact(Text(json), "c", CultureInfo.InvariantCulture, out TimeSpan value) ? value : null,
    };

    /// <summary>
    /// True when <paramref name="type"/> is a scalar this reader owns, with the converted value; throws when the JSON does
    /// not fit it.
    /// </summary>
    public static bool TryRead(JsonNode json, Type type, out object? value)
    {
        if (Integers.ContainsKey(type))
        {
            value = ReadInteger(json, type);
            return true;
        }
        if (Readers.TryGetValue(type, out Func<JsonNode, object?>? reader))
        {
            value = reader(json) ?? throw ValueReader.Mismatch(type, json);
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>An integral JSON number (<c>3</c> or <c>3.0</c>) within the range of <paramref name="type"/>.</summary>
    public static object ReadInteger(JsonNode json, Type type)
    {
        (decimal min, decimal max) = Integers[type];
        if (Integral(json) is not decimal whole)
        {
            throw ValueReader.Mismatch(type, json);
        }
        if (whole < min || whole > max)
        {
            throw new ValueConversionException($"expected {TypeNames.WithArticle(type)}, got {ValueReader.Show(json)} (out of range)");
        }
        return Convert.ChangeType(whole, type, CultureInfo.InvariantCulture);
    }

    private static decimal? Integral(JsonNode json) =>
        NumberText(json) is string text
        && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
        && number == decimal.Truncate(number)
            ? number
            : null;

    private static string? NumberText(JsonNode json) => json.GetValueKind() == JsonValueKind.Number ? json.ToJsonString() : null;

    private static string? Text(JsonNode json) => json.GetValueKind() == JsonValueKind.String ? json.GetValue<string>() : null;

    private static object? FromElement<T>(JsonNode json, ElementReader<T> read)
    {
        if (json.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }
        using var document = JsonDocument.Parse(json.ToJsonString());
        return read(document.RootElement, out T value) ? value : null;
    }
}
