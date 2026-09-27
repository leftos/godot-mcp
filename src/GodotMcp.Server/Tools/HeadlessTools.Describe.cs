using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// describe_class (bridge/godot_mcp_class_info.gd): an engine class's or a project script class's members. The running game's
/// bridge answers while a session is live on the project, since a headless run is refused then; otherwise a headless Godot does.
/// </summary>
internal sealed partial class HeadlessTools
{
    internal const int DefaultMethodLimit = 100;
    private static readonly TimeSpan DescribeTimeout = TimeSpan.FromSeconds(10);

    [McpServerTool(Name = "describe_class", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Describes a class: an engine class (Node2D, Button) or a script class of the project (a GDScript class_name, a C# "
            + "[GlobalClass]). Returns {className, inherits, inheritsChain, canInstantiate, isScript, scriptPath?, language?, "
            + "properties: [{name, type, default}], methods: [{name, args: [{name, type, default?}], returnType, isVirtual, "
            + "isStatic}], signals: [{name, args: [{name, type}]}], constants: {name: value}, enums: {enumName: {key: value}}, "
            + "methodCount, offset, limit, warning?, errors?}. An engine class lists only its own members unless options.inherited; "
            + "a script class lists its script's members, its base scripts' included, and with options.inherited its engine "
            + "base's too. Properties are those the editor shows or a scene stores, and script variables. Methods are sorted by "
            + "name and paged; methodCount is how many there are. warning says a C# script class lists no members, so the C# "
            + "build may be red or stale. A name no class has is refused with the closest class names. While a session is live "
            + "on the project its running game answers; otherwise a headless Godot does, after the prep run_project does."
    )]
    public async Task<string> DescribeClassAsync(
        [Description(ProjectPathDescription)] string projectPath,
        [Description("The class's name, as Godot spells it: an engine class (Sprite2D) or a script class_name (Player).")] string className,
        [Description(
            "{inherited, offset, limit}: also list inherited members (false by default), and the page of methods, 0 and 100 by "
                + "default, at most 500."
        )]
            DescribeOptions? options = null,
        [Description(
            "The session whose running game answers; by default the first live session on the project, and a headless Godot when " + "none is live."
        )]
            string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject parameters = DescribeParameters(className, options);
        return await RunAsync(async () =>
        {
            string projectDir = SessionRegistry.NormaliseProjectDir(projectPath);
            GodotSession? live = LiveSession(projectDir, session);
            if (live is not null)
            {
                RuntimeTools.BridgeCall call = new("describe_class", "describe_class", parameters, DescribeTimeout);
                RuntimeTools.BridgeResult result = await RuntimeTools.CallWithErrorsAsync(live, call, cancellationToken);
                return ErrorReport.AddTo(result.Reply?.DeepClone() as JsonObject ?? [], result.Errors).ToJsonString();
            }

            HeadlessRequest request = new(projectDir, "describe_class", parameters, Prepare: true, RunCeiling);
            return WithErrors(await HeadlessRunner.RunAsync(sessions, request, cancellationToken));
        });
    }

    /// <summary>describe_class's request parameters: <c>{className, inherited, offset, limit}</c>, the name trimmed.</summary>
    /// <exception cref="McpException">
    /// className is empty, offset is negative, or limit is outside 1 to <see cref="RuntimeTools.MaxPageSize"/>.
    /// </exception>
    internal static JsonObject DescribeParameters(string className, DescribeOptions? options)
    {
        string name = CheckClassName(className);
        DescribeOptions given = options ?? new DescribeOptions();
        int offset = given.Offset ?? 0;
        int limit = given.Limit ?? DefaultMethodLimit;
        RuntimeTools.CheckPage(offset, limit);
        return new JsonObject
        {
            ["className"] = name,
            ["inherited"] = given.Inherited ?? false,
            ["offset"] = offset,
            ["limit"] = limit,
        };
    }

    /// <summary>A class name, trimmed.</summary>
    /// <exception cref="McpException">The name is empty.</exception>
    private static string CheckClassName(string className)
    {
        string name = className?.Trim() ?? string.Empty;
        return name.Length == 0
            ? throw new McpException("className is empty; name an engine class (Node2D, Button) or a script class_name of the project.")
            : name;
    }

    /// <summary>
    /// The session whose bridge answers for the project: the named one, else the first live one on the folder (ordered by name);
    /// null when none is named and none is live.
    /// </summary>
    /// <exception cref="SessionException">No session answers to the name.</exception>
    /// <exception cref="McpException">The named session is on another project.</exception>
    private GodotSession? LiveSession(string projectDir, string? session)
    {
        if (!string.IsNullOrWhiteSpace(session))
        {
            GodotSession named = sessions.Resolve(session);
            return ProjectPaths.AreSame(named.ProjectDir, projectDir)
                ? named
                : throw new McpException(
                    $"session '{named.Name}' is on {named.ProjectDir}, not {projectDir}; name a session on the project, or leave session out."
                );
        }

        IReadOnlyList<string> live = sessions.LiveSessionNames(projectDir);
        return live.Count == 0 ? null : sessions.Resolve(live[0]);
    }
}
