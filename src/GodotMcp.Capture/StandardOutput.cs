using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GodotMcp.Capture;

/// <summary>
/// The process's stdout: the frames go through a <see cref="FileStream"/> over the raw handle, because the console stream
/// <see cref="Console.OpenStandardOutput()"/> returns reports a write to a pipe whose reader has gone as success, and a
/// run's end of input is that same handle closed, since disposing that stream leaves the OS handle open.
/// </summary>
internal static partial class StandardOutput
{
    private const int StdOutputHandle = -11;

    /// <summary>
    /// The stream the frames are written to. It owns no handle, so <see cref="Close"/> still ends the pipe, and it is
    /// unbuffered: a frame is one write, so the reader going fails that write and nothing is left to flush.
    /// </summary>
    public static FileStream Open() => new(new SafeFileHandle(GetStdHandle(StdOutputHandle), ownsHandle: false), FileAccess.Write, bufferSize: 0);

    public static void Close()
    {
        nint handle = GetStdHandle(StdOutputHandle);
        if ((handle != 0) && (handle != -1) && !CloseHandle(handle))
        {
            Console.Error.WriteLine($"godot-mcp-capture: closing stdout failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetStdHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
