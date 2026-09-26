using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Server.Wire;

/// <summary>
/// What the bridge's first frame must carry for the run the server launched: <c>{type: "hello", token, projectPath}</c>.
/// </summary>
internal sealed record HandshakeExpectation(string Token, string ProjectPath)
{
    /// <summary>Why a hello does not belong to this run, or null when it does.</summary>
    public string? FindMismatch(JsonObject hello)
    {
        if (ReadString(hello, "type") != "hello")
        {
            return "the first frame is not a hello";
        }

        string? token = ReadString(hello, "token");
        if (token is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Token)))
        {
            return "the session token does not match this run";
        }

        string? projectPath = ReadString(hello, "projectPath");
        if (projectPath is null || !ProjectPaths.AreSame(projectPath, ProjectPath))
        {
            return $"the bridge reports project '{projectPath}', not '{ProjectPath}'";
        }

        return null;
    }

    internal static string? ReadString(JsonObject message, string name) =>
        message[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
