using System.Globalization;
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
/// initial position (<c>main.cpp</c> L2730-2735; <c>project_settings.cpp</c> L1725, Absolute is 0) places it. An unfocused
/// session's file, one that is not quiet, has the <c>[display]</c> section with <see cref="NoFocusSetting"/> and
/// <see cref="WindowModeSetting"/> minimized (<c>main.cpp</c> L2729), so its window is shown minimized and covers none of
/// the user's windows, as a window shown normally does until Godot's first message pump. Godot's show_window takes its
/// minimized branch, <c>ShowWindow(SW_SHOWMINIMIZED)</c> (<c>display_server_windows.cpp</c> L1962-1963), before its
/// no_focus branch (L1964-1966), and Win32 documents SW_SHOWMINIMIZED as activating the window: what keeps the user's
/// foreground is the WS_EX_NOACTIVATE that no_focus gives the window (L2627-2628) together with Windows' foreground lock.
/// The capture tests, whose windows Windows.Graphics.Capture must see on the user's desktop, restore it behind the user's
/// windows.
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

    /// <summary>The project setting an unfocused session's file sets to <see cref="MinimizedWindowMode"/>.</summary>
    public const string WindowModeSetting = "window/size/mode";

    /// <summary>Where a quiet session's window is created: far off every screen.</summary>
    public const string OffScreenPosition = "Vector2i(-9999, -9999)";

    private const int AbsolutePositionType = 0;

    /// <summary>DisplayServer.WINDOW_MODE_MINIMIZED, which <see cref="WindowModeSetting"/> takes as an int.</summary>
    private const int MinimizedWindowMode = 1;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string PathIn(string projectDir) => Path.Combine(projectDir, FileName);

    /// <summary>
    /// Writes the marked file with this server among its owners, keeping the live owners of the marked file it replaces and
    /// dropping the ones that have exited. A marked file another live server owns is shared only when it sets the same bridge,
    /// pad and quiet settings as this write, and is then rewritten in this write's form.
    /// </summary>
    /// <param name="projectDir">The project folder.</param>
    /// <param name="bridgeScriptPath">The bridge script the autoload names.</param>
    /// <param name="shutOutRealGamepads">Whether the bridge shuts the machine's real pads out; the setting is written to match.</param>
    /// <param name="quiet">Whether the window is created unfocused and off-screen.</param>
    /// <param name="unfocused">Whether a window that is not quiet is still created unfocused, in place, and shown minimized.</param>
    /// <exception cref="SessionException">
    /// The project has its own override.cfg, or another live server's marked file injects another bridge or holds other settings.
    /// </exception>
    public static void Write(string projectDir, string bridgeScriptPath, bool shutOutRealGamepads, bool quiet, bool unfocused)
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

        string script = Path.GetFullPath(bridgeScriptPath).Replace('\\', '/');
        string body = BodyFor(script, shutOutRealGamepads, quiet, unfocused);
        OverrideValues values = new(script, quiet, shutOutRealGamepads, Unfocused: quiet || unfocused);
        List<OverrideOwner> owners = exists ? SharersOf(projectDir, Read(path), values) : [];
        owners.Add(OverrideOwner.Current);
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

    /// <summary>
    /// Deletes the folder's marked file when it is stale: every owner it names has exited, or it names none. The bytes
    /// deleted are the bytes judged, and a game starting on the folder still reads a file the judge keeps
    /// (<see cref="LeftoverFile.RemoveIfStale(string, Func{string, LeftoverRemoval?}, Action)"/>). An owner entry that does
    /// not read as an owner counts as live; an unmarked file is never deleted.
    /// </summary>
    public static LeftoverRemoval RemoveIfStale(string projectDir) => LeftoverFile.RemoveIfStale(PathIn(projectDir), KeepReason);

    /// <summary>Why a marked file's text keeps it: the project's own file, a live owner or an unreadable one; null when it is stale.</summary>
    private static LeftoverRemoval? KeepReason(string text)
    {
        int afterMarker = LineEnd(text, 0);
        if (text[..afterMarker].TrimEnd() != Marker)
        {
            return new LeftoverRemoval(LeftoverState.NotOurs);
        }

        string second = text[afterMarker..LineEnd(text, afterMarker)].TrimEnd();
        return second.StartsWith(OwnersPrefix, StringComparison.Ordinal)
            ? LeftoverFile.KeepReason(second[OwnersPrefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            : null;
    }

    public static bool IsOurs(string path)
    {
        using StreamReader reader = new(path);
        string? firstLine = reader.ReadLine();
        return firstLine is not null && firstLine.TrimEnd() == Marker;
    }

    private static string BodyFor(string script, bool shutOutRealGamepads, bool quiet, bool unfocused)
    {
        string body =
            $"[autoload]\n\n{AutoloadName}=\"*{script}\"\n\n[input_devices]\n\n" + $"{IgnoreJoypadOnUnfocusedSetting}={Flag(shutOutRealGamepads)}\n";
        if (quiet)
        {
            body +=
                $"\n[display]\n\n{NoFocusSetting}=true\n{InitialPositionTypeSetting}={AbsolutePositionType}\n"
                + $"{InitialPositionSetting}={OffScreenPosition}\n";
        }
        else if (unfocused)
        {
            body += $"\n[display]\n\n{NoFocusSetting}=true\n{WindowModeSetting}={MinimizedWindowMode}\n";
        }

        return body;
    }

    /// <summary>
    /// The live other owners of the marked file, which the write keeps as owners. Their file is compared by what it sets, not
    /// by its text, so a file another version of the server wrote with the same bridge and settings is shared.
    /// </summary>
    /// <exception cref="SessionException">A live other owner's file injects another bridge or holds other settings.</exception>
    private static List<OverrideOwner> SharersOf(string projectDir, ParsedFile existing, OverrideValues ours)
    {
        List<OverrideOwner> others = LiveOthers(existing.Owners);
        OverrideValues theirs = ValuesIn(existing.Body);
        if (others.Count > 0 && theirs != ours)
        {
            throw new SessionException(DescribeConflict(projectDir, theirs, ours, others[0]));
        }

        return others;
    }

    /// <summary>
    /// What a body sets: the bridge its autoload line names (<c>an unreadable path</c> without one), whether it parks the window
    /// as a quiet one, whether it shuts the real pads out, and whether it creates the window unfocused.
    /// </summary>
    private static OverrideValues ValuesIn(string body) =>
        new(
            BridgeIn(body) ?? "an unreadable path",
            body.Contains($"{InitialPositionSetting}=", StringComparison.Ordinal),
            body.Contains($"{IgnoreJoypadOnUnfocusedSetting}=true", StringComparison.Ordinal),
            body.Contains($"{NoFocusSetting}=true", StringComparison.Ordinal)
        );

    /// <summary>Why a live other owner's file cannot be shared: its bridge path when it differs, else its settings.</summary>
    private static string DescribeConflict(string projectDir, OverrideValues theirs, OverrideValues ours, OverrideOwner other)
    {
        if (theirs.Bridge != ours.Bridge)
        {
            return $"{projectDir}'s override.cfg injects the bridge from {theirs.Bridge} for another godot-mcp server (pid {other.ProcessId}); "
                + $"this server's is {ours.Bridge}: two godot-mcp installs cannot share a folder at once.";
        }

        return $"{projectDir}'s override.cfg is in use by another godot-mcp server (pid {other.ProcessId}) with quiet={Flag(theirs.Quiet)} and "
            + $"shutOutRealGamepads={Flag(theirs.ShutOutRealGamepads)}; start this session with the same values, or end that server's sessions "
            + "on the folder first.";
    }

    /// <summary>The bridge script the body's autoload line names; null when it has none.</summary>
    private static string? BridgeIn(string body)
    {
        string prefix = $"{AutoloadName}=\"*";
        foreach (string line in body.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].TrimEnd().TrimEnd('"');
            }
        }

        return null;
    }

    private static string Flag(bool value) => value ? "true" : "false";

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

    /// <summary>What a marked file sets: the bridge script path as written, the quiet and pad settings, and the unfocused window.</summary>
    private sealed record OverrideValues(string Bridge, bool Quiet, bool ShutOutRealGamepads, bool Unfocused);
}

