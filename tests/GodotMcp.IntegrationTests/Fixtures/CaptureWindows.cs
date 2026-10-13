using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// Reads and places the capture group's Godot windows, which show on the user's desktop. Launched unfocused (the harness's
/// <see cref="GodotMcp.Server.Session.SessionRegistry.LaunchesUnfocused"/>), a game's main window is created with
/// WS_EX_NOACTIVATE (4.7.2 <c>platform/windows/display_server_windows.cpp</c> L2627-2628) and shown minimized. Godot's
/// show_window takes its minimized branch, <c>ShowWindow(SW_SHOWMINIMIZED)</c> (L1962-1963), before its no_focus branch
/// (L1964-1966), and Win32 documents SW_SHOWMINIMIZED as activating the window: what keeps the user's foreground is
/// WS_EX_NOACTIVATE together with Windows' foreground lock, and the foreground test checks the style. The WS_EX_TOPMOST
/// asked for at L2627-2628 is dropped by <c>_update_window_style</c>'s <c>SetWindowPos(HWND_NOTOPMOST)</c> (L2680) before
/// the launch answers. A window shown normally would land above the user's windows, and a request to move it runs on
/// Godot's main thread, which first pumps its messages about 220 ms after the show; minimized, it covers nothing, and the
/// harness restores it without activating it and then pushes it to the bottom of the z-order (<see cref="RestoreBehind"/>),
/// both handled in that first pump. Windows.Graphics.Capture records a window covered by others.
/// </summary>
internal static class CaptureWindows
{
    /// <summary>
    /// The hang guard of a wait on a launched game's window: a passing run never reaches it, and it fits inside
    /// <see cref="TestTimeouts.OwnLaunchMs"/> after a load-adjusted launch of up to 75 s.
    /// </summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    /// <summary>WS_EX_NOACTIVATE: Windows never activates the window on its own.</summary>
    public const long NoActivateStyle = 0x08000000;

    /// <summary>WS_EX_TOPMOST: the window sits above every window without it.</summary>
    public const long TopmostStyle = 0x00000008;

    /// <summary>The InputProbe's client size: its <c>project.godot</c>'s viewport_width and viewport_height.</summary>
    public const int ProbeClientWidth = 640;

    public const int ProbeClientHeight = 360;

    private const int ExtendedStyleIndex = -20;
    private const string GodotWindowClass = "Engine";
    private const string ConsoleHost = "conhost.exe";
    private const string ConsoleWrapperSuffix = "_console.exe";
    private const uint NextWindowBelow = 2;
    private const int ShowNoActivate = 4;
    private const uint SnapshotProcesses = 2;
    private const nint Bottom = 1;

    // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS: the request is posted to Godot's thread, so a
    // game that is not pumping its messages yet never blocks the caller.
    private const uint PushFlags = 0x0001 | 0x0002 | 0x0010 | 0x4000;

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

    /// <summary>Whether a window is minimized.</summary>
    public static bool IsMinimized(nint window) => NativeMethods.IsIconic(window);

    /// <summary>Whether a window still exists.</summary>
    public static bool Exists(nint window) => NativeMethods.IsWindow(window);

