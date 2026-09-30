using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>
/// The one-use file that tells a game attach_project did not launch where to dial, whether it takes the machine's real
/// pads, whether it parks its window and whether its bridge mutes its Master bus: <c>{port, token, shutOutRealGamepads, quiet,
/// mute}</c> at <c>&lt;project&gt;/.godot/godot-mcp/attach.json</c>. The bridge reads it when the environment names no server,
/// and the server deletes it when its wait for the game ends, whether a bridge connected or not, so a later launch does not
/// dial in too; only while it still carries that attach's token, since another server's attach may have written its own since.
/// </summary>
internal static class AttachFile
{
    public static readonly string RelativePath = Path.Combine(".godot", "godot-mcp", "attach.json");

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string PathIn(string projectDir) => Path.Combine(projectDir, RelativePath);

    /// <summary>
    /// Writes the endpoint, creating <c>.godot/godot-mcp/</c> when it is missing and replacing an older file, atomically: a game
    /// or another server never finds it half-written.
    /// </summary>
    public static void Write(string projectDir, BridgeEndpoint endpoint, ArmSettings settings)
    {
        string path = PathIn(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Written whole under another name, then moved into place, as a join file is.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, EndpointJson(endpoint, settings), Utf8NoBom);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The text of a file that tells a game where to dial, <c>{port, token, shutOutRealGamepads, quiet, mute}</c> and a newline:
    /// the attach file's, and a dormant game's join file's (<see cref="DormantGames.WriteJoinFile"/>). Its mute is the game's
    /// effective one: a quiet game is muted too.
    /// </summary>
    internal static string EndpointJson(BridgeEndpoint endpoint, ArmSettings settings)
    {
        JsonObject content = new()
        {
            ["port"] = endpoint.Port,
            ["token"] = endpoint.Token,
            ["shutOutRealGamepads"] = settings.ShutOutRealGamepads,
            ["quiet"] = settings.Quiet,
            ["mute"] = settings.Mute || settings.Quiet,
        };
        return content.ToJsonString() + "\n";
    }

    /// <summary>
    /// Deletes the file when it carries <paramref name="token"/>, the attach's own; returns whether it did. A missing file, one
    /// that is not an attach file, or one another attach has written since, is left alone.
    /// </summary>
    public static bool Remove(string projectDir, string token)
    {
        string path = PathIn(projectDir);
        if (TokenIn(path) != token)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// The token the attach or join file at <paramref name="path"/> carries; null when there is none, or when the file is not
    /// one a server wrote (it is written whole, so it is never read half-written).
    /// </summary>
    internal static string? TokenIn(string path)
    {
        try
        {
            return
                JsonNode.Parse(File.ReadAllText(path)) is JsonObject content
                && content["token"] is JsonValue value
                && value.TryGetValue(out string? token)
                ? token
                : null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // Gone (the game read it, or it was never written) or not a file a server wrote: either way not this server's to delete.
            return null;
        }
    }
}
