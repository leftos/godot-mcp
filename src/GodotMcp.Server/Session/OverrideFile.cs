using System.Text;

namespace GodotMcp.Server.Session;

/// <summary>
/// The <c>override.cfg</c> that injects the bridge into a session's game, run or attached. Godot reads it for game runs only, never for the
/// editor or <c>--import</c>. The server's own file starts with <see cref="Marker"/>; a file without it is the project's
/// and is never written over or deleted. It also sets <see cref="IgnoreJoypadOnUnfocusedSetting"/>: off by default, so
/// losing focus leaves pad state alone (Godot 4.7.2 <c>input.cpp</c> L1600-1623); on when the session shuts the real pads
/// out, so that once the bridge marks the application unfocused Godot drops their driver input (L1652, L1684) while
/// injected pad events still pass. A quiet session's file, a run's or an attach's, adds a <c>[display]</c> section that creates the window unfocused
/// and off-screen: Godot creates the main window focused before any script runs (4.7.2
/// <c>platform/windows/display_server_windows.cpp</c> L1970-1973), while <see cref="NoFocusSetting"/> becomes the
/// NO_FOCUS flag at creation (<c>main.cpp</c> L2723-2724) and shows it without taking focus (L1964-1966), and an absolute
/// initial position (<c>main.cpp</c> L2730-2735; <c>project_settings.cpp</c> L1725, Absolute is 0) places it.
/// The line after the marker lists the file's owners (<see cref="OwnersPrefix"/>): the server processes with live sessions
/// using it, so a server releasing the folder deletes the file only when no other live server still uses it, and a file
/// whose owners have all exited (or that lists none) is stale.
/// </summary>
internal static class OverrideFile
{
    public const string FileName = "override.cfg";
    public const string Marker = "; godot-mcp: bridge injection, removed when the run stops";

    /// <summary>The start of the file's second line; the owners follow, comma-separated, each as <see cref="OverrideOwner"/> writes it.</summary>
    public const string OwnersPrefix = "; godot-mcp owners: ";

    public const string AutoloadName = "GodotMcpBridge";

    /// <summary>The project setting under its <c>[input_devices]</c> section, as <c>override.cfg</c> writes it.</summary>
    public const string IgnoreJoypadOnUnfocusedSetting = "joypads/ignore_joypad_on_unfocused_application";

    /// <summary>The project settings under the <c>[display]</c> section a quiet session writes.</summary>
    public const string NoFocusSetting = "window/size/no_focus";

    public const string InitialPositionTypeSetting = "window/size/initial_position_type";
    public const string InitialPositionSetting = "window/size/initial_position";

    /// <summary>Where a quiet session's window is created: far off every screen.</summary>
    public const string OffScreenPosition = "Vector2i(-9999, -9999)";

    private const int AbsolutePositionType = 0;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string PathIn(string projectDir) => Path.Combine(projectDir, FileName);

    /// <summary>
    /// Writes the marked file with this server among its owners, keeping the live owners of the marked file it replaces and
    /// dropping the ones that have exited.
    /// </summary>
    /// <param name="projectDir">The project folder.</param>
    /// <param name="bridgeScriptPath">The bridge script the autoload names.</param>
    /// <param name="shutOutRealGamepads">Whether the bridge shuts the machine's real pads out; the setting is written to match.</param>
    /// <param name="quiet">Whether the window is created unfocused and off-screen.</param>
    /// <exception cref="SessionException">The project has its own override.cfg.</exception>
    public static void Write(string projectDir, string bridgeScriptPath, bool shutOutRealGamepads, bool quiet)
    {
        string path = PathIn(projectDir);
        bool exists = File.Exists(path);
        if (exists && !IsOurs(path))
        {
            throw new SessionException(
                $"{path} already exists and is the project's own (its first line is not godot-mcp's marker \"{Marker}\"), "
                    + "so godot-mcp will not overwrite it. Rename or remove it, then run again."
            );
        }

        List<OverrideOwner> owners = exists ? LiveOthers(Read(path).Owners) : [];
        owners.Add(OverrideOwner.Current);

        string script = Path.GetFullPath(bridgeScriptPath).Replace('\\', '/');
        string body =
            $"[autoload]\n\n{AutoloadName}=\"*{script}\"\n\n[input_devices]\n\n"
            + $"{IgnoreJoypadOnUnfocusedSetting}={(shutOutRealGamepads ? "true" : "false")}\n";
        if (quiet)
        {
            body +=
                $"\n[display]\n\n{NoFocusSetting}=true\n{InitialPositionTypeSetting}={AbsolutePositionType}\n"
                + $"{InitialPositionSetting}={OffScreenPosition}\n";
        }

        File.WriteAllText(path, Header(owners) + body, Utf8NoBom);
    }

