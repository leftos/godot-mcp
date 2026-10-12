using System.Runtime.InteropServices;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// Reads the capture group's Godot windows, which show on the user's desktop: launched unfocused (the harness's
/// <see cref="GodotMcp.Server.Session.SessionRegistry.LaunchesUnfocused"/>), they never take the foreground. Godot creates a
/// no_focus window with WS_EX_NOACTIVATE (4.7.2 <c>platform/windows/display_server_windows.cpp</c> L2627-2628), which Windows
/// never activates on its own; the WS_EX_TOPMOST it asks for there is gone by the time the launch answers.
/// </summary>
internal static class CaptureWindows
{
    /// <summary>WS_EX_NOACTIVATE: Windows never activates the window on its own.</summary>
    public const long NoActivateStyle = 0x08000000;

    /// <summary>WS_EX_TOPMOST: the window sits above every window without it.</summary>
    public const long TopmostStyle = 0x00000008;

    private const int ExtendedStyleIndex = -20;

    /// <summary>The window that has the foreground; zero when none has.</summary>
    public static nint Foreground() => NativeMethods.GetForegroundWindow();

    /// <summary>The id of the process that owns a window; 0 for no window.</summary>
    public static int OwnerOf(nint window)
    {
        _ = NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        return (int)processId;
    }

    /// <summary>A window's extended style bits (GWL_EXSTYLE).</summary>
    public static long ExtendedStyle(nint window) => NativeMethods.GetWindowLongPtrW(window, ExtendedStyleIndex);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern long GetWindowLongPtrW(nint window, int index);
    }
}
