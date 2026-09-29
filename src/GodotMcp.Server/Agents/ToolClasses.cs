using System.Reflection;
using GodotMcp.Server.Tools;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Agents;

/// <summary>
/// The class of every tool the server serves, which decides the agents an agent-file sweep gives it to: read (changes
/// nothing), edit-scene (a headless edit of the project's files), edit-live (changes the running game's state beyond input)
/// and drive (everything else: the session, input, time, and captures that write files). The class comes from the tool's
/// own attribute (ReadOnly is read; a HeadlessTools tool is edit-scene; Destructive is edit-live; the rest drive), save for
/// the few tools in <see cref="Overrides"/>.
/// </summary>
internal static class ToolClasses
{
    public const string Read = "read";
    public const string Drive = "drive";
    public const string EditScene = "edit-scene";
    public const string EditLive = "edit-live";

    /// <summary>Every class, in the order the docs list them.</summary>
    public static readonly IReadOnlyList<string> All = [Read, Drive, EditScene, EditLive];

    /// <summary>The tools whose attributes give a class that would put them with the wrong agents, each with its reason.</summary>
    public static readonly IReadOnlyDictionary<string, ToolClassOverride> Overrides = new Dictionary<string, ToolClassOverride>(
        StringComparer.Ordinal
    )
    {
        ["stop_project"] = new(Drive, "session lifecycle belongs with run_project: an agent that can start a game can stop it"),
        ["restart_project"] = new(Drive, "session lifecycle belongs with run_project and stop_project"),
        ["set_property"] = new(EditLive, "it changes a live node's state"),
        [RuntimeTools.CsGetToolName] = new(Read, "it reads a member's value"),
    };

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Catalog = new(Load);

    /// <summary>Every served tool's name mapped to its class, sorted by name.</summary>
    public static IReadOnlyDictionary<string, string> ByTool => Catalog.Value;

    /// <summary>The class a tool's attribute gives it, before <see cref="Overrides"/>.</summary>
    /// <param name="tool">The tool's attribute.</param>
    /// <param name="toolType">The type declaring the tool's method.</param>
    public static string FromAttributes(McpServerToolAttribute tool, Type toolType)
    {
        if (tool.ReadOnly)
        {
            return Read;
        }

        if (toolType == typeof(HeadlessTools))
        {
            return EditScene;
        }

        return tool.Destructive ? EditLive : Drive;
    }

    /// <summary>Every <see cref="McpServerToolAttribute"/> on a method of a <see cref="McpServerToolTypeAttribute"/> type, with that type.</summary>
    public static IEnumerable<(McpServerToolAttribute Tool, Type ToolType)> ServedTools()
    {
        const BindingFlags Methods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        IEnumerable<Type> toolTypes = typeof(ToolClasses)
            .Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);
        foreach (Type toolType in toolTypes)
        {
            foreach (MethodInfo method in toolType.GetMethods(Methods))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is { } tool)
                {
                    yield return (tool, toolType);
                }
            }
        }
    }

    private static SortedDictionary<string, string> Load()
    {
        SortedDictionary<string, string> classes = new(StringComparer.Ordinal);
        foreach ((McpServerToolAttribute tool, Type toolType) in ServedTools())
        {
            string name = tool.Name ?? throw new InvalidOperationException($"A tool of {toolType.Name} has no Name in its McpServerTool attribute.");
            classes.Add(name, Overrides.TryGetValue(name, out ToolClassOverride? chosen) ? chosen.Class : FromAttributes(tool, toolType));
        }

        return classes;
    }
}

/// <summary>A tool's class chosen over the one its attribute gives, and why.</summary>
/// <param name="Class">The class the tool gets.</param>
/// <param name="Reason">Why the attribute's class is wrong for it.</param>
internal sealed record ToolClassOverride(string Class, string Reason);