/// <summary>What a stale-file removal (<see cref="OverrideFile.RemoveIfStale"/>, <see cref="ArmFile.RemoveIfStale"/>) did.</summary>
internal enum LeftoverState
{
    /// <summary>There is no file.</summary>
    Absent,

    /// <summary>The file was stale and is deleted.</summary>
    Removed,

    /// <summary>The override.cfg has no godot-mcp marker: it is the project's own.</summary>
    NotOurs,

    /// <summary>Another process holds the file open.</summary>
    InUse,

    /// <summary>The file names an owner that still runs, another server's.</summary>
    LiveOwner,

    /// <summary>The file names this server among its owners: its own session or arm uses it.</summary>
    OwnedHere,

    /// <summary>The file names an entry that does not read as an owner, as a newer server's format would.</summary>
    UnreadableOwner,

    /// <summary>The file judged stale held other bytes when it was opened to be deleted.</summary>
    Changed,
}

/// <summary>A stale-file removal's outcome and the owner that kept the file: a live owner's pid, or the unreadable entry.</summary>
internal sealed record LeftoverRemoval(LeftoverState State, string? Owner = null);

/// <summary>The judge-then-delete that <see cref="OverrideFile"/> and <see cref="ArmFile"/> share.</summary>
internal static class LeftoverFile
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <inheritdoc cref="RemoveIfStale(string, Func{string, LeftoverRemoval?}, Action)"/>
    public static LeftoverRemoval RemoveIfStale(string path, Func<string, LeftoverRemoval?> keepReason) => RemoveIfStale(path, keepReason, () => { });

    /// <summary>
    /// Reads the file through a fully shared open and judges its text with no handle held, so a game starting on the folder
    /// still reads a file the judge keeps. A file judged stale is opened again sharing only deletion, and deleted while that
    /// handle is open only when it still holds the bytes judged; every godot-mcp writer rewrites the file in place, so a
    /// write racing the delete fails and a write before it leaves the file changed. Between the byte compare and the delete,
    /// another program may delete and re-create the path, or rename a file over it, since the handle shares deletion; that is
    /// not prevented. godot-mcp's own writers cannot, since they write in place under the list's hold. A file another process
    /// holds open is reported, not thrown.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="keepReason">Why the file's text keeps it; null when it is stale.</param>
    /// <param name="whileHeld">Run while the deleting handle is open, just before the delete.</param>
    public static LeftoverRemoval RemoveIfStale(string path, Func<string, LeftoverRemoval?> keepReason, Action whileHeld)
    {
        if (Open(path, FileShare.ReadWrite | FileShare.Delete) is not { } shared)
        {
            return Unopened(path);
        }

        byte[] judged;
        using (shared)
        {
            judged = ReadAll(shared);
        }

        if (keepReason(Decode(judged)) is { } kept)
        {
            return kept;
        }

        if (Open(path, FileShare.Delete) is not { } held)
        {
            return Unopened(path);
        }

        using (held)
        {
            if (!ReadAll(held).AsSpan().SequenceEqual(judged))
            {
                return new LeftoverRemoval(LeftoverState.Changed);
            }

            whileHeld();
            File.Delete(path);
            return new LeftoverRemoval(LeftoverState.Removed);
        }
    }

    /// <summary>The file opened for reading with <paramref name="share"/>; null when it is missing or another handle refuses the open.</summary>
    private static FileStream? Open(string path, FileShare share)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, share);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException e) when (e.HResult is SharingViolation or LockViolation)
        {
            return null;
        }
    }

    /// <summary>Why <see cref="Open"/> gave nothing: the file is gone, else another handle holds it.</summary>
    private static LeftoverRemoval Unopened(string path) => new(File.Exists(path) ? LeftoverState.InUse : LeftoverState.Absent);

    /// <summary>Every byte of <paramref name="file"/> from its start.</summary>
    private static byte[] ReadAll(FileStream file)
    {
        using MemoryStream bytes = new();
        file.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static string Decode(byte[] bytes)
    {
        using StreamReader reader = new(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Why a file naming <paramref name="entries"/> is kept: its first live owner (this server's own, or another's pid), else
    /// its first entry that does not read as an owner; null when every entry is an owner that has exited, or there are none.
    /// </summary>
    public static LeftoverRemoval? KeepReason(IEnumerable<string> entries)
    {
        string? unreadable = null;
        foreach (string entry in entries)
        {
            if (!OverrideOwner.TryParse(entry, out OverrideOwner owner))
            {
                unreadable ??= entry.Trim();
            }
            else if (owner == OverrideOwner.Current)
            {
                return new LeftoverRemoval(LeftoverState.OwnedHere);
            }
            else if (owner.IsAlive())
            {
                return new LeftoverRemoval(LeftoverState.LiveOwner, owner.ProcessId.ToString(CultureInfo.InvariantCulture));
            }
        }

        return unreadable is null ? null : new LeftoverRemoval(LeftoverState.UnreadableOwner, unreadable);
    }
}
