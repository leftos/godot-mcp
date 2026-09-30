using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The game's own tools through the C# helper: list_game_tools lists the methods a game marks with its own
/// <c>GodotMcpToolAttribute</c>, each with its argument schema and where it runs.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const string ListGameToolsToolName = "list_game_tools";

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
        GodotSession game = FindCSharpGame(session);
        string framework = await GameRuntimeDirectoryAsync(ListGameToolsToolName, game, session, cancellationToken);
        SnippetReferences references = FindSnippetReferences(ListGameToolsToolName, game.ProjectDir, framework);
        request["game"] = references.Game;
        request["expect"] = ExpectedMvids(references);
        (CSharpReply reply, IReadOnlyList<ErrorEntry> errors) = await SendCSharpAsync(
            ListGameToolsToolName,
            request,
            null,
            session,
            cancellationToken
        );
        return ErrorReport.AddTo(GameToolsResult(reply, offset, limit), errors).ToJsonString();
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
    private GodotSession FindCSharpGame(string? session)
    {
        GodotSession game = Find(session);
        if (PrepScan.FindCsproj(game.ProjectDir) is { Note: null, Kind: CsprojKind.None })
        {
            throw new McpException(
                $"{ListGameToolsToolName} failed: This project has no C# assembly, so it has no game tools, which are C# methods "
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
