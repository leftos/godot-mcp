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
    public static string UserDataDir(string projectDir, string root, string projectSettings, bool recording) =>
        PathJoin(OperatingSystem.IsWindows() ? root.Replace('\\', '/') : root, RelativeUserDir(projectDir, projectSettings, recording));

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
    public static string Create(string projectDir, string root, string? seed, string projectSettings, bool recording)
    {
        Delete(root);
        Directory.CreateDirectory(root);
        string userDir = UserDataDir(projectDir, root, projectSettings, recording);
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

    /// <summary>
    /// The folder a scratch run's result names, from the one the server worked out and the one the game's hello reported: the
    /// game's, with a warning naming both, when the two differ (ordinally, both in Godot's '/' form); the server's, with no
    /// warning, when they agree or the hello carried none (a bridge older than the field).
    /// </summary>
    public static (string UserDataDir, string? Warning) Reconcile(string serverDir, string? gameDir) =>
        gameDir is null || string.Equals(gameDir, serverDir, StringComparison.Ordinal)
            ? (serverDir, null)
            : (
                gameDir,
                $"The game reads its user data from {gameDir}, not {serverDir} where godot-mcp made the scratch folder and copied any "
                    + "seed; set userData's seed or the project's custom_user_dir_name so they agree."
            );

    /// <summary>
    /// The game's user data folder relative to its data path, as <see cref="UserDataDir"/> works it out: its three settings read
    /// from project.godot's <c>[application]</c> section and then the run's <paramref name="projectSettings"/> block's, in the
    /// order Godot registers them (it reads override.cfg after project.godot, 4.7.2 <c>core/config/project_settings.cpp</c>
    /// L773-781, L985-989).
    /// </summary>
    private static string RelativeUserDir(string projectDir, string projectSettings, bool recording)
    {
        ApplicationSettings settings = new(
            [
                .. ProjectWindowSize.ReadSectionRaw(projectDir, ApplicationSection),
                .. ProjectWindowSize.ReadSectionRaw(projectSettings.Split('\n'), ApplicationSection),
            ],
            recording
        );
        string appName = SafeDirName(settings.Get("config/name"), allowPaths: false);
        string appUserdata = $"{GodotDirName}/app_userdata";
        if (appName.Length == 0)
        {
            return $"{appUserdata}/{UnnamedProject}";
        }

        if (settings.Get("config/use_custom_user_dir") != "true")
        {
            return $"{appUserdata}/{appName}";
        }

        string custom = SafeDirName(settings.Get("config/custom_user_dir_name"), allowPaths: true);
        return custom.Length == 0 ? appName : custom;
    }

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

    /// <summary>
    /// One section's settings as a game run reads them with <c>ProjectSettings::get_setting_with_override</c> (4.7.2
    /// <c>core/config/project_settings.cpp</c> L415-439; <c>GLOBAL_GET</c> is that call, <c>project_settings.h</c> L265).
    /// Setting <c>key.&lt;tag&gt;[.&lt;tag&gt;…]</c> registers one feature override of <c>key</c> per tag, each tag's edges
    /// stripped, in the order the names are set (L324-340); the lookup takes the first whose tag the game has
    /// (<see cref="GameFeatureTags"/>), else the plain key. A name set twice keeps its first place and takes its last value.
    /// </summary>
    /// <param name="registered">Every <c>key=value</c> in the order Godot sets them, values as written.</param>
    /// <param name="recording">Whether the run records from launch, which gives the game the <c>movie</c> tag.</param>
    private sealed class ApplicationSettings(IReadOnlyList<KeyValuePair<string, string>> registered, bool recording)
    {
        /// <summary>
        /// The value <paramref name="key"/> reads as, a string's quotes and escapes undone and any other value as written; empty
        /// when it is unset or a string Godot fails to parse, as Godot's default is.
        /// </summary>
        public string Get(string key)
        {
            string name = registered.Select(entry => entry.Key).FirstOrDefault(set => IsOverrideTheGameHas(set, key)) ?? key;
            return LastValue(name) is { } text ? GodotValue(text) ?? "" : "";
        }

        /// <summary>Whether <paramref name="name"/> is a feature override of <paramref name="key"/> for a tag the game has.</summary>
        private bool IsOverrideTheGameHas(string name, string key)
        {
            string[] parts = name.Split('.');
            return parts.Length > 1
                && string.Equals(parts[0], key, StringComparison.Ordinal)
                && parts.Skip(1).Any(tag => GameFeatureTags.Has(tag.Trim(Edges), recording));
        }

        /// <summary>The value text <paramref name="name"/> was last set to; null when it was never set.</summary>
        private string? LastValue(string name)
        {
            for (int index = registered.Count - 1; index >= 0; index--)
            {
                if (string.Equals(registered[index].Key, name, StringComparison.Ordinal))
                {
                    return registered[index].Value;
                }
            }

            return null;
        }

        /// <summary>A quoted value's content (<see cref="QuotedString"/>); an unquoted one (<c>true</c>) as written.</summary>
        private static string? GodotValue(string text) => text.StartsWith('"') ? new QuotedString(text).Read() : text;
    }

    /// <summary>
    /// A quoted Godot string's content, its escapes undone as Godot's parser undoes them (4.7.2
    /// <c>core/variant/variant_parser.cpp</c> L277-395): <c>\b \t \n \f \r</c>, <c>\uXXXX</c> (4 hex digits) and
    /// <c>\UXXXXXX</c> (6), a UTF-16 surrogate pair of escapes joined into one code point, and any other escaped character
    /// standing for itself. Read by code point, as Godot reads it.
    /// </summary>
    /// <param name="text">The value text, starting with its opening quote.</param>
    private sealed class QuotedString(string text)
    {
        private const string HexDigits = "0123456789abcdef";
        private static readonly int[] EscapeLetters = ['b', 't', 'n', 'f', 'r'];
        private static readonly int[] EscapeCodes = [8, 9, 10, 12, 13];
        private readonly int[] _codes = [.. text.EnumerateRunes().Select(rune => rune.Value)];
        private readonly StringBuilder _value = new();
        private int _at = 1;
        private int _lead;

        /// <summary>
        /// The content up to the closing quote; null where Godot fails to parse it: an unterminated string, a malformed hex
        /// escape, an unpaired surrogate, or a code point past U+10FFFF.
        /// </summary>
        public string? Read()
        {
            while (_at < _codes.Length)
            {
                int code = _codes[_at++];
                if (code == '"')
                {
                    return _lead == 0 ? _value.ToString() : null;
                }

                int? character = code == '\\' ? Escaped() : code;
                if (character is null || !Add(character.Value))
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>The code point the escape after a backslash stands for; null when the string ends or a hex digit is bad.</summary>
        private int? Escaped()
        {
            if (_at >= _codes.Length)
            {
                return null;
            }

            int next = _codes[_at++];
            int letter = Array.IndexOf(EscapeLetters, next);
            if (letter >= 0)
            {
                return EscapeCodes[letter];
            }

            return next is 'u' or 'U' ? Hex(next == 'u' ? 4 : 6) : next;
        }

        /// <summary>The next <paramref name="digits"/> hex digits' value; null when the string ends first or one is not hex.</summary>
        private int? Hex(int digits)
        {
            int value = 0;
            for (int read = 0; read < digits; read++)
            {
                int digit = _at < _codes.Length ? HexValue(_codes[_at++]) : -1;
                if (digit < 0)
                {
                    return null;
                }

                value = (value << 4) | digit;
            }

            return value;
        }

        /// <summary>A hex digit's value, either case; -1 for any other code point.</summary>
        private static int HexValue(int code) => code <= 0x7F ? HexDigits.IndexOf(char.ToLowerInvariant((char)code), StringComparison.Ordinal) : -1;

        /// <summary>
        /// Appends one code point, holding a lead surrogate for the trail that must follow it; false where Godot fails: a lead
        /// after a lead, a trail without one, anything else after one, or a code point past U+10FFFF.
        /// </summary>
        private bool Add(int code)
        {
            if (code is >= 0xD800 and <= 0xDBFF)
            {
                return Lead(code);
            }

            int joined =
                code is >= 0xDC00 and <= 0xDFFF ? Trail(code)
                : _lead == 0 ? code
                : -1;
            if (joined is < 0 or > 0x10FFFF)
            {
                return false;
            }

            _value.Append(char.ConvertFromUtf32(joined));
            return true;
        }

        /// <summary>Holds a lead surrogate; false when one is already held.</summary>
        private bool Lead(int code)
        {
            if (_lead != 0)
            {
                return false;
            }

            _lead = code;
            return true;
        }

        /// <summary>The code point the held lead and this trail make; -1 when no lead is held.</summary>
        private int Trail(int code)
        {
            if (_lead == 0)
            {
                return -1;
            }

            int joined = char.ConvertToUtf32((char)_lead, (char)code);
            _lead = 0;
            return joined;
        }
    }
}
