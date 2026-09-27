using System.Text.Json.Nodes;

namespace GodotMcp.Dotnet.Core;

/// <summary>Writes the values <see cref="ValueWriter"/> has no rule for, such as engine objects and structs.</summary>
public interface IValueFormatter
{
    /// <summary>Asked first for every value the writer meets; false leaves the value to the writer's own rules.</summary>
    bool TryFormat(object value, out JsonNode? json);
}
