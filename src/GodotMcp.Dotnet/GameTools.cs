using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>
/// The helper's <c>tools</c> op: every method of the game's own assemblies carrying a <c>GodotMcpToolAttribute</c>, with its
/// mark, its argument schema and where it runs. The assemblies are walked once per game process; each tool's owner (an
/// autoload, the current scene's root, or none for a static) and whether it can be called are worked out at every request.
/// </summary>
internal static class GameTools
{
    private const string AutoloadPrefix = "autoload/";

    private const BindingFlags Declared =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private const string NoToolsHint =
        "The game marks no method as a tool: declare an attribute class named GodotMcpToolAttribute in the game (any "
        + "namespace) whose constructor takes the description, and put [GodotMcpTool(\"what it does\")] on the methods to list. "
        + "A [Conditional(\"DEBUG\")] attribute class leaves the marks out of a Release build.";

    private const string StaleHint = "The game runs an older build than the one on disk; restart_project runs the new one.";

    /// <summary>The return type written for a method whose signature could not be read at all.</summary>
    private const string UnknownReturns = "unknown";

    /// <summary>The marked methods of the game's load context, walked on the first request of the game process.</summary>
    private static List<Marked>? _marked;

    /// <summary>A marked method, its mark and its signature, which never change while the game runs.</summary>
    private sealed record Marked(MethodInfo Method, ToolMark Mark, ToolSignature Signature);

    /// <summary>A marked method where it runs now: <see cref="On"/> null when nothing owns it, <see cref="Reason"/> why it cannot run.</summary>
    private sealed record Placed(Marked Tool, string? On, string? Reason);

    /// <summary>
    /// <c>{tools, build?, hint?}</c>: every marked method whose tool name holds the request's <c>name</c> (case-insensitive; every
    /// one when absent), sorted by name; <c>build: "stale"</c> when an assembly of the request's <c>expect</c> (simple name to
    /// the MVID on disk, as run's) is loaded from another build; and a hint for a stale build or a game that marks none at all.
    /// The request's <c>game</c> names the game's assembly, whose load context is walked.
    /// </summary>
    public static JsonObject List(JsonObject request)
    {
        if (_marked is null)
        {
            string game = request["game"]!.GetValue<string>();
            if (SnippetContext.Holding(game) is not { } context)
            {
                return Helper.Failure($"No loaded assembly is named '{game}', so the game's tools cannot be found.");
            }
            _marked = Walk(context);
        }
        string? name = request["name"]?.GetValue<string>();
        JsonArray tools =
        [
            .. Place(_marked)
                .Where(placed => name is null || placed.Tool.Mark.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(placed => placed.Tool.Mark.Name, StringComparer.Ordinal)
                .ThenBy(placed => placed.On, StringComparer.Ordinal)
                .ThenBy(placed => placed.Tool.Method.Name, StringComparer.Ordinal)
                .Select(Entry),
        ];
        JsonObject result = new() { ["tools"] = tools };
        AddBuildAndHint(result, Snippets.StaleDlls(request["expect"]?.AsObject()).Count > 0, _marked.Count == 0);
        return new JsonObject { ["ok"] = true, ["result"] = result };
    }

    /// <summary>
    /// Adds <c>build: "stale"</c> when the game runs an older build than the one on disk, and the hint for that and for a game
    /// that marks nothing, joined when both apply.
    /// </summary>
    private static void AddBuildAndHint(JsonObject result, bool stale, bool unmarked)
    {
        List<string> hints = [];
        if (stale)
        {
            result["build"] = "stale";
            hints.Add(StaleHint);
        }
        if (unmarked)
        {
            hints.Add(NoToolsHint);
        }
        if (hints.Count > 0)
        {
            result["hint"] = string.Join(" ", hints);
        }
    }

    /// <summary>
    /// Every marked method of every assembly in the game's load context, where its referenced project assemblies load too; a
    /// type whose methods, or a method whose attributes, name an assembly that cannot load is skipped with a warning.
    /// </summary>
    private static List<Marked> Walk(AssemblyLoadContext context)
    {
        List<Marked> marked = [];
        foreach (Assembly assembly in context.Assemblies)
        {
            foreach (Type type in Types(assembly))
            {
                foreach (MethodInfo method in Methods(type))
                {
                    if (Mark(method) is { } mark)
                    {
                        marked.Add(new Marked(method, mark, Signature(method)));
                    }
                }
            }
        }
        return marked;
    }

    /// <summary>The assembly's types, keeping those that loaded when some did not.</summary>
    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }

    /// <summary>The methods <paramref name="type"/> declares, or none when they cannot be enumerated.</summary>
    private static MethodInfo[] Methods(Type type)
    {
        try
        {
            return type.GetMethods(Declared);
        }
        catch (Exception e) when (IsUnloadable(e))
        {
            GD.PushWarning($"godot-mcp: list_game_tools skipped {type.FullName}, whose methods cannot be read: {e.Message}");
            return [];
        }
    }

    /// <summary>The method's mark, or null when it has none or its attributes cannot be read.</summary>
    private static ToolMark? Mark(MethodInfo method)
    {
        try
        {
            return ToolMark.Read(method);
        }
        catch (Exception e) when (IsUnloadable(e))
        {
            string where = $"{method.DeclaringType?.FullName}.{method.Name}";
            GD.PushWarning($"godot-mcp: list_game_tools skipped {where}, whose attributes cannot be read: {e.Message}");
            return null;
        }
    }

    private static bool IsUnloadable(Exception e) => e is FileNotFoundException or FileLoadException or TypeLoadException;

