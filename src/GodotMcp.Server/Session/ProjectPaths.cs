namespace GodotMcp.Server.Session;

/// <summary>Compares project folder paths the way the file system does.</summary>
internal static class ProjectPaths
{
    /// <summary>The full path with native separators and no trailing separator (a drive root keeps its own).</summary>
    public static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Whether two paths name the same folder; case-insensitive on Windows.</summary>
    public static bool AreSame(string first, string second)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Normalise(first), Normalise(second), comparison);
    }
}