    /// <summary>
    /// Takes this server off the owners of the folder's marked file: deletes the file when no live owner is left, else
    /// rewrites it with the live owners left, its content otherwise unchanged. An unmarked file is left alone.
    /// </summary>
    /// <returns>Whether it deleted the file.</returns>
    public static bool Release(string projectDir)
    {
        string path = PathIn(projectDir);
        if (!File.Exists(path) || !IsOurs(path))
        {
            return false;
        }

        ParsedFile parsed = Read(path);
        List<OverrideOwner> remaining = LiveOthers(parsed.Owners);
        if (remaining.Count == 0)
        {
            File.Delete(path);
            return true;
        }

        if (!remaining.SequenceEqual(parsed.Owners))
        {
            File.WriteAllText(path, Header(remaining) + parsed.Body, Utf8NoBom);
        }

        return false;
    }

    /// <summary>
    /// The owners of the folder's marked file that still run, this server included; empty when there is no marked file,
    /// or when the file is stale: its owners have all exited, or it lists none.
    /// </summary>
    public static IReadOnlyList<OverrideOwner> LiveOwners(string projectDir)
    {
        string path = PathIn(projectDir);
        if (!File.Exists(path) || !IsOurs(path))
        {
            return [];
        }

        return [.. Read(path).Owners.Where(owner => owner == OverrideOwner.Current || owner.IsAlive()).Distinct()];
    }

    public static bool IsOurs(string path)
    {
        using StreamReader reader = new(path);
        string? firstLine = reader.ReadLine();
        return firstLine is not null && firstLine.TrimEnd() == Marker;
    }

    /// <summary>The owners that still run, leaving this server out.</summary>
    private static List<OverrideOwner> LiveOthers(IEnumerable<OverrideOwner> owners) =>
        [.. owners.Where(owner => owner != OverrideOwner.Current && owner.IsAlive()).Distinct()];

    private static string Header(IEnumerable<OverrideOwner> owners) => $"{Marker}\n{OwnersPrefix}{string.Join(',', owners)}\n";

    /// <summary>
    /// A marked file's owners and what follows them. A file without the owners line (one godot-mcp 0.8.0 wrote) has none, and
    /// its body is everything after the marker; an entry that is not an owner is skipped.
    /// </summary>
    private static ParsedFile Read(string path)
    {
        string text = File.ReadAllText(path);
        int afterMarker = LineEnd(text, 0);
        int afterOwners = LineEnd(text, afterMarker);
        string second = text[afterMarker..afterOwners].TrimEnd();
        if (!second.StartsWith(OwnersPrefix, StringComparison.Ordinal))
        {
            return new ParsedFile([], text[afterMarker..]);
        }

        List<OverrideOwner> owners = [];
        foreach (string entry in second[OwnersPrefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (OverrideOwner.TryParse(entry, out OverrideOwner owner))
            {
                owners.Add(owner);
            }
        }

        return new ParsedFile(owners, text[afterOwners..]);
    }

    /// <summary>The index just past the line starting at <paramref name="start"/> and its newline.</summary>
    private static int LineEnd(string text, int start)
    {
        int newline = text.IndexOf('\n', start);
        return newline < 0 ? text.Length : newline + 1;
    }

    private sealed record ParsedFile(IReadOnlyList<OverrideOwner> Owners, string Body);
}
