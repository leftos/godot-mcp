using System.Globalization;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>
/// The helper's <c>state</c> op, get_game_state's C# half: each node's <c>_McpState()</c>, found by
/// <see cref="StateMethods"/> and called, its value written as <c>cs_get</c> writes one, so a Godot object reads as
/// <c>{"$node": path}</c> and a level past <c>maxDepth</c> as <c>"&lt;depth limit: Type&gt;"</c>.
/// </summary>
internal static class States
{
    private const int DefaultDepth = 4;

    private static readonly StateMethods Methods = new(Targets.StopAtGodot);

    /// <summary>
    /// <c>{"op":"state","ids":["&lt;instance id&gt;",..],"maxDepth":4}</c> → <c>{"nodes":[..]}</c>, one entry per id in the ids'
    /// order: <c>{id, state}</c>, <c>{id, error}</c>, or <c>{id, missing: true, error}</c> for an object with no state method.
    /// </summary>
    public static JsonObject Read(JsonObject request)
    {
        int maxDepth = request["maxDepth"]?.GetValue<int>() ?? DefaultDepth;
        JsonArray nodes = [];
        foreach (JsonNode? id in request["ids"] as JsonArray ?? [])
        {
            nodes.Add(Entry(id?.GetValue<string>() ?? string.Empty, maxDepth));
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["result"] = new JsonObject { ["nodes"] = nodes },
        };
    }

    private static JsonObject Entry(string id, int maxDepth)
    {
        JsonObject entry = new() { ["id"] = id };
        GodotObject? target = ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong instanceId)
            ? GodotObject.InstanceFromId(instanceId)
            : null;
        if (target is null)
        {
            entry["error"] = $"no object has instance id {id}: it was freed before the C# helper read it";
            return entry;
        }
        Methods.ReadEntry(entry, target, new GodotFormatter(maxDepth), maxDepth);
        return entry;
    }
}
