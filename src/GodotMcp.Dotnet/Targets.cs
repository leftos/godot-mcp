using System.Diagnostics.CodeAnalysis;
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

    /// <summary>How many of a node's children a not-found refusal names.</summary>
    private const int MaxListedChildren = 10;

    /// <summary>How many of the scenes holding a unique name an ambiguity refusal names.</summary>
    private const int MaxListedOwners = 10;

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

    /// <summary>
    /// The node a path or a bare name names, by the bridge's own rule: a path starting at a unique name (<c>%Rows</c>) read
    /// from the one node that name reaches in any scene, a path, or the first of that name.
    /// </summary>
    public static Node? Find(string value)
    {
        Window root = ((SceneTree)Engine.GetMainLoop()).Root;
        if (value.StartsWith('%'))
        {
            return FindUnique(root, value);
        }
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

    /// <summary>
    /// The node <see cref="Find"/> finds for <paramref name="value"/>, or the refusal saying why there is none to reach: it is
    /// missing, or it is the bridge's own.
    /// </summary>
    public static bool TryReach(string value, [NotNullWhen(true)] out Node? node, [NotNullWhen(false)] out string? refusal)
    {
        node = Find(value);
        refusal =
            node is null ? NotFound(value)
            : InBridge(node) ? $"'{node.GetPath()}' is part of the godot-mcp bridge, which the C# tools do not reach."
            : null;
        return refusal is null;
    }

    /// <summary>
    /// The refusal for a path or bare name that names no node: a bare name was searched for everywhere under /root; a path
    /// names the base it is read from, the deepest node on it that exists, the name that node lacks and up to
    /// <see cref="MaxListedChildren"/> of its children. A unique name first (<c>%Rows</c>) that no scene, or more than one,
    /// holds says so instead. The bridge's <c>not_found</c> (godot_mcp_inspect.gd) spells the same text.
    /// </summary>
    public static string NotFound(string value)
    {
        const string Tail = "; get_scene_tree lists the nodes' paths.";
        if (UniqueRefusal(value) is { } unique)
        {
            return unique;
        }
        return value.Contains('/', StringComparison.Ordinal)
            ? PathNotFound(value, Tail)
            : $"No node named '{value}' anywhere under /root in the running game{Tail}";
    }

    /// <summary>
    /// A path's not-found text: the deepest node on it that exists and the name it lacks, a child or a unique name its scene
    /// does not hold. A path starting at a unique name is read from the node holding it.
    /// </summary>
    private static string PathNotFound(string value, string tail)
    {
        (Node? parent, string segment) = DeepestAncestor(value);
        string from = value.StartsWith('/') || value.StartsWith('%') ? "" : "a path is read from /root, and ";
        string where = parent is null ? "/" : parent.GetPath().ToString();
        if (parent is not null && segment.StartsWith('%'))
        {
            return $"No node '{value}' in the running game: {from}{where}'s scene has no node with the unique name '{segment}'{tail}";
        }
        string children = parent is null ? "root" : ChildList(parent);
        return $"No node '{value}' in the running game: {from}{where} has no child '{segment}' (children: {children}){tail}";
    }

    /// <summary>
    /// The node a path starting at a unique name names: the first segment looked up in every scene owner, and the rest read
    /// from the one node found; null when no owner, or more than one, holds that name (<see cref="UniqueRefusal"/> says which).
    /// </summary>
    private static Node? FindUnique(Window root, string value)
    {
        string segment = FirstSegment(value);
        List<(Node Owner, Node Found)> matches = UniqueMatches(root, segment);
        if (matches.Count != 1)
        {
            return null;
        }
        string rest = value.Length > segment.Length ? value[(segment.Length + 1)..] : "";
        return rest.Length == 0 ? matches[0].Found : matches[0].Found.GetNodeOrNull(rest);
    }

    private static string FirstSegment(string value)
    {
        int slash = value.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? value : value[..slash];
    }

    /// <summary>
    /// Each distinct node the unique name <paramref name="segment"/> reaches from a scene owner under the root, breadth first,
    /// with the first owner that reached it: GetNodeOrNull looks the name up in the owner's unique nodes, then in its own
    /// owner's (scene/main/node.cpp L1942-1951 in 4.7.2), so a sub-scene root also reaches its parent scene's.
    /// </summary>
    private static List<(Node Owner, Node Found)> UniqueMatches(Window root, string segment)
    {
        List<(Node Owner, Node Found)> matches = [];
        Queue<Node> queue = new();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            Node node = queue.Dequeue();
            foreach (Node child in node.GetChildren())
            {
                queue.Enqueue(child);
            }
            if (IsSceneOwner(root, node) && node.GetNodeOrNull(segment) is { } found && !matches.Exists(match => match.Found == found))
            {
                matches.Add((node, found));
            }
        }
        return matches;
    }

    /// <summary>
    /// A scene owner: the root of an instanced scene (the current scene's, an autoload scene's), or a node with no owner (an
    /// autoload script's, one added at runtime); the root owns nothing.
    /// </summary>
    private static bool IsSceneOwner(Window root, Node node) => node != root && (!string.IsNullOrEmpty(node.SceneFilePath) || node.Owner is null);

    /// <summary>Why a path starting at a unique name names no node, or null: no scene holds the name, or more than one does.</summary>
    private static string? UniqueRefusal(string value)
    {
        if (!value.StartsWith('%'))
        {
            return null;
        }
        string segment = FirstSegment(value);
        List<(Node Owner, Node Found)> matches = UniqueMatches(((SceneTree)Engine.GetMainLoop()).Root, segment);
        if (matches.Count == 0)
        {
            return $"No node '{value}' in the running game: no scene under /root has a node with the unique name '{segment}' "
                + "(a unique name is one saved with unique_name_in_owner).";
        }
        if (matches.Count == 1)
        {
            return null;
        }
        List<string> owners = [.. matches.Take(MaxListedOwners).Select(match => match.Owner.GetPath().ToString())];
        if (matches.Count > MaxListedOwners)
        {
            owners.Add("…");
        }
        return $"'{segment}' is a unique name in {matches.Count} scenes: {string.Join(", ", owners)}; put its owner's path first, as in "
            + $"{owners[0]}/{segment}.";
    }

    /// <summary>The deepest node on the path <paramref name="value"/> that exists, null for <c>/</c>, and the name it lacks.</summary>
    private static (Node? Parent, string Segment) DeepestAncestor(string value)
    {
        string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        (Node? start, int first) = WalkStart(value, segments);
        if (start is not { } parent)
        {
            return (null, segments.Length == 0 ? "" : segments[0]);
        }
        for (int i = first; i < segments.Length - 1; i++)
        {
            Node? next = parent.GetNodeOrNull(segments[i]);
            if (next is null)
            {
                return (parent, segments[i]);
            }
            parent = next;
        }
        return (parent, segments[^1]);
    }

    /// <summary>
    /// The node a path is read from and the index of its first segment read from there: the root for an absolute path (null
    /// when its first segment is not the root's name), the one node holding a unique first segment, else the root.
    /// </summary>
    private static (Node? Start, int First) WalkStart(string value, string[] segments)
    {
        Window root = ((SceneTree)Engine.GetMainLoop()).Root;
        if (value.StartsWith('/'))
        {
            return segments.Length > 0 && segments[0] == root.Name.ToString() ? (root, 1) : (null, 0);
        }
        return value.StartsWith('%') ? (UniqueMatches(root, segments[0])[0].Found, 1) : (root, 0);
    }

    /// <summary>The names of up to <see cref="MaxListedChildren"/> children, the bridge left out, and a count of the rest.</summary>
    private static string ChildList(Node parent)
    {
        Node? bridge = ((SceneTree)Engine.GetMainLoop()).Root.GetNodeOrNull(AutoloadName);
        List<string> names = [.. parent.GetChildren().Where(child => child != bridge).Select(child => child.Name.ToString())];
        if (names.Count == 0)
        {
            return "none";
        }
        string shown = string.Join(", ", names.Take(MaxListedChildren));
        return names.Count > MaxListedChildren ? $"{shown} (+{names.Count - MaxListedChildren})" : shown;
    }

    /// <summary>Where a walk stops: Godot's own API is <c>describe_class</c>'s to list, not the helper's.</summary>
    public static bool StopAtGodot(Type type) => type.Assembly == typeof(GodotObject).Assembly;

    /// <summary>The node and its script's own type, or the refusal for a path or name the game's tree does not hold.</summary>
    private static Resolution ByNode(string value, TargetHints hints)
    {
        if (!TryReach(value, out Node? node, out string? refusal))
        {
            return Refused(refusal);
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
    internal static List<Type> Found(string name)
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
