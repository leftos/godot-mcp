using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Binds a condition's equals so a JSON null reads as a Null element and a missing member stays a C# null, telling
/// <c>equals: null</c> apart from no equals. Put on that one property only: elsewhere a JSON null stays a C# null.
/// </summary>
internal sealed class ExplicitNullConverter : JsonConverter<JsonElement?>
{
    private static readonly JsonElement NullElement = JsonElement.Parse("null");

    /// <inheritdoc/>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? NullElement : JsonElement.ParseValue(ref reader);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
    {
        if (value is { } element)
        {
            element.WriteTo(writer);
            return;
        }

        writer.WriteNullValue();
    }
}
