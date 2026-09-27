using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// Whether a debugger is attached to another process: on Windows, <c>CheckRemoteDebuggerPresent</c> on a handle opened for
/// query; elsewhere never. The handle needs <c>PROCESS_QUERY_INFORMATION</c>, the right <c>NtQueryInformationProcess</c>'s
/// <c>ProcessDebugPort</c> class requires, which the limited query right does not carry.
/// </summary>
internal static partial class DebuggerPresence
{
    private const uint ProcessQueryInformation = 0x0400;

    /// <summary>
    /// Whether a debugger is attached to the process; false off Windows and when the process cannot be opened or queried, which
    /// is logged at Debug with the Win32 error.
    /// </summary>
    public static bool IsAttached(int processId, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        nint process = OpenProcess(ProcessQueryInformation, inheritHandle: false, unchecked((uint)processId));
        if (process == 0)
        {
            LogFailure(logger, processId, nameof(OpenProcess));
            return false;
        }

        try
        {
            if (CheckRemoteDebuggerPresent(process, out bool present))
            {
                return present;
            }

            LogFailure(logger, processId, nameof(CheckRemoteDebuggerPresent));
            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Logs the failed call's Win32 error; called straight after it, before any other P/Invoke can overwrite the error.</summary>
    private static void LogFailure(ILogger logger, int processId, string call)
    {
        int error = Marshal.GetLastPInvokeError();
        Log.DebuggerCheckFailed(logger, processId, call, error);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CheckRemoteDebuggerPresent(nint process, [MarshalAs(UnmanagedType.Bool)] out bool debuggerPresent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
