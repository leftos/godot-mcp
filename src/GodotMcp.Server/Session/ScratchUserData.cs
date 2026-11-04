using System.Security.Cryptography;
using System.Text;

namespace GodotMcp.Server.Session;

/// <summary>
/// A run's scratch <c>user://</c>: run_project's <c>userData: "scratch"</c>, or <c>{"seed": "&lt;folder&gt;"}</c> with
/// <see cref="SeedFolder"/> the absolute folder copied into it first.
/// </summary>
internal sealed record ScratchUserDataRequest(string? SeedFolder);

/// <summary>
/// The per-session folder a scratch run's game takes as its platform data folder, and the game's own user data folder inside it,
/// worked out as Godot works it out; made, seeded and deleted here.
/// </summary>
internal static class ScratchUserData
{
    /// <summary>The prefix of a session's folder under <c>&lt;project&gt;/.godot/godot-mcp/</c>.</summary>
    public const string FolderPrefix = "userdata-";

    private const string ApplicationSection = "application";
    private const string UnnamedProject = "[unnamed project]";

    /// <summary>The characters Godot replaces with '-' in a folder name (4.7.2 <c>core/os/os.cpp</c> L264).</summary>
    private static readonly string[] InvalidCharacters = [":", "*", "?", "\"", "<", ">", "|"];

    /// <summary>The characters Godot's <c>String::strip_edges</c> trims: every one up to and including the space.</summary>
    private static readonly char[] Edges = [.. Enumerable.Range(0, 33).Select(code => (char)code)];

    /// <summary>
    /// The engine's folder name in a data path: "Godot" on Windows (<c>platform/windows/os_windows.cpp</c> L2466-2468
    /// capitalises it), "godot" elsewhere (<c>core/os/os.cpp</c> L294-297).
    /// </summary>
    public static string GodotDirName => OperatingSystem.IsWindows() ? "Godot" : "godot";

    /// <summary>
    /// The session's scratch folder: <c>&lt;project&gt;/.godot/godot-mcp/userdata-&lt;server pid&gt;-&lt;session&gt;-&lt;hash&gt;</c>,
    /// so two servers' sessions of one name on one folder never share it. The hash, the first 8 hex digits of the SHA-256 of the
    /// session's exact name, keeps names Windows folds into one folder (<c>game</c> and <c>game.</c>, <c>Game</c> and <c>game</c>)
    /// apart, and keeps the folder's name from ending in a period.
    /// </summary>
    public static string RootFor(string projectDir, string session) =>
        Path.Combine(ProjectPrep.LogFolder(projectDir), $"{FolderPrefix}{Environment.ProcessId}-{session}-{NameHash(session)}");

