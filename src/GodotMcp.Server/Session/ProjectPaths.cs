namespace GodotMcp.Server.Session;

/// <summary>Compares project folder paths the way the file system does.</summary>
internal static class ProjectPaths
{
    /// <summary>The comparison that decides whether two normalised paths name the same folder: case-insensitive on Windows.</summary>
    public static StringComparer FolderComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The full path with native separators and no trailing separator (a drive root keeps its own).</summary>
    public static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Whether two paths name the same folder; case-insensitive on Windows.</summary>
    public static bool AreSame(string first, string second) => FolderComparer.Equals(Normalise(first), Normalise(second));
}
