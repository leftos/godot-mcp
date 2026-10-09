#requires -Version 7

<#
.SYNOPSIS
Runs one command on a desktop of its own that nobody looks at, and exits with the command's exit status.

.DESCRIPTION
Usage: pwsh tools/hidden-desktop.ps1 -- <program> [args...]

Why this exists: the integration tests start real Godot games, and Windows clamps every new window onto the screen
(Godot 4.7.2 display_server_windows.cpp L7208-7212), so even a quiet run, which the bridge parks off-screen in _ready,
flashes a 640x360 window at the top-left of the primary screen for about 300 ms, and a run that is not quiet shows
centred and takes the focus. A process started on another desktop draws there instead, and every process it starts
inherits that desktop, so the whole test run is invisible on the user's own desktop. CreateDesktopW creates the desktop
in this script's own window station (WinSta0 in an interactive session, a service station under ssh or a service), and
STARTUPINFO.lpDesktop names that station: "<station>\<name>", with the station's name read by
GetProcessWindowStation and GetUserObjectInformationW. A child named onto a station it cannot open fails before any of
its code runs. `run.ps1 itest` runs its test gates through this script on Windows; it is Windows only.

 - The desktop is named godot-mcp-itest-<this script's process id>, so two runs at once (two worktrees) never share
   one, and it is closed when the command ends.
 - The command inherits this script's standard input, output and error (STARTF_USESTDHANDLES with bInheritHandles),
   so everything it prints lands where this script's output goes: tools/gate.ps1's log.
 - It is started with CREATE_NO_WINDOW: CREATE_NEW_CONSOLE makes Windows Terminal open a visible window.
 - It runs in a job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE held only by this script, so when the gate kills
   this script, the kernel closes the job and kills the command with every process it started. On a normal end
   the limit is lifted before the job is closed, so a process the command leaves behind (a build server that another
   worktree's build may be using) outlives it, as it would without this script.
 - The command line is built with the Microsoft C runtime's quoting rules, since CreateProcessW takes one string, and
   is started as it arrived, never through a shell.

The command is read by hand out of $args, as tools/gate.ps1 reads its own: a param block would send the words through
PowerShell's parameter binder, which takes the bare -- and the command's own options for this script's. Everything
past a leading -- is the command; a caller in a session of its own that had the -- eaten by the parser has every word
taken as the command.

A Win32 call that fails stops the script with the call's name and its Win32 error code (status 1). A usage this script
cannot read, or a run on another OS, exits 2.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$usage = 'usage: pwsh tools/hidden-desktop.ps1 -- <program> [args...]'

$words = @($args)
if ($words.Count -gt 0 -and [string]$words[0] -eq '--') {
    $words = @($words | Select-Object -Skip 1)
}
if ($words.Count -lt 1) {
    [Console]::Error.WriteLine($usage)
    exit 2
}
if (-not $IsWindows) {
    [Console]::Error.WriteLine('hidden-desktop: desktops are a Windows feature; run the command directly on this OS.')
    exit 2
}

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class HiddenDesktop
{
    const uint GenericAll = 0x10000000;
    const uint CreateSuspended = 0x00000004;
    const uint CreateNoWindow = 0x08000000;
    const int StartfUseStdHandles = 0x00000100;
    const uint HandleFlagInherit = 0x00000001;
    const uint Infinite = 0xFFFFFFFF;
    const uint WaitObject0 = 0;
    const uint ResumeFailed = 0xFFFFFFFF;
    const int StdInputHandle = -10;
    const int StdOutputHandle = -11;
    const int StdErrorHandle = -12;
    const int JobObjectExtendedLimitInformation = 9;
    const uint JobObjectLimitKillOnJobClose = 0x00002000;
    const int UoiName = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    // JOBOBJECT_BASIC_LIMIT_INFORMATION, IO_COUNTERS and JOBOBJECT_EXTENDED_LIMIT_INFORMATION (winnt.h).
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devMode, uint flags, uint access, IntPtr attributes);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetUserObjectInformationW(IntPtr handle, int index, [Out] char[] buffer, int length, out int needed);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    // Runs the command line on a new desktop named desktopName, waits for it and returns its exit code.
    public static int Run(string desktopName, string commandLine)
    {
        IntPtr desktop = CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        Check(desktop != IntPtr.Zero, "CreateDesktopW(" + desktopName + ")");
        try
        {
            return RunInJob(desktopName, commandLine);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    static int RunInJob(string desktopName, string commandLine)
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        Check(job != IntPtr.Zero, "CreateJobObjectW");
        try
        {
            SetKillOnClose(job, true);
            int exitCode = RunChild(job, desktopName, commandLine);
            SetKillOnClose(job, false);
            return exitCode;
        }
        finally
        {
            CloseHandle(job);
        }
    }

    static void SetKillOnClose(IntPtr job, bool kill)
    {
        var limits = new ExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = kill ? JobObjectLimitKillOnJobClose : 0;
        bool set = SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf(limits));
        Check(set, "SetInformationJobObject");
    }

    // Started suspended so that nothing it starts can run before it is in the job, then resumed.
    static int RunChild(IntPtr job, string desktopName, string commandLine)
    {
        var startup = new StartupInfo();
        startup.cb = Marshal.SizeOf(typeof(StartupInfo));
        startup.lpDesktop = OwnStationName() + "\\" + desktopName;
        startup.dwFlags = StartfUseStdHandles;
        startup.hStdInput = InheritableStdHandle(StdInputHandle);
        startup.hStdOutput = InheritableStdHandle(StdOutputHandle);
        startup.hStdError = InheritableStdHandle(StdErrorHandle);
        ProcessInformation child;
        bool created = CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
            CreateNoWindow | CreateSuspended, IntPtr.Zero, null, ref startup, out child);
        Check(created, "CreateProcessW(" + commandLine + ")");
        try
        {
            if (!AssignProcessToJobObject(job, child.hProcess))
            {
                int error = Marshal.GetLastWin32Error();
                TerminateProcess(child.hProcess, 1);
                throw Failure("AssignProcessToJobObject", error);
            }
            Check(ResumeThread(child.hThread) != ResumeFailed, "ResumeThread");
            Check(WaitForSingleObject(child.hProcess, Infinite) == WaitObject0, "WaitForSingleObject");
            uint exitCode;
            Check(GetExitCodeProcess(child.hProcess, out exitCode), "GetExitCodeProcess");
            return unchecked((int)exitCode);
        }
        finally
        {
            CloseHandle(child.hThread);
            CloseHandle(child.hProcess);
        }
    }

    // The name of the window station this process runs in, where CreateDesktopW put the desktop. The station's
    // handle belongs to the process and is not closed.
    static string OwnStationName()
    {
        IntPtr station = GetProcessWindowStation();
        Check(station != IntPtr.Zero, "GetProcessWindowStation");
        var name = new char[256];
        int needed;
        Check(GetUserObjectInformationW(station, UoiName, name, name.Length * sizeof(char), out needed), "GetUserObjectInformationW(UOI_NAME)");
        return new string(name, 0, Array.IndexOf(name, '\0'));
    }

    // STARTF_USESTDHANDLES requires inheritable handles (STARTUPINFOW, hStdInput). A missing one (null or
    // INVALID_HANDLE_VALUE) is passed on as it is, and the command has none either.
    static IntPtr InheritableStdHandle(int which)
    {
        IntPtr handle = GetStdHandle(which);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return handle;
        }
        Check(SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit), "SetHandleInformation(std " + which + ")");
        return handle;
    }

    static void Check(bool succeeded, string operation)
    {
        if (!succeeded)
        {
            throw Failure(operation, Marshal.GetLastWin32Error());
        }
    }

    static Win32Exception Failure(string operation, int error)
    {
        string reason = new Win32Exception(error).Message;
        return new Win32Exception(error, "hidden-desktop: " + operation + " failed with Win32 error " + error + ": " + reason);
    }

    // One command line out of separate words, each quoted so that the Microsoft C runtime reads it back as the same
    // word ("Parsing C command-line arguments": https://learn.microsoft.com/cpp/c-language/parsing-c-command-line-arguments).
    public static string JoinArguments(string[] words)
    {
        var line = new StringBuilder();
        for (int i = 0; i < words.Length; i++)
        {
            if (i > 0)
            {
                line.Append(' ');
            }
            AppendQuoted(line, words[i]);
        }
        return line.ToString();
    }

    // A word without white space or quotes goes as it is. Otherwise it is quoted, a quote inside it is escaped with a
    // backslash, and the backslashes just before a quote (the escaped one or the closing one) are doubled; other
    // backslashes are literal.
    static void AppendQuoted(StringBuilder line, string word)
    {
        if (word.Length > 0 && word.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            line.Append(word);
            return;
        }
        line.Append('"');
        int backslashes = 0;
        foreach (char c in word)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            line.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            line.Append(c);
            backslashes = 0;
        }
        line.Append('\\', backslashes * 2);
        line.Append('"');
    }
}
'@

$desktopName = "godot-mcp-itest-$PID"
$commandLine = [HiddenDesktop]::JoinArguments([string[]]$words)
exit ([HiddenDesktop]::Run($desktopName, $commandLine))
