using System.Diagnostics;

namespace GodotMcp.Server.Session;

/// <summary>Waiting for a process to be gone rather than merely exited.</summary>
internal static class ProcessExit
{
    /// <summary>Waits until Windows signals the process, which it does only after closing the process's handles (its current
    /// directory among them); Process.HasExited and WaitForExitAsync return as soon as the exit code is set, tens of ms
    /// earlier. True when the process is gone within the timeout.</summary>
    public static Task<bool> WaitUntilGoneAsync(Process process, TimeSpan timeout) => Task.Run(() => process.WaitForExit(timeout));
}
