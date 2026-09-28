using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The game's own C# types through the helper the bridge loads (its <c>dotnet</c> command), which answers from the game's
/// own assemblies: cs_members lists what a type carries without running any of it, cs_get reads a property or field,
/// cs_set writes one and cs_call calls a method or constructor, private and internal ones included.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const string CsMembersToolName = "cs_members";
    internal const string CsGetToolName = "cs_get";
    internal const string CsSetToolName = "cs_set";
    internal const string CsCallToolName = "cs_call";
    private const int DefaultMembersLimit = 100;
    private const int DefaultGetDepth = 8;
    private const int MaxGetDepth = 32;
    private const string CsTargetDescription = "Exactly one of {node}, {type} or {handle}.";

    // A type's first C# call loads the helper into the game, which takes longer than a bridge command.
    private const int CsTimeoutMs = 30_000;

    [McpServerTool(Name = CsMembersToolName, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Lists the C# members of a game object or type, the ones Godot's call cannot show included: methods with every "
            + "overload spelled out, properties, fields, events and, for a type, constructors; private and internal ones by "
            + "default. Walks the game's own types up to the first Godot class; describe_class lists Godot's API. Reads only: "
            + "no getter or constructor runs."
    )]
    public async Task<string> CsMembersAsync(
        [Description(CsTargetDescription)] CSharpTarget target,
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
        (CSharpReply reply, IReadOnlyList<ErrorEntry> errors) = await SendCSharpAsync(CsMembersToolName, request, null, session, cancellationToken);
        return ErrorReport.AddTo(MembersResult(reply, offset, limit), errors).ToJsonString();
    }

    [McpServerTool(Name = CsGetToolName, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Reads a C# property or field of a game object or type, private and internal ones included, and returns {value, "
            + "type}: type is the value's runtime full name, or the member's declared type when it is null. member is a name "
            + "or a dotted path through properties, fields, list indexes and dictionary keys (Pending.Options[0], "
            + "Scores[\"key\"]); a {type} target starts at a static member. A Godot object comes back as {\"$node\": path}, "
            + "or {\"$object\": class, id} off the tree, and a value whose JSON is longer than 20000 characters as "
            + "{valuePreview, valueLength}. options.keep also returns a handle usable as {handle} in a later C# call; a null "
            + "value gets no handle and a warning, and a value type a handle to a copy, with a warning that a set through it "
            + "does not reach the source. A getter runs game code. Fails when the member is a method and when a getter "
            + "throws, with the exception's type, message and stack."
    )]
    public async Task<string> CsGetAsync(
        [Description(CsTargetDescription)] CSharpTarget target,
        [Description("A property or field name, or a dotted path: Pending.Options[0], Scores[\"key\"].")] string member,
        [Description(
            "{maxDepth, keep}: how many levels of nested objects are written, 1 to 32 (8 by default), and whether a handle "
                + "to the value comes back too (false by default)."
        )]
            GetOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject request = new()
        {
            ["op"] = "get",
            ["target"] = CSharpTarget.ToHelper(target),
            ["member"] = CheckMember(member),
            ["maxDepth"] = CheckGetDepth(options?.MaxDepth),
            ["keep"] = options?.Keep ?? false,
        };
        (CSharpReply reply, IReadOnlyList<ErrorEntry> errors) = await SendCSharpAsync(CsGetToolName, request, null, session, cancellationToken);
        return ErrorReport.AddTo(GetResult(reply), errors).ToJsonString();
    }

    [McpServerTool(Name = CsSetToolName, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Sets a C# property or field of a game object or type, private and internal ones included, and reads it back: "
            + "{member, before, after}. member is a name or a dotted path whose last segment is a property, field, list "
            + "index or dictionary key (Pending.Count, Items[2], Scores[\"key\"]). The JSON value is converted by the member's "
            + "type: a record or class from an object, an enum by name or number, a list from an array, and an interface or "
            + "abstract member from {\"$handle\": h} or {\"$node\": path}. Writes what reflection allows, non-public and init "
            + "setters and readonly instance fields included; refuses a property with no setter, a const, a static readonly "
            + "field, and a path through a struct, whose set would change a copy. When the member reads something else after "
            + "the set (a setter that clamps), the old value is put back and the call fails with both. before and after "
            + "longer than 20000 characters come back as {valuePreview, valueLength}. A setter that throws fails with the "
            + "exception's type, message and stack."
    )]
    public async Task<string> CsSetAsync(
        [Description(CsTargetDescription)] CSharpTarget target,
        [Description("The property, field, list index or dictionary key to set, by name or dotted path: Pending.Count, Items[2].")] string member,
        [Description("The new value, as JSON.")] JsonElement value,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject request = new()
        {
            ["op"] = "set",
            ["target"] = CSharpTarget.ToHelper(target),
            ["member"] = CheckMember(member),
            ["value"] = JsonSerializer.SerializeToNode(value),
        };
        (CSharpReply reply, IReadOnlyList<ErrorEntry> errors) = await SendCSharpAsync(CsSetToolName, request, null, session, cancellationToken);
        JsonObject shaped = reply.Result?.DeepClone() as JsonObject ?? [];
        shaped["before"] = CutCSharpValue(shaped["before"]);
        shaped["after"] = CutCSharpValue(shaped["after"]);
        return ErrorReport.AddTo(shaped, errors).ToJsonString();
    }

    [McpServerTool(Name = CsCallToolName, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Calls a C# method or constructor of a game object or type, private and internal ones included, and returns {value, "
            + "type}: type is the value's runtime full name, System.Void for a void. A {node} or {handle} target calls an "
            + "instance method, a {type} target a static one, and member \".ctor\" on a {type} constructs one. args is a JSON "
            + "array in parameter order, each converted as cs_set converts a value ({\"$handle\": h} or {\"$node\": path} for "
            + "an object): a params parameter takes its array as one value, an out parameter takes null, and what ref and out "
            + "parameters hold after the call comes back as outs {name: value}. The overload is chosen by the arguments; when "
            + "several fit, the call fails listing each, and options.signature picks one by its parameter types in "
            + "cs_members' spelling ([\"float\"]). options.typeArgs gives a generic method's type arguments as C# keywords or "
            + "full names. A returned Task or ValueTask is awaited up to options.timeoutMs; past it the call fails and the "
            + "Task keeps running in the game. options.keep also returns a handle to the value; a Node made by \".ctor\" is "
            + "freed after the call unless kept, and a kept one is outside the tree, with a warning. A value or out longer "
            + "than 20000 characters comes back as {valuePreview, valueLength}. Runs game code: a thrown exception fails with "
            + "its type, message and stack. Godot's own methods: call_method; properties and fields: cs_get."
    )]
    public async Task<string> CsCallAsync(
        [Description(CsTargetDescription)] CSharpTarget target,
        [Description("The method's name, or \".ctor\" with a {type} target to construct one.")] string member,
        [Description("The arguments, as JSON, in parameter order; none when left out. An out parameter takes null.")] JsonElement[]? args = null,
        [Description(
            "{signature, typeArgs, keep, timeoutMs, maxDepth}: the overload's parameter types in cs_members' spelling, a "
                + "generic method's type arguments, whether a handle to the value comes back too (false by default), how long a "
                + "Task is awaited, 1 to 120000 ms, load-adjusted (10000 by default), and how many levels of nested objects are written, 1 to "
                + "32 (8 by default)."
        )]
            CsCallOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        JsonObject request = BuildCallRequest(target, member, args, options);
        int timeoutMs = CheckCallTimeout(options?.TimeoutMs);
        (CSharpReply reply, IReadOnlyList<ErrorEntry> errors) = await SendCSharpAsync(CsCallToolName, request, timeoutMs, session, cancellationToken);
        JsonObject result = GetResult(reply);
        if (reply.Result?["outs"] is JsonObject outs)
        {
            result["outs"] = new JsonObject([.. outs.Select(pair => KeyValuePair.Create(pair.Key, CutCSharpValue(pair.Value)))]);
        }

        return ErrorReport.AddTo(result, errors).ToJsonString();
    }

    /// <summary>The helper's <c>call</c> request: the target, the member, the arguments and the options that reach the helper.</summary>
    /// <exception cref="McpException">
    /// The target names none or more than one of node, type and handle, the member is empty, or maxDepth is out of range.
    /// </exception>
    private static JsonObject BuildCallRequest(CSharpTarget target, string member, JsonElement[]? args, CsCallOptions? options)
    {
        JsonObject request = new()
        {
            ["op"] = "call",
            ["target"] = CSharpTarget.ToHelper(target),
            ["member"] = CheckName(member, "member", "Pass a method name, or .ctor with a {type} target; cs_members lists them."),
            ["args"] = new JsonArray([.. (args ?? []).Select(arg => JsonSerializer.SerializeToNode(arg))]),
            ["maxDepth"] = CheckGetDepth(options?.MaxDepth),
            ["keep"] = options?.Keep ?? false,
        };
        AddTypeNames(request, "signature", options?.Signature);
        AddTypeNames(request, "typeArgs", options?.TypeArgs);
        return request;
    }

    /// <summary>Adds the type names under <paramref name="key"/> when they were given; leaves the request alone otherwise.</summary>
    private static void AddTypeNames(JsonObject request, string key, string[]? names)
    {
        if (names is not null)
        {
            request[key] = new JsonArray([.. names.Select(name => JsonValue.Create(name))]);
        }
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

    /// <exception cref="McpException">The member is empty.</exception>
    private static string CheckMember(string member) =>
        CheckName(member, "member", "Pass a property or field name, or a dotted path (Pending.Options[0]); cs_members lists them.");

    /// <exception cref="McpException">maxDepth is outside 1 to <see cref="MaxGetDepth"/>.</exception>
    private static int CheckGetDepth(int? requested)
    {
        int maxDepth = requested ?? DefaultGetDepth;
        return maxDepth is >= 1 and <= MaxGetDepth ? maxDepth : throw new McpException($"maxDepth must be 1 to {MaxGetDepth}; got {maxDepth}.");
    }

    /// <summary>
    /// Sends one helper request to the session's game, failing as a bridge call fails (refusals and helper errors as
    /// <c>&lt;tool&gt; failed: …</c>), and returns the reply with the errors the game logged meanwhile. A call's
    /// <paramref name="pendingTimeoutMs"/> is how long the bridge awaits a returned Task; the server waits that long more.
    /// </summary>
    private async Task<(CSharpReply Reply, IReadOnlyList<ErrorEntry> Errors)> SendCSharpAsync(
        string tool,
        JsonObject request,
        int? pendingTimeoutMs,
        string? session,
        CancellationToken cancellationToken
    )
    {
        GodotSession game = Find(session);
        long mark = game.Errors.Mark();
        int waitMs = CsTimeoutMs + (pendingTimeoutMs ?? 0);
        CSharpReply reply = await SendMappedAsync(
            game,
            tool,
            TimeSpan.FromMilliseconds(waitMs),
            () => csharp.SendAsync(game, request.ToJsonString(), waitMs, pendingTimeoutMs, cancellationToken),
            cancellationToken
        );
        return (reply, game.Errors.ErrorsSince(mark));
    }

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

    /// <summary>cs_get's answer: the value, cut as run_script's is, with its type and, when kept, its handle and any warning.</summary>
    private static JsonObject GetResult(CSharpReply reply)
    {
        JsonObject result = ShapeScriptValue(reply.Result?["value"]);
        foreach (string key in (string[])["type", "handle", "warning"])
        {
            if (reply.Result?[key] is JsonNode node)
            {
                result[key] = node.DeepClone();
            }
        }

        return result;
    }

    /// <summary>A value cs_set read, or <c>{valuePreview, valueLength}</c> when its JSON is longer than <see cref="MaxValueLength"/>.</summary>
    private static JsonNode? CutCSharpValue(JsonNode? value) => ValuePreview(value, MaxValueLength) ?? value?.DeepClone();
}
