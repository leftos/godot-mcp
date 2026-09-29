using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Server.Wire;

/// <summary>
/// What the bridge's first frame must carry for the run the server launched: <c>{type: "hello", token, projectPath}</c>.
/// The hello's <c>pid</c>, the game's own process id, is read by <see cref="ReadProcessId"/>, and its <c>window</c>, the game
/// window's size, by <see cref="ReadWindow"/>; neither is checked.
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

    /// <summary>
    /// The game's own process id, which the hello carries as <c>pid</c>; null when it carries none (a bridge older than the
    /// field) or one that is not a positive whole number. GDScript's JSON may write it as a float.
    /// </summary>
    internal static int? ReadProcessId(JsonObject hello) => ReadPositiveInt(hello["pid"]);

    /// <summary>
    /// The game window's size, which the hello carries as <c>window: {width, height}</c>; null when it carries none (a bridge
    /// older than the field) or either side is not a positive whole number.
    /// </summary>
    internal static WindowSize? ReadWindow(JsonObject hello)
    {
        if (hello["window"] is not JsonObject window)
        {
            return null;
        }

        return ReadPositiveInt(window["width"]) is { } width && ReadPositiveInt(window["height"]) is { } height
            ? new WindowSize(width, height)
            : null;
    }

    /// <summary>A positive whole number, which GDScript's JSON may write as a float; null for anything else.</summary>
    private static int? ReadPositiveInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out int whole))
        {
            return whole >= 1 ? whole : null;
        }

        return value.TryGetValue(out double number) && number >= 1 && number <= int.MaxValue && number == Math.Floor(number) ? (int)number : null;
    }
}
