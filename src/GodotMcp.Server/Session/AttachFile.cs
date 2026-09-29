using System.Text;
using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>
/// The one-use file that tells a game attach_project did not launch where to dial, whether it takes the machine's real
/// pads and whether it parks its window: <c>{port, token, shutOutRealGamepads, quiet}</c> at
/// <c>&lt;project&gt;/.godot/godot-mcp/attach.json</c>. The bridge reads it when the environment names no server, and the
/// server deletes it as soon as a bridge has connected, so a later launch does not dial in too.
/// </summary>
internal static class AttachFile
{
    public static readonly string RelativePath = Path.Combine(".godot", "godot-mcp", "attach.json");

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string PathIn(string projectDir) => Path.Combine(projectDir, RelativePath);

    /// <summary>Writes the endpoint, creating <c>.godot/godot-mcp/</c> when it is missing and replacing an older file.</summary>
    public static void Write(string projectDir, BridgeEndpoint endpoint, bool shutOutRealGamepads, bool quiet)
    {
        string path = PathIn(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, EndpointJson(endpoint, shutOutRealGamepads, quiet), Utf8NoBom);
    }

    /// <summary>
    /// The text of a file that tells a game where to dial, <c>{port, token, shutOutRealGamepads, quiet}</c> and a newline: the
    /// attach file's, and a dormant game's join file's (<see cref="DormantGames.WriteJoinFile"/>).
    /// </summary>
    internal static string EndpointJson(BridgeEndpoint endpoint, bool shutOutRealGamepads, bool quiet)
    {
        JsonObject content = new()
        {
            ["port"] = endpoint.Port,
            ["token"] = endpoint.Token,
            ["shutOutRealGamepads"] = shutOutRealGamepads,
            ["quiet"] = quiet,
        };
        return content.ToJsonString() + "\n";
    }

    /// <summary>Deletes the file; returns whether there was one.</summary>
    public static bool Remove(string projectDir)
    {
        string path = PathIn(projectDir);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
