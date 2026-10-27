namespace GodotMcp.Server.Session;

/// <summary>
/// The line Godot prints when it cannot create a window because no usable OpenGL is available: on Windows,
/// <c>platform/windows/display_server_windows.cpp</c>'s <c>_create_gl_window</c> fails with "Failed to create ANGLE OpenGL
/// window." and the engine stays alive behind a modal alert. The OpenGL 3.3 warning that precedes a working ANGLE fallback is
/// not this line, so a game that falls back and runs is never refused for it.
/// </summary>
internal static class NoGlLine
{
    private const string Fragment = "Failed to create ANGLE OpenGL window";

    /// <summary>
    /// Whether <paramref name="line"/> is the engine's refusal to create an ANGLE OpenGL window, matched as a case-sensitive
    /// substring so the engine's own <c>ERROR: </c> prefix and the <c>   at: </c> line beside it do not decide the match.
    /// </summary>
    public static bool Matches(string line) => line.Contains(Fragment, StringComparison.Ordinal);
}
