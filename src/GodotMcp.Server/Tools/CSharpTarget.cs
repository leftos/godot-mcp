using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>What a C# tool reads: a node with a C# script, a type by its full name, or a handle from an earlier C# call.</summary>
internal sealed record CSharpTarget(
    [property: Description("A node path or name; the node must have a C# script.")] string? Node = null,
    [property: Description("A full type name with its namespace, as cs_members reports it: statics and constructors.")] string? Type = null,
    [property: Description("A handle from cs_get or cs_call with keep.")] string? Handle = null
)
{
    /// <summary>The target as the helper reads it: <c>{node}</c>, <c>{type}</c> or <c>{handle}</c>.</summary>
    /// <exception cref="McpException">The target is missing, or names none or more than one of the three.</exception>
    public static JsonObject ToHelper(CSharpTarget? target)
    {
        CSharpTarget named = target ?? new CSharpTarget();
        List<string> given = Given(named);
        if (given.Count != 1)
        {
            string names = given.Count == 0 ? "none" : string.Join(", ", given);
            throw new McpException($"target takes exactly one of node, type or handle; got {names}.");
        }

        return given[0] == "node" ? new JsonObject { ["node"] = named.Node }
            : given[0] == "type" ? new JsonObject { ["type"] = named.Type }
            : new JsonObject { ["handle"] = named.Handle };
    }

    /// <summary>The kinds the target names, in the order node, type, handle; an empty string names none of them.</summary>
    private static List<string> Given(CSharpTarget named)
    {
        List<string> given = [];
        if (!string.IsNullOrWhiteSpace(named.Node))
        {
            given.Add("node");
        }

        if (!string.IsNullOrWhiteSpace(named.Type))
        {
            given.Add("type");
        }

        if (!string.IsNullOrWhiteSpace(named.Handle))
        {
            given.Add("handle");
        }

        return given;
    }
}
