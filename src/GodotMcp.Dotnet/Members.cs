using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>
/// The helper's <c>members</c> op: the members of the type behind the request's <c>{node}</c>, <c>{type}</c> or
/// <c>{handle}</c> target, as <see cref="MemberListing"/> lists them, or the reply's refusal.
/// </summary>
internal static class Members
{
    /// <summary>The bridge's autoload (the server's <c>OverrideFile.AutoloadName</c>), whose nodes are out of reach.</summary>
    private const string AutoloadName = "GodotMcpBridge";

    /// <summary>A fresh epoch each game process, so a handle from before a restart is refused as one.</summary>
    private static readonly HandleTable Handles = new(Random.Shared.Next(1, int.MaxValue));

    public static JsonObject Answer(JsonObject request)
    {
        JsonObject target = request["target"]!.AsObject();
        bool nonPublic = request["nonPublic"]?.GetValue<bool>() ?? true;
        string? name = request["name"]?.GetValue<string>();
        if (target["node"] is { } node)
        {
            return ByNode(node.GetValue<string>(), nonPublic, name);
        }
        if (target["type"] is { } type)
        {
            return ByType(type.GetValue<string>(), nonPublic, name);
        }
        return ByHandle(target["handle"]!.GetValue<string>(), nonPublic, name);
    }

    /// <summary>The script's own type, or the refusal for a path or name the game's tree does not hold.</summary>
    private static JsonObject ByNode(string value, bool nonPublic, string? name)
    {
        Node? node = Find(value);
        if (node is null || InBridge(node))
        {
            return Helper.Failure($"No node '{value}' in the running game; get_scene_tree lists the nodes' paths.");
        }
        if (StopAtGodot(node.GetType()))
        {
            return Helper.Failure($"{node.GetPath()} is a {node.GetClass()} with no C# script; describe_class lists its API.");
        }
        return Listing(node.GetType(), MemberScope.InstanceAndStatic, nonPublic, name);
    }

    private static JsonObject ByType(string value, bool nonPublic, string? name)
    {
        List<Type> found = Found(value);
        if (found.Count == 0)
        {
            return Helper.Failure(
                $"No type '{value}' in the game's assemblies; give the full name with its namespace, as cs_members reports it in type."
            );
        }
        if (found.Count > 1)
        {
            string assemblies = string.Join(", ", found.Select(type => type.Assembly.GetName().Name).Distinct());
            return Helper.Failure($"'{value}' names a type in several assemblies ({assemblies}); cs_members cannot tell them apart.");
        }
        return Listing(found[0], MemberScope.StaticAndConstructors, nonPublic, name);
    }

    /// <summary>The kept object's own type, or <see cref="HandleTable.Get"/>'s own refusal.</summary>
    private static JsonObject ByHandle(string id, bool nonPublic, string? name)
    {
        object value;
        try
        {
            value = Handles.Get(id);
        }
        catch (HandleException e)
        {
            return Helper.Failure(e.Message);
        }
        return Listing(value.GetType(), MemberScope.InstanceAndStatic, nonPublic, name);
    }

    private static JsonObject Listing(Type type, MemberScope scope, bool nonPublic, string? name)
    {
        JsonArray members = [];
        foreach (MemberEntry entry in MemberListing.List(type, scope, nonPublic, name, StopAtGodot))
        {
            members.Add(
                new JsonObject
                {
                    ["kind"] = entry.Kind,
                    ["name"] = entry.Name,
                    ["signature"] = entry.Signature,
                    ["static"] = entry.Static,
                }
            );
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["result"] = new JsonObject { ["type"] = type.FullName, ["members"] = members },
        };
    }

    /// <summary>Every loaded assembly's type of this name, in one load context each.</summary>
    private static List<Type> Found(string name)
    {
        HashSet<Type> found = [];
        foreach (AssemblyLoadContext context in AssemblyLoadContext.All)
        {
            foreach (Assembly assembly in context.Assemblies)
            {
                if (assembly.GetType(name, throwOnError: false) is { } type)
                {
                    found.Add(type);
                }
            }
        }
        return [.. found];
    }

    /// <summary>The node a path or a bare name names, by the bridge's own rule: a path, or the first of that name.</summary>
    private static Node? Find(string value)
    {
        Window root = ((SceneTree)Engine.GetMainLoop()).Root;
        if (value.Contains('/', StringComparison.Ordinal))
        {
            return root.GetNodeOrNull(value);
        }
        Queue<Node> queue = new();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            Node node = queue.Dequeue();
            if (node.Name.ToString() == value)
            {
                return node;
            }
            foreach (Node child in node.GetChildren())
            {
                queue.Enqueue(child);
            }
        }
        return null;
    }

    private static bool InBridge(Node node)
    {
        Node? bridge = ((SceneTree)Engine.GetMainLoop()).Root.GetNodeOrNull(AutoloadName);
        return bridge is not null && (node == bridge || bridge.IsAncestorOf(node));
    }

    /// <summary>Where a walk stops: Godot's own API is <c>describe_class</c>'s to list, not this op's.</summary>
    private static bool StopAtGodot(Type type) => type.Assembly == typeof(GodotObject).Assembly;
}
