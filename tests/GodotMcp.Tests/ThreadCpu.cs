using System.Runtime.InteropServices;

namespace GodotMcp.Tests;

/// <summary>
/// This thread's CPU time through kernel32, kernel plus user in 100 ns units, so a test can prove work costs little CPU
/// without asserting on a wall clock: a busy machine slows the run without failing it.
/// </summary>
internal static class ThreadCpu
{
    /// <summary>The CPU time this thread has used so far.</summary>
    public static TimeSpan Current()
    {
        if (GetThreadTimes(GetCurrentThread(), out _, out _, out ulong kernel, out ulong user) == 0)
        {
            throw new InvalidOperationException($"GetThreadTimes failed with the Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        return TimeSpan.FromTicks((long)(kernel + user));
    }

    /// <summary>The calling thread's handle: a pseudo-handle the caller never closes.</summary>
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    /// <summary>The thread's creation, exit, kernel and user times, in 100 ns units; zero on failure, which sets the last error.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetThreadTimes(nint thread, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
}
