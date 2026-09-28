using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// The server's own work: a job object every child the server starts joins, and with it every process that child starts,
/// used only to count their CPU so the load clock can tell the server's own load from other work on the machine. It sets
/// no limits and kills nothing when it closes. Where a child cannot join (not Windows, or a job the server runs in that
/// refuses nesting), its CPU is left out, logged once.
/// </summary>
internal static partial class OwnWork
{
    private const int BasicAccountingInformation = 1;

    private static readonly nint Job = OperatingSystem.IsWindows() ? CreateJobObjectW(0, null) : 0;
    private static int _adoptFailureLogged;

    /// <summary>Adds a started child to the server's job.</summary>
    public static void Adopt(Process process) => Adopt(process.Handle);

    /// <summary>Adds a child to the server's job by its process handle; a suspended child joins before it runs.</summary>
    public static void Adopt(nint processHandle)
    {
        if (Job == 0 || AssignProcessToJobObject(Job, processHandle))
        {
            return;
        }

        int error = Marshal.GetLastPInvokeError();
        if (Interlocked.Exchange(ref _adoptFailureLogged, 1) == 0)
        {
            Log.OwnWorkAdoptFailed(Log.Ambient, "the server's own-work job", error, "its CPU counts as other load");
        }
    }

    /// <summary>The CPU the server's children and their descendants have used, and the server's own, in 100 ns units.</summary>
    public static long CpuTicks() => (JobTicks(Job) ?? 0) + Environment.CpuUsage.TotalTime.Ticks;

    /// <returns>The user and kernel time of every process that is or was in <paramref name="job"/>, or null when it cannot be read.</returns>
    private static long? JobTicks(nint job)
    {
        if (job == 0 || !QueryInformationJobObject(job, BasicAccountingInformation, out BasicAccounting info, Marshal.SizeOf<BasicAccounting>(), 0))
        {
            return null;
        }

        return info.TotalUserTime + info.TotalKernelTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        nint job,
        int informationClass,
        out BasicAccounting information,
        int length,
        nint returnLength
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary><c>JOBOBJECT_BASIC_ACCOUNTING_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public int TotalPageFaultCount;
        public int TotalProcesses;
        public int ActiveProcesses;
        public int TotalTerminatedProcesses;
    }

    /// <summary>
    /// The CPU one tool process and its descendants have used: a job of its own, nested in the server's. When the process
    /// cannot join one, it falls back to the process's own CPU, logged once.
    /// </summary>
    internal sealed class TreeCpu : IDisposable
    {
        private static int _fallbackLogged;
        private readonly nint _job;
        private readonly Process _process;
        private long _last;

        private TreeCpu(nint job, Process process)
        {
            _job = job;
            _process = process;
        }

        /// <summary>The tree's user and kernel time so far, in 100 ns units; it never goes back.</summary>
        public long Ticks
        {
            get
            {
                long now = JobTicks(_job) ?? ProcessTicks();
                _last = Math.Max(_last, now);
                return _last;
            }
        }

        /// <summary>Whether the whole tree's CPU is measured; false when the process could not join a job of its own.</summary>
        public bool Measurable => _job != 0;

        /// <summary>Puts a started process, and every process it starts from now on, in a job of its own.</summary>
        /// <param name="process">The started process.</param>
        /// <param name="logger">Where a failed join is reported, once.</param>
        /// <param name="join">False skips the job, as a failed join does: the seam tests use for an unmeasurable tree.</param>
        public static TreeCpu Adopt(Process process, ILogger logger, bool join)
        {
            nint job = join && OperatingSystem.IsWindows() ? CreateJobObjectW(0, null) : 0;
            if (job != 0 && !AssignProcessToJobObject(job, process.Handle))
            {
                int error = Marshal.GetLastPInvokeError();
                _ = CloseHandle(job);
                job = 0;
                if (Interlocked.Exchange(ref _fallbackLogged, 1) == 0)
                {
                    Log.OwnWorkAdoptFailed(logger, "a tool process's job", error, "its stall guard is off; its ceiling and backstop still apply");
                }
            }

            return new TreeCpu(job, process);
        }

        public void Dispose()
        {
            if (_job != 0)
            {
                _ = CloseHandle(_job);
            }
        }

        private long ProcessTicks()
        {
            try
            {
                return _process.TotalProcessorTime.Ticks;
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // The process has gone or cannot be read; the last reading stands.
                return _last;
            }
        }
    }
}
