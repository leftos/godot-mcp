using System.Globalization;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>The call lane's tool side: taking it for a request that runs game code, and wording the refusal another call gets.</summary>
internal sealed partial class RuntimeTools
{
    /// <summary>How many characters of a call's description a refusal carries; a longer one keeps its first 119 and an ellipsis.</summary>
    private const int CallDescriptionLength = 120;

    /// <summary>Takes <paramref name="target"/>'s call lane for <paramref name="what"/>, freed when the returned scope is disposed.</summary>
    /// <exception cref="McpException">Another call on the session holds the lane.</exception>
    private static IDisposable EnterCallLane(GodotSession target, string what)
    {
        if (target.CallLane.TryEnter(what, out CallLaneHolder? held) is { } entry)
        {
            return entry;
        }

        throw new McpException(CallLaneRefusal(target.Name, held!));
    }

    /// <summary>The refusal a call gets while <paramref name="held"/> keeps <paramref name="session"/>'s lane.</summary>
    internal static string CallLaneRefusal(string session, CallLaneHolder held) =>
        $"session '{session}' is running {held.What} for {held.Age.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s; "
        + "send this call after it returns, or chain both as steps of one batch_drive.";

    /// <summary>
    /// What a tool's parameters say runs game code, as the lane names its holder: the first of call, then.call, start.call and
    /// start.then.call, as <c>"{tool} with {path} {call}"</c>; null when the request runs no call.
    /// </summary>
    internal static string? CallHolderOf(string tool, JsonObject parameters)
    {
        (string Path, JsonObject? Call)[] paths =
        [
            ("call", parameters["call"] as JsonObject),
            ("then.call", parameters["then"]?["call"] as JsonObject),
            ("start.call", parameters["start"]?["call"] as JsonObject),
            ("start.then.call", parameters["start"]?["then"]?["call"] as JsonObject),
        ];
        foreach ((string path, JsonObject? call) in paths)
        {
            if (call is not null)
            {
                return $"{tool} with {path} {DescribeCall(call)}";
            }
        }

        return null;
    }

    /// <summary>
    /// A call's description for the lane: a game tool's <c>game tool {name}</c>, or a method's <c>{node}.{method}({args})</c> with
    /// each argument its compact JSON, cut to <see cref="CallDescriptionLength"/> characters.
    /// </summary>
    internal static string DescribeCall(JsonObject call)
    {
        string text = call["tool"] is { } tool ? $"game tool {tool.GetValue<string>()}" : DescribeMethod(call);
        return text.Length <= CallDescriptionLength ? text : string.Concat(text.AsSpan(0, CallDescriptionLength - 1), "…");
    }

    /// <summary>A method call as <c>{node}.{method}({args})</c>, the arguments' compact JSON joined by ", ".</summary>
    private static string DescribeMethod(JsonObject call)
    {
        string args = call["args"] is JsonArray values
            ? string.Join(", ", values.Select(argument => argument?.ToJsonString() ?? "null"))
            : string.Empty;
        return $"{call["node"]?.GetValue<string>()}.{call["method"]?.GetValue<string>()}({args})";
    }
}