    /// <summary>The method's signature, or one unavailable with the message of whatever reading it threw.</summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A schema that cannot be built for one method makes that tool unavailable, whatever it threw; the list goes on."
    )]
    private static ToolSignature Signature(MethodInfo method)
    {
        try
        {
            return ToolSchema.Describe(method, IsNode);
        }
        catch (Exception e)
        {
            return new ToolSignature(null, e.Message, UnknownReturns);
        }
    }

    private static bool IsNode(Type type) => typeof(Node).IsAssignableFrom(type);

    /// <summary>Each tool's owner in the live tree, and its reason not to run: its mark, a name it shares, its signature, or no owner.</summary>
    private static List<Placed> Place(List<Marked> marked)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        List<(string Name, Node Node)> autoloads = Autoloads(tree.Root);
        Node? scene = tree.CurrentScene;
        HashSet<string> sharedShortNames = SharedStaticNames(marked);
        List<Placed> owned = [.. marked.Select(tool => Own(tool, autoloads, scene, sharedShortNames))];
        ILookup<string, Placed> byName = owned.ToLookup(placed => placed.Tool.Mark.Name, StringComparer.Ordinal);
        return [.. owned.Select(placed => Checked(placed, byName[placed.Tool.Mark.Name]))];
    }

    /// <summary>The children of the root, in order, that an <c>autoload/*</c> project setting names: the game's autoloads.</summary>
    private static List<(string Name, Node Node)> Autoloads(Window root)
    {
        List<(string Name, Node Node)> found = [];
        foreach (Node child in root.GetChildren())
        {
            string name = child.Name.ToString();
            if (ProjectSettings.HasSetting(AutoloadPrefix + name))
            {
                found.Add((name, child));
            }
        }
        return found;
    }

    /// <summary>The short names two or more types owning a static tool share, which their <c>on</c> spells in full.</summary>
    private static HashSet<string> SharedStaticNames(List<Marked> marked) =>
        [
            .. marked
                .Where(tool => tool.Method.IsStatic)
                .Select(tool => tool.Method.DeclaringType!)
                .Distinct()
                .GroupBy(TypeNames.Format, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key),
        ];

    /// <summary>Where a tool runs: a static on its type, else the first autoload of its type, else the current scene's root.</summary>
    private static Placed Own(Marked tool, List<(string Name, Node Node)> autoloads, Node? scene, HashSet<string> sharedShortNames)
    {
        Type type = tool.Method.DeclaringType!;
        if (tool.Method.IsStatic)
        {
            string shortName = TypeNames.Format(type);
            return new Placed(tool, $"static {(sharedShortNames.Contains(shortName) ? type.FullName ?? shortName : shortName)}", null);
        }
        foreach ((string name, Node node) in autoloads)
        {
            if (type.IsAssignableFrom(node.GetType()))
            {
                return new Placed(tool, name, null);
            }
        }
        if (scene is not null && type.IsAssignableFrom(scene.GetType()))
        {
            return new Placed(tool, scene.GetPath().ToString(), null);
        }
        return new Placed(tool, null, Unowned(tool.Method, scene));
    }

    private static string Unowned(MethodInfo method, Node? scene)
    {
        string current = scene is null ? "there is none" : $"{scene.GetPath()}, a {TypeNames.Format(scene.GetType())}";
        return $"{TypeNames.Format(method.DeclaringType!)} is neither an autoload's type nor the current scene root's ({current}), and "
            + $"{method.Name} is not static; a game tool runs on an autoload, on the current scene's root, or as a static method.";
    }

    /// <summary>
    /// The tool with the reason it cannot run: a mark with no description first, then a name another tool shares, then its
    /// signature, then its owner.
    /// </summary>
    private static Placed Checked(Placed placed, IEnumerable<Placed> named)
    {
        if (placed.Tool.Mark.Malformed is { } malformed)
        {
            return placed with { Reason = malformed };
        }
        List<Placed> others = [.. named.Where(other => !ReferenceEquals(other, placed))];
        if (others.Count > 0)
        {
            string also = string.Join(" and ", others.Select(other => $"{other.Tool.Method.Name} on {Where(other)}"));
            return placed with
            {
                Reason =
                    $"'{placed.Tool.Mark.Name}' also names {also}; a tool is one method, so give one of them another Name in its "
                    + "GodotMcpTool mark.",
            };
        }
        return placed.Tool.Signature.Unavailable is { } unavailable ? placed with { Reason = unavailable } : placed;
    }

    /// <summary>A tool's owner, or its type when nothing owns it.</summary>
    private static string Where(Placed placed) => placed.On ?? TypeNames.Format(placed.Tool.Method.DeclaringType!);

    /// <summary>One listed tool: <c>{name, description, when?, readOnly, on, args?, returns, available, reason?}</c>.</summary>
    private static JsonObject Entry(Placed placed)
    {
        ToolMark mark = placed.Tool.Mark;
        JsonObject entry = new() { ["name"] = mark.Name, ["description"] = mark.Description };
        if (mark.When is { } when)
        {
            entry["when"] = when;
        }
        entry["readOnly"] = mark.ReadOnly;
        entry["on"] = placed.On;
        if (placed.Tool.Signature.Arguments is { } arguments)
        {
            entry["args"] = arguments.DeepClone();
        }
        entry["returns"] = placed.Tool.Signature.Returns;
        entry["available"] = placed.Reason is null;
        if (placed.Reason is { } reason)
        {
            entry["reason"] = reason;
        }
        return entry;
    }
}
