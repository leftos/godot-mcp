using System.Runtime.InteropServices;

namespace GodotMcp.Dotnet.Shim;

/// <summary>The kernel32 calls the shim needs to find itself and the hostfxr already loaded in the process.</summary>
internal static unsafe partial class NativeMethods
{
    private const uint FromAddress = 0x4;
    private const uint UnchangedRefcount = 0x2;
    private const int MaxPath = 32768;

    /// <summary>The handle of a module already loaded in the process, or zero; never loads one.</summary>
    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint ModuleHandle(string name);

    /// <summary>The module that holds <paramref name="address"/>.</summary>
    public static nint ModuleAt(nint address) =>
        GetModuleHandleExW(FromAddress | UnchangedRefcount, address, out nint module)
            ? module
            : throw new InvalidOperationException($"GetModuleHandleExW failed: {Marshal.GetLastPInvokeError()}");

    /// <summary>The full path of a loaded module's file.</summary>
    public static string ModuleFileName(nint module)
    {
        char[] buffer = new char[MaxPath];
        fixed (char* start = buffer)
        {
            uint length = GetModuleFileNameW(module, start, MaxPath);
            return length == 0
                ? throw new InvalidOperationException($"GetModuleFileNameW failed: {Marshal.GetLastPInvokeError()}")
                : new string(start, 0, (int)length);
        }
    }

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial uint GetModuleFileNameW(nint module, char* buffer, uint size);
}