    /// <summary>A window's client area size.</summary>
    public static (int Width, int Height) ClientSize(nint window)
    {
        if (!NativeMethods.GetClientRect(window, out Rect client))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"GetClientRect failed for window {window}");
        }

        return (client.Right - client.Left, client.Bottom - client.Top);
    }

    /// <summary>
    /// Whether <paramref name="upper"/> is above <paramref name="lower"/> in the z-order: it is met walking down from
    /// <paramref name="upper"/>.
    /// </summary>
    public static bool IsAbove(nint upper, nint lower)
    {
        for (
            nint below = NativeMethods.GetWindow(upper, NextWindowBelow);
            below != nint.Zero;
            below = NativeMethods.GetWindow(below, NextWindowBelow)
        )
        {
            if (below == lower)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The game a run's process started; null until it has, or once the run's process has exited. A run through the
    /// Godot_console.exe wrapper (an image named <c>*_console.exe</c>) starts the game as its child beside its console host;
    /// any other image, such as a <c>GODOT_PATH</c> naming the non-console build, is the game itself. A child is taken only
    /// if it started no earlier than the run: Toolhelp records a parent's pid at creation and never updates it, so a process
    /// started by an earlier holder of the run's pid, before Windows reused it, is not the game.
    /// </summary>
    public static int? GameOf(int runProcessId, DateTime runStart)
    {
        List<ProcessRow> processes = Processes();
        if (processes.Find(row => row.Id == runProcessId) is not ProcessRow run)
        {
            return null;
        }

        if (!run.Image.EndsWith(ConsoleWrapperSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return runProcessId;
        }

        foreach (ProcessRow child in processes)
        {
            // A child that has exited has no start time, and the comparison with null is false.
            if (
                child.ParentId == runProcessId
                && !string.Equals(child.Image, ConsoleHost, StringComparison.OrdinalIgnoreCase)
                && StartTimeOf(child.Id) >= runStart
            )
            {
                return child.Id;
            }
        }

        return null;
    }

    /// <summary>When a process started, as Windows recorded its creation; null when no process has the id any more.</summary>
    public static DateTime? StartTimeOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime;
        }
        catch (ArgumentException)
        {
            // No process has the id: it exited before it was opened.
            return null;
        }
        catch (InvalidOperationException)
        {
            // It exited between its opening and the read.
            return null;
        }
    }

    /// <summary>The game's main windows that are shown minimized: its visible top-level windows of Godot's class that are iconic.</summary>
    /// <exception cref="Win32Exception">The windows could not be enumerated.</exception>
    public static List<nint> MinimizedMainWindowsOf(int gameProcessId)
    {
        List<nint> windows = [];
        bool enumerated = NativeMethods.EnumWindows(
            (window, _) =>
            {
                if (
                    OwnerOf(window) == gameProcessId
                    && NativeMethods.IsWindowVisible(window)
                    && IsMinimized(window)
                    && ClassOf(window) == GodotWindowClass
                )
                {
                    windows.Add(window);
                }

                return true;
            },
            0
        );
        if (!enumerated)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumWindows failed");
        }

        return windows;
    }

    /// <summary>
    /// Restores a minimized window without activating it, then pushes it to the bottom of the z-order, in that order: a
    /// push posted before the restore can be undone by it. Both requests are posted to the window's thread.
    /// </summary>
    /// <exception cref="Win32Exception">Either request could not be posted.</exception>
    public static void RestoreBehind(nint window)
    {
        if (!NativeMethods.ShowWindowAsync(window, ShowNoActivate))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"ShowWindowAsync(SW_SHOWNOACTIVATE) failed for window {window}");
        }

        if (!NativeMethods.SetWindowPos(window, Bottom, 0, 0, 0, 0, PushFlags))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"SetWindowPos(HWND_BOTTOM) failed for window {window}");
        }
    }

    /// <summary>
    /// Fails unless the game's main window was restored at the InputProbe's size: a window left minimized can stop drawing,
    /// which would fail a capture test for the wrong reason. The restore lands at Godot's first message pump, before the
    /// launch answers; the wait is a hang guard (<see cref="HangGuard"/>), and it ends at once when the game exits or the
    /// harness's restore loop fails.
    /// </summary>
    public static async Task AssertRestoredAsync(SessionHarness harness, int gameProcessId, nint window, CancellationToken cancellationToken)
    {
        using var game = Process.GetProcessById(gameProcessId);
        bool restored = await Poll.UntilAsync(
            () =>
            {
                ThrowIfEnded(harness, game);
                return !IsMinimized(window);
            },
            HangGuard,
            cancellationToken
        );
        (int width, int height) = ClientSize(window);
        string state = FormattableString.Invariant(
            $"the game's (pid {gameProcessId}) main window {window} is minimized={IsMinimized(window)} with a {width}x{height} client"
        );
        Assert.True(
            restored && (width, height) == (ProbeClientWidth, ProbeClientHeight),
            FormattableString.Invariant(
                $"{state}, not restored at the project's {ProbeClientWidth}x{ProbeClientHeight}: the harness did not restore it"
            )
        );
    }

    /// <summary>Ends a wait on a launched game's window at once with its cause: the harness's restore loop failed, or the game exited.</summary>
    /// <exception cref="InvalidOperationException">The harness's restore loop failed.</exception>
    public static void ThrowIfEnded(SessionHarness harness, Process game)
    {
        harness.ThrowIfRestoreFailed();
        if (game.HasExited)
        {
            Assert.Fail($"the game (pid {game.Id}) exited while the test waited on its window");
        }
    }

    private static string ClassOf(nint window)
    {
        char[] name = new char[256];
        int length = NativeMethods.GetClassNameW(window, name, name.Length);
        return new string(name, 0, length);
    }

    /// <summary>Every process Toolhelp lists, oldest first.</summary>
    private static List<ProcessRow> Processes()
    {
        nint snapshot = NativeMethods.CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == -1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateToolhelp32Snapshot failed");
        }

        try
        {
            List<ProcessRow> processes = [];
            ProcessEntry entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool more = NativeMethods.Process32FirstW(snapshot, ref entry); more; more = NativeMethods.Process32NextW(snapshot, ref entry))
            {
                processes.Add(new ProcessRow((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            }

            return processes;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(snapshot);
        }
    }

    private sealed record ProcessRow(int Id, int ParentId, string Image);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    private static class NativeMethods
    {
        public delegate bool EnumWindowsCallback(nint window, nint parameter);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern long GetWindowLongPtrW(nint window, int index);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(nint window);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(nint window, out Rect client);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern nint GetWindow(nint window, uint command);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetClassNameW(nint window, [Out] char[] name, int capacity);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindowAsync(nint window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint handle);
    }
}
