using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GodotMcp.Server.Session;

/// <summary>
/// A process pinned by a handle opened by its pid before anything about it is read, and kept open until disposed: Windows hands
/// no pid on while a handle to its process is open, so every check, kill and wait made while this is undisposed is of the
/// process whose creation time was checked through <see cref="Handle"/>, never of a later process given the same pid.
/// </summary>
internal sealed partial class HeldProcess : IDisposable
{
    /// <summary>The rights a stop needs on a game it may kill: to end it, to wait for it, and to read its creation time.</summary>
    public const uint GameRights = ProcessTerminate | Synchronize | ProcessQueryLimitedInformation;

    /// <summary>The rights a stop needs on a process it only waits for: to wait for it, and to read its creation time.</summary>
    public const uint WaitRights = Synchronize | ProcessQueryLimitedInformation;

    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const int ErrorAccessDenied = 5;
    private const uint WaitTimeout = 0x102;

    /// <summary>
    /// Whether <see cref="Handle"/> is this one's own, opened by <see cref="Open"/>, rather than the process's
    /// (<see cref="Holding"/>).
    /// </summary>
    private readonly bool _ownsHandle;

    private bool _handedOver;

    private HeldProcess(Process process, SafeProcessHandle handle, DateTime startTime, bool ownsHandle)
    {
        Process = process;
        Handle = handle;
        StartTime = startTime;
        _ownsHandle = ownsHandle;
    }

    /// <summary>
    /// The process. From <see cref="Open"/>, found by its pid while <see cref="Handle"/> keeps the pid its own, with no handle of
    /// its own; from <see cref="Holding"/>, the process given, whose own handle is <see cref="Handle"/>.
    /// </summary>
    public Process Process { get; }

    /// <summary>The handle that pins the process, through which its creation time was read and through which it is waited for.</summary>
    public SafeProcessHandle Handle { get; }

    public int Id => Process.Id;

    /// <summary>The creation time read through <see cref="Handle"/>, as <see cref="Process.StartTime"/> gives it, in local time.</summary>
    public DateTime StartTime { get; }

    /// <summary>
    /// Opens the process with <paramref name="pid"/> with <paramref name="rights"/> and holds it when its creation time, read
    /// through the handle just opened, is <paramref name="started"/> and it is not gone (its handle is not signalled). A process
    /// killed whose exit code is set but which Windows has not yet let go of opens this way, though .NET's own open of it
    /// (<see cref="Process.Handle"/>) refuses it, since it checks the exit code first. The caller disposes the process returned.
    /// </summary>
    /// <returns>The held process; null when no process has the pid, its creation time differs or cannot be read, or it is gone.</returns>
    /// <exception cref="UnauthorizedAccessException">A process has the pid but refuses the open: it is not gone.</exception>
    /// <exception cref="InvalidOperationException">The list of processes cannot be read.</exception>
    /// <exception cref="Win32Exception">The list of processes cannot be read.</exception>
    public static HeldProcess? Open(int pid, DateTime started, uint rights)
    {
        SafeProcessHandle? handle = OpenProcess(rights, inheritHandle: false, pid);
        try
        {
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                return error == ErrorAccessDenied
                    ? throw new UnauthorizedAccessException($"Process {pid} refused to be opened.", new Win32Exception(error))
                    : null;
            }

            // Only once the handle holds the pid is the process looked up by it, so the one found is the one checked.
            if (
                CreationTime(handle) is not { } created
                || created != started
                || WaitForSingleObject(handle, 0) != WaitTimeout
                || RunKill.FromProcessList(pid) is not { } process
            )
            {
                return null;
            }

            HeldProcess held = new(process, handle, created, ownsHandle: true);
            handle = null;
            return held;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>
    /// Opens the process with <paramref name="pid"/> with <paramref name="rights"/> only to read its creation time and whether it
    /// is still running (its handle is not signalled), and closes the handle before returning: the pid is not held.
    /// </summary>
    /// <returns>What the probe read; null when no process has the pid or its creation time cannot be read.</returns>
    /// <exception cref="UnauthorizedAccessException">A process has the pid but refuses the open: it is not gone.</exception>
    public static ProcessProbe? Probe(int pid, uint rights)
    {
        using SafeProcessHandle handle = OpenProcess(rights, inheritHandle: false, pid);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            return error == ErrorAccessDenied
                ? throw new UnauthorizedAccessException($"Process {pid} refused to be opened.", new Win32Exception(error))
                : null;
        }

        return CreationTime(handle) is { } created ? new ProcessProbe(created, WaitForSingleObject(handle, 0) == WaitTimeout) : null;
    }

    /// <summary>
    /// Holds <paramref name="process"/>, whose own handle is already opened and kept on it and was what its identity was proven
    /// through, as a held process; its creation time is read through that handle, the default when it cannot be. Disposing the
    /// held process disposes <paramref name="process"/>.
    /// </summary>
    public static HeldProcess Holding(Process process)
    {
        SafeProcessHandle handle = process.SafeHandle;
        return new HeldProcess(process, handle, CreationTime(handle) ?? default, ownsHandle: false);
    }

    /// <summary>
    /// Closes <see cref="Handle"/> and hands <see cref="Process"/> to the caller, which disposes it; disposing this then leaves
    /// it alone. The pid is no longer held: what is done with the process after is by its pid.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This holds a process by its own handle (<see cref="Holding"/>), which closing would leave the process without.
    /// </exception>
    public Process ReleaseHandle()
    {
        if (!_ownsHandle)
        {
            throw new InvalidOperationException("A process held by its own handle cannot be handed over without it.");
        }

        _handedOver = true;
        Handle.Dispose();
        return Process;
    }

    public void Dispose()
    {
        Handle.Dispose();
        if (!_handedOver)
        {
            Process.Dispose();
        }
    }

    /// <summary>The process's creation time read through <paramref name="handle"/>, in local time; null when it cannot be read.</summary>
    private static DateTime? CreationTime(SafeProcessHandle handle) =>
        GetProcessTimes(handle, out long creation, out _, out _, out _) ? DateTime.FromFileTime(creation) : null;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}

/// <summary>
/// What <see cref="HeldProcess.Probe"/> read of a process: its creation time, in local time as <see cref="Process.StartTime"/>
/// gives it, and whether it was still running.
/// </summary>
internal readonly record struct ProcessProbe(DateTime Created, bool Running);
