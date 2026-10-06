using System.Diagnostics;

namespace GodotMcp.Server.Session;

/// <summary>Waiting for a process to be gone rather than merely exited.</summary>
internal static class ProcessExit
{
    /// <summary>How long past a deadline's backstop the wait's own thread blocks, so the deadline always ends the wait first.</summary>
    private static readonly TimeSpan BackstopMargin = TimeSpan.FromSeconds(1);

    /// <summary>Waits until Windows signals the process, which it does only after closing the process's handles (its current
    /// directory among them); Process.HasExited and WaitForExitAsync return as soon as the exit code is set, tens of ms
    /// earlier. True when the process is gone within the timeout.</summary>
    public static Task<bool> WaitUntilGoneAsync(Process process, TimeSpan timeout) => Task.Run(() => process.WaitForExit(timeout));

    /// <summary>
    /// Waits, as <see cref="WaitUntilGoneAsync(Process, TimeSpan)"/> does, until the process is gone or
    /// <paramref name="deadline"/> expires, at its load-adjusted budget or its backstop.
    /// </summary>
    /// <returns>True when the process is gone before the deadline expires.</returns>
    public static Task<bool> WaitUntilGoneAsync(Process process, LoadDeadline deadline) =>
        GoneWithinAsync(WaitUntilGoneAsync(process, deadline.Backstop + BackstopMargin), deadline);

    /// <summary>Whether <paramref name="gone"/> reports the process gone before <paramref name="deadline"/> expires.</summary>
    /// <param name="gone">Completes with true once the process is gone, or false when its own wait gave up.</param>
    /// <param name="deadline">The load-adjusted wait.</param>
    internal static async Task<bool> GoneWithinAsync(Task<bool> gone, LoadDeadline deadline)
    {
        try
        {
            return await gone.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.Expired)
        {
            return false;
        }
    }
}
