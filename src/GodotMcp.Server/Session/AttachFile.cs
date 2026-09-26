using System.Text;
using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>
/// The one-use file that tells a game attach_project did not launch where to dial: <c>{port, token}</c> at
/// <c>&lt;project&gt;/.godot/godot-mcp/attach.json</c>. The bridge reads it when the environment names no server, and the
/// server deletes it as soon as a bridge has connected, so a later launch does not dial in too.
/// </summary>
internal static class AttachFile
{
    public static readonly string RelativePath = Path.Combine(".godot", "godot-mcp", "attach.json");

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string PathIn(string projectDir) => Path.Combine(projectDir, RelativePath);

    /// <summary>Writes the endpoint, creating <c>.godot/godot-mcp/</c> when it is missing and replacing an older file.</summary>
    public static void Write(string projectDir, BridgeEndpoint endpoint)
    {
        string path = PathIn(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonObject content = new() { ["port"] = endpoint.Port, ["token"] = endpoint.Token };
        File.WriteAllText(path, content.ToJsonString() + "\n", Utf8NoBom);
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
