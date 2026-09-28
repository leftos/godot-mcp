using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>How every tool's arguments bind from JSON: the MCP SDK's defaults, refusing a key an options record does not have.</summary>
internal static class ToolJson
{
    /// <summary>
    /// <see cref="McpJsonUtilities.DefaultOptions"/> with unmapped members disallowed. A value typed <see cref="JsonElement"/>, a
    /// JSON node or a dictionary still takes any keys.
    /// </summary>
    internal static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(McpJsonUtilities.DefaultOptions) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.MakeReadOnly();
        return options;
    }
}