    /// <summary>The first 8 lowercase hex digits of the SHA-256 of <paramref name="session"/>'s UTF-8 bytes.</summary>
    private static string NameHash(string session) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session)))[..8];

    /// <summary>
    /// The game's user data folder when its platform data folder is <paramref name="root"/>, in Godot's form ('/' separators), as
    /// <c>OS.get_user_data_dir()</c> returns it. 4.7.2 <c>core/os/os.cpp</c> L338-354: the made-safe
    /// <c>application/config/name</c>, empty giving <c>Godot/app_userdata/[unnamed project]</c>; with
    /// <c>use_custom_user_dir</c>, the made-safe <c>custom_user_dir_name</c> (paths allowed), empty giving the name; otherwise
    /// <c>Godot/app_userdata/&lt;name&gt;</c>, each joined onto the data path (<c>os_windows.cpp</c> L2417-2426, L2508-2510:
    /// <c>APPDATA</c> with '\' turned to '/'). Only Windows turns '\' to '/': elsewhere (<c>drivers/unix/os_unix.cpp</c>
    /// L1142-1144) it is a file name's own character, so the root is kept as it is.
    /// </summary>
    public static string UserDataDir(string projectDir, string root) =>
        PathJoin(OperatingSystem.IsWindows() ? root.Replace('\\', '/') : root, RelativeUserDir(projectDir));

    /// <summary>
    /// Godot's <c>OS::get_safe_dir_name</c> (4.7.2 <c>core/os/os.cpp</c> L262-289): with paths allowed, '\' becomes '/', "//"
    /// becomes "/" and ".." joins the invalid set; otherwise '/' and '\' join it and "." and ".." become "dot" and "twodots";
    /// the edges are stripped before the replacement, every invalid string becomes '-', and trailing periods go.
    /// </summary>
    public static string SafeDirName(string name, bool allowPaths)
    {
        List<string> invalid = [.. InvalidCharacters];
        string safe;
        if (allowPaths)
        {
            invalid.Add("..");
            safe = name.Replace('\\', '/').Replace("//", "/", StringComparison.Ordinal).Trim(Edges);
        }
        else
        {
            invalid.Add("/");
            invalid.Add("\\");
            safe = name.Trim(Edges) switch
            {
                "." => "dot",
                ".." => "twodots",
                var stripped => stripped,
            };
        }

        foreach (string character in invalid)
        {
            safe = safe.Replace(character, "-", StringComparison.Ordinal);
        }

        return safe.TrimEnd('.');
    }

    /// <summary>
    /// Makes <paramref name="root"/> afresh and copies <paramref name="seed"/>, when given, into the game's user data folder inside
    /// it. A folder already there (a killed server's, or a delete that failed) is deleted first with <see cref="Delete"/>, and a
    /// link at the root only unlinked, so the run starts empty and nothing is written through a link.
    /// </summary>
    /// <returns>The game's user data folder (<see cref="UserDataDir"/>).</returns>
    /// <exception cref="IOException">The folder could not be made or a seed file not copied.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for want of access.</exception>
    public static string Create(string projectDir, string root, string? seed)
    {
        Delete(root);
        Directory.CreateDirectory(root);
        string userDir = UserDataDir(projectDir, root);
        if (seed is not null)
        {
            CopyTree(seed, userDir);
        }

        return userDir;
    }

    /// <summary>
    /// Deletes <paramref name="root"/> and everything in it; a missing one is left alone. A root that is itself a link is only
    /// unlinked, so the delete never follows it out of the project.
    /// </summary>
    /// <exception cref="IOException">A file in it is held open, or the folder could not be removed.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for want of access.</exception>
    public static void Delete(string root)
    {
        DirectoryInfo folder = new(root);
        if (!folder.Exists)
        {
            return;
        }

        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            folder.Delete();
            return;
        }

        folder.Delete(recursive: true);
    }

    /// <summary>The game's user data folder relative to its data path, as <see cref="UserDataDir"/> works it out.</summary>
    private static string RelativeUserDir(string projectDir)
    {
        string appName = SafeDirName(Setting(projectDir, "config/name"), allowPaths: false);
        string appUserdata = $"{GodotDirName}/app_userdata";
        if (appName.Length == 0)
        {
            return $"{appUserdata}/{UnnamedProject}";
        }

        if (Setting(projectDir, "config/use_custom_user_dir") != "true")
        {
            return $"{appUserdata}/{appName}";
        }

        string custom = SafeDirName(Setting(projectDir, "config/custom_user_dir_name"), allowPaths: true);
        return custom.Length == 0 ? appName : custom;
    }

    /// <summary>One <c>[application]</c> setting of project.godot; empty when the file leaves it out, as Godot's default is.</summary>
    private static string Setting(string projectDir, string key) => ProjectWindowSize.Read(projectDir, ApplicationSection, key) ?? "";

    /// <summary>Godot's <c>String::path_join</c>: one '/' between the two unless either side already has it.</summary>
    private static string PathJoin(string path, string file) =>
        path.Length == 0 ? file
        : path.EndsWith('/') || file.StartsWith('/') ? path + file
        : $"{path}/{file}";

    /// <summary>Copies every folder and file under <paramref name="source"/> into <paramref name="destination"/>; links are skipped.</summary>
    private static void CopyTree(string source, string destination)
    {
        EnumerationOptions everything = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        Directory.CreateDirectory(destination);
        foreach (string folder in Directory.EnumerateDirectories(source, "*", everything))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, folder)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", everything))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}
