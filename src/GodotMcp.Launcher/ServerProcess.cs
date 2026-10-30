using System.Runtime.InteropServices;

namespace GodotMcp.Launcher;

/// <summary>
/// Runs the server in a job that kills it when the launcher's handle to the job closes, however the launcher ends. The
/// job lets the server's own children leave it silently, so the games it starts outlive it and quit by themselves.
/// </summary>
internal static partial class ServerProcess
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint HandleFlagInherit = 0x1;
    private const int UseStdHandles = 0x100;
    private const uint CreateSuspended = 0x4;
    private const uint JobObjectLimitSilentBreakawayOk = 0x1000;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int ExtendedLimitInformationClass = 9;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const string NotStarted = "could not be started";

    /// <summary>Starts the server suspended, puts it in the job, resumes it, waits for it and returns its exit code.</summary>
    /// <exception cref="LauncherException">A step failed; a server already created is ended before it ran.</exception>
    public static int Run(string server, string commandLine)
    {
        nint job = CreateKillOnCloseJob(server);
        ProcessInformation native = CreateSuspendedServer(server, commandLine);
        if (!AssignProcessToJobObject(job, native.Process))
        {
            throw Abandon(native, "AssignProcessToJobObject", server);
        }

        if (ResumeThread(native.Thread) == uint.MaxValue)
        {
            throw Abandon(native, "ResumeThread", server);
        }

        _ = CloseHandle(native.Thread);
        if (WaitForSingleObject(native.Process, Infinite) == WaitFailed || !GetExitCodeProcess(native.Process, out uint exitCode))
        {
            throw Failure(server, "could not be waited for", "WaitForSingleObject or GetExitCodeProcess");
        }

        // The job's handle is left open for this process's exit to close, so the server is ended with it however it ends.
        return unchecked((int)exitCode);
    }

    private static nint CreateKillOnCloseJob(string server)
    {
        nint job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            throw Failure(server, NotStarted, "CreateJobObjectW");
        }

        ExtendedLimitInformation information = new();
        information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitSilentBreakawayOk;
        return SetInformationJobObject(job, ExtendedLimitInformationClass, ref information, Marshal.SizeOf<ExtendedLimitInformation>())
            ? job
            : throw Failure(server, NotStarted, "SetInformationJobObject");
    }

    /// <summary>Creates the server suspended, on this process's own standard handles, in this process's environment.</summary>
    private static ProcessInformation CreateSuspendedServer(string server, string commandLine)
    {
        StartupInfo info = new()
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = UseStdHandles,
            StdInput = InheritableStdHandle(StdInputHandle, server),
            StdOutput = InheritableStdHandle(StdOutputHandle, server),
            StdError = InheritableStdHandle(StdErrorHandle, server),
        };
        nint line = Marshal.StringToHGlobalUni(commandLine);
        try
        {
            return CreateProcessW(server, line, 0, 0, true, CreateSuspended, 0, null, ref info, out ProcessInformation native)
                ? native
                : throw Failure(server, NotStarted, "CreateProcessW");
        }
        finally
        {
            Marshal.FreeHGlobal(line);
        }
    }

    /// <summary>This process's standard handle, made inheritable so the server gets it; a missing one is passed on as it is.</summary>
    private static nint InheritableStdHandle(int which, string server)
    {
        nint handle = GetStdHandle(which);
        if (handle is 0 or -1)
        {
            return handle;
        }

        return SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit)
            ? handle
            : throw Failure(server, NotStarted, "SetHandleInformation");
    }

    /// <summary>Ends a server that never ran, and the failure that stopped it.</summary>
    private static LauncherException Abandon(ProcessInformation native, string step, string server)
    {
        LauncherException failure = Failure(server, NotStarted, step);
        _ = TerminateProcess(native.Process, 1);
        _ = CloseHandle(native.Thread);
        _ = CloseHandle(native.Process);
        return failure;
    }

    /// <summary>The refusal naming the server, what went wrong with it, and the call that failed with its Windows error.</summary>
    private static LauncherException Failure(string server, string outcome, string call)
    {
        int error = Marshal.GetLastPInvokeError();
        return new LauncherException($"{server} {outcome}: {call} failed with Windows error {error}: {Marshal.GetPInvokeErrorMessage(error)}");
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string application,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation
    );

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimitInformation information, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary><c>STARTUPINFOW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    /// <summary><c>PROCESS_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

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
}
