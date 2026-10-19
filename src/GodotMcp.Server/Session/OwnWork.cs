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
    private const int ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimitInformation information, int length);

    /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary><c>IO_COUNTERS</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

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
    /// cannot join one, it falls back to the process's own CPU, logged once; a job made to kill on close refuses the start
    /// instead, since nothing else would end the tree.
    /// </summary>
    internal sealed class TreeCpu : IDisposable
    {
        private static int _fallbackLogged;
        private nint _job;
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
        /// <param name="killOnClose">
        /// True gives the job <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so closing it ends every process in it; a job that
        /// cannot be made or joined is then a failure, not a fallback, since nothing else would end the tree.
        /// </param>
        /// <exception cref="Win32Exception">The kill-on-close job could not be made or joined.</exception>
        public static TreeCpu Adopt(Process process, ILogger logger, bool join, bool killOnClose = false)
        {
            nint job = CreateJob(join, killOnClose);
            if (job != 0 && !AssignProcessToJobObject(job, process.Handle))
            {
                job = JoinFailed(job, logger, killOnClose);
            }

            return new TreeCpu(job, process);
        }

        /// <summary>The job a start joins: none when <paramref name="join"/> is false, made to kill on close when asked.</summary>
        private static nint CreateJob(bool join, bool killOnClose)
        {
            if (!join || !OperatingSystem.IsWindows())
            {
                return killOnClose ? throw JobFailure(0) : 0;
            }

            nint job = CreateJobObjectW(0, null);
            if (job == 0)
            {
                return killOnClose ? throw JobFailure(Marshal.GetLastPInvokeError()) : 0;
            }

            if (killOnClose && !SetKillOnClose(job))
            {
                int error = Marshal.GetLastPInvokeError();
                _ = CloseHandle(job);
                throw JobFailure(error);
            }

            return job;
        }

        /// <summary>Closes the job a process could not join: a failure when it must kill on close, else the logged CPU fallback.</summary>
        private static nint JoinFailed(nint job, ILogger logger, bool killOnClose)
        {
            int error = Marshal.GetLastPInvokeError();
            _ = CloseHandle(job);
            if (killOnClose)
            {
                throw JobFailure(error);
            }

            if (Interlocked.Exchange(ref _fallbackLogged, 1) == 0)
            {
                Log.OwnWorkAdoptFailed(logger, "a tool process's job", error, "its stall guard is off; its ceiling and backstop still apply");
            }

            return 0;
        }

        /// <summary>Gives the job the kill-on-close limit; false, with the Windows error set, when it cannot.</summary>
        private static bool SetKillOnClose(nint job)
        {
            ExtendedLimitInformation information = new();
            information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            return SetInformationJobObject(job, ExtendedLimitInformationClass, ref information, Marshal.SizeOf<ExtendedLimitInformation>());
        }

        /// <summary>The refusal of a start whose kill-on-close job could not be made or joined.</summary>
        private static Win32Exception JobFailure(int error) =>
            new(
                error,
                "The process could not be put in a job that ends it and everything it starts when the job closes "
                    + $"(Windows error {error}), so it was not started."
            );

        /// <summary>Closes the job, ending every process in it when it was made to kill on close; later calls do nothing.</summary>
        public void CloseJob()
        {
            nint job = Interlocked.Exchange(ref _job, 0);
            if (job != 0)
            {
                _ = CloseHandle(job);
            }
        }

        public void Dispose() => CloseJob();

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
