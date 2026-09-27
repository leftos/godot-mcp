using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Dotnet.Core;

namespace GodotMcp.Dotnet;

/// <summary>
/// The helper's entry, run by the loader on the main thread: it stores the callable GDScript reaches the helper through
/// as the SceneTree meta <see cref="MetaName"/>. The callable takes a JSON request — <c>{"op":"ping"}</c>, optionally
/// with <c>"id":"&lt;instance id&gt;"</c>, <c>{"op":"members","target":{...}}</c>, which <see cref="Members"/>
/// answers, <c>{"op":"get"|"set",...}</c>, which <see cref="MemberAccess"/> answers, <c>{"op":"call",...}</c>, which
/// <see cref="Calls"/> answers, <c>{"op":"run",...}</c>, which <see cref="Snippets"/> answers, or
/// <c>{"op":"poll"|"forget",...}</c>, which <see cref="PendingTasks"/> answers — and returns a JSON reply,
/// <c>{"ok":true,"result":{...}}</c>, <c>{"ok":true,"pending":"c&lt;n&gt;"}</c> for a call or snippet still awaiting its task, or
/// <c>{"ok":false,"error":"..."}</c>.
/// </summary>
public static class Helper
{
    public const string MetaName = "godot_mcp_dotnet";

    /// <summary>Relaxed escaping, so the value writer's markers such as <c>"&lt;cycle: T&gt;"</c> arrive as written.</summary>
    private static readonly JsonSerializerOptions ReplyOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Install()
    {
        MainLoop tree = Engine.GetMainLoop();
        tree.SetMeta(MetaName, Callable.From<string, string>(Handle));
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A throw across the Callable would reach Godot as an engine error, not a reply."
    )]
    private static string Handle(string request)
    {
        JsonObject reply;
        try
        {
            reply = Answer(request);
        }
        catch (Exception e)
        {
            reply = Failure($"{e.GetType().Name}: {e.Message}");
        }

        return reply.ToJsonString(ReplyOptions);
    }

    private static JsonObject Answer(string request)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(request);
        }
        catch (JsonException e)
        {
            return NotAnObject(e.Message);
        }

        if (parsed is not JsonObject json)
        {
            return NotAnObject($"found {(parsed is null ? "null" : parsed.GetValueKind().ToString())}");
        }

        return Dispatch(json["op"]?.GetValue<string>(), json);
    }

    private static JsonObject Dispatch(string? op, JsonObject request) =>
        op switch
        {
            "ping" => Ping(request),
            "members" => Members.Answer(request),
            "get" => MemberAccess.Get(request),
            "set" => MemberAccess.Set(request),
            "call" => Calls.Call(request),
            "run" => Snippets.Run(request),
            "poll" => PendingTasks.Poll(request),
            "forget" => PendingTasks.Forget(request),
            _ => Failure($"Unknown op '{op}'."),
        };

    /// <summary>
    /// The load contexts the helper and <c>GodotSharp</c> are in, a type name from Core (so Core loads through the
    /// loader's resolver), and, when the request has an <c>id</c>, the C# type of the object with that instance id.
    /// </summary>
    private static JsonObject Ping(JsonObject request)
    {
        JsonObject result = new()
        {
            ["helperContext"] = AssemblyLoadContext.GetLoadContext(typeof(Helper).Assembly)?.Name,
            ["godotSharpContext"] = AssemblyLoadContext.GetLoadContext(typeof(GodotObject).Assembly)?.Name,
            ["core"] = TypeNames.Format(typeof(List<int>)),
            // The folder of the runtime the game runs on: a snippet compiles against its assemblies, not the server's.
            ["runtimeDirectory"] = RuntimeEnvironment.GetRuntimeDirectory(),
        };
        if (request["id"] is { } id)
        {
            ulong instanceId = ulong.Parse(id.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture);
            result["node"] = GodotObject.InstanceFromId(instanceId)?.GetType().FullName;
        }

        return new JsonObject { ["ok"] = true, ["result"] = result };
    }

    private static JsonObject NotAnObject(string reason) => Failure($"The request is not a JSON object: {reason}");

    /// <summary>The refusal a tool's own checks spell, shared with the ops beside <see cref="Ping"/>.</summary>
    internal static JsonObject Failure(string message) => new() { ["ok"] = false, ["error"] = message };
}
