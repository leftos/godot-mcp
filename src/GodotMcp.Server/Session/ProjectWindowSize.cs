using System.Globalization;

namespace GodotMcp.Server.Session;

/// <summary>
/// A project's <c>project.godot</c> settings, read one <c>[section]</c> <c>key=value</c> at a time, and the window size the file
/// lays a game out at.
/// </summary>
internal static class ProjectWindowSize
{
    /// <summary>Godot's default viewport width, 1152 (4.7.2 <c>core/config/project_settings.cpp</c> L1719).</summary>
    public const int DefaultWidth = 1152;

    /// <summary>Godot's default viewport height, 648 (4.7.2 <c>core/config/project_settings.cpp</c> L1720).</summary>
    public const int DefaultHeight = 648;

    private const string ProjectFile = "project.godot";
    private const string DisplaySection = "display";
    private const string ViewportWidthKey = "window/size/viewport_width";
    private const string ViewportHeightKey = "window/size/viewport_height";
    private const string WindowWidthOverrideKey = "window/size/window_width_override";
    private const string WindowHeightOverrideKey = "window/size/window_height_override";

    /// <summary>
    /// The size the project's window is created at, worked out as Godot works it out for a run without <c>--resolution</c>
    /// (4.7.2 <c>main/main.cpp</c> L2684-2699): <c>display/window/size/viewport_width</c> and <c>viewport_height</c>, each
    /// replaced by <c>window_width_override</c> or <c>window_height_override</c> when that one is above 0. Both overrides are
    /// registered with default 0 (<c>core/config/project_settings.cpp</c> L1740-1741) and <c>has_setting</c> is
    /// <c>props.has</c> (L903-907), so Godot takes its override branch on every run: each side replaces its own viewport side,
    /// and a file that sets one side alone still gets it. A side the file does not set to a positive number falls back to
    /// Godot's own default (L1719-1720), which is the right answer for a setting the project left alone.
    /// </summary>
    public static WindowSize Read(string projectDir)
    {
        IReadOnlyDictionary<string, string> display = ReadSection(projectDir, DisplaySection);
        return new WindowSize(
            Positive(ReadSide(display, WindowWidthOverrideKey)) ?? Positive(ReadSide(display, ViewportWidthKey)) ?? DefaultWidth,
            Positive(ReadSide(display, WindowHeightOverrideKey)) ?? Positive(ReadSide(display, ViewportHeightKey)) ?? DefaultHeight
        );
    }

    /// <summary>
    /// The value <paramref name="key"/> holds under <paramref name="section"/>, unquoted, or null when the file, the section or the
    /// key is missing or the file cannot be read.
    /// </summary>
    public static string? Read(string projectDir, string section, string key) =>
        ReadSection(projectDir, section).TryGetValue(key, out string? value) ? value : null;

    /// <summary>
    /// Every <c>key=value</c> of one <c>[section]</c>, a quote around a value taken off. A folder with no project.godot, or one
    /// the server cannot read, yields no settings: a launch of such a folder is refused before it reaches here, so the caller
    /// reading a default is the right answer rather than a failure to report.
    /// </summary>
    private static Dictionary<string, string> ReadSection(string projectDir, string section)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        string file = Path.Combine(projectDir, ProjectFile);
        try
        {
            if (!File.Exists(file))
            {
                return values;
            }

            bool inSection = false;
            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inSection = line == $"[{section}]";
                    continue;
                }

                int equals = line.IndexOf('=');
                if (inSection && equals > 0)
                {
                    values[line[..equals].Trim()] = line[(equals + 1)..].Trim().Trim('"');
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return values;
        }

        return values;
    }

    /// <summary>One setting's whole number; a value that is not one, or not a number at all, is absent.</summary>
    private static int? ReadSide(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out string? text) || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int side))
        {
            return null;
        }

        return side;
    }

    /// <summary>A side only a positive number sets: Godot leaves an override at 0 alone, and no window is 0 pixels wide.</summary>
    private static int? Positive(int? side) => side > 0 ? side : null;
}
