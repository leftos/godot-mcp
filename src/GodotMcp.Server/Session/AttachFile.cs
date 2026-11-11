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
        RetryWhileHeldOpen(path, () => File.Move(temporary, path, overwrite: true));
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

        RetryWhileHeldOpen(path, () => File.Delete(path));
        return true;
    }

    /// <summary>
    /// Raised with the file's path each time <see cref="RetryWhileHeldOpen"/> tries again, for tests to act on; no product code
    /// subscribes.
    /// </summary>
    internal static event Action<string>? HeldOpenRetried;

    /// <summary>
    /// Runs <paramref name="operation"/>, a delete or replace of the attach or join file at <paramref name="path"/>, trying
    /// again every 50 ms while it fails as a file held open fails it, at most <see cref="HeldOpenAttempts"/> attempts in all:
    /// Godot opens a file for reading sharing read and write but not delete, so a delete landing during a bridge's read fails
    /// with a sharing violation and a replace with access denied (as a delete of a delete-pending file does) until the read
    /// ends. This server's own reads share delete (<see cref="ReadShared"/>), so only a bridge's read holds a delete off. The
    /// last attempt's exception, and any other, propagates.
    /// </summary>
    internal static void RetryWhileHeldOpen(string path, Action operation)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception e) when (IsHeldOpen(e) && attempt < HeldOpenAttempts)
            {
                HeldOpenRetried?.Invoke(path);
                Thread.Sleep(HeldOpenDelay);
            }
        }
    }

    private static bool IsHeldOpen(Exception e) =>
        e is IOException { HResult: SharingViolation } or UnauthorizedAccessException { HResult: AccessDenied };

    private const int HeldOpenAttempts = 20;

    private static readonly TimeSpan HeldOpenDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>ERROR_SHARING_VIOLATION (32) as an HRESULT.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>ERROR_ACCESS_DENIED (5) as an HRESULT.</summary>
    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>
    /// The token the attach or join file at <paramref name="path"/> carries; null when there is none, or when the file is not
    /// one a server wrote (it is written whole, so it is never read half-written).
    /// </summary>
    internal static string? TokenIn(string path)
    {
        try
        {
            return
                JsonNode.Parse(ReadShared(path)) is JsonObject content && content["token"] is JsonValue value && value.TryGetValue(out string? token)
                ? token
                : null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // Gone (the game read it, or it was never written) or not a file a server wrote: either way not this server's to delete.
            return null;
        }
    }

    /// <summary>
    /// The text of the bridge-shared file at <paramref name="path"/>, read sharing read, write and delete, so the bridge can
    /// delete or rename over it while a server reads it.
    /// </summary>
    internal static string ReadShared(string path)
    {
        using StreamReader reader = new(OpenShared(path));
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Opens a bridge-shared file for reading sharing read, write and delete, so the bridge can delete or rename over it
    /// while a server reads it.
    /// </summary>
    internal static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
