namespace GodotMcp.Launcher;

/// <summary>
/// The pointer file <c>current</c> beside the launcher, whose first line names the version folder under <c>versions</c>
/// that holds the server to start.
/// </summary>
internal static class ServerPointer
{
    public const string PointerFileName = "current";
    public const string VersionsFolderName = "versions";
    public const string ServerFileName = "godot-mcp.exe";

    /// <summary>The server the pointer in <paramref name="launcherFolder"/> names, by its real versioned path.</summary>
    /// <exception cref="LauncherException">The pointer cannot be read, names no single folder, or its folder has no server.</exception>
    public static string Resolve(string launcherFolder)
    {
        string pointer = Path.Combine(launcherFolder, PointerFileName);
        string name = ReadFirstLine(pointer);
        if (name.Length == 0)
        {
            throw new LauncherException($"{pointer} names no version folder: its first line is blank.");
        }

        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new LauncherException($"{pointer} names '{name}', which is not a single folder name.");
        }

        string server = Path.Combine(launcherFolder, VersionsFolderName, name, ServerFileName);
        return File.Exists(server) ? server : throw new LauncherException($"{pointer} names version '{name}', but {server} does not exist.");
    }

    /// <summary>The file's first line, trimmed; empty for an empty file. Opened so an install can replace it meanwhile.</summary>
    private static string ReadFirstLine(string pointer)
    {
        try
        {
            using FileStream stream = new(pointer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            return (reader.ReadLine() ?? "").Trim();
        }
        catch (FileNotFoundException exception)
        {
            throw new LauncherException($"{pointer} does not exist.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"{pointer} could not be read: {exception.Message}", exception);
        }
    }
}
