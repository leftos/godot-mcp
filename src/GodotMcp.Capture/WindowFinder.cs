using System.Runtime.InteropServices;

namespace GodotMcp.Capture;

/// <summary>Says whether a handle names a window, and whether that window is minimized.</summary>
internal static class WindowFinder
{
    public static bool IsWindow(nint hwnd) => IsWindowNative(hwnd);

    public static bool IsMinimized(nint hwnd) => IsIconic(hwnd);

    [DllImport("user32.dll", EntryPoint = "IsWindow", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowNative(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
}
