using System.Text;

namespace GodotMcp.Server.Session;

/// <summary>
/// The <c>override.cfg</c> that injects the bridge into a game run. Godot reads it for game runs only, never for the
/// editor or <c>--import</c>. The server's own file starts with <see cref="Marker"/>; a file without it is the project's
/// and is never written over or deleted. It also sets <see cref="IgnoreJoypadOnUnfocusedSetting"/>: off by default, so
/// losing focus leaves pad state alone (Godot 4.7.2 <c>input.cpp</c> L1600-1623); on when the run shuts the real pads
/// out, so that once the bridge marks the application unfocused Godot drops their driver input (L1652, L1684) while
/// injected pad events still pass.
/// </summary>
internal static class OverrideFile
{
    public const string FileName = "override.cfg";
    public const string Marker = "; godot-mcp: bridge injection, removed when the run stops";
    public const string AutoloadName = "GodotMcpBridge";

    /// <summary>The project setting under its <c>[input_devices]</c> section, as <c>override.cfg</c> writes it.</summary>
    public const string IgnoreJoypadOnUnfocusedSetting = "joypads/ignore_joypad_on_unfocused_application";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string PathIn(string projectDir) => Path.Combine(projectDir, FileName);

    /// <summary>Writes the marked file, replacing a marked one a crashed run left behind.</summary>
    /// <param name="projectDir">The project folder.</param>
    /// <param name="bridgeScriptPath">The bridge script the autoload names.</param>
    /// <param name="shutOutRealGamepads">Whether the bridge shuts the machine's real pads out; the setting is written to match.</param>
    /// <exception cref="SessionException">The project has its own override.cfg.</exception>
    public static void Write(string projectDir, string bridgeScriptPath, bool shutOutRealGamepads)
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
