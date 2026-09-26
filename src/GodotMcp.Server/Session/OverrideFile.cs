using System.Text;

namespace GodotMcp.Server.Session;

/// <summary>
/// The <c>override.cfg</c> that injects the bridge into a game run. Godot reads it for game runs only, never for the
/// editor or <c>--import</c>. The server's own file starts with <see cref="Marker"/>; a file without it is the project's
/// and is never written over or deleted. It also sets <see cref="IgnoreJoypadOnUnfocusedSetting"/>: off by default, so
/// losing focus leaves pad state alone (Godot 4.7.2 <c>input.cpp</c> L1600-1623); on when the run shuts the real pads
/// out, so that once the bridge marks the application unfocused Godot drops their driver input (L1652, L1684) while
/// injected pad events still pass. A quiet run's file adds a <c>[display]</c> section that creates the window unfocused
/// and off-screen: Godot creates the main window focused before any script runs (4.7.2
/// <c>platform/windows/display_server_windows.cpp</c> L1970-1973), while <see cref="NoFocusSetting"/> becomes the
/// NO_FOCUS flag at creation (<c>main.cpp</c> L2723-2724) and shows it without taking focus (L1964-1966), and an absolute
/// initial position (<c>main.cpp</c> L2730-2735; <c>project_settings.cpp</c> L1725, Absolute is 0) places it.
/// </summary>
internal static class OverrideFile
{
    public const string FileName = "override.cfg";
    public const string Marker = "; godot-mcp: bridge injection, removed when the run stops";
    public const string AutoloadName = "GodotMcpBridge";

    /// <summary>The project setting under its <c>[input_devices]</c> section, as <c>override.cfg</c> writes it.</summary>
    public const string IgnoreJoypadOnUnfocusedSetting = "joypads/ignore_joypad_on_unfocused_application";

    /// <summary>The project settings under the <c>[display]</c> section a quiet run writes.</summary>
    public const string NoFocusSetting = "window/size/no_focus";

    public const string InitialPositionTypeSetting = "window/size/initial_position_type";
    public const string InitialPositionSetting = "window/size/initial_position";

    /// <summary>Where a quiet run's window is created: far off every screen.</summary>
    public const string OffScreenPosition = "Vector2i(-9999, -9999)";

    private const int AbsolutePositionType = 0;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string PathIn(string projectDir) => Path.Combine(projectDir, FileName);

    /// <summary>Writes the marked file, replacing a marked one a crashed run left behind.</summary>
    /// <param name="projectDir">The project folder.</param>
    /// <param name="bridgeScriptPath">The bridge script the autoload names.</param>
    /// <param name="shutOutRealGamepads">Whether the bridge shuts the machine's real pads out; the setting is written to match.</param>
    /// <param name="quiet">Whether the window is created unfocused and off-screen.</param>
    /// <exception cref="SessionException">The project has its own override.cfg.</exception>
    public static void Write(string projectDir, string bridgeScriptPath, bool shutOutRealGamepads, bool quiet)
    {
        string path = PathIn(projectDir);
        if (File.Exists(path) && !IsOurs(path))
        {
            throw new SessionException(
                $"{path} already exists and is the project's own (its first line is not godot-mcp's marker \"{Marker}\"), "
                    + "so godot-mcp will not overwrite it. Rename or remove it, then run again."
            );
        }

        string script = Path.GetFullPath(bridgeScriptPath).Replace('\\', '/');
        string content =
            $"{Marker}\n[autoload]\n\n{AutoloadName}=\"*{script}\"\n\n[input_devices]\n\n{IgnoreJoypadOnUnfocusedSetting}={(shutOutRealGamepads ? "true" : "false")}\n";
        if (quiet)
        {
            content +=
                $"\n[display]\n\n{NoFocusSetting}=true\n{InitialPositionTypeSetting}={AbsolutePositionType}\n"
                + $"{InitialPositionSetting}={OffScreenPosition}\n";
        }

        File.WriteAllText(path, content, Utf8NoBom);
    }

    /// <summary>Deletes the file if it is the server's own; returns whether it deleted one.</summary>
    public static bool Remove(string projectDir)
    {
        string path = PathIn(projectDir);
        if (!File.Exists(path) || !IsOurs(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public static bool IsOurs(string path)
    {
        using StreamReader reader = new(path);
        string? firstLine = reader.ReadLine();
        return firstLine is not null && firstLine.TrimEnd() == Marker;
    }
}
