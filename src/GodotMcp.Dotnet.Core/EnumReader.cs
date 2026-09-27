using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>Reads an enum by name (any case) or by number; a <c>[Flags]</c> enum also by names joined with commas.</summary>
internal static class EnumReader
{
    public static object Read(JsonNode json, Type type) =>
        json.GetValueKind() switch
        {
            JsonValueKind.Number => FromNumber(json, type),
            JsonValueKind.String => FromNames(json, type),
            _ => throw Refusal(type, json),
        };

    private static object FromNumber(JsonNode json, Type type)
    {
        object number;
        try
        {
            number = ScalarReader.ReadInteger(json, Enum.GetUnderlyingType(type));
        }
        catch (ValueConversionException)
        {
            throw Refusal(type, json);
        }
        object value = Enum.ToObject(type, number);
        return IsFlags(type) || Enum.IsDefined(type, value) ? value : throw Refusal(type, json);
    }

    private static object FromNames(JsonNode json, Type type)
    {
        string text = json.GetValue<string>();
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        string[] names = Enum.GetNames(type);
        bool named = parts.All(part => names.Contains(part, StringComparer.OrdinalIgnoreCase)) && (parts.Length == 1 || IsFlags(type));
        return named && Enum.TryParse(type, text, ignoreCase: true, out object? value) ? value! : throw Refusal(type, json);
    }

    private static bool IsFlags(Type type) => type.IsDefined(typeof(FlagsAttribute), inherit: false);

    private static ValueConversionException Refusal(Type type, JsonNode json) =>
        new($"expected one of {string.Join(", ", Enum.GetNames(type))} (or its number), got {ValueReader.Show(json)}");
}
