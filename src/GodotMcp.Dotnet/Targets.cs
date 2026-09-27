using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>A resolved target: an instance and its type, or a type alone (<see cref="Instance"/> null) naming its statics.</summary>
internal sealed record Target(object? Instance, Type Type)
{
    public MemberRoot Root => Instance is null ? MemberRoot.Statics(Type) : MemberRoot.Of(Instance);
}

/// <summary>
/// The words a refusal spells for the op that asked: <see cref="Tool"/> is its tool's name, and <see cref="Scriptless"/>
/// the clause that says what reaches a node with no C# script instead.
/// </summary>
internal sealed record TargetHints(string Tool, string Scriptless);

/// <summary>A resolved target, or the reply refusing it; exactly one is set.</summary>
internal sealed record Resolution(Target? Found, JsonObject? Failure);

/// <summary>
/// Resolves a request's <c>{node}</c>, <c>{type}</c> or <c>{handle}</c> target, and holds the handle table every op
/// shares.
/// </summary>
internal static class Targets
{
    /// <summary>The bridge's autoload (the server's <c>OverrideFile.AutoloadName</c>), whose nodes are out of reach.</summary>
    private const string AutoloadName = "GodotMcpBridge";

    /// <summary>The kept objects, with a fresh epoch each game process, so a handle from before a restart is refused as one.</summary>
    public static HandleTable Handles { get; } = new(Random.Shared.Next(1, int.MaxValue));

    public static Resolution Resolve(JsonObject target, TargetHints hints)
    {
        if (target["node"] is { } node)
        {
            return ByNode(node.GetValue<string>(), hints);
        }
        if (target["type"] is { } type)
        {
            return ByType(type.GetValue<string>(), hints);
        }
        return ByHandle(target["handle"]!.GetValue<string>());
    }

    /// <summary>The node a path or a bare name names, by the bridge's own rule: a path, or the first of that name.</summary>
    public static Node? Find(string value)
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

    /// <summary>Where a walk stops: Godot's own API is <c>describe_class</c>'s to list, not the helper's.</summary>
    public static bool StopAtGodot(Type type) => type.Assembly == typeof(GodotObject).Assembly;

    /// <summary>The node and its script's own type, or the refusal for a path or name the game's tree does not hold.</summary>
    private static Resolution ByNode(string value, TargetHints hints)
    {
        Node? node = Find(value);
        if (node is null || InBridge(node))
        {
            return Refused($"No node '{value}' in the running game; get_scene_tree lists the nodes' paths.");
        }
        if (StopAtGodot(node.GetType()))
        {
            return Refused($"{node.GetPath()} is a {node.GetClass()} with no C# script; {hints.Scriptless}.");
        }
        return new Resolution(new Target(node, node.GetType()), null);
    }

    private static Resolution ByType(string value, TargetHints hints)
    {
        List<Type> found = Found(value);
        if (found.Count == 0)
        {
            return Refused($"No type '{value}' in the game's assemblies; give the full name with its namespace, as cs_members reports it in type.");
        }
        if (found.Count > 1)
        {
            string assemblies = string.Join(", ", found.Select(type => type.Assembly.GetName().Name).Distinct());
            return Refused($"'{value}' names a type in several assemblies ({assemblies}); {hints.Tool} cannot tell them apart.");
        }
        return new Resolution(new Target(null, found[0]), null);
    }

    /// <summary>The kept object and its own type, or <see cref="HandleTable.Get"/>'s own refusal.</summary>
    private static Resolution ByHandle(string id)
    {
        try
        {
            object value = Handles.Get(id);
            if (value is GodotObject godot && !GodotObject.IsInstanceValid(godot))
            {
                return Refused($"handle {id} is a freed {TypeNames.Format(value.GetType())}");
            }
            return new Resolution(new Target(value, value.GetType()), null);
        }
        catch (HandleException e)
        {
            return Refused(e.Message);
        }
    }

    private static Resolution Refused(string message) => new(null, Helper.Failure(message));

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

    /// <summary>Whether the node is the bridge's autoload or under it, which the C# tools never reach.</summary>
    public static bool InBridge(Node node)
    {
        Node? bridge = ((SceneTree)Engine.GetMainLoop()).Root.GetNodeOrNull(AutoloadName);
        return bridge is not null && (node == bridge || bridge.IsAncestorOf(node));
    }
}
