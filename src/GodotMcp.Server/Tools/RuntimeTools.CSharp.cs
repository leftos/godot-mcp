using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The game's own C# types through the helper the bridge loads (its <c>dotnet</c> command), which answers from the game's
/// own assemblies: cs_members lists what a type carries. Reads only, like describe_class: no getter or constructor runs.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const string CsMembersToolName = "cs_members";
    private const int DefaultMembersLimit = 100;

    // A type's first C# call loads the helper into the game, which takes longer than a bridge command.
    private const int CsTimeoutMs = 30_000;
    private static readonly TimeSpan CsTimeout = TimeSpan.FromMilliseconds(CsTimeoutMs);

    [McpServerTool(Name = CsMembersToolName, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Lists the C# members of a game object or type, the ones Godot's call cannot show included: methods with every "
            + "overload spelled out, properties, fields, events and, for a type, constructors; private and internal ones by "
            + "default. Walks the game's own types up to the first Godot class; describe_class lists Godot's API. Reads only: "
            + "no getter or constructor runs."
    )]
    public async Task<string> CsMembersAsync(
        [Description("Exactly one of {node}, {type} or {handle}.")] CSharpTarget target,
        [Description(
            "{name, nonPublic, offset, limit}: a part of the member name to keep (case-insensitive; every member when left "
                + "out), whether private, protected and internal members are listed (they are by default), and the page, 0 and "
                + "100 by default."
        )]
            MembersOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject request = BuildMembersRequest(target, options);
        (int offset, int limit) = CheckMembersPage(options);
        GodotSession game = Find(session);
        long mark = game.Errors.Mark();
        CSharpReply reply = await SendMembersAsync(game, request, cancellationToken);
        IReadOnlyList<ErrorEntry> errors = game.Errors.ErrorsSince(mark);
        return ErrorReport.AddTo(MembersResult(reply, offset, limit), errors).ToJsonString();
    }

    /// <summary>The helper's <c>members</c> request: the target, whether non-public members are listed, and a name filter.</summary>
    /// <exception cref="McpException">The target names none or more than one of node, type and handle.</exception>
    private static JsonObject BuildMembersRequest(CSharpTarget target, MembersOptions? options)
    {
        JsonObject request = new()
        {
            ["op"] = "members",
            ["target"] = CSharpTarget.ToHelper(target),
            ["nonPublic"] = options?.NonPublic ?? true,
        };
        if (options?.Name is { Length: > 0 } name)
        {
            request["name"] = name;
        }

        return request;
    }

    /// <exception cref="McpException">The offset is negative, or the limit is outside 1 to <see cref="MaxPageSize"/>.</exception>
    private static (int Offset, int Limit) CheckMembersPage(MembersOptions? options)
    {
        int offset = options?.Offset ?? 0;
        int limit = options?.Limit ?? DefaultMembersLimit;
        CheckPage(offset, limit);
        return (offset, limit);
    }

    /// <summary>Sends one helper request, failing as a bridge call fails: refusals and helper errors as <c>cs_members failed: …</c>.</summary>
    private Task<CSharpReply> SendMembersAsync(GodotSession game, JsonObject request, CancellationToken cancellationToken) =>
        SendMappedAsync(
            game,
            CsMembersToolName,
            CsTimeout,
            () => csharp.SendAsync(game, request.ToJsonString(), CsTimeoutMs, cancellationToken),
            cancellationToken
        );

    /// <summary>The helper's answer as the tool returns it: the target type, and one page of its members.</summary>
    private static JsonObject MembersResult(CSharpReply reply, int offset, int limit)
    {
        JsonObject page = PageList(reply.Result, "members", offset, limit);
        JsonObject result = new() { ["type"] = reply.Result?["type"]?.DeepClone() };
        foreach ((string key, JsonNode? value) in page)
        {
            result[key] = value?.DeepClone();
        }

        return result;
    }
}
