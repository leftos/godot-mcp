using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The game's own tools through the C# helper: list_game_tools lists the methods a game marks with its own
/// <c>GodotMcpToolAttribute</c>, each with its argument schema and where it runs, and call_game_tool calls one by name with
/// named arguments.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const string ListGameToolsToolName = "list_game_tools";
    internal const string CallGameToolToolName = "call_game_tool";

    [McpServerTool(Name = ListGameToolsToolName, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Lists the game's own tools: the C# methods it marks with [GodotMcpTool], an attribute the game declares itself (in any "
            + "namespace; matched by its name), private and internal ones included. Each is {name, description, when, readOnly, "
            + "on, args, returns, available, reason}: when is the game's note on when the tool is taken; on is where it runs, an "
            + "autoload's name, the current scene root's path, or \"static <Type>\" (the full name when two types share a short "
            + "one), and null when nothing owns it; args is a JSON Schema object of its arguments by name; returns is its C# "
            + "return type. available is false, with a reason, for a mark with no description, a tool whose parameter no call "
            + "can pass (no args then), one whose type is neither an autoload's, the current scene root's nor static, and two "
            + "tools sharing a name. Owners and availability are read from the live tree at each call. cs_call reaches the same "
            + "methods. Sorted by name. Reads only: no game code runs. When the game runs an older build than the one on disk, the result "
            + "says build: \"stale\" and lists what the running game loaded; restart_project runs the new build. A GDScript "
            + "project is refused; call_method calls its methods."
    )]
    public async Task<string> ListGameToolsAsync(
        [Description(
            "{name, offset, limit}: a part of the tool name to keep (case-insensitive; every tool when left out), and the page, 0 "
                + "and 100 by default."
        )]
            GameToolsOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        (int offset, int limit) = CheckGameToolsPage(options);
        JsonObject request = GameToolsRequest(options);
        await AddGameAsync(ListGameToolsToolName, request, session, cancellationToken);
        (CSharpReply reply, IReadOnlyList<ErrorEntry> errors) = await SendCSharpAsync(
            ListGameToolsToolName,
            request,
            null,
            session,
            cancellationToken
        );
        return ErrorReport.AddTo(GameToolsResult(reply, offset, limit), errors).ToJsonString();
    }

    [McpServerTool(Name = CallGameToolToolName, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Calls one of the game's own tools, a C# method it marks with [GodotMcpTool], by its name as list_game_tools lists "
            + "it, with args an object keyed by parameter name ({\"enemy\": true, \"hp\": 6}), each value converted as cs_set "
            + "converts one ({\"$node\": path} for a node; an enum by name or number). A parameter with a default may be left "
            + "out. Returns {tool, value, type}: type is the value's runtime full name, System.Void for a void; a returned "
            + "Node reads as {\"$node\": path}, and a value longer than 20000 characters as {valuePreview, valueLength}. A "
            + "returned Task is awaited up to options.timeoutMs; past it the call fails and the Task keeps running in the game. "
            + "Refused before any game code runs: a name no mark gives (cs_call reaches an unmarked member), a tool "
            + "list_game_tools shows unavailable (with its reason), and an unknown, missing or wrongly typed argument. Runs game "
            + "code: a thrown exception fails with its type, message and stack. When the game runs an older build than the one "
            + "on disk, the result says build: \"stale\"; restart_project runs the new build. A GDScript project is refused; "
            + "call_method calls its methods."
    )]
    public async Task<string> CallGameToolAsync(
        [Description("The tool's name, as list_game_tools lists it; matched exactly.")] string name,
        [Description("The arguments, a JSON object keyed by parameter name; none when left out.")] JsonElement? args = null,
        [Description(
            "{timeoutMs, maxDepth}: how long a returned Task is awaited, 1 to 120000 ms, load-adjusted (10000 by default), and "
                + "how many levels of nested objects are written, 1 to 32 (8 by default)."
        )]
            CallGameToolOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject request = CallGameToolRequest(name, args, options);
        int timeoutMs = CheckCallTimeout(options?.TimeoutMs);
        await AddGameAsync(CallGameToolToolName, request, session, cancellationToken);
        (CSharpReply reply, IReadOnlyList<ErrorEntry> errors) = await SendCSharpAsync(
            CallGameToolToolName,
            request,
            timeoutMs,
            session,
            cancellationToken
        );
        return ErrorReport.AddTo(CallGameToolResult(name, reply), errors).ToJsonString();
    }

    /// <summary>The helper's <c>tool_call</c> request before the game is named: the tool, its named arguments and the writer's depth.</summary>
    /// <exception cref="McpException">
    /// The name is empty, args is not an object or names a parameter twice, or maxDepth is out of range.
    /// </exception>
    internal static JsonObject CallGameToolRequest(string name, JsonElement? args, CallGameToolOptions? options) =>
        new()
        {
            ["op"] = "tool_call",
            ["name"] = CheckName(name, "name", "Pass a tool's name; list_game_tools lists them."),
            ["args"] = CheckGameToolArgs(args),
            ["maxDepth"] = CheckGetDepth(options?.MaxDepth),
        };

    /// <summary>
    /// An options.call's game tool before the game is named: {tool, request}, request the helper's <c>tool_call</c> as
    /// call_game_tool builds it, at its default depth. <paramref name="field"/> names the option in a refusal.
    /// </summary>
    /// <exception cref="McpException">args is given and is not an object, names a parameter twice, or the name is empty.</exception>
    internal static JsonObject GameToolCallParameters(string tool, JsonElement? args, string field)
    {
        if (args is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null) })
        {
            throw new McpException(
                $"{field}.args for a game tool is an object of named arguments, as call_game_tool takes them: "
                    + "{tool: \"<name>\", args: {\"<parameter>\": value}}."
            );
        }

        return new JsonObject { ["tool"] = tool, ["request"] = CallGameToolRequest(tool, args, null) };
    }

    /// <summary>
    /// Readies each game tool call in <paramref name="parameters"/> (call, then.call and start.then.call) for the bridge, which calls it through
    /// the C# helper in its frame: names the game in its request as call_game_tool does, and sends that request as the helper's
    /// JSON text beside the helper copy the game loads, {tool, extension, request}. A method call is left as it is and asks
    /// nothing.
    /// </summary>
    /// <exception cref="McpException">
    /// No session answers <paramref name="session"/>, the project has no C# assembly or build, or the helper cannot be prepared;
    /// <paramref name="toolName"/> fails.
    /// </exception>
    private async Task PrepareGameToolCallsAsync(string toolName, JsonObject parameters, string? session, CancellationToken cancellationToken)
    {
        JsonObject[] calls =
        [
            .. new[] { parameters["call"], ThenCall(parameters), ThenCall(parameters["start"] as JsonObject) }
                .OfType<JsonObject>()
                .Where(call => call.ContainsKey("tool")),
        ];
        foreach (JsonObject call in calls)
        {
            JsonObject request = call["request"]!.AsObject();
            await AddGameAsync(toolName, request, session, cancellationToken);
            call["request"] = request.ToJsonString();
            call["extension"] = HelperExtension(toolName, session);
        }
    }

    /// <summary>The call of <paramref name="holder"/>'s then, or null when it has no then or no call.</summary>
    private static JsonNode? ThenCall(JsonObject? holder) => (holder?["then"] as JsonObject)?["call"];

    /// <summary>The helper copy the session's game loads, prepared as get_game_state prepares it.</summary>
    /// <exception cref="McpException">The helper cannot run in the project, or its copy could not be made.</exception>
    private string HelperExtension(string toolName, string? session)
    {
        try
        {
            return csharp.PrepareExtension(Find(session).ProjectDir);
        }
        catch (InvalidOperationException e)
        {
            throw new McpException($"{toolName} failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// A game tool's result in <paramref name="holder"/>, {tool, reply} as the bridge answers it with the helper's reply text
    /// untouched, as call_game_tool answers it: {tool, value, type, build?}, its numbers as the helper wrote them. Any other
    /// holder, or none, is left as it is.
    /// </summary>
    /// <exception cref="McpException">The reply is not the helper's JSON answer.</exception>
    private static void ShapeToolReply(JsonObject? holder)
    {
        if (holder?["reply"] is null || holder["tool"]?.GetValue<string>() is not { } tool)
        {
            return;
        }

        CSharpReply reply;
        try
        {
            reply = CSharpBridge.ParseReply(holder);
        }
        catch (InvalidOperationException e)
        {
            throw new McpException($"The game tool {tool}'s answer could not be read: {e.Message}", e);
        }

        holder.Remove("reply");
        foreach ((string key, JsonNode? value) in CallGameToolResult(tool, reply))
        {
            holder[key] = value?.DeepClone();
        }
    }

    /// <summary>The arguments as the helper takes them: an empty object when left out or null.</summary>
    /// <exception cref="McpException">args is not a JSON object, or names a parameter twice.</exception>
    private static JsonObject CheckGameToolArgs(JsonElement? args)
    {
        if (args is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } given)
        {
            return [];
        }
        if (given.ValueKind != JsonValueKind.Object)
        {
            throw new McpException($"args must be an object keyed by parameter name; got {KindName(given.ValueKind)}.");
        }
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in given.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new McpException($"args names '{property.Name}' twice.");
            }
        }

        return JsonSerializer.SerializeToNode(given)!.AsObject();
    }

    /// <summary>A JSON value kind as a refusal names it: <c>boolean</c> for true and false, else the kind in lower case.</summary>
    private static string KindName(JsonValueKind kind) =>
        kind is JsonValueKind.True or JsonValueKind.False ? "boolean" : kind.ToString().ToLowerInvariant();

    /// <summary>call_game_tool's answer: the tool's name, then cs_call's value and type, and <c>build: "stale"</c> when the helper said so.</summary>
    private static JsonObject CallGameToolResult(string name, CSharpReply reply)
    {
        JsonObject result = GetResult(reply);
        result.Insert(0, "tool", name);
        if (reply.Result?["build"] is JsonNode build)
        {
            result["build"] = build.DeepClone();
        }

        return result;
    }

    /// <summary>
    /// Names the game's assembly and the MVIDs of its build on disk in <paramref name="request"/>, as the helper's game tool ops
    /// take them, after refusing a project with no C# assembly.
    /// </summary>
    /// <exception cref="McpException">No session answers <paramref name="session"/>, or the project has no C# assembly or build.</exception>
    private async Task AddGameAsync(string tool, JsonObject request, string? session, CancellationToken cancellationToken)
    {
        GodotSession game = FindCSharpGame(tool, session);
        string framework = await GameRuntimeDirectoryAsync(tool, game, session, cancellationToken);
        SnippetReferences references = FindSnippetReferences(tool, game.ProjectDir, framework);
        request["game"] = references.Game;
        request["expect"] = ExpectedMvids(references);
    }

    /// <summary>The helper's <c>tools</c> request, with the name filter when one was given.</summary>
    internal static JsonObject GameToolsRequest(GameToolsOptions? options)
    {
        JsonObject request = new() { ["op"] = "tools" };
        if (options?.Name is { Length: > 0 } name)
        {
            request["name"] = name;
        }

        return request;
    }

    /// <exception cref="McpException">The offset is negative, or the limit is outside 1 to <see cref="MaxPageSize"/>.</exception>
    private static (int Offset, int Limit) CheckGameToolsPage(GameToolsOptions? options)
    {
        int offset = options?.Offset ?? 0;
        int limit = options?.Limit ?? DefaultMembersLimit;
        CheckPage(offset, limit);
        return (offset, limit);
    }

    /// <summary>
    /// The session's game, refusing a project with no C# assembly before the game is asked, naming the tool that reaches a
    /// GDScript game's methods; every other refusal is <see cref="CSharpBridge.Refusal"/>'s, as the cs_* tools spell it.
    /// </summary>
    /// <exception cref="McpException">No session answers <paramref name="session"/>, or the project has no C# assembly.</exception>
    private GodotSession FindCSharpGame(string tool, string? session)
    {
        GodotSession game = Find(session);
        if (PrepScan.FindCsproj(game.ProjectDir) is { Note: null, Kind: CsprojKind.None })
        {
            throw new McpException(
                $"{tool} failed: This project has no C# assembly, so it has no game tools, which are C# methods "
                    + "marked [GodotMcpTool]; call_method calls a GDScript game's own methods."
            );
        }

        return game;
    }

    /// <summary>
    /// The helper's answer as the tool returns it: one page of the tools, <c>build: "stale"</c> when the game runs an older build
    /// than the one on disk, and the hint for that or for a game that marks none.
    /// </summary>
    private static JsonObject GameToolsResult(CSharpReply reply, int offset, int limit)
    {
        JsonObject result = PageList(reply.Result, "tools", offset, limit);
        foreach (string key in (string[])["build", "hint"])
        {
            if (reply.Result?[key] is JsonNode node)
            {
                result[key] = node.DeepClone();
            }
        }

        return result;
    }
}

/// <summary>Which game tools list_game_tools returns: a name filter and the page.</summary>
internal sealed record GameToolsOptions(
    [property: Description("Case-insensitive part of the tool name; every tool when left out.")] string? Name = null,
    [property: Description("How many tools of the list to skip: 0 (the default), or the next of the previous page.")] int? Offset = null,
    [property: Description("How many tools to return, 1 to 500; 100 by default.")] int? Limit = null
);

/// <summary>How call_game_tool waits for and writes a tool's answer.</summary>
internal sealed record CallGameToolOptions(
    [property: Description("How long a returned Task is awaited, 1 to 120000 ms, load-adjusted; 10000 by default.")] int? TimeoutMs = null,
    [property: Description("How many levels of nested objects are written, 1 to 32; 8 by default.")] int? MaxDepth = null
);
